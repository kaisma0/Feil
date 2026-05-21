using System;
using System.IO;
using Serilog;

namespace Feil.Services;

public static class AppEnvironmentService
{
    public const string AppFolderName = ".Feil";

    public static string GetAppDataFolder()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppFolderName);
        Log.Debug("Resolved AppData folder to: {Path}", path);
        return path;
    }
}
