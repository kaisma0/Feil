#nullable enable
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using Serilog;

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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(depotManifestIds);
        Log.Debug("Executing DownloadAsync for App {AppId}. Branch: {Branch}, OS: {OS}, Arch: {Arch}, Language: {Language}", 
            appId, branch, os ?? "default", arch ?? "default", language ?? "default");

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
                Log.Debug("Loaded AccountSettingsStore from account.config");
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
                    Log.Information("Connecting to Steam for App {AppId}", appId);

                    // Prevent side effects on caller-owned input when the downloader expands depot list.
                    List<(uint depotId, ulong manifestId)> requestedDepots = [.. depotManifestIds];

                    await ContentDownloader
                        .DownloadAppAsync(appId, requestedDepots, branch, os, arch, language, lowViolence, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    Log.Information(ex, "Download canceled for App {AppId}", appId);
                    return 1;
                }
                catch (Exception e)
                {
                    Log.Error(e, "Download failed due to an unhandled exception for App {AppId}", appId);
                    throw;
                }
                finally
                {
                    ContentDownloader.ShutdownSteam3();
                }
            }
            else
            {
                Log.Error("InitializeSteam failed for App {AppId}", appId);
                return 1;
            }

            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fatal Error during download of App {AppId}", appId);
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