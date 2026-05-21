using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Feil.ViewModels;
using Feil.Views;
using Serilog;

namespace Feil;

public partial class App : Application
{
    private static readonly Uri TrayIconResource = new("avares://Feil/Assets/icon.ico");

    private TrayIcon? _trayIcon;
    private MainWindowViewModel? _viewModel;

    public override void Initialize()
    {
        Log.Debug("Avalonia Application Initialization Started.");
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var settings = Services.SettingsService.Load();
            _viewModel = new MainWindowViewModel(settings);
            var mainWindow = new MainWindow
            {
                DataContext = _viewModel,
            };

            desktop.MainWindow = mainWindow;

            ConfigureTrayIcon(desktop, mainWindow);

            if (settings.StartMinimised)
            {
                mainWindow.WindowState = WindowState.Minimized;
                mainWindow.Opened += HandleStartMinimised;
            }

            desktop.ShutdownRequested += (_, _) =>
            {
                Log.Information("Desktop shutdown requested. Cleaning up...");
                mainWindow.IsExitRequested = true;
                _viewModel.Dispose();
            };

            desktop.Exit += (_, _) =>
            {
                Log.Information("Desktop exit triggered. Disposing tray icon...");
                DisposeTrayIcon();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ConfigureTrayIcon(IClassicDesktopStyleApplicationLifetime desktop, MainWindow mainWindow)
    {
        var showItem = new NativeMenuItem("Show Feil");
        showItem.Click += (_, _) => mainWindow.ShowFromTray();

        var hideItem = new NativeMenuItem("Minimize to Tray");
        hideItem.Click += (_, _) => mainWindow.HideToTray();

        var exitItem = new NativeMenuItem("Exit Feil");
        exitItem.Click += (_, _) => ExitFromTray(desktop, mainWindow);

        var menu = new NativeMenu();
        menu.Items.Add(showItem);
        menu.Items.Add(hideItem);
        menu.Items.Add(exitItem);
        menu.NeedsUpdate += (_, _) =>
        {
            var canShowWindow = !mainWindow.IsVisible || mainWindow.WindowState == WindowState.Minimized;
            showItem.IsVisible = canShowWindow;
            hideItem.IsVisible = !canShowWindow;
        };

        _trayIcon = new TrayIcon
        {
            Icon = CreateTrayIcon(),
            ToolTipText = "Feil",
            Menu = menu,
            IsVisible = true,
        };
        _trayIcon.Clicked += (_, _) => mainWindow.ShowFromTray();

        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
    }

    private void ExitFromTray(IClassicDesktopStyleApplicationLifetime desktop, MainWindow mainWindow)
    {
        mainWindow.IsExitRequested = true;
        desktop.Shutdown();
    }

    private void DisposeTrayIcon()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
        TrayIcon.SetIcons(this, new TrayIcons());
    }

    private static WindowIcon CreateTrayIcon()
    {
        using var stream = AssetLoader.Open(TrayIconResource);
        return new WindowIcon(stream);
    }

    private static void HandleStartMinimised(object? sender, EventArgs e)
    {
        if (sender is not MainWindow window)
        {
            return;
        }

        window.Opened -= HandleStartMinimised;
        window.HideToTray();
    }
}
