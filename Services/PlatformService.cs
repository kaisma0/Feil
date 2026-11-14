using System;

namespace Feil.Services;

public static class PlatformService
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    public static bool IsLinux => OperatingSystem.IsLinux();

    public static bool SupportsSLSsteam => IsLinux;

    public static bool UsesWineLaunchOptions => IsLinux;

    public static string AppDataFolderName => "Feil";
}