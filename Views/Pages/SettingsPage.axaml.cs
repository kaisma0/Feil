using Serilog;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Feil.ViewModels.Pages;

namespace Feil.Views.Pages;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    private async void OnBrowseInstallPathClick(object? sender, RoutedEventArgs e)
    {
        Log.Information("User clicked to browse for install path");
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Install Location",
            AllowMultiple = false
        });

        if (folders.Count > 0 && DataContext is SettingsPageViewModel vm)
        {
            vm.InstallPath = folders[0].Path.LocalPath;
        }
    }
}
