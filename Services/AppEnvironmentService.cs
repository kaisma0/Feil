using System;
using System.IO;

namespace Feil.Services;

public static class AppEnvironmentService
{
    public const string AppFolderName = ".Feil";

    public static string GetAppDataFolder()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppFolderName);
    }
}
