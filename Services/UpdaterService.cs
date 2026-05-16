using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;
using Velopack.Locators;

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
        if (!IsUpdateSupported)
        {
            return UpdateCheckResult.Failed("Auto-updates are not supported in this build.");
        }

        try
        {
            return UpdateCheckResult.Success(await Manager.CheckForUpdatesAsync());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error checking for updates: {ex.Message}");
            return UpdateCheckResult.Failed($"Could not check for updates: {ex.Message}");
        }
    }

    public static async Task DownloadAndApplyUpdateAsync(UpdateInfo updateInfo, Action<int>? progressCallback = null)
    {
        if (!IsUpdateSupported) return;

        try
        {
            await Manager.DownloadUpdatesAsync(updateInfo, progressCallback);
            Manager.ApplyUpdatesAndRestart(updateInfo);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error applying update: {ex.Message}");
        }
    }
}

public sealed record UpdateCheckResult(UpdateInfo? UpdateInfo, string? ErrorMessage)
{
    public bool IsSuccess => string.IsNullOrWhiteSpace(ErrorMessage);

    public static UpdateCheckResult Success(UpdateInfo? updateInfo) => new(updateInfo, null);

    public static UpdateCheckResult Failed(string message) => new(null, message);
}
