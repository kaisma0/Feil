#nullable disable
using Serilog;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.CDN;

namespace Feil.Core;

class ContentDownloaderException(string value) : Exception(value)
{
}

static class ContentDownloader
{
    public const uint INVALID_APP_ID = uint.MaxValue;
    public const uint INVALID_DEPOT_ID = uint.MaxValue;
    public const ulong INVALID_MANIFEST_ID = ulong.MaxValue;
    public const string DEFAULT_BRANCH = "public";

    public static DownloadConfig Config = new();

    private static Steam3Session steam3;
    private static CDNClientPool cdnPool;

    // Pause gate: SemaphoreSlim(1,1) used as a binary pass-through.
    // count=1 → running (WaitAsync+Release is ~1 CAS, essentially free).
    // count=0 → paused (chunk tasks block until Resume() releases).
    private static readonly SemaphoreSlim _pauseGate = new(initialCount: 1, maxCount: 1);

    // 0 = running, 1 = paused. All transitions via Interlocked to avoid TOCTOU on CurrentCount.
    private static volatile int _isPausedInt;

    public static void Pause()
    {
        if (Interlocked.Exchange(ref _isPausedInt, 1) == 0)
            _pauseGate.Wait();
    }

    public static void Resume()
    {
        if (Interlocked.Exchange(ref _isPausedInt, 0) == 1)
            _pauseGate.Release();
    }

    // Resets to running state before a new job — clears leftover paused state.
    public static void ResetPauseGate() => Resume();

    private const string DEFAULT_DOWNLOAD_DIR = "depots";
    private const string CONFIG_DIR = Feil.Services.AppEnvironmentService.AppFolderName;
    private static readonly string STAGING_DIR = Path.Combine(CONFIG_DIR, "staging");

    private sealed class DepotDownloadInfo(
        uint depotid, uint appId, ulong manifestId, string branch,
        string installDir, byte[] depotKey)
    {
        public uint DepotId { get; } = depotid;
        public uint AppId { get; } = appId;
        public ulong ManifestId { get; } = manifestId;
        public string Branch { get; } = branch;
        public string InstallDir { get; } = installDir;
        public byte[] DepotKey { get; } = depotKey;
    }

    static bool CreateDirectories(uint depotId, uint depotVersion, out string installDir)
    {
        installDir = null;
        try
        {
            if (string.IsNullOrWhiteSpace(Config.InstallDirectory))
            {
                Directory.CreateDirectory(DEFAULT_DOWNLOAD_DIR);

                var depotPath = Path.Combine(DEFAULT_DOWNLOAD_DIR, depotId.ToString());
                Directory.CreateDirectory(depotPath);

                installDir = Path.Combine(depotPath, depotVersion.ToString());
                Directory.CreateDirectory(installDir);

                Directory.CreateDirectory(Path.Combine(installDir, CONFIG_DIR));
                Directory.CreateDirectory(Path.Combine(installDir, STAGING_DIR));
            }
            else
            {
                Directory.CreateDirectory(Config.InstallDirectory);

                installDir = Config.InstallDirectory;

                Directory.CreateDirectory(Path.Combine(installDir, CONFIG_DIR));
                Directory.CreateDirectory(Path.Combine(installDir, STAGING_DIR));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create install directories for depot {DepotId}", depotId);
            return false;
        }

        return true;
    }

    static bool TestIsFileIncluded(string filename)
    {
        if (!Config.UsingFileList)
            return true;

        filename = filename.Replace('\\', '/');

        if (Config.FilesToDownload.Contains(filename))
        {
            return true;
        }

        foreach (var rgx in Config.FilesToDownloadRegex)
        {
            var m = rgx.Match(filename);

            if (m.Success)
                return true;
        }

        return false;
    }

    internal static KeyValue GetSteam3AppSection(uint appId, EAppInfoSection section)
    {
        if (steam3 == null || steam3.AppInfo == null)
        {
            return null;
        }

        if (!steam3.AppInfo.TryGetValue(appId, out var app) || app == null)
        {
            return null;
        }

        var appinfo = app.KeyValues;
        var section_key = section switch
        {
            EAppInfoSection.Common => "common",
            EAppInfoSection.Extended => "extended",
            EAppInfoSection.Config => "config",
            EAppInfoSection.Depots => "depots",
            _ => throw new NotImplementedException(),
        };
        var section_kv = appinfo.Children.Where(c => c.Name == section_key).FirstOrDefault();
        return section_kv;
    }

    static uint GetSteam3AppBuildNumber(uint appId, string branch)
    {
        if (appId == INVALID_APP_ID)
            return 0;


        var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);
        if (depots == null) return 0;
        var branches = depots["branches"];
        var node = branches[branch];

        if (node == KeyValue.Invalid)
            return 0;

        var buildid = node["buildid"];

        if (buildid == KeyValue.Invalid)
            return 0;

        return uint.Parse(buildid.Value);
    }

    static uint GetSteam3DepotProxyAppId(uint depotId, uint appId)
    {
        var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);
        if (depots == null) return INVALID_APP_ID;
        var depotChild = depots[depotId.ToString()];

        if (depotChild == KeyValue.Invalid)
            return INVALID_APP_ID;

        if (depotChild["depotfromapp"] == KeyValue.Invalid)
            return INVALID_APP_ID;

