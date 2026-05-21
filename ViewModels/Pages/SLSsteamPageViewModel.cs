using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Feil.Services.SLSsteam;
using System;
using Serilog;

namespace Feil.ViewModels.Pages;

public partial class SLSsteamPageViewModel : ViewModelBase
{
    private readonly SLSsteamService _slsService;

    [ObservableProperty]
    private string _idleStatusAppId = "";

    [ObservableProperty]
    private string _idleStatusTitle = "";

    [ObservableProperty]
    private bool _notifications;

    [ObservableProperty]
    private string _fakeEmail = "";

    [ObservableProperty]
    private string _fakeWalletBalance = "";

    public bool IsLinux => OperatingSystem.IsLinux();
    public bool IsSlsInstalled => IsLinux && _slsService.IsInstalled();

    public SLSsteamPageViewModel()
    {
        _slsService = new SLSsteamService();
        if (IsSlsInstalled)
        {
            LoadValues();
        }
    }

    private void LoadValues()
    {
        var idleAppId = _slsService.GetConfigValue(new[] { "IdleStatus", "AppId" });
        IdleStatusAppId = (idleAppId == "0" || string.IsNullOrWhiteSpace(idleAppId)) ? "" : idleAppId;
        IdleStatusTitle = (_slsService.GetConfigValue(new[] { "IdleStatus", "Title" }) ?? "").Trim('"');

        var notifyVal = (_slsService.GetConfigValue(new[] { "Notifications" }) ?? "no").Trim().ToLower();
        Notifications = (notifyVal == "yes" || notifyVal == "true");

        FakeEmail = _slsService.GetConfigValue(new[] { "FakeEmail" }) ?? "";

        var wallet = _slsService.GetConfigValue(new[] { "FakeWalletBalance" });
        FakeWalletBalance = (wallet == "0" || string.IsNullOrWhiteSpace(wallet)) ? "" : wallet;
    }

    [RelayCommand]
    private void UpdateSls()
    {
        if (!IsSlsInstalled) return;
        Serilog.Log.Information("User requested to update SLSsteam via terminal script");

        try
        {
            string command = "curl -fsSL headcrab.pages.dev | bash; echo ''; read -p 'Press Enter to exit...'";
            string shellCmd = "";

            // Chain common terminal emulators, the first one that successfully runs will execute our command.
            if (OperatingSystem.IsLinux())
            {
                // We wrap it in a bash script to try available terminal emulators
                shellCmd = $@"
                    if command -v x-terminal-emulator > /dev/null; then x-terminal-emulator -e bash -c ""{command}""
                    elif command -v gnome-terminal > /dev/null; then gnome-terminal -- bash -c ""{command}""
                    elif command -v konsole > /dev/null; then konsole -e bash -c ""{command}""
                    elif command -v xfce4-terminal > /dev/null; then xfce4-terminal -x bash -c ""{command}""
                    elif command -v kitty > /dev/null; then kitty bash -c ""{command}""
                    elif command -v alacritty > /dev/null; then alacritty -e bash -c ""{command}""
                    else xterm -e bash -c ""{command}""
                    fi";

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "bash",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(shellCmd);
                System.Diagnostics.Process.Start(psi);
            }
        }
        catch (Exception ex)
        {
            // Log or handle error if needed
            Log.Error(ex, "Failed to launch terminal for SLS update");
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (!IsSlsInstalled) return;
        Serilog.Log.Information("User requested to save SLSsteam settings page");

        _slsService.ModifyConfig(new[] { "IdleStatus", "AppId" }, "set", string.IsNullOrWhiteSpace(IdleStatusAppId) ? "0" : IdleStatusAppId);
        _slsService.ModifyConfig(new[] { "IdleStatus", "Title" }, "set", string.IsNullOrWhiteSpace(IdleStatusTitle) ? "\"\"" : $"\"{IdleStatusTitle}\"");

        string notifyStr = Notifications ? "yes" : "no";
        _slsService.ModifyConfig(new[] { "Notifications" }, "set", notifyStr);
        _slsService.ModifyConfig(new[] { "NotifyInit" }, "set", notifyStr);

        _slsService.ModifyConfig(new[] { "FakeEmail" }, "set", string.IsNullOrWhiteSpace(FakeEmail) ? "" : FakeEmail);
        _slsService.ModifyConfig(new[] { "FakeWalletBalance" }, "set", string.IsNullOrWhiteSpace(FakeWalletBalance) ? "0" : FakeWalletBalance);
    }
}