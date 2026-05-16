using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Feil.Models;
using Feil.Services.Achievements;

namespace Feil.ViewModels.Pages;

public partial class HistoryPageViewModel : ViewModelBase
{
    private readonly SemaphoreSlim _historySaveSemaphore = new(1, 1);
    private long _historySaveVersion;

    public Action<HistoryEntry>? VerifyRequested { get; set; }
    public Action<HistoryEntry>? RetryRequested { get; set; }

    public ObservableCollection<HistoryEntryViewModel> Entries { get; } = [];

    [ObservableProperty]
    private bool _isEmpty;

    public HistoryPageViewModel()
    {
        var state = Feil.Services.HistoryStateService.Load();
        if (state != null)
        {
            foreach (var entry in state.Entries)
            {
                Entries.Add(new HistoryEntryViewModel(entry));
            }
        }

        IsEmpty = Entries.Count == 0;
        Entries.CollectionChanged += OnEntriesChanged;
    }

    public void AddEntry(HistoryEntry entry)
    {
        var existing = Entries.FirstOrDefault(e => e.AppId == entry.AppId);
        if (existing != null)
        {
            Entries.Remove(existing);
        }
        Entries.Insert(0, new HistoryEntryViewModel(entry));
    }

    private void OnEntriesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        IsEmpty = Entries.Count == 0;
        SaveHistory();
    }

    private void SaveHistory()
    {
        var state = new PersistedHistoryState
        {
            Entries = Entries.Select(e => e.Entry).ToList()
        };
        var version = Interlocked.Increment(ref _historySaveVersion);
        _ = Task.Run(() => SaveHistoryAsync(state, version));
    }

    private async Task SaveHistoryAsync(PersistedHistoryState state, long version)
    {
        await _historySaveSemaphore.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _historySaveVersion))
            {
                return;
            }

            Feil.Services.HistoryStateService.Save(state);
        }
        finally
        {
            _historySaveSemaphore.Release();
        }
    }

    [RelayCommand]
    private void ClearHistory()
    {
        Entries.Clear();
    }

    [RelayCommand]
    private void Verify(HistoryEntryViewModel? entry)
    {
        if (entry is null || !entry.CanVerify)
        {
            return;
        }

        VerifyRequested?.Invoke(entry.Entry);
    }

    [RelayCommand]
    private void RetryFailed(HistoryEntryViewModel? entry)
    {
        if (entry is null || !entry.CanRetry)
        {
            return;
        }

        RetryRequested?.Invoke(entry.Entry);
    }

    [RelayCommand]
    private void RemoveEntry(HistoryEntryViewModel? entry)
    {
        if (entry is null) return;

        if (OperatingSystem.IsLinux() && entry.IsSuccess)
        {
            var sls = new Feil.Services.SLSsteam.SLSsteamService();
            if (sls.IsInstalled())
            {
                sls.ModifyConfig(new[] { "AdditionalApps" }, "remove", entry.AppId);
            }
        }

        Entries.Remove(entry);
    }

    [RelayCommand]
    private void AddOnline(HistoryEntryViewModel? entry)
    {
        if (entry is null || !entry.IsSuccess) return;

        if (OperatingSystem.IsLinux())
        {
            var sls = new Feil.Services.SLSsteam.SLSsteamService();
            if (sls.IsInstalled())
            {
                sls.ModifyConfig(new[] { "FakeAppIds" }, "add", new System.Collections.Generic.KeyValuePair<string, string>(entry.AppId.ToString(), "480"), "dictionary");
            }
        }
    }

    [RelayCommand]
    private void GenerateAchievements(HistoryEntryViewModel? entry)
    {
        if (entry is null) return;
        _ = StatsSchemaService.TriggerAsync((uint)entry.AppId);
    }
}
