#nullable enable
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;

namespace Feil.Core;

public class DownloadService
{
    private volatile bool _isRunning;

    public bool IsRunning => _isRunning;

    public void Pause()
    {
        if (_isRunning)
            ContentDownloader.Pause();
    }

    public void Resume()
    {
        if (_isRunning)
            ContentDownloader.Resume();
    }

    public Action<ulong, ulong>? ProgressChanged;

    public Action<ulong>? TotalSizeChanged;

    public Action<ulong>? DiskProgressChanged;

    public async Task<int> ExecuteDownloadAsync(
        uint appId,
        List<(uint, ulong)> depotManifestIds,
        DownloadConfig config,
        string branch = "public",
        string? os = null,
        string? arch = null,
        string? language = null,
        bool lowViolence = false,
        Action<string>? logMessage = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(depotManifestIds);

        try
        {
            DebugLog.Enabled = false;

            ContentDownloader.ProgressCallback = ProgressChanged;
            ContentDownloader.TotalSizeCallback = TotalSizeChanged;
            ContentDownloader.DiskProgressCallback = DiskProgressChanged;
            ContentDownloader.ResetPauseGate();
            _isRunning = true;

            // Configure backend state natively
            DownloadConfig effectiveConfig = config ?? new DownloadConfig();
            ContentDownloader.Config = effectiveConfig;


            DepotConfigStore.Reset();

            // (Optional) load user account cache
            if (!AccountSettingsStore.IsLoaded)
            {
                AccountSettingsStore.LoadFromFile("account.config");
            }

            // Process custom file filters natively (which were lost in CLI refactor)
            if (!string.IsNullOrWhiteSpace(effectiveConfig.ManifestFile))
            {
                ContentDownloader.Config.UseManifestFile = true;
            }

            if (ContentDownloader.Config.UsingFileList && ContentDownloader.Config.FilesToDownload == null)
            {
                ContentDownloader.Config.FilesToDownload = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ContentDownloader.Config.FilesToDownloadRegex = new List<Regex>();
                // A proper GUI implementation would loop through the strings supplied in the `FilesToDownload` list
                // from the UI's View, rather than loading it from a "file path". We assume the UI already hydrated it.
            }

            if (ContentDownloader.InitializeSteam3())
            {
                try
                {
                    logMessage?.Invoke("Connecting to Steam...");

                    // Prevent side effects on caller-owned input when the downloader expands depot list.
                    List<(uint depotId, ulong manifestId)> requestedDepots = [.. depotManifestIds];

                    await ContentDownloader
                        .DownloadAppAsync(appId, requestedDepots, branch, os, arch, language, lowViolence, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    logMessage?.Invoke($"Download canceled: {ex.Message}");
                    return 1;
                }
                catch (Exception e)
                {
                    logMessage?.Invoke($"Download failed due to an unhandled exception: {e.Message}");
                    throw;
                }
                finally
                {
                    ContentDownloader.ShutdownSteam3();
                }
            }
            else
            {
                logMessage?.Invoke("Error: InitializeSteam failed");
                return 1;
            }

            return 0;
        }
        catch (Exception ex)
        {
            logMessage?.Invoke($"Fatal Error: {ex}");
            return 1;
        }
        finally
        {
            ContentDownloader.ProgressCallback = null;
            ContentDownloader.TotalSizeCallback = null;
            ContentDownloader.DiskProgressCallback = null;
            ContentDownloader.Resume();
            _isRunning = false;
        }
    }
}