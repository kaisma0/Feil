using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Feil.Core;
using Feil.Models;
using Feil.Services;
using Feil.Services.Achievements;
using Feil.Services.JobParser;

namespace Feil.ViewModels.Pages;

public partial class QueuePageViewModel : ViewModelBase, IDisposable
{
    private readonly DownloadService _downloadService = new();
    private readonly JobArchiveImportService _jobArchiveImporter = new();
    private readonly Action<HistoryEntry>? _onJobFinished;
    private readonly SettingsPageViewModel? _settings;
    private CancellationTokenSource? _activeJobCts;
    private readonly System.Threading.Timer _speedTimer;
    private bool _isRestoringQueueState;
    private volatile bool _isDisposed;
    // Queue mutations are UI-thread owned. Background callbacks must post to the dispatcher
    // before touching ObservableCollection, ActiveJob, or this batching counter.
    private int _persistenceSuspendCount;
    private long _lastDownloadedBytes;

    private long _currentDownloadedBytes;
    private long _currentTotalBytes;
    private long _currentDiskWrittenBytes;
    private long _lastDiskWrittenBytes;
    private double _smoothedNetworkBps;
    private double _smoothedDiskBps;
    private double _smoothedEtaBps;
    private const double SpeedAlpha = 0.3;
    private const double EtaAlpha = 0.15;

    [ObservableProperty]
    private DownloadJobViewModel? _activeJob;

    public ObservableCollection<DownloadJobViewModel> QueuedJobs { get; } = [];

    [ObservableProperty]
    private bool _hasActiveJob;

    [ObservableProperty]
    private bool _isQueueEmpty;

    [ObservableProperty]
    private bool _hasQueuedJobs;

