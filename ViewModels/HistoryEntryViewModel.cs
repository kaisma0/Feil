using System;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Feil.Helpers;
using Feil.Models;
using Feil.Services.Steam;

namespace Feil.ViewModels;

public partial class HistoryEntryViewModel : ObservableObject
{
    public HistoryEntry        Entry          { get; }
    public Guid                Id           { get; }
    public string              GameName     { get; }
    public string?             GameIconUrl  { get; }
    public int                 AppId        { get; }
    public DownloadJobStatus   FinalStatus  { get; }
    public long                TotalBytes   { get; }
    public DateTimeOffset      StartedAt    { get; }
    public DateTimeOffset      FinishedAt   { get; }
    public TimeSpan            Duration     { get; }
    public int                 DepotCount   { get; }
    public string?             FailureReason { get; }
    [ObservableProperty] private IImage?    _gameIcon;

    // ── Formatted display strings ──────────────────────────────────
    public string FormattedSize     => ByteFormatter.FormatBytes(TotalBytes);
    public string FormattedDuration => ByteFormatter.FormatDuration(Duration);
    public string FormattedDate     => FinishedAt.LocalDateTime.ToString("d MMM yyyy HH:mm");

    // ── Status helpers (used by AXAML IsVisible bindings) ──────────
    public bool IsSuccess   => FinalStatus == DownloadJobStatus.Completed;
    public bool IsFailed    => FinalStatus == DownloadJobStatus.Failed;
    public bool IsCancelled => FinalStatus == DownloadJobStatus.Cancelled;
    public bool CanRetry    => (IsFailed || IsCancelled) && !string.IsNullOrWhiteSpace(Entry.JobDirectory);
    public bool CanVerify   => IsSuccess && (!string.IsNullOrWhiteSpace(Entry.JobDirectory) || Entry.Job is not null) && !string.IsNullOrWhiteSpace(Entry.InstallDirectory);
    public bool HasGameIcon => GameIcon is not null;

    partial void OnGameIconChanged(IImage? value) => OnPropertyChanged(nameof(HasGameIcon));

    public HistoryEntryViewModel(HistoryEntry entry)
    {
        Entry          = entry;
        Id            = entry.Id;
        GameName      = entry.GameName;
        GameIconUrl   = entry.GameIconUrl;
        AppId         = entry.AppId;
        FinalStatus   = entry.FinalStatus;
        TotalBytes    = entry.TotalBytes;
        StartedAt     = entry.StartedAt;
        FinishedAt    = entry.FinishedAt;
        Duration      = entry.Duration;
        DepotCount    = entry.DepotCount;
        FailureReason = entry.FailureReason;

        _ = LoadGameIconAsync();
    }

    private async Task LoadGameIconAsync()
    {
        try
        {
            var icon = await SteamAppImageService.GetGameImageAsync(AppId, GameIconUrl);
            if (icon is null)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() => GameIcon = icon);
        }
        catch
        {
            // Best-effort UI metadata only.
        }
    }
}
