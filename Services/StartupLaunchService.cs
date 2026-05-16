using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace Feil.Services;

public static class StartupLaunchService
{
    private const string ApplicationName = "Feil";
    private const string LinuxDesktopFileName = "feil.desktop";
    private const string WindowsRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        var result = TryGetState();
        return result.IsSuccess && result.IsEnabled;
    }

    public static StartupStateResult TryGetState()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!TryGetLaunchExecutablePath(out var executablePath, out _))
                {
                    return StartupStateResult.Failure("Feil could not determine its startup executable.");
                }

                return StartupStateResult.Success(IsWindowsStartupEnabled(executablePath));
            }

            if (OperatingSystem.IsLinux())
            {
                return StartupStateResult.Success(IsLinuxStartupEnabled());
            }
        }
        catch (Exception ex)
        {
            return StartupStateResult.Failure(ex.Message);
        }

        return StartupStateResult.Failure("Launch on startup is only supported on Windows and Linux.");
    }

    public static StartupRegistrationResult SetEnabled(bool enabled)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!enabled)
                {
                    SetWindowsStartupDisabled();
                    return StartupRegistrationResult.Success();
                }

                if (!TryGetLaunchExecutablePath(out var executablePath, out var errorMessage))
                {
                    return StartupRegistrationResult.Failure(errorMessage ?? "Feil could not determine its startup executable.");
                }

                SetWindowsStartup(executablePath);
                return StartupRegistrationResult.Success();
            }

            if (OperatingSystem.IsLinux())
            {
                if (!enabled)
                {
                    SetLinuxStartupDisabled();
                    return StartupRegistrationResult.Success();
                }

                if (!TryGetLaunchExecutablePath(out var executablePath, out var errorMessage))
                {
                    return StartupRegistrationResult.Failure(errorMessage ?? "Feil could not determine its startup executable.");
                }

                SetLinuxStartup(executablePath);
                return StartupRegistrationResult.Success();
            }

            return StartupRegistrationResult.Failure("Launch on startup is only supported on Windows and Linux.");
        }
        catch (Exception ex)
        {
            return StartupRegistrationResult.Failure(ex.Message);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsStartupEnabled(string executablePath)
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(WindowsRunKeyPath);
        var value = runKey?.GetValue(ApplicationName) as string;
        return string.Equals(value, BuildWindowsCommand(executablePath), StringComparison.Ordinal);
    }

    [SupportedOSPlatform("windows")]
    private static void SetWindowsStartup(string executablePath)
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(WindowsRunKeyPath);
        runKey.SetValue(ApplicationName, BuildWindowsCommand(executablePath), RegistryValueKind.String);
    }

    [SupportedOSPlatform("windows")]
    private static void SetWindowsStartupDisabled()
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(WindowsRunKeyPath);
        runKey.DeleteValue(ApplicationName, throwOnMissingValue: false);
    }

    [SupportedOSPlatform("linux")]
    private static bool IsLinuxStartupEnabled()
    {
        var effectiveDesktopFilePath = GetEffectiveLinuxDesktopFilePath();
        if (effectiveDesktopFilePath is null)
        {
            return false;
        }

        return !IsHiddenDesktopEntry(effectiveDesktopFilePath);
    }

    [SupportedOSPlatform("linux")]
    private static void SetLinuxStartup(string executablePath)
    {
        var desktopFilePath = GetLinuxUserDesktopFilePath();
        WriteTextFile(desktopFilePath, BuildLinuxDesktopEntry(executablePath));
    }

    [SupportedOSPlatform("linux")]
    private static void SetLinuxStartupDisabled()
    {
        WriteTextFile(GetLinuxUserDesktopFilePath(), BuildLinuxHiddenDesktopEntry());
    }

    [SupportedOSPlatform("linux")]
    private static string? GetEffectiveLinuxDesktopFilePath()
    {
        foreach (var directory in EnumerateLinuxAutostartDirectories())
        {
            var candidate = Path.Combine(directory, LinuxDesktopFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    [SupportedOSPlatform("linux")]
    private static IEnumerable<string> EnumerateLinuxAutostartDirectories()
    {
        yield return GetLinuxUserAutostartDirectory();

        var configDirs = Environment.GetEnvironmentVariable("XDG_CONFIG_DIRS");
        if (string.IsNullOrWhiteSpace(configDirs))
        {
            configDirs = "/etc/xdg";
        }

        foreach (var configDir in configDirs.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(configDir, "autostart");
        }
    }

    [SupportedOSPlatform("linux")]
    private static string GetLinuxUserDesktopFilePath() => Path.Combine(
        GetLinuxUserAutostartDirectory(),
        LinuxDesktopFileName);

    [SupportedOSPlatform("linux")]
    private static string GetLinuxUserAutostartDirectory()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(configHome))
        {
            return Path.Combine(configHome, "autostart");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config",
            "autostart");
    }

    [SupportedOSPlatform("linux")]
    private static bool IsHiddenDesktopEntry(string desktopFilePath)
    {
        var isInDesktopEntryGroup = false;

        foreach (var rawLine in File.ReadLines(desktopFilePath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                isInDesktopEntryGroup = line.Equals("[Desktop Entry]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!isInDesktopEntryGroup)
            {
                continue;
            }

            if (!line.StartsWith("Hidden=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line["Hidden=".Length..].Trim();
            return value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string BuildWindowsCommand(string executablePath) => $"\"{executablePath}\"";

    private static string BuildLinuxDesktopEntry(string executablePath) => $$"""
[Desktop Entry]
Type=Application
Version=1.0
Name=Feil
Comment=Launch Feil when you sign in
Exec={{EscapeDesktopExecArgument(executablePath)}}
Terminal=false
StartupNotify=false
X-GNOME-Autostart-enabled=true

""";

    private static string BuildLinuxHiddenDesktopEntry() => """
[Desktop Entry]
Type=Application
Name=Feil
Hidden=true

""";

    private static string EscapeDesktopExecArgument(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');

        foreach (var character in value)
        {
            if (character is '"' or '\\' or '$' or '`')
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static bool TryGetLaunchExecutablePath(out string executablePath, out string? errorMessage)
    {
        var appImagePath = Environment.GetEnvironmentVariable("APPIMAGE");
        if (!string.IsNullOrWhiteSpace(appImagePath) && File.Exists(appImagePath))
        {
            executablePath = appImagePath;
            errorMessage = null;
            return true;
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && !IsDotNetHost(processPath))
        {
            executablePath = processPath;
            errorMessage = null;
            return true;
        }

        var appHostPath = GetAppHostPathFromBaseDirectory();
        if (appHostPath is not null)
        {
            executablePath = appHostPath;
            errorMessage = null;
            return true;
        }

        executablePath = string.Empty;
        errorMessage = "Feil could not determine a launchable app executable for startup registration.";
        return false;
    }

    private static bool IsDotNetHost(string path)
    {
        var fileName = Path.GetFileName(path);
        return string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase)
               || string.Equals(fileName, "dotnet.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetAppHostPathFromBaseDirectory()
    {
        var baseDirectory = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(baseDirectory))
            return null;

        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        var candidate = Path.Combine(baseDirectory, ApplicationName + extension);
        return File.Exists(candidate) ? candidate : null;
    }

    private static void WriteTextFile(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}

public readonly record struct StartupRegistrationResult(bool IsSuccess, string? ErrorMessage)
{
    public static StartupRegistrationResult Success() => new(true, null);

    public static StartupRegistrationResult Failure(string message) => new(false, message);
}

public readonly record struct StartupStateResult(bool IsSuccess, bool IsEnabled, string? ErrorMessage)
{
    public static StartupStateResult Success(bool isEnabled) => new(true, isEnabled, null);

    public static StartupStateResult Failure(string message) => new(false, false, message);
}
