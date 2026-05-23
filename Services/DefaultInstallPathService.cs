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

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Games", "Feil");
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
        foreach (var candidate in EnumerateSteamRootCandidates())
        {
            if (Directory.Exists(candidate))
                return TryResolveLinkTarget(candidate) ?? candidate;
        }

        return null;
    }

    public static IEnumerable<string> EnumerateSteamRootCandidates()
    {
        if (OperatingSystem.IsLinux())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

            yield return !string.IsNullOrWhiteSpace(xdgDataHome)
                ? Path.Combine(xdgDataHome, "Steam")
                : Path.Combine(home, ".local", "share", "Steam");

            yield return Path.Combine(home, ".steam", "steam");
            yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
        }
        else if (OperatingSystem.IsWindows())
        {
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrWhiteSpace(programFilesX86))
                yield return Path.Combine(programFilesX86, "Steam");

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
                yield return Path.Combine(programFiles, "Steam");
        }
    }

    // Resolves paths to all localconfig.vdf files across all discovered Steam roots.
    public static IEnumerable<string> EnumerateLocalConfigPaths()
    {
        var seenRoots = new HashSet<string>(StringComparer.Ordinal);

        foreach (var steamRoot in EnumerateSteamRootCandidates())
        {
            if (!Directory.Exists(steamRoot))
                continue;

            // Resolve symlinks (e.g. ~/.steam/steam -> ~/.local/share/Steam) to avoid duplicates.
            var resolvedRoot = TryResolveLinkTarget(steamRoot) ?? steamRoot;
            if (!seenRoots.Add(resolvedRoot))
                continue;

            var userdataDir = Path.Combine(resolvedRoot, "userdata");
            if (!Directory.Exists(userdataDir))
                continue;

            foreach (var userDir in Directory.EnumerateDirectories(userdataDir))
            {
                var path = Path.Combine(userDir, "config", "localconfig.vdf");
                if (File.Exists(path))
                    yield return path;
            }
        }
    }

    private static string? TryResolveLinkTarget(string path)
    {
        try
        {
            var dir = new DirectoryInfo(path);
            if (dir.LinkTarget != null)
                return dir.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return null;
    }
}