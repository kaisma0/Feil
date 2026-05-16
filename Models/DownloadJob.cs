using System.Collections.Generic;

namespace Feil.Models;

public class DownloadJob
{
    public required int AppId { get; init; }
    public string? AppKey { get; set; }
    public required List<DepotInfo> Depots { get; init; }
    public required List<int> Entitlements { get; init; }
}

public class DepotInfo
{
    public required int AppId { get; init; }
    public required int Branch { get; init; }
    public string? DecryptionKey { get; set; }

    // Set via setManifestid
    public string? ManifestId { get; set; }
    public long? SizeBytes { get; set; }
}
