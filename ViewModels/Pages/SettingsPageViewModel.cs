using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Feil.Models;
using Feil.Services;
using Velopack;
using Serilog;

namespace Feil.ViewModels.Pages;

public partial class SettingsPageViewModel : ViewModelBase
{
    // ── Download ──
    private UpdateInfo? _pendingUpdate;
    [ObservableProperty]
    private string _installPath = DefaultInstallPathService.GetDefaultInstallPath();

    // ── Startup ──
    [ObservableProperty]
    private bool _launchOnStartup;

    [ObservableProperty]
    private bool _startMinimised;

    [ObservableProperty]
    private bool _autoResumeOnStart = true;

    [ObservableProperty]
    private bool _skipDepotSelection;

    // ── Steam ──
    [ObservableProperty]
    private string _steamAccountId = string.Empty;

    [RelayCommand]
    private void Save()
    {
        var result = SettingsService.Save(ToAppSettings());
        Log.Information("Settings saved with outcome {Outcome}", result.Outcome);
        LoadFrom(result.PersistedSettings);
        ApplySaveStatus(result);
    }

    [ObservableProperty]
    private string _saveStatusMessage = string.Empty;

    [ObservableProperty]
    private bool _showSaveSuccess;

    [ObservableProperty]
    private bool _showSaveWarning;

    [ObservableProperty]
    private bool _showSaveError;

    partial void OnInstallPathChanged(string value) => ClearSaveStatus();

    partial void OnLaunchOnStartupChanged(bool value) => ClearSaveStatus();

    partial void OnStartMinimisedChanged(bool value) => ClearSaveStatus();

    partial void OnAutoResumeOnStartChanged(bool value) => ClearSaveStatus();

    partial void OnSkipDepotSelectionChanged(bool value) => ClearSaveStatus();

    partial void OnSteamAccountIdChanged(string value) => ClearSaveStatus();

    public void LoadFrom(AppSettings s)
    {
        InstallPath         = string.IsNullOrWhiteSpace(s.InstallPath)
            ? DefaultInstallPathService.GetDefaultInstallPath()
            : s.InstallPath;
        LaunchOnStartup     = s.LaunchOnStartup;
        StartMinimised      = s.StartMinimised;
        AutoResumeOnStart   = s.AutoResumeOnStart;
        SkipDepotSelection  = s.SkipDepotSelection;
        SteamAccountId      = s.SteamAccountId == 0 ? string.Empty : s.SteamAccountId.ToString();
    }

    public AppSettings ToAppSettings() => new()
    {
        InstallPath         = InstallPath,
        LaunchOnStartup     = LaunchOnStartup,
        StartMinimised      = StartMinimised,
        AutoResumeOnStart   = AutoResumeOnStart,
        SkipDepotSelection  = SkipDepotSelection,
        SteamAccountId      = uint.TryParse(SteamAccountId, out var id) ? id : 0,
    };

    [RelayCommand]
    private void ResetDefaults()
    {
        Log.Information("User reset settings to defaults");
        InstallPath = DefaultInstallPathService.GetDefaultInstallPath();
        LaunchOnStartup = false;
        StartMinimised = false;
        AutoResumeOnStart = true;
        SkipDepotSelection = false;
        SteamAccountId = string.Empty;
        ClearSaveStatus();
    }

    private void ApplySaveStatus(SettingsSaveResult result)
    {
        SaveStatusMessage = result.Message;
        ShowSaveSuccess = result.Outcome == SettingsSaveOutcome.Success;
        ShowSaveWarning = result.Outcome == SettingsSaveOutcome.Warning;
        ShowSaveError = result.Outcome == SettingsSaveOutcome.Error;
    }

    private void ClearSaveStatus()
    {
        SaveStatusMessage = string.Empty;
        ShowSaveSuccess = false;
        ShowSaveWarning = false;
        ShowSaveError = false;
    }

    // ── Updates ──
    [ObservableProperty]
    private string _updateStatusMessage = UpdaterService.IsUpdateSupported ? "Ready to check for updates" : "Auto-updates are not supported in this build";

    [ObservableProperty]
    private bool _isCheckingForUpdates;

    [ObservableProperty]
    private bool _updateAvailable;

    [ObservableProperty]
    private int _updateProgress;

    [ObservableProperty]
    private bool _isDownloadingUpdate;

    public string UpdateButtonText => UpdateAvailable ? "Download & Install" : "Check for Updates";

    public bool ShowUpdateProgress => IsCheckingForUpdates || IsDownloadingUpdate;

    partial void OnIsCheckingForUpdatesChanged(bool value) => OnPropertyChanged(nameof(ShowUpdateProgress));

    partial void OnUpdateAvailableChanged(bool value) => OnPropertyChanged(nameof(UpdateButtonText));

    partial void OnIsDownloadingUpdateChanged(bool value) => OnPropertyChanged(nameof(ShowUpdateProgress));

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (!UpdaterService.IsUpdateSupported) return;

        if (UpdateAvailable && _pendingUpdate != null)
        {
            Log.Information("User requested to download and apply update {Version}", _pendingUpdate.TargetFullRelease.Version);
            // Already checked, now we download and apply
            IsDownloadingUpdate = true;
            UpdateProgress = 0;
            UpdateStatusMessage = "Downloading update...";
            try
            {
                await UpdaterService.DownloadAndApplyUpdateAsync(_pendingUpdate, progress =>
                {
                    UpdateProgress = progress;
                });
            }
            finally
            {
                IsDownloadingUpdate = false;
            }
            return;
        }

        Log.Information("User manually requested an update check");
        IsCheckingForUpdates = true;
        UpdateStatusMessage = "Checking for updates...";
        try
        {
            var updateCheck = await UpdaterService.CheckForUpdatesAsync();

            if (!updateCheck.IsSuccess)
            {
                _pendingUpdate = null;
                UpdateAvailable = false;
                UpdateStatusMessage = updateCheck.ErrorMessage ?? "Could not check for updates.";
                return;
            }

            _pendingUpdate = updateCheck.UpdateInfo;

            if (_pendingUpdate != null)
            {
                UpdateAvailable = true;
                UpdateStatusMessage = $"Version {_pendingUpdate.TargetFullRelease.Version} is available!";
            }
            else
            {
                UpdateAvailable = false;
                UpdateStatusMessage = "You are on the latest version.";
            }
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }
}