    public QueuePageViewModel(Action<HistoryEntry>? onJobFinished = null, SettingsPageViewModel? settings = null)
    {
        _onJobFinished = onJobFinished;
        _settings = settings;
        HasActiveJob = ActiveJob is not null;
        IsQueueEmpty = QueuedJobs.Count == 0 && ActiveJob is null;
        HasQueuedJobs = QueuedJobs.Count > 0;

        QueuedJobs.CollectionChanged += OnQueuedJobsChanged;

        _downloadService.ProgressChanged = OnProgressChanged;
        _downloadService.TotalSizeChanged = OnTotalSizeChanged;
        _downloadService.DiskProgressChanged = OnDiskProgressChanged;

        _speedTimer = new System.Threading.Timer(UpdateSpeedMetrics, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        RestorePersistedQueueState();

        // Observe any unexpected crash from the background loop so it
        // isn't silently swallowed by the GC finaliser.
        var queueTask = ProcessQueueAsync();
        _ = queueTask.ContinueWith(
            t => System.Diagnostics.Trace.TraceError($"[Feil] ProcessQueueAsync terminated unexpectedly: {t.Exception}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private void OnQueuedJobsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        IsQueueEmpty = QueuedJobs.Count == 0 && ActiveJob is null;
        HasQueuedJobs = QueuedJobs.Count > 0;
        PersistQueueState();
    }

    partial void OnActiveJobChanged(DownloadJobViewModel? value)
    {
        HasActiveJob = value is not null;
        IsQueueEmpty = QueuedJobs.Count == 0 && value is null;
        Interlocked.Exchange(ref _lastDownloadedBytes, value?.DownloadedBytes ?? 0);
        Interlocked.Exchange(ref _currentDownloadedBytes, value?.DownloadedBytes ?? 0);
        Interlocked.Exchange(ref _currentTotalBytes, value?.TotalBytes ?? 0);
        Interlocked.Exchange(ref _currentDiskWrittenBytes, 0);
        Interlocked.Exchange(ref _lastDiskWrittenBytes, 0);
        _smoothedNetworkBps = 0;
        _smoothedDiskBps = 0;
        _smoothedEtaBps = 0;
        PersistQueueState();
    }

    [RelayCommand]
    private void PauseResumeActive()
    {
        if (ActiveJob is null) return;

        if (ActiveJob.Status is DownloadJobStatus.Downloading or DownloadJobStatus.Verifying)
        {
            _downloadService.Pause();
            ActiveJob.ResumeStatus = ActiveJob.Status;
            ActiveJob.Status = DownloadJobStatus.Paused;
        }
        else if (ActiveJob.Status == DownloadJobStatus.Paused)
        {
            _downloadService.Resume();
            ActiveJob.Status = ActiveJob.ResumeStatus;
        }

        PersistQueueState();
    }

    [RelayCommand]
    private void CancelActive()
    {
        if (ActiveJob is null) return;

        var cancelledJob = ActiveJob;

        MutateQueueState(() =>
        {
            cancelledJob.Status = DownloadJobStatus.Cancelled;

            var ctsToCancell = _activeJobCts;
            _activeJobCts = null;
            ctsToCancell?.Cancel();
            ctsToCancell?.Dispose();

            _onJobFinished?.Invoke(CreateHistoryEntry(cancelledJob, DownloadJobStatus.Cancelled));

            ActiveJob = null;
            if (QueuedJobs.Count > 0)
            {
                PromoteNextJob();
            }
        });
    }

    [RelayCommand]
    private void RemoveFromQueue(DownloadJobViewModel? job)
    {
        if (job is null) return;
        QueuedJobs.Remove(job);
    }

    [RelayCommand]
    private void MoveUp(DownloadJobViewModel? job)
    {
        if (job is null) return;
        int index = QueuedJobs.IndexOf(job);
        if (index > 0)
        {
            QueuedJobs.Move(index, index - 1);
        }
        else if (index == 0)
        {
            MutateQueueState(() =>
            {
                if (ActiveJob is not null)
                {
                    var oldActive = ActiveJob;

                    var ctsToCancell = _activeJobCts;
                    _activeJobCts = null;
                    ctsToCancell?.Cancel();
                    ctsToCancell?.Dispose();

                    oldActive.Status = oldActive.Status == DownloadJobStatus.Paused
                        ? DownloadJobStatus.Paused
                        : DownloadJobStatus.Queued;

                    job.SetRunningStatus(job.GetInitialRunningStatus());
                    ActiveJob = job;

                    QueuedJobs[0] = oldActive;
                }
                else
                {
                    job.SetRunningStatus(job.GetInitialRunningStatus());
                    ActiveJob = job;
                    QueuedJobs.RemoveAt(0);
                }
            });
        }
    }

    [RelayCommand]
    private void MoveDown(DownloadJobViewModel? job)
    {
        if (job is null) return;
        int index = QueuedJobs.IndexOf(job);
        if (index >= 0 && index < QueuedJobs.Count - 1)
            QueuedJobs.Move(index, index + 1);
    }

    public async Task ProcessZipFilesAsync(IEnumerable<string> zipPaths)
    {
        foreach (var zipPath in zipPaths)
        {
            PreparedJobArchive? importedJob = null;

            try
            {
                importedJob = await _jobArchiveImporter.PrepareAsync(zipPath, GetInstallBaseDirectory());
                if (importedJob is null)
                {
                    continue;
                }

                var confirmed = await ShowDepotSelectionDialogAsync(
                    importedJob.Job,
                    importedJob.GameName,
                    importedJob.LuaFilePath);

                if (!confirmed)
                {
                    continue;
                }

                _jobArchiveImporter.Commit(importedJob);

                var newJob = await DownloadJobViewModel.CreateAsync(
                    importedJob.Job,
                    importedJob.JobDirectory,
                    importedJob.InstallDirectory,
                    importedJob.GameName);

                EnqueueJob(newJob);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"[Feil] Error processing zip file {zipPath}: {ex}");
            }
            finally
            {
                if (importedJob is not null)
                {
                    JobArchiveImportService.DeleteDirectoryBestEffort(importedJob.TemporaryJobDirectory);
                }
            }
        }
    }

    private void PromoteNextJob()
    {
        if (QueuedJobs.Count == 0) return;

        MutateQueueState(() =>
        {
            DownloadJobViewModel next = QueuedJobs[0];
            if (next.Status != DownloadJobStatus.Paused)
            {
                next.SetRunningStatus(next.GetInitialRunningStatus());
            }

            ActiveJob = next;
            QueuedJobs.RemoveAt(0);
        });
    }

    public void EnqueueVerification(HistoryEntry entry)
    {
        EnqueueHistoryJob(entry, DownloadJobRunMode.VerifyOnly, "reverification");
    }

    public void EnqueueRetry(HistoryEntry entry)
    {
        EnqueueHistoryJob(entry, DownloadJobRunMode.DownloadAndVerify, "retry");
    }

    private void EnqueueHistoryJob(HistoryEntry entry, DownloadJobRunMode runMode, string operationName)
    {
        if (runMode == DownloadJobRunMode.VerifyOnly && string.IsNullOrWhiteSpace(entry.InstallDirectory))
        {
            return;
        }

        if (runMode == DownloadJobRunMode.DownloadAndVerify && string.IsNullOrWhiteSpace(entry.JobDirectory))
        {
            return;
        }

        var source = ResolveHistoryJobSource(entry, operationName);
        if (source is null)
        {
            return;
        }

        var effectiveEntry = entry.Job == source.Job ? entry : entry with { Job = source.Job };

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (source.LuaFilePath != null)
                {
                    var confirmed = await ShowDepotSelectionDialogAsync(
                        effectiveEntry.Job!,
                        effectiveEntry.GameName,
                        source.LuaFilePath);

                    if (!confirmed)
                    {
                        return;
                    }
                }

                var queueJob = runMode == DownloadJobRunMode.VerifyOnly
                    ? DownloadJobViewModel.CreateForVerification(effectiveEntry)
                    : await DownloadJobViewModel.CreateAsync(
                        effectiveEntry.Job!,
                        effectiveEntry.JobDirectory!,
                        effectiveEntry.InstallDirectory,
                        effectiveEntry.GameName);

                EnqueueJob(queueJob);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"[Feil] Failed to enqueue history {operationName}: {ex}");
            }
        });
    }

    private static HistoryJobSource? ResolveHistoryJobSource(HistoryEntry entry, string operationName)
    {
        string? luaFilePath = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(entry.JobDirectory) && Directory.Exists(entry.JobDirectory))
            {
                luaFilePath = Directory.GetFiles(entry.JobDirectory, "*.lua").FirstOrDefault();
            }

            var job = entry.Job;
            if (job is null && luaFilePath is not null)
            {
                var parser = new LuaJobParser();
                var lines = File.ReadAllLines(luaFilePath);
                job = parser.Parse(lines);
            }

            return job is null ? null : new HistoryJobSource(job, luaFilePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"[Feil] Failed to resolve job from history for {operationName}: {ex}");
            return null;
        }
    }

    private async Task<bool> ShowDepotSelectionDialogAsync(DownloadJob job, string gameName, string luaFilePath)
    {
        var metadata = await Feil.Services.Steam.SteamAppInfoService.GetDepotMetadataAsync(job.AppId);

        var targetOs = "windows"; // "always pick windows if multiple are available"
        var skipSetting = Feil.Services.SettingsService.Load().SkipDepotSelection;

        var osExcludedIds = new HashSet<int>();
        var dialogItems = new List<DepotSelectionItemViewModel>();

        foreach (var depot in job.Depots)
        {
            metadata.TryGetValue(depot.AppId, out var meta);
            var osList = meta?.OsList ?? string.Empty;
            var isOsSpecific = !string.IsNullOrWhiteSpace(osList);

            if (isOsSpecific && !osList.Contains(targetOs, StringComparison.OrdinalIgnoreCase))
            {
                // Skip OS depots that don't match target OS
                osExcludedIds.Add(depot.AppId);
                continue;
            }

            if (isOsSpecific || depot.AppId == job.AppId || string.IsNullOrWhiteSpace(depot.DecryptionKey))
            {
                // Hide OS depots, main game depot, and keyless depots but keep them included implicitly
                continue;
            }

            if (skipSetting)
            {
                continue;
            }

            var name = $"Depot {depot.AppId}";
            if (meta != null && !string.IsNullOrWhiteSpace(meta.Name))
            {
                name = meta.Name;
            }
            else if (depot.AppId != job.AppId)
            {
                var appName = await Feil.Services.Steam.SteamAppInfoService.GetGameNameAsync(depot.AppId);
                if (!string.IsNullOrWhiteSpace(appName))
                {
                    name = appName;
                }
            }

            dialogItems.Add(new DepotSelectionItemViewModel
            {
                DepotId = depot.AppId,
                Name = name,
                IsSelected = true,
                IsOsSpecific = isOsSpecific,
                OsList = osList
            });
        }

        if (dialogItems.Count == 0 || skipSetting)
        {
            await ProcessExclusionsAsync(job, luaFilePath, osExcludedIds);
            return true;
        }

        var confirmed = await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var vm = new DepotSelectionDialogViewModel { GameName = gameName };
            foreach (var item in dialogItems.OrderBy(d => d.Name))
            {
                vm.Depots.Add(item);
            }

            var view = new Feil.Views.DepotSelectionDialog { DataContext = vm };
            return await Ursa.Controls.OverlayDialog.ShowCustomAsync<bool>(view, vm);
        });

        if (confirmed)
        {
            var excludedDepots = new HashSet<int>(osExcludedIds);
            foreach (var depotVm in dialogItems.Where(d => !d.IsSelected))
            {
                excludedDepots.Add(depotVm.DepotId);
            }

            await ProcessExclusionsAsync(job, luaFilePath, excludedDepots);
        }

        return confirmed;
    }

    private async Task ProcessExclusionsAsync(DownloadJob job, string luaFilePath, HashSet<int> excludedDepots)
    {
        if (excludedDepots.Count == 0) return;

        job.Depots.RemoveAll(d => excludedDepots.Contains(d.AppId));
        job.Entitlements.RemoveAll(id => excludedDepots.Contains(id));

        if (File.Exists(luaFilePath))
        {
            var parser = new Feil.Services.JobParser.LuaJobParser();
            var lines = await File.ReadAllLinesAsync(luaFilePath);
            var filteredLines = parser.FilterLines(lines, excludedDepots);
            await File.WriteAllLinesAsync(luaFilePath, filteredLines);
        }
    }

    private async Task ProcessQueueAsync()
    {
        while (!_isDisposed)
        {
            try
            {
                if (ActiveJob != null && IsRunnableStatus(ActiveJob.Status) && !_downloadService.IsRunning)
                {
                    var oldCts = _activeJobCts;
                    var jobCts = new CancellationTokenSource();
                    _activeJobCts = jobCts;
                    oldCts?.Dispose();

                    var jobToRun = ActiveJob;

                    jobToRun.StartedAt ??= DateTimeOffset.UtcNow;
                    PersistQueueState();

                    int result = await ExecuteJobAsync(jobToRun, jobCts.Token);

                    if (_isDisposed) break;

                    bool isCancelled = jobToRun.Status == DownloadJobStatus.Cancelled
                                       || jobCts.IsCancellationRequested
                                       || !ReferenceEquals(_activeJobCts, jobCts);

                    if (!isCancelled && ActiveJob == jobToRun)
                    {
                        if (result == 0)
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                if (jobToRun.Status == DownloadJobStatus.Cancelled) return;

                                MutateQueueState(() =>
                                {
                                    jobToRun.Status = DownloadJobStatus.Completed;
                                    _onJobFinished?.Invoke(CreateHistoryEntry(jobToRun, DownloadJobStatus.Completed));

                                    if (OperatingSystem.IsLinux())
                                    {
                                        var sls = new Feil.Services.SLSsteam.SLSsteamService();
                                        if (sls.IsInstalled())
                                        {
                                            sls.ModifyConfig(new[] { "AdditionalApps" }, "add", jobToRun.AppId, "list");
                                        }
                                    }

                                    // Fire-and-forget: generate Steam achievement schema
                                    _ = StatsSchemaService.TriggerAsync((uint)jobToRun.AppId);

                                    if (ActiveJob == jobToRun)
                                    {
                                        ActiveJob = null;
                                        PromoteNextJob();
                                    }
                                });
                            });
                        }
                        else
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                if (jobToRun.Status == DownloadJobStatus.Cancelled) return;

                                MutateQueueState(() =>
                                {
                                    jobToRun.Status = DownloadJobStatus.Failed;
                                    _onJobFinished?.Invoke(CreateHistoryEntry(jobToRun, DownloadJobStatus.Failed));

                                    if (ActiveJob == jobToRun)
                                    {
                                        ActiveJob = null;
                                        PromoteNextJob();
                                    }
                                });
                            });
                        }
                    }

                    if (ReferenceEquals(_activeJobCts, jobCts))
                    {
                        _activeJobCts = null;
                        jobCts.Dispose();
                    }
                }
                else if (ActiveJob == null && QueuedJobs.Count > 0)
                {
                    Dispatcher.UIThread.Post(PromoteNextJob);
                }
            }
            catch (Exception ex)
            {
                if (_isDisposed) break;

                System.Diagnostics.Trace.TraceError($"[Feil] Queue loop error: {ex}");
                if (ActiveJob != null && IsRunnableStatus(ActiveJob.Status))
                {
                    var jobToFail = ActiveJob;
                    Dispatcher.UIThread.Post(() =>
                    {
                        MutateQueueState(() =>
                        {
                            if (jobToFail.Status == DownloadJobStatus.Cancelled) return;
                            jobToFail.Status = DownloadJobStatus.Failed;
                            _onJobFinished?.Invoke(CreateHistoryEntry(jobToFail, DownloadJobStatus.Failed));
                            if (ActiveJob == jobToFail)
                            {
                                ActiveJob = null;
                            }
                            PromoteNextJob();
                        });
                    });
                }
            }

            await Task.Delay(200);
        }
    }

    private void OnProgressChanged(ulong downloaded, ulong total)
    {
        Interlocked.Exchange(ref _currentDownloadedBytes, (long)downloaded);
        Interlocked.Exchange(ref _currentTotalBytes, (long)total);
    }

    private void OnDiskProgressChanged(ulong diskBytesWritten)
    {
        Interlocked.Exchange(ref _currentDiskWrittenBytes, (long)diskBytesWritten);
    }

    private void OnTotalSizeChanged(ulong compressedTotal)
    {
        Interlocked.Exchange(ref _currentTotalBytes, (long)compressedTotal);

        Dispatcher.UIThread.Post(() =>
        {
            var job = ActiveJob;
            if (_isDisposed || job is null) return;
            job.TotalBytes = (long)compressedTotal;
        });
    }

    private void UpdateSpeedMetrics(object? state)
    {
        if (_isDisposed) return;

        var job = ActiveJob;
        if (job == null || !IsRunnableStatus(job.Status) || !_downloadService.IsRunning)
        {
            _lastDownloadedBytes = job?.DownloadedBytes ?? 0;
            _lastDiskWrittenBytes = 0;
            _smoothedNetworkBps = 0;
            _smoothedDiskBps = 0;
            _smoothedEtaBps = 0;
            if (job != null)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_isDisposed) return;

                    job.NetworkSpeedBps = 0;
                    job.DiskSpeedBps = 0;
                    job.EstimatedTimeRemaining = TimeSpan.Zero;
                });
            }
            return;
        }

        long currentBytes = Interlocked.Read(ref _currentDownloadedBytes);
        long totalBytes = Interlocked.Read(ref _currentTotalBytes);
        long lastBytes = Interlocked.Read(ref _lastDownloadedBytes);
        long networkDelta = Math.Max(0, currentBytes - lastBytes);

        long currentDisk = Interlocked.Read(ref _currentDiskWrittenBytes);
        long lastDisk = Interlocked.Read(ref _lastDiskWrittenBytes);
        long diskDelta = Math.Max(0, currentDisk - lastDisk);

        // EMA smoothing: seed directly on first non-zero sample to avoid slow ramp-up
        _smoothedNetworkBps = _smoothedNetworkBps == 0 && networkDelta > 0
            ? networkDelta
            : SpeedAlpha * networkDelta + (1.0 - SpeedAlpha) * _smoothedNetworkBps;

        _smoothedDiskBps = _smoothedDiskBps == 0 && diskDelta > 0
            ? diskDelta
            : SpeedAlpha * diskDelta + (1.0 - SpeedAlpha) * _smoothedDiskBps;

        _smoothedEtaBps = _smoothedEtaBps == 0 && networkDelta > 0
            ? networkDelta
            : EtaAlpha * networkDelta + (1.0 - EtaAlpha) * _smoothedEtaBps;

        // Capture for closure
        var netSpeed = _smoothedNetworkBps;
        var diskSpeed = _smoothedDiskBps;
        var etaSpeed = _smoothedEtaBps;

        Dispatcher.UIThread.Post(() =>
        {
            if (!_isDisposed && ActiveJob == job && IsRunnableStatus(job.Status))
            {
                job.DownloadedBytes = currentBytes;
                if (totalBytes > 0)
                {
                    job.TotalBytes = totalBytes;
                    job.ProgressPercent = (double)currentBytes / totalBytes * 100.0;
                }

                job.NetworkSpeedBps = netSpeed;
                job.DiskSpeedBps = diskSpeed;

                if (etaSpeed > 0 && totalBytes > currentBytes)
                {
                    job.EstimatedTimeRemaining = TimeSpan.FromSeconds((totalBytes - currentBytes) / etaSpeed);
                }
                else
                {
                    job.EstimatedTimeRemaining = TimeSpan.Zero;
                }
            }
        });

        Interlocked.Exchange(ref _lastDownloadedBytes, currentBytes);
        Interlocked.Exchange(ref _lastDiskWrittenBytes, currentDisk);
    }

    private HistoryEntry CreateHistoryEntry(DownloadJobViewModel job, DownloadJobStatus status) =>
        new(
            Guid.NewGuid(),
            job.GameName,
            job.GameIconUrl,
            job.AppId,
            status,
            job.TotalBytes,
            job.StartedAt ?? DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            job.DepotCount,
            Job: job.Job,
            JobDirectory: job.JobDirectory,
            InstallDirectory: job.InstallDirectory
        );

    private void RestorePersistedQueueState()
    {
        var state = QueueStateService.Load();
        if (state is null) return;

        _isRestoringQueueState = true;
        try
        {
            ActiveJob = RestoreJob(state.ActiveJob, isActive: true);

            foreach (var queuedJob in state.QueuedJobs)
            {
                var restored = RestoreJob(queuedJob, isActive: false);
                if (restored is not null)
                {
                    QueuedJobs.Add(restored);
                }
            }
        }
        finally
        {
            _isRestoringQueueState = false;
        }

        PersistQueueState();
    }

    private DownloadJobViewModel? RestoreJob(PersistedDownloadJob? persisted, bool isActive)
    {
        if (persisted?.Job is null) return null;

        var job = DownloadJobViewModel.Restore(persisted);
        if (job.IsFinished) return null;

        if (isActive)
        {
            if (job.Status != DownloadJobStatus.Paused)
            {
                var autoResumeOnStart = _settings?.AutoResumeOnStart ?? true;
                var initialStatus = job.GetInitialRunningStatus();
                job.ResumeStatus = initialStatus;
                job.Status = autoResumeOnStart ? initialStatus : DownloadJobStatus.Paused;
            }
        }
        else if (job.Status is DownloadJobStatus.Downloading or DownloadJobStatus.Allocating or DownloadJobStatus.Verifying)
        {
            job.Status = DownloadJobStatus.Queued;
        }

        return job;
    }

    private void PersistQueueState(bool force = false)
    {
        if (_isDisposed && !force) return;
        if (_isRestoringQueueState) return;
        if (_persistenceSuspendCount > 0)
        {
            return;
        }

        try
        {
            var state = new PersistedQueueState
            {
                ActiveJob = ActiveJob is { IsFinished: false } active ? active.ToPersisted() : null,
                QueuedJobs = QueuedJobs
                    .Where(job => !job.IsFinished)
                    .Select(job => job.ToPersisted())
                    .ToList()
            };

            QueueStateService.Save(state);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"[Feil] Failed to persist queue state: {ex}");
        }
    }



    private void MutateQueueState(Action mutation)
    {
        _persistenceSuspendCount++;
        try
        {
            mutation();
        }
        finally
        {
            _persistenceSuspendCount--;
            if (_persistenceSuspendCount == 0)
            {
                PersistQueueState();
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        _isDisposed = true;
        _speedTimer.Dispose();
        PersistQueueState(force: true);

        var cts = _activeJobCts;
        _activeJobCts = null;
        cts?.Cancel();
        cts?.Dispose();
    }

    private async Task<int> ExecuteJobAsync(DownloadJobViewModel job, CancellationToken cancellationToken)
    {
        var depotManifests = BuildDepotManifestList(job.Job);

        PreparePhaseProgress(job, resetProgress: job.Status == DownloadJobStatus.Verifying);
        int result = await ExecutePhaseAsync(job, depotManifests, verifyAll: job.Status == DownloadJobStatus.Verifying, cancellationToken);

        if (result != 0 || cancellationToken.IsCancellationRequested)
        {
            return result;
        }

        if (job.RunMode == DownloadJobRunMode.VerifyOnly || job.Status == DownloadJobStatus.Verifying)
        {
            return result;
        }

        var shouldRunVerifyPhase = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_isDisposed || ActiveJob != job || job.Status == DownloadJobStatus.Cancelled)
            {
                return false;
            }

            job.SetRunningStatus(DownloadJobStatus.Verifying);
            PersistQueueState();
            return true;
        });

        if (!shouldRunVerifyPhase || cancellationToken.IsCancellationRequested)
        {
            return cancellationToken.IsCancellationRequested ? 1 : 0;
        }

        PreparePhaseProgress(job, resetProgress: true);
        return await ExecutePhaseAsync(job, depotManifests, verifyAll: true, cancellationToken);
    }

    private async Task<int> ExecutePhaseAsync(
        DownloadJobViewModel job,
        List<(uint depotId, ulong manifestId)> depotManifests,
        bool verifyAll,
        CancellationToken cancellationToken)
    {
        var config = new Feil.Core.DownloadConfig
        {
            JobDirectory = job.JobDirectory,
            InstallDirectory = await EnsureInstallDirectoryAsync(job),
            VerifyAll = verifyAll,
        };

        return await Task.Run(async () => await _downloadService.ExecuteDownloadAsync(
            (uint)job.AppId,
            depotManifests,
            config,
            cancellationToken: cancellationToken
        ));
    }

    private static List<(uint depotId, ulong manifestId)> BuildDepotManifestList(DownloadJob job)
    {
        var depotManifests = new List<(uint depotId, ulong manifestId)>();

        foreach (var depot in job.Depots)
        {
            depotManifests.Add(ulong.TryParse(depot.ManifestId, out var manifestId)
                ? ((uint)depot.AppId, manifestId)
                : ((uint)depot.AppId, Feil.Core.ContentDownloader.INVALID_MANIFEST_ID));

            if (!string.IsNullOrWhiteSpace(depot.DecryptionKey))
            {
                var keyBytes = Convert.FromHexString(depot.DecryptionKey);
                Feil.Core.DepotKeyStore.Add((uint)depot.AppId, keyBytes);
            }
        }

        return depotManifests;
    }

    private void PreparePhaseProgress(DownloadJobViewModel job, bool resetProgress)
    {
        var downloadedBytes = resetProgress ? 0 : job.DownloadedBytes;
        var totalBytes = job.TotalBytes;

        Interlocked.Exchange(ref _currentDownloadedBytes, downloadedBytes);
        Interlocked.Exchange(ref _currentTotalBytes, totalBytes);
        Interlocked.Exchange(ref _lastDownloadedBytes, downloadedBytes);
        Interlocked.Exchange(ref _currentDiskWrittenBytes, 0);
        Interlocked.Exchange(ref _lastDiskWrittenBytes, 0);
        _smoothedNetworkBps = 0;
        _smoothedDiskBps = 0;
        _smoothedEtaBps = 0;

        if (!resetProgress)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_isDisposed || ActiveJob != job)
            {
                return;
            }

            job.DownloadedBytes = 0;
            job.ProgressPercent = 0;
            job.NetworkSpeedBps = 0;
            job.DiskSpeedBps = 0;
            job.EstimatedTimeRemaining = TimeSpan.Zero;
        });
    }

    private async Task<string> EnsureInstallDirectoryAsync(DownloadJobViewModel job)
    {
        if (string.IsNullOrWhiteSpace(job.InstallDirectory))
        {
            job.InstallDirectory = await ResolveInstallDirectoryAsync(job.GameName, job.AppId);
            PersistQueueState();
        }

        Directory.CreateDirectory(job.InstallDirectory);
        return job.InstallDirectory;
    }

    private void EnqueueJob(DownloadJobViewModel job)
    {
        MutateQueueState(() =>
        {
            if (ActiveJob is null)
            {
                job.SetRunningStatus(job.GetInitialRunningStatus());
                ActiveJob = job;
            }
            else
            {
                job.Status = DownloadJobStatus.Queued;
                QueuedJobs.Add(job);
            }
        });
    }

    private async Task<string> ResolveInstallDirectoryAsync(string gameName, int appId)
    {
        return await JobArchiveImportService.ResolveInstallDirectoryAsync(GetInstallBaseDirectory(), appId, gameName);
    }

    private string GetInstallBaseDirectory() =>
        string.IsNullOrWhiteSpace(_settings?.InstallPath)
            ? DefaultInstallPathService.GetDefaultInstallPath()
            : _settings.InstallPath;

    private static bool IsRunnableStatus(DownloadJobStatus status) =>
        status is DownloadJobStatus.Downloading or DownloadJobStatus.Verifying;

    private sealed record HistoryJobSource(DownloadJob Job, string? LuaFilePath);
}
