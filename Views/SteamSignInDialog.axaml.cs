using Avalonia.Controls;
using Feil.ViewModels;
using Ursa.Controls;

namespace Feil.Views;

public partial class SteamSignInDialog : UserControl
{
    public SteamSignInDialog()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);

        var pinCode = this.FindControl<PinCode>("GuardPinCode");
        if (pinCode != null)
        {
            pinCode.Complete += OnGuardPinCodeComplete;
        }
    }

    protected override void OnUnloaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        var pinCode = this.FindControl<PinCode>("GuardPinCode");
        if (pinCode != null)
        {
            pinCode.Complete -= OnGuardPinCodeComplete;
        }

        base.OnUnloaded(e);
    }

    private void OnGuardPinCodeComplete(object? sender, PinCodeCompleteEventArgs e)
    {
        if (DataContext is SteamSignInDialogViewModel vm)
        {
            var code = string.Join("", e.Code);
            vm.OnGuardCodeCompleted(code);
        }
    }
}
