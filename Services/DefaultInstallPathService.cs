using System.IO;

namespace Feil.Services;

public static class DefaultInstallPathService
{
    // Default game install path: steam/steamapps/common.
    public static string GetDefaultInstallPath()
    {
        var steamRoot = GetSteamRootPath();
        if (steamRoot != null)
            return Path.Combine(steamRoot, "steamapps", "common");

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games", "Feil");
    }

    // Steam appcache/stats directory for achievement schemas.
    public static string? GetSteamStatsPath()
    {
        var steamRoot = GetSteamRootPath();
        return steamRoot != null ? Path.Combine(steamRoot, "appcache", "stats") : null;
    }

    // Resolves the Steam installation root directory.
    public static string? GetSteamRootPath()
    {
        foreach (var candidate in EnumerateSteamRootPaths())
        {
            if (Directory.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSteamRootPaths()
    {
        if (OperatingSystem.IsLinux())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

            if (!string.IsNullOrWhiteSpace(xdgDataHome))
            {
                yield return Path.Combine(xdgDataHome, "Steam");
            }
            else
            {
                yield return Path.Combine(home, ".local", "share", "Steam");
            }

            yield return Path.Combine(home, ".steam", "steam");
            yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
        }
        else if (OperatingSystem.IsWindows())
        {
            var programFiles86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrWhiteSpace(programFiles86))
                yield return Path.Combine(programFiles86, "Steam");

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
                yield return Path.Combine(programFiles, "Steam");
        }
    }
}
