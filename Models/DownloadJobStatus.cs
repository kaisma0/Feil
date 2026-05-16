namespace Feil.Models;

public enum DownloadJobRunMode
{
    DownloadAndVerify,
    VerifyOnly,
}

public enum DownloadJobStatus
{
    Queued,
    Allocating,
    Downloading,
    Paused,
    Verifying,
    Completed,
    Failed,
    Cancelled,
}
