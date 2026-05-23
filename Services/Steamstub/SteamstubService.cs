using System.IO.Compression;
using Feil.Core;
using Serilog;

namespace Feil.Services.Steamstub;

// Applies the steamstub DLL proxy to a game's install directory and
// configures the necessary Steam launch options.
public static class SteamstubService
{
    private const string GitHubApiUrl =
        "https://api.github.com/repos/kaisma0/steamstubbed-standalone/releases/latest";

    private const string DllName = "winmm.dll";

    // ── Public API ────────────────────────────────────────────────────────────

    // Scans installDirectory for SteamStub-protected EXEs by reading each PE section table
    // and looking for a .bind section, then downloads and deploys winmm.dll as a proxy and
    // sets the Steam launch option for appId.
    // Returns true if a protected EXE was found and the DLL was deployed;
    // false if no matching EXE was found (nothing to do).
    public static async Task<bool> ApplyAsync(
        int appId,
        string installDirectory,
        CancellationToken ct = default)
    {
        Log.Information("Starting Steamstub scan for AppId {AppId} in {Dir}", appId, installDirectory);

        var matchedDirs = await Task.Run(() => FindMatchingDirectories(installDirectory, ct), ct).ConfigureAwait(false);

        if (matchedDirs.Count == 0)
        {
            Log.Information("No SteamStub-protected EXE found for AppId {AppId}", appId);
            return false;
        }

        Log.Information(
            "Found SteamStub .bind section in {Count} director(y/ies) for AppId {AppId}",
            matchedDirs.Count, appId);

        var tempDir = Path.Combine(Path.GetTempPath(), $"feil-steamstub-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);

            var dllPath = await DownloadAndExtractDllAsync(tempDir, ct).ConfigureAwait(false);
            if (dllPath is null)
            {
                Log.Error("Failed to obtain {Dll} from release", DllName);
                return false;
            }

            foreach (var dir in matchedDirs)
            {
                var dest = Path.Combine(dir, DllName);
                File.Copy(dllPath, dest, overwrite: true);
                Log.Information("Deployed {Dll} to {Dest}", DllName, dest);
            }
        }
        finally
        {
            DeleteDirectoryBestEffort(tempDir);
        }

        KillSteamProcesses();

        SteamLaunchOptionsService.SetWinmmOverride(appId);

        return true;
    }

    private static void KillSteamProcesses()
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("steam"))
            {
                try
                {
                    Log.Information("Killing Steam process {Pid} to allow localconfig.vdf changes", p.Id);
                    p.Kill();
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Failed to kill Steam process {Pid}", p.Id);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error enumerating Steam processes");
        }
    }

    // ── EXE scanning (PE header) ──────────────────────────────────────────────

    // Returns the set of unique parent directories containing EXEs that have
    // a .bind PE section — the reliable indicator of SteamStub DRM.
    private static HashSet<string> FindMatchingDirectories(
        string root, CancellationToken ct)
    {
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> exes;
        try
        {
            exes = Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Cannot enumerate files in {Root}", root);
            return matched;
        }

        foreach (var exe in exes)
        {
            ct.ThrowIfCancellationRequested();

            if (HasBindSection(exe))
            {
                var dir = Path.GetDirectoryName(exe);
                if (dir is not null)
                    matched.Add(dir);
            }
        }

        return matched;
    }

    // Returns true if the PE file at filePath contains a section named .bind,
    // which indicates SteamStub DRM protection.
    // Only the DOS header, PE signature, COFF header, and section table are read;
    // the file body is never touched.
    private static bool HasBindSection(string filePath)
    {
        try
        {
            using var fs = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 512,
                FileOptions.None);

            using var br = new BinaryReader(fs);

            if (br.ReadUInt16() != 0x5A4D)
                return false;

            fs.Seek(0x3C, SeekOrigin.Begin);
            uint peOffset = br.ReadUInt32();

            if (peOffset < 0x40)
                return false;

            fs.Seek(peOffset, SeekOrigin.Begin);
            if (br.ReadUInt32() != 0x00004550)
                return false;

            br.ReadUInt16();
            ushort numberOfSections = br.ReadUInt16();
            br.ReadUInt32();
            br.ReadUInt32();
            br.ReadUInt32();
            ushort sizeOfOptionalHeader = br.ReadUInt16();
            br.ReadUInt16();

            fs.Seek(sizeOfOptionalHeader, SeekOrigin.Current);

            var nameBuf = new byte[8];
            for (int i = 0; i < numberOfSections; i++)
            {
                fs.ReadExactly(nameBuf, 0, 8);
                fs.Seek(32, SeekOrigin.Current);

                if (nameBuf[0] == '.' &&
                    nameBuf[1] == 'b' &&
                    nameBuf[2] == 'i' &&
                    nameBuf[3] == 'n' &&
                    nameBuf[4] == 'd' &&
                    nameBuf[5] == 0 &&
                    nameBuf[6] == 0 &&
                    nameBuf[7] == 0)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read PE headers from {File}", filePath);
            return false;
        }
    }

    // ── Download & extract ────────────────────────────────────────────────────

    private static async Task<string?> DownloadAndExtractDllAsync(
        string tempDir, CancellationToken ct)
    {
        using var client = HttpClientFactory.CreateHttpClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        var zipUrl = await ResolveLatestZipUrlAsync(client, ct).ConfigureAwait(false);
        if (zipUrl is null) return null;

        var zipPath = Path.Combine(tempDir, "steam-stubbed.zip");
        Log.Information("Downloading SteamStub release from {Url}", zipUrl);

        client.DefaultRequestHeaders.Accept.Clear();

        using var response = await client
            .GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using (var fs = File.Create(zipPath))
        await using (var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await src.CopyToAsync(fs, ct).ConfigureAwait(false);

        var extractDir = Path.Combine(tempDir, "extracted");
        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

        var dll = Directory
            .EnumerateFiles(extractDir, DllName, SearchOption.AllDirectories)
            .FirstOrDefault();

        if (dll is null)
            Log.Error("{Dll} not found in the downloaded archive", DllName);

        return dll;
    }

    private static async Task<string?> ResolveLatestZipUrlAsync(
        HttpClient client, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GitHubApiUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            var assets = doc.RootElement.GetProperty("assets");
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? string.Empty;
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    return asset.GetProperty("browser_download_url").GetString();
            }

            Log.Error("No .zip asset found in latest SteamStub release");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to resolve latest SteamStub release URL");
            return null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void DeleteDirectoryBestEffort(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not clean up SteamStub temp dir {Dir}", dir);
        }
    }
}
