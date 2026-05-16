using System;
using System.Collections.Generic;

namespace Feil.Models;

public sealed class PersistedQueueState
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;
    public PersistedDownloadJob? ActiveJob { get; set; }
    public List<PersistedDownloadJob> QueuedJobs { get; set; } = [];
}

public sealed class PersistedDownloadJob
{
    public Guid Id { get; set; }
    public required DownloadJob Job { get; set; }
    public required string GameName { get; set; }
    public string? GameIconUrl { get; set; }
    public int AppId { get; set; }
    public DownloadJobRunMode RunMode { get; set; } = DownloadJobRunMode.DownloadAndVerify;
    public DownloadJobStatus Status { get; set; }
    public DownloadJobStatus ResumeStatus { get; set; } = DownloadJobStatus.Downloading;
    public long TotalBytes { get; set; }
    public int DepotCount { get; set; }
    public string? JobDirectory { get; set; }
    public string? InstallDirectory { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
}
