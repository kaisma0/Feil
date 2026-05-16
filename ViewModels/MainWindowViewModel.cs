using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Feil.Models;
using Feil.Services;
using Feil.ViewModels.Pages;

namespace Feil.ViewModels;

public enum PageType { Queue, History, Settings, SLSsteam }

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    public string AppVersionLabel { get; }

    // ── Page ViewModels ──
    public HistoryPageViewModel HistoryPage { get; }
    public SettingsPageViewModel SettingsPage { get; }
    public QueuePageViewModel QueuePage { get; }
    public SLSsteamPageViewModel SLSsteamPage { get; }

    // ── Navigation ──
    [ObservableProperty]
    private ViewModelBase _currentPage;

    [ObservableProperty]
    private PageType _currentPageKey = PageType.Queue;

    public MainWindowViewModel()
        : this(SettingsService.Load())
    {
    }

    public MainWindowViewModel(AppSettings settings)
    {
        AppVersionLabel = $"v{GetAppVersion()}";
        HistoryPage = new HistoryPageViewModel();
        SettingsPage = new SettingsPageViewModel();
        SLSsteamPage = new SLSsteamPageViewModel();
        SettingsPage.LoadFrom(settings);
        QueuePage = new QueuePageViewModel(onJobFinished: HistoryPage.AddEntry, settings: SettingsPage);
        HistoryPage.VerifyRequested = QueuePage.EnqueueVerification;
        HistoryPage.RetryRequested = QueuePage.EnqueueRetry;
        _currentPage = QueuePage;
    }

    [RelayCommand]
    private void NavigateTo(PageType page)
    {
        CurrentPageKey = page;
        CurrentPage = page switch
        {
            PageType.Queue => QueuePage,
            PageType.History => HistoryPage,
            PageType.SLSsteam => SLSsteamPage,
            PageType.Settings => SettingsPage,
            _ => QueuePage,
        };
    }

    private static string GetAppVersion()
    {
        var informational = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+')[0];

        return Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";
    }

    public void Dispose()
    {
        QueuePage.Dispose();
    }
}
