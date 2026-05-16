#nullable disable
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Feil.Core;

public class DownloadConfig
{
    public bool UseAppToken { get; set; }
    public bool UsePackageToken { get; set; }
    public ulong AppToken { get; set; }
    public ulong PackageToken { get; set; }
    public int CellID { get; set; }
    public bool DownloadAllPlatforms { get; set; }
    public bool DownloadAllArchs { get; set; }
    public bool DownloadAllLanguages { get; set; }
    public bool DownloadManifestOnly { get; set; }
    public string InstallDirectory { get; set; }

    public bool UsingFileList { get; set; }
    public HashSet<string> FilesToDownload { get; set; }
    public List<Regex> FilesToDownloadRegex { get; set; }

    public string BetaPassword { get; set; }

    public bool VerifyAll { get; set; }

    public int MaxDownloads { get; set; } = 32;

    public bool UseManifestFile { get; set; }
    public string ManifestFile { get; set; }
    public string JobDirectory { get; set; }
}