        return depotChild["depotfromapp"].AsUnsignedInteger();
    }

    static async Task<ulong> GetSteam3DepotManifest(uint depotId, uint appId, string branch)
    {
        var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);
        var depotChild = depots[depotId.ToString()];

        if (depotChild == KeyValue.Invalid)
            return INVALID_MANIFEST_ID;

        // Shared depots can either provide manifests, or leave you relying on their parent app.
        // It seems that with the latter, "sharedinstall" will exist (and equals 2 in the one existance I know of).
        // Rather than relay on the unknown sharedinstall key, just look for manifests. Test cases: 111710, 346680.
        if (depotChild["manifests"] == KeyValue.Invalid && depotChild["depotfromapp"] != KeyValue.Invalid)
        {
            var otherAppId = depotChild["depotfromapp"].AsUnsignedInteger();
            if (otherAppId == appId)
            {
                // This shouldn't ever happen, but ya never know with Valve. Don't infinite loop.
                Log.Information("App {Arg0}, Depot {Arg1} has depotfromapp of {Arg2}!",
                    appId, depotId, otherAppId);
                return INVALID_MANIFEST_ID;
            }

            await steam3.RequestAppInfo(otherAppId);

            return await GetSteam3DepotManifest(depotId, otherAppId, branch);
        }

        var manifests = depotChild["manifests"];

        if (manifests.Children.Count == 0)
            return INVALID_MANIFEST_ID;

        var node = manifests[branch]["gid"];

        // Non passworded branch, found the manifest
        if (node.Value != null)
            return ulong.Parse(node.Value);

        // If we requested public branch and it had no manifest, nothing to do
        if (string.Equals(branch, DEFAULT_BRANCH, StringComparison.OrdinalIgnoreCase))
            return INVALID_MANIFEST_ID;

        if (string.IsNullOrEmpty(Config.BetaPassword))
        {
            Log.Information("Branch {Branch} for depot {DepotId} was not found, either it does not exist or it has a password", branch, depotId);
            return INVALID_MANIFEST_ID;
        }

        if (!steam3.AppBetaPasswords.ContainsKey(branch))
        {
            // Submit the password to Steam now to get encryption keys
            await steam3.CheckAppBetaPassword(appId, Config.BetaPassword);

            if (!steam3.AppBetaPasswords.ContainsKey(branch))
            {
                Log.Error("Error: Password was invalid for branch {Branch} (or the branch does not exist)", branch);
                return INVALID_MANIFEST_ID;
            }
        }

        // Got the password, request private depot section
        // TODO: We're probably repeating this request for every depot?
        var privateDepotSection = await steam3.GetPrivateBetaDepotSection(appId, branch);

        // Now repeat the same code to get the manifest gid from depot section
        depotChild = privateDepotSection[depotId.ToString()];

        if (depotChild == KeyValue.Invalid)
            return INVALID_MANIFEST_ID;

        manifests = depotChild["manifests"];

        if (manifests.Children.Count == 0)
            return INVALID_MANIFEST_ID;

        node = manifests[branch]["gid"];

        if (node.Value == null)
            return INVALID_MANIFEST_ID;

        return ulong.Parse(node.Value);
    }

    public static bool InitializeSteam3()
    {
        if (steam3 != null && steam3.IsLoggedOn)
            return true;

        steam3 = new Steam3Session();

        if (!steam3.WaitForLogon())
        {
            Log.Error("Unable to initialize Steam3 connection.");
            return false;
        }

        Task.Run(steam3.TickCallbacks);

        return true;
    }

    public static void ShutdownSteam3()
    {
        if (steam3 == null)
            return;

        steam3.Disconnect();
        steam3 = null;
    }

    public static Action<ulong, ulong> ProgressCallback;

    // Invoked periodically with the cumulative uncompressed bytes written to disk.
    public static Action<ulong> DiskProgressCallback;

    // Invoked once all depot manifests have been fetched, with the true compressed
    // (CDN download) byte total for the job. 
    public static Action<ulong> TotalSizeCallback;

    public static async Task DownloadAppAsync(uint appId, List<(uint depotId, ulong manifestId)> depotManifestIds, string branch, string os, string arch, string language, bool lv, CancellationToken cancellationToken = default)
    {
        cdnPool = new CDNClientPool(steam3, appId);

        // Load our configuration data containing the depots currently installed
        var configPath = Config.InstallDirectory;
        if (string.IsNullOrWhiteSpace(configPath))
        {
            configPath = DEFAULT_DOWNLOAD_DIR;
        }

        Directory.CreateDirectory(Path.Combine(configPath, CONFIG_DIR));

        if (!DepotConfigStore.Loaded)
        {
            DepotConfigStore.LoadFromFile(Path.Combine(configPath, CONFIG_DIR, "depot.config"));
        }

        await steam3?.RequestAppInfo(appId);

        var hasSpecificDepots = depotManifestIds.Count > 0;
        var depotIdsFound = new List<uint>();
        var depotIdsExpected = depotManifestIds.Select(x => x.depotId).ToList();
        var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);

        Log.Information("Using app branch: '{Arg0}'.", branch);

        if (depots != null)
        {
            foreach (var depotSection in depots.Children)
            {
                var id = INVALID_DEPOT_ID;
                if (depotSection.Children.Count == 0)
                    continue;

                if (!uint.TryParse(depotSection.Name, out id))
                    continue;

                if (hasSpecificDepots && !depotIdsExpected.Contains(id))
                    continue;

                if (!hasSpecificDepots)
                {
                    var depotConfig = depotSection["config"];
                    if (depotConfig != KeyValue.Invalid)
                    {
                        if (!Config.DownloadAllPlatforms &&
                            depotConfig["oslist"] != KeyValue.Invalid &&
                            !string.IsNullOrWhiteSpace(depotConfig["oslist"].Value))
                        {
                            var oslist = depotConfig["oslist"].Value.Split(',');
                            if (Array.IndexOf(oslist, os ?? Util.GetSteamOS()) == -1)
                                continue;
                        }

                        if (!Config.DownloadAllArchs &&
                            depotConfig["osarch"] != KeyValue.Invalid &&
                            !string.IsNullOrWhiteSpace(depotConfig["osarch"].Value))
                        {
                            var depotArch = depotConfig["osarch"].Value;
                            if (depotArch != (arch ?? Util.GetSteamArch()))
                                continue;
                        }

                        if (!Config.DownloadAllLanguages &&
                            depotConfig["language"] != KeyValue.Invalid &&
                            !string.IsNullOrWhiteSpace(depotConfig["language"].Value))
                        {
                            var depotLang = depotConfig["language"].Value;
                            if (depotLang != (language ?? "english"))
                                continue;
                        }

                        if (!lv &&
                            depotConfig["lowviolence"] != KeyValue.Invalid &&
                            depotConfig["lowviolence"].AsBoolean())
                            continue;
                    }
                }

                depotIdsFound.Add(id);

                if (!hasSpecificDepots)
                    depotManifestIds.Add((id, INVALID_MANIFEST_ID));
            }
        }

        if (depotManifestIds.Count == 0 && !hasSpecificDepots)
        {
            throw new ContentDownloaderException(string.Format("Couldn't find any depots to download for app {0}", appId));
        }

        if (depotIdsFound.Count < depotIdsExpected.Count)
        {
            var remainingDepotIds = depotIdsExpected.Except(depotIdsFound);
            //throw new ContentDownloaderException(string.Format("Depot {0} not listed for app {1}", string.Join(", ", remainingDepotIds), appId));
            // Mod: force download even if depot not listed
        }

        var infos = new List<DepotDownloadInfo>();

        foreach (var (depotId, manifestId) in depotManifestIds)
        {
            var info = await GetDepotInfo(depotId, appId, manifestId, branch);
            if (info != null)
            {
                infos.Add(info);
            }
        }



        try
        {
            await DownloadSteam3Async(infos, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log.Information("App {Arg0} was not completely downloaded.", appId);
            throw;
        }
    }

    static async Task<DepotDownloadInfo> GetDepotInfo(uint depotId, uint appId, ulong manifestId, string branch)
    {
        if (steam3 != null && appId != INVALID_APP_ID)
        {
            await steam3.RequestAppInfo(appId);
        }

        if (manifestId == INVALID_MANIFEST_ID)
        {
            manifestId = await GetSteam3DepotManifest(depotId, appId, branch);
            if (manifestId == INVALID_MANIFEST_ID && !string.Equals(branch, DEFAULT_BRANCH, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning("Warning: Depot {Arg0} does not have branch named \"{Arg1}\". Trying {Arg2} branch.", depotId, branch, DEFAULT_BRANCH);
                branch = DEFAULT_BRANCH;
                manifestId = await GetSteam3DepotManifest(depotId, appId, branch);
            }

            if (manifestId == INVALID_MANIFEST_ID)
            {
                Log.Information("Depot {Arg0} missing public subsection or manifest section.", depotId);
                return null;
            }
        }

        byte[] depotKey = null;
        if (DepotKeyStore.ContainsKey(depotId))
        {
            depotKey = DepotKeyStore.Get(depotId);
        }

        if (depotKey == null)
        {
            Log.Error("No depot key provided for {Arg0}, unable to download.", depotId);
            return null;
        }

        var uVersion = GetSteam3AppBuildNumber(appId, branch);

        if (!CreateDirectories(depotId, uVersion, out var installDir))
        {
            Log.Error("Error: Unable to create install directories!");
            return null;
        }

        // For depots that are proxied through depotfromapp, we still need to resolve the proxy app id, unless the app is freetodownload
        var containingAppId = appId;
        var proxyAppId = GetSteam3DepotProxyAppId(depotId, appId);
        if (proxyAppId != INVALID_APP_ID)
        {
            var common = GetSteam3AppSection(appId, EAppInfoSection.Common);
            if (common == null || !common["FreeToDownload"].AsBoolean())
            {
                containingAppId = proxyAppId;
            }
        }

        return new DepotDownloadInfo(depotId, containingAppId, manifestId, branch, installDir, depotKey);
    }

    private class ChunkMatch(DepotManifest.ChunkData oldChunk, DepotManifest.ChunkData newChunk)
    {
        public DepotManifest.ChunkData OldChunk { get; } = oldChunk;
        public DepotManifest.ChunkData NewChunk { get; } = newChunk;
    }

    private class DepotFilesData
    {
        public DepotDownloadInfo depotDownloadInfo;
        public DepotDownloadCounter depotCounter;
        public string stagingDir;
        public DepotManifest manifest;
        public DepotManifest previousManifest;
        public List<DepotManifest.FileData> filteredFiles;
        public HashSet<string> allFileNames;
    }

    private class FileStreamData
    {
        public FileStream fileStream;
        public SemaphoreSlim fileLock;
        public int chunksToDownload;
    }

    private class GlobalDownloadCounter
    {
        public ulong completeDownloadSize;
        public ulong totalBytesCompressed;
        public ulong totalBytesUncompressed;
        // long so Interlocked.Add/Read can manage them lock-free
        public long totalBytesTarget;
        public long totalBytesDownloaded;
        public long totalBytesWrittenToDisk;
    }

    private class DepotDownloadCounter
    {
        public ulong completeDownloadSize;
        public ulong sizeDownloaded;
        public ulong depotBytesCompressed;
        public ulong depotBytesUncompressed;
    }

    private static async Task DownloadSteam3Async(List<DepotDownloadInfo> depots, CancellationToken cancellationToken)
    {
        Log.Debug("Starting download process for {Count} depots", depots.Count);
        await cdnPool.UpdateServerList();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var downloadCounter = new GlobalDownloadCounter();
        var depotsToDownload = new List<DepotFilesData>(depots.Count);
        var allFileNamesAllDepots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // First, fetch all the manifests for each depot (including previous manifests) and perform the initial setup
        foreach (var depot in depots)
        {
            var depotFileData = await ProcessDepotManifestAndFiles(cts, depot, downloadCounter);

            if (depotFileData != null)
            {
                depotsToDownload.Add(depotFileData);
                allFileNamesAllDepots.UnionWith(depotFileData.allFileNames);
            }

            cts.Token.ThrowIfCancellationRequested();
        }

        // If we're about to write all the files to the same directory, we will need to first de-duplicate any files by path
        // This is in last-depot-wins order, from Steam or the list of depots supplied by the user
        if (!string.IsNullOrWhiteSpace(Config.InstallDirectory) && depotsToDownload.Count > 0)
        {
            Log.Debug("Deduplicating files across {Count} depots for shared install directory", depotsToDownload.Count);
            var claimedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = depotsToDownload.Count - 1; i >= 0; i--)
            {
                ulong removedUncompressed = 0;
                long removedCompressed = 0;

                // For each depot, remove all files from the list that have been claimed by a later depot
                depotsToDownload[i].filteredFiles.RemoveAll(file =>
                {
                    if (claimedFileNames.Contains(file.FileName))
                    {
                        // Only subtract sizes for files, directories don't add to our byte counters
                        if (!file.Flags.HasFlag(EDepotFileFlag.Directory))
                        {
                            removedUncompressed += file.TotalSize;
                            removedCompressed += file.Chunks.Sum(c => (long)c.CompressedLength);
                        }
                        return true;
                    }
                    return false;
                });

                // Deduct the sizes of the stripped duplicates from our global and depot targets
                downloadCounter.completeDownloadSize -= removedUncompressed;
                Interlocked.Add(ref downloadCounter.totalBytesTarget, -removedCompressed);
                depotsToDownload[i].depotCounter.completeDownloadSize -= removedUncompressed;

                claimedFileNames.UnionWith(depotsToDownload[i].allFileNames);
            }
        }

        // Notify the UI of the real compressed (CDN) download total now that all manifests
        // are fetched, deduplicated, and totalBytesTarget is perfectly accurate.
        TotalSizeCallback?.Invoke((ulong)Interlocked.Read(ref downloadCounter.totalBytesTarget));

        if (depotsToDownload.Count > 0)
        {
            var firstInstallDir = depotsToDownload[0].depotDownloadInfo.InstallDir;
            var driveRoot = Path.GetPathRoot(Path.GetFullPath(firstInstallDir));
            if (driveRoot != null)
            {
                Log.Debug("Checking available disk space on {DriveRoot} for {Size} bytes", driveRoot, downloadCounter.completeDownloadSize);
                var drive = new DriveInfo(driveRoot);
                if ((ulong)drive.AvailableFreeSpace < downloadCounter.completeDownloadSize)
                {
                    var missingSpace = downloadCounter.completeDownloadSize - (ulong)drive.AvailableFreeSpace;
                    throw new ContentDownloaderException(
                        $"Not enough disk space available. Requires approximately {downloadCounter.completeDownloadSize} bytes, " +
                        $"but only {drive.AvailableFreeSpace} bytes are available on {driveRoot}. Missing {missingSpace} bytes.");
                }
            }
        }

        using var progressTimer = new System.Threading.Timer(_ =>
        {
            var downloaded = Interlocked.Read(ref downloadCounter.totalBytesDownloaded);
            var target = Interlocked.Read(ref downloadCounter.totalBytesTarget);
            ProgressCallback?.Invoke((ulong)downloaded, (ulong)target);
            DiskProgressCallback?.Invoke((ulong)Interlocked.Read(ref downloadCounter.totalBytesWrittenToDisk));
        },
        null, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));

        foreach (var depotFileData in depotsToDownload)
        {
            await DownloadSteam3AsyncDepotFiles(cts, downloadCounter, depotFileData, allFileNamesAllDepots);
        }

        progressTimer.Change(Timeout.Infinite, Timeout.Infinite);
        ProgressCallback?.Invoke(
            (ulong)Interlocked.Read(ref downloadCounter.totalBytesDownloaded),
            (ulong)Interlocked.Read(ref downloadCounter.totalBytesTarget));
        DiskProgressCallback?.Invoke((ulong)Interlocked.Read(ref downloadCounter.totalBytesWrittenToDisk));

        Log.Information("Total downloaded: {Arg0} bytes ({Arg1} bytes uncompressed) from {Arg2} depots",
            downloadCounter.totalBytesCompressed, downloadCounter.totalBytesUncompressed, depots.Count);
    }

    private static async Task<DepotFilesData> ProcessDepotManifestAndFiles(CancellationTokenSource cts, DepotDownloadInfo depot, GlobalDownloadCounter downloadCounter)
    {
        var depotCounter = new DepotDownloadCounter();

        Log.Information("Processing depot {Arg0}", depot.DepotId);

        DepotManifest oldManifest = null;
        DepotManifest newManifest = null;
        var configDir = string.IsNullOrWhiteSpace(Config.JobDirectory)
            ? Path.Combine(depot.InstallDir, CONFIG_DIR)
            : Config.JobDirectory;

        var lastManifestId = INVALID_MANIFEST_ID;
        DepotConfigStore.Instance.InstalledManifestIDs.TryGetValue(depot.DepotId, out lastManifestId);

        // In case we have an early exit, this will force equiv of verifyall next run.
        DepotConfigStore.Instance.InstalledManifestIDs[depot.DepotId] = INVALID_MANIFEST_ID;
        DepotConfigStore.Save();

        if (lastManifestId != INVALID_MANIFEST_ID)
        {
            // We only have to show this warning if the old manifest ID was different
            var badHashWarning = (lastManifestId != depot.ManifestId);
            oldManifest = Util.LoadManifestFromFile(configDir, depot.DepotId, lastManifestId, badHashWarning);
        }

        if (Config.UseManifestFile)
        {
            lastManifestId = depot.ManifestId;
            oldManifest = DepotManifest.LoadFromFile(Config.ManifestFile);
            if (oldManifest.FilenamesEncrypted)
            {
                if (!oldManifest.DecryptFilenames(depot.DepotKey))
                {
                    Log.Error("Failed to decrypt filenames in manifest file.");
                    return null;
                }
            }
        }

        if (lastManifestId == depot.ManifestId && oldManifest != null)
        {
            newManifest = oldManifest;
            Log.Information("Already have manifest {Arg0} for depot {Arg1}.", depot.ManifestId, depot.DepotId);
        }
        else
        {
            newManifest = Util.LoadManifestFromFile(configDir, depot.DepotId, depot.ManifestId, true);

            if (newManifest != null)
            {
                Log.Information("Already have manifest {Arg0} for depot {Arg1}.", depot.ManifestId, depot.DepotId);
            }
            else
            {
                Log.Information("Downloading depot {DepotId} manifest", depot.DepotId);

                ulong manifestRequestCode = 0;
                var manifestRequestCodeExpiration = DateTime.MinValue;

                do
                {
                    cts.Token.ThrowIfCancellationRequested();

                    Server connection = null;

                    try
                    {
                        connection = cdnPool.GetConnection();

                        string cdnToken = null;
                        if (steam3.CDNAuthTokens.TryGetValue((depot.DepotId, connection.Host), out var authTokenCallbackPromise))
                        {
                            var result = await authTokenCallbackPromise.Task;
                            cdnToken = result.Token;
                        }

                        var now = DateTime.Now;

                        // In order to download this manifest, we need the current manifest request code
                        // The manifest request code is only valid for a specific period in time
                        if (manifestRequestCode == 0 || now >= manifestRequestCodeExpiration)
                        {
                            manifestRequestCode = await steam3.GetDepotManifestRequestCodeAsync(
                                depot.DepotId,
                                depot.AppId,
                                depot.ManifestId,
                                depot.Branch);
                            // This code will hopefully be valid for one period following the issuing period
                            manifestRequestCodeExpiration = now.Add(TimeSpan.FromMinutes(5));

                            // If we could not get the manifest code, this is a fatal error
                            if (manifestRequestCode == 0)
                            {
                                cts.Cancel();
                            }
                        }

                        Log.Debug(
                            "Downloading manifest {ManifestId} from {Connection} with {Proxy}",
                            depot.ManifestId,
                            connection,
                            cdnPool.ProxyServer != null ? cdnPool.ProxyServer : "no proxy");
                        newManifest = await cdnPool.CDNClient.DownloadManifestAsync(
                            depot.DepotId,
                            depot.ManifestId,
                            manifestRequestCode,
                            connection,
                            depot.DepotKey,
                            cdnPool.ProxyServer,
                            cdnToken).ConfigureAwait(false);

                        cdnPool.ReturnConnection(connection);
                    }
                    catch (TaskCanceledException)
                    {
                        Log.Information("Connection timeout downloading depot manifest {Arg0} {Arg1}. Retrying.", depot.DepotId, depot.ManifestId);
                    }
                    catch (SteamKitWebRequestException e)
                    {
                        // If the CDN returned 403, attempt to get a cdn auth if we didn't yet
                        if (e.StatusCode == HttpStatusCode.Forbidden && !steam3.CDNAuthTokens.ContainsKey((depot.DepotId, connection.Host)))
                        {
                            await steam3.RequestCDNAuthToken(depot.AppId, depot.DepotId, connection);

                            cdnPool.ReturnConnection(connection);

                            continue;
                        }

                        cdnPool.ReturnBrokenConnection(connection);

                        if (e.StatusCode == HttpStatusCode.Unauthorized || e.StatusCode == HttpStatusCode.Forbidden)
                        {
                            Log.Error("Encountered {Arg2} for depot manifest {Arg0} {Arg1}. Aborting.", depot.DepotId, depot.ManifestId, (int)e.StatusCode);
                            break;
                        }

                        if (e.StatusCode == HttpStatusCode.NotFound)
                        {
                            Log.Error("Encountered 404 for depot manifest {Arg0} {Arg1}. Aborting.", depot.DepotId, depot.ManifestId);
                            break;
                        }

                        Log.Error("Encountered error downloading depot manifest {Arg0} {Arg1}: {Arg2}", depot.DepotId, depot.ManifestId, e.StatusCode);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception e)
                    {
                        cdnPool.ReturnBrokenConnection(connection);
                        Log.Error("Encountered error downloading manifest for depot {Arg0} {Arg1}: {Arg2}", depot.DepotId, depot.ManifestId, e.Message);
                    }
                } while (newManifest == null);

                if (newManifest == null)
                {
                    Log.Error("\nUnable to download manifest {Arg0} for depot {Arg1}", depot.ManifestId, depot.DepotId);
                    cts.Cancel();
                }

                // Throw the cancellation exception if requested so that this task is marked failed
                cts.Token.ThrowIfCancellationRequested();

                Util.SaveManifestToFile(configDir, newManifest);
            }
        }

        Log.Information("Manifest {Arg0} ({Arg1})", depot.ManifestId, newManifest.CreationTime);

        if (Config.DownloadManifestOnly)
        {
            DumpManifestToTextFile(depot, newManifest);
            return null;
        }

        var stagingDir = Path.Combine(depot.InstallDir, STAGING_DIR);

        var filesAfterExclusions = newManifest.Files.AsParallel().Where(f => TestIsFileIncluded(f.FileName)).ToList();
        var allFileNames = new HashSet<string>(filesAfterExclusions.Count);

        // Pre-process
        filesAfterExclusions.ForEach(file =>
        {
            allFileNames.Add(file.FileName);

            var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
            var fileStagingPath = Path.Combine(stagingDir, file.FileName);

            if (file.Flags.HasFlag(EDepotFileFlag.Directory))
            {
                Directory.CreateDirectory(fileFinalPath);
                Directory.CreateDirectory(fileStagingPath);
            }
            else
            {
                // Some manifests don't explicitly include all necessary directories
                Directory.CreateDirectory(Path.GetDirectoryName(fileFinalPath));
                Directory.CreateDirectory(Path.GetDirectoryName(fileStagingPath));

                // Use the compressed chunk sizes (actual CDN download bytes) 
                var compressedFileSize = (ulong)file.Chunks.Sum(c => (long)c.CompressedLength);

                downloadCounter.completeDownloadSize += file.TotalSize;  // disk-space check still needs uncompressed
                Interlocked.Add(ref downloadCounter.totalBytesTarget, (long)compressedFileSize);
                depotCounter.completeDownloadSize += file.TotalSize;
            }
        });

        return new DepotFilesData
        {
            depotDownloadInfo = depot,
            depotCounter = depotCounter,
            stagingDir = stagingDir,
            manifest = newManifest,
            previousManifest = oldManifest,
            filteredFiles = filesAfterExclusions,
            allFileNames = allFileNames
        };
    }

    private static async Task DownloadSteam3AsyncDepotFiles(CancellationTokenSource cts,
        GlobalDownloadCounter downloadCounter, DepotFilesData depotFilesData, HashSet<string> allFileNamesAllDepots)
    {
        var depot = depotFilesData.depotDownloadInfo;
        var depotCounter = depotFilesData.depotCounter;

        Log.Information("Downloading depot {Arg0}", depot.DepotId);

        var files = depotFilesData.filteredFiles.Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory)).ToArray();
        var networkChunkQueue = new ConcurrentQueue<(FileStreamData fileStreamData, DepotManifest.FileData fileData, DepotManifest.ChunkData chunk)>();

        // Track all FileStreamData so we can clean up file handles on cancellation.
        // Without this, cancelled downloads leave FileStreams (opened with FileShare.None)
        // alive until GC, blocking any subsequent download attempt on the same files.
        var allFileStreamDatas = new ConcurrentBag<FileStreamData>();

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Config.MaxDownloads,
            CancellationToken = cts.Token
        };

        try
        {
            await Parallel.ForEachAsync(files, parallelOptions, async (file, cancellationToken) =>
            {
                await Task.Yield();
                DownloadSteam3AsyncDepotFile(cts, downloadCounter, depotFilesData, file, networkChunkQueue, allFileStreamDatas);
            });

            await Parallel.ForEachAsync(networkChunkQueue, parallelOptions, async (q, cancellationToken) =>
            {
                await DownloadSteam3AsyncDepotFileChunk(
                    cts, downloadCounter, depotFilesData,
                    q.fileData, q.fileStreamData, q.chunk
                );
            });
        }
        finally
        {
            // Dispose all file handles that were opened during chunk downloads.
            // On normal completion the per-file cleanup (remainingChunks == 0) already
            // closed these, so Dispose() here is a safe no-op for those.
            // On cancellation this is the only path that releases the locks.
            foreach (var fsd in allFileStreamDatas)
            {
                try { fsd.fileStream?.Dispose(); } catch { /* best-effort */ }
                try { fsd.fileLock?.Dispose(); } catch { /* best-effort */ }
            }
        }

        // Check for deleted files if updating the depot.
        if (depotFilesData.previousManifest != null)
        {
            var fileNameComparer = string.IsNullOrWhiteSpace(Config.InstallDirectory)
                ? StringComparer.Ordinal
                : StringComparer.OrdinalIgnoreCase;

            var previousFilteredFiles = depotFilesData.previousManifest.Files
                .AsParallel()
                .Where(f => TestIsFileIncluded(f.FileName))
                .Select(f => f.FileName)
                .ToHashSet(fileNameComparer);

            // Check if we are writing to a single output directory. If not, each depot folder is managed independently
            if (string.IsNullOrWhiteSpace(Config.InstallDirectory))
            {
                // Of the list of files in the previous manifest, remove any file names that exist in the current set of all file names
                previousFilteredFiles.ExceptWith(depotFilesData.allFileNames);
            }
            else
            {
                // Of the list of files in the previous manifest, remove any file names that exist in the current set of all file names across all depots being downloaded
                previousFilteredFiles.ExceptWith(allFileNamesAllDepots);
            }

            foreach (var existingFileName in previousFilteredFiles)
            {
                var fileFinalPath = Path.Combine(depot.InstallDir, existingFileName);

                if (!File.Exists(fileFinalPath))
                    continue;

                File.Delete(fileFinalPath);
                Log.Information("Deleted {Arg0}", fileFinalPath);
            }
        }

        DepotConfigStore.Instance.InstalledManifestIDs[depot.DepotId] = depot.ManifestId;
        DepotConfigStore.Save();

        Log.Information("Depot {Arg0} - Downloaded {Arg1} bytes ({Arg2} bytes uncompressed)", depot.DepotId, depotCounter.depotBytesCompressed, depotCounter.depotBytesUncompressed);
    }

    private static void DownloadSteam3AsyncDepotFile(
        CancellationTokenSource cts,
        GlobalDownloadCounter downloadCounter,
        DepotFilesData depotFilesData,
        DepotManifest.FileData file,
        ConcurrentQueue<(FileStreamData, DepotManifest.FileData, DepotManifest.ChunkData)> networkChunkQueue,
        ConcurrentBag<FileStreamData> allFileStreamDatas)
    {
        cts.Token.ThrowIfCancellationRequested();

        var depot = depotFilesData.depotDownloadInfo;
        var stagingDir = depotFilesData.stagingDir;
        var depotDownloadCounter = depotFilesData.depotCounter;
        var oldProtoManifest = depotFilesData.previousManifest;
        DepotManifest.FileData oldManifestFile = null;
        if (oldProtoManifest != null)
        {
            oldManifestFile = oldProtoManifest.Files.SingleOrDefault(f => f.FileName == file.FileName);
        }

        var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
        var fileStagingPath = Path.Combine(stagingDir, file.FileName);

        // This may still exist if the previous run exited before cleanup
        if (File.Exists(fileStagingPath))
        {
            File.Delete(fileStagingPath);
        }

        List<DepotManifest.ChunkData> neededChunks;
        var fi = new FileInfo(fileFinalPath);
        var fileDidExist = fi.Exists;
        if (!fileDidExist)
        {
            Log.Information("Pre-allocating {Arg0}", fileFinalPath);

            // create new file. need all chunks
            using var fs = File.Create(fileFinalPath);
            try
            {
                fs.SetLength((long)file.TotalSize);
            }
            catch (IOException ex)
            {
                throw new ContentDownloaderException(string.Format("Failed to allocate file {0}: {1}", fileFinalPath, ex.Message));
            }

            neededChunks = new List<DepotManifest.ChunkData>(file.Chunks);
        }
        else
        {
            // open existing
            if (oldManifestFile != null)
            {
                neededChunks = [];

                var hashMatches = oldManifestFile.FileHash.SequenceEqual(file.FileHash);
                if (Config.VerifyAll || !hashMatches)
                {
                    // we have a version of this file, but it doesn't fully match what we want
                    if (Config.VerifyAll)
                    {
                        Log.Information("Validating {Arg0}", fileFinalPath);
                    }

                    var matchingChunks = new List<ChunkMatch>();

                    foreach (var chunk in file.Chunks)
                    {
                        var oldChunk = oldManifestFile.Chunks.FirstOrDefault(c => c.ChunkID.SequenceEqual(chunk.ChunkID));
                        if (oldChunk != null)
                        {
                            matchingChunks.Add(new ChunkMatch(oldChunk, chunk));
                        }
                        else
                        {
                            neededChunks.Add(chunk);
                        }
                    }

                    var orderedChunks = matchingChunks.OrderBy(x => x.OldChunk.Offset);

                    var copyChunks = new List<ChunkMatch>();

                    using (var fsOld = File.Open(fileFinalPath, FileMode.Open))
                    {
                        foreach (var match in orderedChunks)
                        {
                            fsOld.Seek((long)match.OldChunk.Offset, SeekOrigin.Begin);

                            var adler = Util.AdlerHash(fsOld, (int)match.OldChunk.UncompressedLength);
                            if (!adler.SequenceEqual(BitConverter.GetBytes(match.OldChunk.Checksum)))
                            {
                                neededChunks.Add(match.NewChunk);
                            }
                            else
                            {
                                copyChunks.Add(match);
                            }
                        }
                    }

                    if (!hashMatches)
                    {
                        File.Move(fileFinalPath, fileStagingPath);

                        using (var fsOld = File.Open(fileStagingPath, FileMode.Open))
                        {
                            using var fs = File.Open(fileFinalPath, FileMode.Create);
                            try
                            {
                                fs.SetLength((long)file.TotalSize);
                            }
                            catch (IOException ex)
                            {
                                throw new ContentDownloaderException(string.Format("Failed to resize file to expected size {0}: {1}", fileFinalPath, ex.Message));
                            }

                            foreach (var match in copyChunks)
                            {
                                fsOld.Seek((long)match.OldChunk.Offset, SeekOrigin.Begin);

                                var tmp = new byte[match.OldChunk.UncompressedLength];
                                fsOld.ReadExactly(tmp);

                                fs.Seek((long)match.NewChunk.Offset, SeekOrigin.Begin);
                                fs.Write(tmp, 0, tmp.Length);
                            }
                        }

                        File.Delete(fileStagingPath);
                    }
                }
            }
            else
            {
                // No old manifest or file not in old manifest. We must validate.

                using var fs = File.Open(fileFinalPath, FileMode.Open);
                if ((ulong)fi.Length != file.TotalSize)
                {
                    try
                    {
                        fs.SetLength((long)file.TotalSize);
                    }
                    catch (IOException ex)
                    {
                        throw new ContentDownloaderException(string.Format("Failed to allocate file {0}: {1}", fileFinalPath, ex.Message));
                    }
                }

                Log.Information("Validating {Arg0}", fileFinalPath);
                neededChunks = Util.ValidateSteam3FileChecksums(fs, [.. file.Chunks.OrderBy(x => x.Offset)]);
            }

            if (neededChunks.Count == 0)
            {
                // All chunks verified on disk — count their compressed sizes as 'downloaded'
                // so the progress ratio stays consistent with the compressed-based target.
                var compressedFileSize = (ulong)file.Chunks.Sum(c => (long)c.CompressedLength);

                lock (depotDownloadCounter)
                {
                    depotDownloadCounter.sizeDownloaded += file.TotalSize;
                    Log.Information("{0,6:#00.00}% {Arg1}", (depotDownloadCounter.sizeDownloaded / (float)depotDownloadCounter.completeDownloadSize) * 100.0f, fileFinalPath);
                }

                lock (downloadCounter)
                {
                    downloadCounter.completeDownloadSize -= file.TotalSize;
                }
                Interlocked.Add(ref downloadCounter.totalBytesDownloaded, (long)compressedFileSize);
                Interlocked.Add(ref downloadCounter.totalBytesWrittenToDisk, (long)file.TotalSize);

                return;
            }

            // Chunks already good on disk: credit their compressed sizes as downloaded
            // so the progress denominator (compressed target) stays balanced.
            // compressedTotal - neededCompressed = compressed bytes already satisfied on disk.
            var compressedFileTotal = (ulong)file.Chunks.Sum(c => (long)c.CompressedLength);
            var compressedNeeded = (ulong)neededChunks.Sum(c => (long)c.CompressedLength);
            var compressedSizeOnDisk = compressedFileTotal - compressedNeeded;
            var sizeOnDisk = (file.TotalSize - (ulong)neededChunks.Select(x => (long)x.UncompressedLength).Sum());
            lock (depotDownloadCounter)
            {
                depotDownloadCounter.sizeDownloaded += sizeOnDisk;
            }

            lock (downloadCounter)
            {
                downloadCounter.completeDownloadSize -= sizeOnDisk;
            }
            Interlocked.Add(ref downloadCounter.totalBytesDownloaded, (long)compressedSizeOnDisk);
            Interlocked.Add(ref downloadCounter.totalBytesWrittenToDisk, (long)sizeOnDisk);
        }

        var fileIsExecutable = file.Flags.HasFlag(EDepotFileFlag.Executable);
        if (fileIsExecutable && (!fileDidExist || oldManifestFile == null || !oldManifestFile.Flags.HasFlag(EDepotFileFlag.Executable)))
        {
            PlatformUtilities.SetExecutable(fileFinalPath, true);
        }
        else if (!fileIsExecutable && oldManifestFile != null && oldManifestFile.Flags.HasFlag(EDepotFileFlag.Executable))
        {
            PlatformUtilities.SetExecutable(fileFinalPath, false);
        }

        var fileStreamData = new FileStreamData
        {
            fileStream = null,
            fileLock = new SemaphoreSlim(1),
            chunksToDownload = neededChunks.Count
        };

        allFileStreamDatas.Add(fileStreamData);

        foreach (var chunk in neededChunks)
        {
            networkChunkQueue.Enqueue((fileStreamData, file, chunk));
        }
    }

    private static async Task DownloadSteam3AsyncDepotFileChunk(
        CancellationTokenSource cts,
        GlobalDownloadCounter downloadCounter,
        DepotFilesData depotFilesData,
        DepotManifest.FileData file,
        FileStreamData fileStreamData,
        DepotManifest.ChunkData chunk)
    {
        cts.Token.ThrowIfCancellationRequested();

        // Pause gate: volatile read is ~free; semaphore only entered when actually paused.
        if (_isPausedInt != 0)
        {
            await _pauseGate.WaitAsync(cts.Token).ConfigureAwait(false);
            _pauseGate.Release();
        }

        var depot = depotFilesData.depotDownloadInfo;
        var depotDownloadCounter = depotFilesData.depotCounter;

        var chunkID = Convert.ToHexString(chunk.ChunkID).ToLowerInvariant();

        var written = 0;
        var chunkBuffer = ArrayPool<byte>.Shared.Rent((int)chunk.UncompressedLength);

        try
        {
            do
            {
                cts.Token.ThrowIfCancellationRequested();

                Server connection = null;

                try
                {
                    connection = cdnPool.GetConnection();

                    string cdnToken = null;
                    if (steam3.CDNAuthTokens.TryGetValue((depot.DepotId, connection.Host), out var authTokenCallbackPromise))
                    {
                        var result = await authTokenCallbackPromise.Task;
                        cdnToken = result.Token;
                    }

                    Log.Debug("Downloading chunk {ChunkID} from {Connection} with {Proxy}", chunkID, connection, cdnPool.ProxyServer != null ? cdnPool.ProxyServer : "no proxy");
                    written = await cdnPool.CDNClient.DownloadDepotChunkAsync(
                        depot.DepotId,
                        chunk,
                        connection,
                        chunkBuffer,
                        depot.DepotKey,
                        cdnPool.ProxyServer,
                        cdnToken).ConfigureAwait(false);

                    cdnPool.ReturnConnection(connection);

                    break;
                }
                catch (TaskCanceledException)
                {
                    Log.Information("Connection timeout downloading chunk {Arg0}", chunkID);
                    cdnPool.ReturnBrokenConnection(connection);
                }
                catch (SteamKitWebRequestException e)
                {
                    // If the CDN returned 403, attempt to get a cdn auth if we didn't yet,
                    // if auth task already exists, make sure it didn't complete yet, so that it gets awaited above
                    if (e.StatusCode == HttpStatusCode.Forbidden &&
                        (!steam3.CDNAuthTokens.TryGetValue((depot.DepotId, connection.Host), out var authTokenCallbackPromise) || !authTokenCallbackPromise.Task.IsCompleted))
                    {
                        await steam3.RequestCDNAuthToken(depot.AppId, depot.DepotId, connection);

                        cdnPool.ReturnConnection(connection);

                        continue;
                    }

                    cdnPool.ReturnBrokenConnection(connection);

                    if (e.StatusCode == HttpStatusCode.Unauthorized || e.StatusCode == HttpStatusCode.Forbidden)
                    {
                        Log.Error("Encountered {Arg1} for chunk {Arg0}. Aborting.", chunkID, (int)e.StatusCode);
                        break;
                    }

                    Log.Error("Encountered error downloading chunk {Arg0}: {Arg1}", chunkID, e.StatusCode);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    cdnPool.ReturnBrokenConnection(connection);
                    Log.Error("Encountered unexpected error downloading chunk {Arg0}: {Arg1}", chunkID, e.Message);
                }
            } while (written == 0);

            if (written == 0)
            {
                Log.Error("Failed to find any server with chunk {Arg0} for depot {Arg1}. Aborting.", chunkID, depot.DepotId);
                cts.Cancel();
            }

            // Throw the cancellation exception if requested so that this task is marked failed
            cts.Token.ThrowIfCancellationRequested();

            bool lockAcquired = false;
            try
            {
                await fileStreamData.fileLock.WaitAsync(cts.Token).ConfigureAwait(false);
                lockAcquired = true;

                if (fileStreamData.fileStream == null)
                {
                    var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
                    fileStreamData.fileStream = new FileStream(fileFinalPath, FileMode.Open,
                        FileAccess.ReadWrite, FileShare.None, bufferSize: 4096, FileOptions.Asynchronous);
                }

                fileStreamData.fileStream.Seek((long)chunk.Offset, SeekOrigin.Begin);
                await fileStreamData.fileStream.WriteAsync(chunkBuffer.AsMemory(0, written), cts.Token);
            }
            finally
            {
                if (lockAcquired)
                    fileStreamData.fileLock.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunkBuffer);
        }

        var remainingChunks = Interlocked.Decrement(ref fileStreamData.chunksToDownload);
        if (remainingChunks == 0)
        {
            fileStreamData.fileStream?.Dispose();
            fileStreamData.fileLock.Dispose();
        }

        ulong sizeDownloaded = 0;
        lock (depotDownloadCounter)
        {
            sizeDownloaded = depotDownloadCounter.sizeDownloaded + (ulong)written;
            depotDownloadCounter.sizeDownloaded = sizeDownloaded;
            depotDownloadCounter.depotBytesCompressed += chunk.CompressedLength;
            depotDownloadCounter.depotBytesUncompressed += chunk.UncompressedLength;
        }

        lock (downloadCounter)
        {
            downloadCounter.totalBytesCompressed += chunk.CompressedLength;
            downloadCounter.totalBytesUncompressed += chunk.UncompressedLength;
        }
        Interlocked.Add(ref downloadCounter.totalBytesDownloaded, chunk.CompressedLength);
        Interlocked.Add(ref downloadCounter.totalBytesWrittenToDisk, written);

        if (remainingChunks == 0)
        {
            var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
            Log.Information("{0,6:#00.00}% {Arg1}", (sizeDownloaded / (float)depotDownloadCounter.completeDownloadSize) * 100.0f, fileFinalPath);
        }
    }

    class ChunkIdComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[] x, byte[] y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x == null || y == null) return false;
            return x.SequenceEqual(y);
        }

        public int GetHashCode(byte[] obj)
        {
            ArgumentNullException.ThrowIfNull(obj);

            // ChunkID is SHA-1, so we can just use the first 4 bytes
            return BitConverter.ToInt32(obj, 0);
        }
    }

    static void DumpManifestToTextFile(DepotDownloadInfo depot, DepotManifest manifest)
    {
        var txtManifest = Path.Combine(depot.InstallDir, $"manifest_{depot.DepotId}_{depot.ManifestId}.txt");
        using var sw = new StreamWriter(txtManifest);

        sw.WriteLine($"Content Manifest for Depot {depot.DepotId} ");
        sw.WriteLine();
        sw.WriteLine($"Manifest ID / date     : {depot.ManifestId} / {manifest.CreationTime} ");

        var uniqueChunks = new HashSet<byte[]>(new ChunkIdComparer());

        foreach (var file in manifest.Files)
        {
            foreach (var chunk in file.Chunks)
            {
                uniqueChunks.Add(chunk.ChunkID);
            }
        }

        sw.WriteLine($"Total number of files  : {manifest.Files.Count} ");
        sw.WriteLine($"Total number of chunks : {uniqueChunks.Count} ");
        sw.WriteLine($"Total bytes on disk    : {manifest.TotalUncompressedSize} ");
        sw.WriteLine($"Total bytes compressed : {manifest.TotalCompressedSize} ");
        sw.WriteLine();
        sw.WriteLine();
        sw.WriteLine("          Size Chunks File SHA                                 Flags Name");

        foreach (var file in manifest.Files)
        {
            var sha1Hash = Convert.ToHexString(file.FileHash).ToLower();
            sw.WriteLine($"{file.TotalSize,14:d} {file.Chunks.Count,6:d} {sha1Hash} {(int)file.Flags,5:x} {file.FileName}");
        }
    }
}
