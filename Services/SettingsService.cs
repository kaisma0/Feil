using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Feil.Models;
using Serilog;

namespace Feil.Services;

[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext { }

public static class SettingsService
{
    private static readonly string ConfigPath = Path.Combine(
        AppEnvironmentService.GetAppDataFolder(), "settings.json");

    public static AppSettings Load()
    {
        Log.Information("Loading AppSettings.");
        var settings = LoadFromDisk();
        settings.InstallPath = NormalizeInstallPath(settings.InstallPath);
        var startupState = StartupLaunchService.TryGetState();

        if (startupState.IsSuccess && settings.LaunchOnStartup != startupState.IsEnabled)
        {
            Log.Debug("Startup state mismatch detected. Syncing LaunchOnStartup to {IsEnabled}", startupState.IsEnabled);
            settings.LaunchOnStartup = startupState.IsEnabled;
            TryWrite(settings);
        }

        return settings;
    }

    public static SettingsSaveResult Save(AppSettings settings)
    {
        Log.Information("Saving AppSettings.");
        var existingSettings = LoadFromDisk();
        var persistedSettings = Clone(settings);
        var startupResult = StartupLaunchService.SetEnabled(persistedSettings.LaunchOnStartup);

        if (!startupResult.IsSuccess)
        {
            Log.Warning("Failed to update system startup registration: {ErrorMessage}", startupResult.ErrorMessage);
            var startupState = StartupLaunchService.TryGetState();
            persistedSettings.LaunchOnStartup = startupState.IsSuccess
                ? startupState.IsEnabled
                : existingSettings.LaunchOnStartup;
        }

        try
        {
            Write(persistedSettings);
            Log.Debug("AppSettings successfully written to disk.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to write AppSettings to disk.");
            var message = startupResult.IsSuccess
                ? $"Could not save settings: {ex.Message}"
                : $"Could not save settings, and launch on startup could not be updated: {startupResult.ErrorMessage}";

            return new SettingsSaveResult(persistedSettings, SettingsSaveOutcome.Error, message);
        }

        if (startupResult.IsSuccess)
        {
            return new SettingsSaveResult(persistedSettings, SettingsSaveOutcome.Success, "Settings saved.");
        }

        var action = settings.LaunchOnStartup ? "enabled" : "disabled";
        return new SettingsSaveResult(
            persistedSettings,
            SettingsSaveOutcome.Warning,
            $"Settings saved, but launch on startup could not be {action}: {startupResult.ErrorMessage}");
    }

    private static AppSettings LoadFromDisk()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                Log.Debug("Settings file not found at {ConfigPath}. Using defaults.", ConfigPath);
                return new AppSettings();
            }
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)
                   ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Corrupted or unreadable settings file at {ConfigPath}. Returning defaults.", ConfigPath);
            // Corrupted or unreadable file — return defaults silently.
            return new AppSettings();
        }
    }

    private static void TryWrite(AppSettings settings)
    {
        try
        {
            Write(settings);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Silently handled failure to save config updates.");
            try
            {
                Write(settings);
            }
            catch
            {
                // Keep runtime settings usable even if the config file cannot be updated.
            }
        }
    }

    private static void Write(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
        File.WriteAllText(ConfigPath, json);
    }

    private static AppSettings Clone(AppSettings settings) => new()
    {
        InstallPath = NormalizeInstallPath(settings.InstallPath),
        LaunchOnStartup = settings.LaunchOnStartup,
        StartMinimised = settings.StartMinimised,
        AutoResumeOnStart = settings.AutoResumeOnStart,
        SkipDepotSelection = settings.SkipDepotSelection,
        SteamAccountId = settings.SteamAccountId,
    };

    private static string NormalizeInstallPath(string? installPath) =>
        string.IsNullOrWhiteSpace(installPath)
            ? DefaultInstallPathService.GetDefaultInstallPath()
            : installPath;
}

public enum SettingsSaveOutcome
{
    Success,
    Warning,
    Error,
}

public sealed record SettingsSaveResult(AppSettings PersistedSettings, SettingsSaveOutcome Outcome, string Message);
