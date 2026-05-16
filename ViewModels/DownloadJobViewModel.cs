using System;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Feil.Helpers;
using Feil.Models;
using Feil.Services.Steam;

namespace Feil.ViewModels;

public partial class DownloadJobViewModel : ObservableObject
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public DownloadJob Job { get; private set; } = null!;
    public DownloadJobStatus ResumeStatus { get; set; } = DownloadJobStatus.Downloading;

    [ObservableProperty] private string             _gameName                = string.Empty;
    [ObservableProperty] private int                _appId;
    [ObservableProperty] private DownloadJobRunMode _runMode                 = DownloadJobRunMode.DownloadAndVerify;
    [ObservableProperty] private DownloadJobStatus  _status                  = DownloadJobStatus.Queued;
    [ObservableProperty] private double             _progressPercent;
    [ObservableProperty] private long               _downloadedBytes;
    [ObservableProperty] private long               _totalBytes;
    [ObservableProperty] private double             _networkSpeedBps;
    [ObservableProperty] private double             _diskSpeedBps;
    [ObservableProperty] private TimeSpan           _estimatedTimeRemaining;
    [ObservableProperty] private int                _depotCount;
    [ObservableProperty] private string?            _gameIconUrl;
    [ObservableProperty] private IImage?            _gameIcon;
    [ObservableProperty] private string?            _jobDirectory;
    [ObservableProperty] private string?            _installDirectory;

    public DateTimeOffset? StartedAt { get; set; }

    // ── Derived status flags ───────────────────────────────────────
    public bool   IsActive          => Status is DownloadJobStatus.Downloading or DownloadJobStatus.Allocating or DownloadJobStatus.Verifying;
    public bool   IsPaused          => Status == DownloadJobStatus.Paused;
    public bool   IsQueued          => Status == DownloadJobStatus.Queued;
    public bool   IsFinished        => Status is DownloadJobStatus.Completed or DownloadJobStatus.Failed or DownloadJobStatus.Cancelled;
    public bool   HasGameIcon       => GameIcon is not null;
    public string PauseResumeLabel  => IsPaused ? "Resume" : "Pause";
    public string ActivePhaseLabel  => Status == DownloadJobStatus.Verifying ? "NOW VERIFYING" : "NOW DOWNLOADING";

    partial void OnStatusChanged(DownloadJobStatus value)
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsQueued));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(PauseResumeLabel));
        OnPropertyChanged(nameof(ActivePhaseLabel));
    }

    partial void OnGameIconChanged(IImage? value) => OnPropertyChanged(nameof(HasGameIcon));

    // ── Formatted display strings ──────────────────────────────────
    public string FormattedDownloaded    => ByteFormatter.FormatBytes(DownloadedBytes);
    public string FormattedTotal         => ByteFormatter.FormatBytes(TotalBytes);
    public string FormattedNetworkSpeed  => ByteFormatter.FormatSpeed(NetworkSpeedBps);
    public string FormattedDiskSpeed     => ByteFormatter.FormatSpeed(DiskSpeedBps);
    public string FormattedEta           => ByteFormatter.FormatEta(EstimatedTimeRemaining);

    partial void OnDownloadedBytesChanged(long value)        => OnPropertyChanged(nameof(FormattedDownloaded));
    partial void OnTotalBytesChanged(long value)             => OnPropertyChanged(nameof(FormattedTotal));
    partial void OnNetworkSpeedBpsChanged(double value)      => OnPropertyChanged(nameof(FormattedNetworkSpeed));
    partial void OnDiskSpeedBpsChanged(double value)         => OnPropertyChanged(nameof(FormattedDiskSpeed));
    partial void OnEstimatedTimeRemainingChanged(TimeSpan value) => OnPropertyChanged(nameof(FormattedEta));

    public static async Task<DownloadJobViewModel> CreateAsync(
        DownloadJob job,
        string jobDirectory,
        string? installDirectory = null,
        string? gameName = null)
    {
        var vm = new DownloadJobViewModel
        {
            AppId = job.AppId,
            GameName = string.IsNullOrWhiteSpace(gameName) ? $"App {job.AppId}" : gameName,
            DepotCount = job.Depots.Count,
            TotalBytes = job.Depots.Sum(d => d.SizeBytes ?? 0),
            JobDirectory = jobDirectory,
            InstallDirectory = installDirectory,
            Status = DownloadJobStatus.Queued,
            Job = job
        };

        if (string.IsNullOrWhiteSpace(gameName))
        {
            await vm.LoadSteamMetadataAsync(overwriteFallbackName: true);
        }
        else
        {
            await vm.LoadSteamMetadataAsync(overwriteFallbackName: false);
        }

        return vm;
    }

    public static DownloadJobViewModel Restore(PersistedDownloadJob persisted)
    {
        var vm = new DownloadJobViewModel
        {
            Id = persisted.Id == Guid.Empty ? Guid.NewGuid() : persisted.Id,
            AppId = persisted.AppId == 0 ? persisted.Job.AppId : persisted.AppId,
            GameName = string.IsNullOrWhiteSpace(persisted.GameName)
                ? $"App {persisted.Job.AppId}"
                : persisted.GameName,
            GameIconUrl = persisted.GameIconUrl,
            RunMode = persisted.RunMode,
            DepotCount = persisted.DepotCount == 0 ? persisted.Job.Depots.Count : persisted.DepotCount,
            TotalBytes = persisted.TotalBytes > 0
                ? persisted.TotalBytes
                : persisted.Job.Depots.Sum(d => d.SizeBytes ?? 0),
            JobDirectory = persisted.JobDirectory,
            InstallDirectory = persisted.InstallDirectory,
            Status = persisted.Status,
            ResumeStatus = persisted.ResumeStatus,
            StartedAt = persisted.StartedAt,
            Job = persisted.Job
        };

        vm.BeginLoadingSteamMetadata(string.IsNullOrWhiteSpace(persisted.GameName));
        return vm;
    }

    public static DownloadJobViewModel CreateForVerification(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(entry.Job);

        var vm = new DownloadJobViewModel
        {
            AppId = entry.AppId,
            GameName = string.IsNullOrWhiteSpace(entry.GameName) ? $"App {entry.AppId}" : entry.GameName,
            GameIconUrl = entry.GameIconUrl,
            RunMode = DownloadJobRunMode.VerifyOnly,
            Status = DownloadJobStatus.Queued,
            ResumeStatus = DownloadJobStatus.Verifying,
            DepotCount = entry.DepotCount == 0 ? entry.Job.Depots.Count : entry.DepotCount,
            TotalBytes = entry.TotalBytes > 0 ? entry.TotalBytes : entry.Job.Depots.Sum(d => d.SizeBytes ?? 0),
            JobDirectory = entry.JobDirectory,
            InstallDirectory = entry.InstallDirectory,
            Job = entry.Job
        };

        vm.BeginLoadingSteamMetadata(string.IsNullOrWhiteSpace(entry.GameName));
        return vm;
    }

    public DownloadJobStatus GetInitialRunningStatus() =>
        RunMode == DownloadJobRunMode.VerifyOnly
            ? DownloadJobStatus.Verifying
            : DownloadJobStatus.Downloading;

    public void SetRunningStatus(DownloadJobStatus status)
    {
        ResumeStatus = status;
        Status = status;
    }

    public PersistedDownloadJob ToPersisted() => new()
    {
        Id = Id,
        Job = Job,
        GameName = GameName,
        GameIconUrl = GameIconUrl,
        AppId = AppId,
        RunMode = RunMode,
        Status = Status,
        ResumeStatus = ResumeStatus,
        TotalBytes = TotalBytes,
        DepotCount = DepotCount,
        JobDirectory = JobDirectory,
        InstallDirectory = InstallDirectory,
        StartedAt = StartedAt
    };

    private void BeginLoadingSteamMetadata(bool overwriteFallbackName)
    {
        _ = LoadSteamMetadataAsync(overwriteFallbackName);
    }

    private async Task LoadSteamMetadataAsync(bool overwriteFallbackName)
    {
        try
        {
            string? resolvedName = null;
            var resolvedIconUrl = GameIconUrl;

            if (overwriteFallbackName || string.IsNullOrWhiteSpace(resolvedIconUrl))
            {
                var metadata = await SteamAppInfoService.GetMetadataAsync(AppId);
                if (overwriteFallbackName)
                {
                    resolvedName = metadata?.Name;
                }

                resolvedIconUrl ??= metadata?.HeaderImageUrl;
            }

            var icon = await SteamAppImageService.GetGameImageAsync(AppId, resolvedIconUrl);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (overwriteFallbackName && !string.IsNullOrWhiteSpace(resolvedName))
                {
                    GameName = resolvedName;
                }

                if (string.IsNullOrWhiteSpace(GameIconUrl) && !string.IsNullOrWhiteSpace(resolvedIconUrl))
                {
                    GameIconUrl = resolvedIconUrl;
                }

                if (icon is not null)
                {
                    GameIcon = icon;
                }
            });
        }
        catch
        {
            // Best-effort UI metadata only.
        }
    }
}
