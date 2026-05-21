using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;
using Velopack.Locators;
using Serilog;

namespace Feil.Services;

public static class UpdaterService
{
    private static UpdateManager? _manager;

    public static UpdateManager Manager
    {
        get
        {
            if (_manager == null)
            {
                // Set up Velopack to check the GitHub repository releases
                _manager = new UpdateManager(new GithubSource("https://github.com/kaisma0/Feil", null, false));
            }
            return _manager;
        }
    }

    public static bool IsUpdateSupported => Manager.IsInstalled;

    public static async Task<UpdateCheckResult> CheckForUpdatesAsync()
    {
        Log.Information("Checking for updates...");
        if (!IsUpdateSupported)
        {
            Log.Warning("Auto-updates are not supported in this build.");
            return UpdateCheckResult.Failed("Auto-updates are not supported in this build.");
        }

        try
        {
            var updateInfo = await Manager.CheckForUpdatesAsync();
            Log.Information("Update check completed. Update found: {UpdateFound}", updateInfo != null);
            return UpdateCheckResult.Success(updateInfo);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error checking for updates.");
            return UpdateCheckResult.Failed($"Could not check for updates: {ex.Message}");
        }
    }

    public static async Task DownloadAndApplyUpdateAsync(UpdateInfo updateInfo, Action<int>? progressCallback = null)
    {
        Log.Information("Downloading and applying update process started.");
        if (!IsUpdateSupported)
        {
            Log.Warning("DownloadAndApplyUpdateAsync skipped - auto-updates are not supported.");
            return;
        }

        try
        {
            await Manager.DownloadUpdatesAsync(updateInfo, progressCallback);
            Log.Information("Update downloaded successfully, applying and restarting.");
            Manager.ApplyUpdatesAndRestart(updateInfo);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error applying update.");
        }
    }
}

public sealed record UpdateCheckResult(UpdateInfo? UpdateInfo, string? ErrorMessage)
{
    public bool IsSuccess => string.IsNullOrWhiteSpace(ErrorMessage);

    public static UpdateCheckResult Success(UpdateInfo? updateInfo) => new(updateInfo, null);

    public static UpdateCheckResult Failed(string message) => new(null, message);
}
