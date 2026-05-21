using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Feil.ViewModels;
using Ursa.Controls;

namespace Feil.Views;

public partial class MainWindow : UrsaWindow
{
    public bool IsExitRequested { get; set; }

    // ── Sidebar collapse state ──────────────────────────────────────────────
    // Tracks whether the sidebar is in icon-only (collapsed) mode.
    private bool _sidebarCollapsed;
    private bool _sidebarAnimating;

    private const double SidebarExpandedWidth  = 220;
    private const double SidebarCollapsedWidth = 56;
    private const int    AnimationMs           = 280; // matches DoubleTransition duration

    public MainWindow()
    {
        InitializeComponent();
    }

    // ── Tray helpers ────────────────────────────────────────────────────────

    public void HideToTray()
    {
        Serilog.Log.Information("Application window hidden to tray");
        Hide();
    }

    public void ShowFromTray()
    {
        Serilog.Log.Information("Application window restored from tray");
        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    // ── Window lifecycle ────────────────────────────────────────────────────

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (e.Cancel
            || IsExitRequested
            || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
        {
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    // ── Title-bar drag / controls ───────────────────────────────────────────

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            Serilog.Log.Information("User double clicked title bar");
            ToggleMaximized();
            return;
        }

        BeginMoveDrag(e);
    }

    private void OnMinimizeClicked(object? sender, RoutedEventArgs e)
    {
        Serilog.Log.Information("User requested to minimize window");
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeClicked(object? sender, RoutedEventArgs e)
    {
        Serilog.Log.Information("User requested to toggle window maximize");
        ToggleMaximized();
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        Serilog.Log.Information("User requested to close window");
        HideToTray();
    }

    private void ToggleMaximized()
    {
        if (!CanResize || !CanMaximize)
        {
            return;
        }

        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    // ── Sidebar toggle ──────────────────────────────────────────────────────

    private async void OnSidebarToggleClicked(object? sender, RoutedEventArgs e)
    {
        if (_sidebarAnimating)
        {
            return;
        }

        _sidebarAnimating = true;
        _sidebarCollapsed = !_sidebarCollapsed;

        SidebarToggleBtn.Classes.Remove(_sidebarCollapsed ? "sidebar-open" : "sidebar-closed");
        SidebarToggleBtn.Classes.Add(_sidebarCollapsed ? "sidebar-closed" : "sidebar-open");

        SidebarBorder.Width = _sidebarCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth;

        await Task.Delay(AnimationMs);

        _sidebarAnimating = false;
    }

    // ── Navigation ──────────────────────────────────────────────────────────

    private Button[] NavItems => [NavDownloads, NavHistory, NavSLSsteam];

    // Marks <paramref name="active"/> as selected and clears every other nav button
    private void SetActiveNavItem(Button active)
    {
        foreach (var btn in NavItems)
        {
            btn.Classes.Remove("sidebar-nav-selected");
        }

        SettingsPinBtn.Classes.Remove("sidebar-nav-selected");
        active.Classes.Add("sidebar-nav-selected");
    }

    private void OnNavItemClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag)
        {
            return;
        }

        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        SetActiveNavItem(btn);

        if (Enum.TryParse<PageType>(tag, true, out var pageType))
        {
            vm.NavigateToCommand.Execute(pageType);
        }
    }

    private void OnSettingsPinClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        foreach (var btn in NavItems)
        {
            btn.Classes.Remove("sidebar-nav-selected");
        }

        SettingsPinBtn.Classes.Add("sidebar-nav-selected");
        vm.NavigateToCommand.Execute(PageType.Settings);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // Set Downloads as the default selected nav item on startup.
        NavDownloads.Classes.Add("sidebar-nav-selected");
    }
}