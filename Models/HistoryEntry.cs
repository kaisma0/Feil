using System;
using System.Text.Json.Serialization;

namespace Feil.Models;

public record HistoryEntry(
    Guid Id,
    string GameName,
    string? GameIconUrl,
    int AppId,
    DownloadJobStatus FinalStatus,
    long TotalBytes,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int DepotCount,
    string? FailureReason = null,
    [property: JsonIgnore] DownloadJob? Job = null,
    string? JobDirectory = null,
    string? InstallDirectory = null
)
{
    public TimeSpan Duration => FinishedAt - StartedAt;
}
