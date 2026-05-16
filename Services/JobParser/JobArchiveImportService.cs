using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Feil.Models;
using Feil.Services.Steam;

namespace Feil.Services.JobParser;

public sealed class JobArchiveImportService
{
    private const string MetadataDirectoryName = AppEnvironmentService.AppFolderName;
    private const string ImportedJobDirectoryName = "job";

    public async Task<PreparedJobArchive?> PrepareAsync(string zipPath, string installBaseDirectory)
    {
        var parser = new LuaJobParser();

        using var archive = ZipFile.OpenRead(zipPath);
        var luaEntry = archive.Entries.FirstOrDefault(entry =>
            entry.FullName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));

        if (luaEntry is null)
        {
            return null;
        }

        string[] lines;
        using (var reader = new StreamReader(luaEntry.Open()))
        {
            var content = await reader.ReadToEndAsync();
            lines = content.Split(["\r\n", "\n"], StringSplitOptions.None);
        }

        var job = parser.Parse(lines);
        var gameName = await ResolveGameNameAsync(job.AppId);
        var installDirectory = ResolveInstallDirectory(installBaseDirectory, gameName, job.AppId);
        var jobDirectory = GetImportedJobDirectory(installDirectory);
        var metadataDirectory = Path.GetDirectoryName(jobDirectory)!;
        Directory.CreateDirectory(metadataDirectory);

        var temporaryJobDirectory = Path.Combine(metadataDirectory, $"job-import-{Guid.NewGuid():N}");
        try
        {
            ZipFile.ExtractToDirectory(zipPath, temporaryJobDirectory, overwriteFiles: true);
        }
        catch
        {
            DeleteDirectoryBestEffort(temporaryJobDirectory);
            throw;
        }

        var luaFilePath = GetExtractedEntryPath(temporaryJobDirectory, luaEntry.FullName);
        if (!File.Exists(luaFilePath))
        {
            luaFilePath = Directory
                .GetFiles(temporaryJobDirectory, "*.lua", SearchOption.AllDirectories)
                .FirstOrDefault();
        }

        if (luaFilePath is null)
        {
            DeleteDirectoryBestEffort(temporaryJobDirectory);
            return null;
        }

        return new PreparedJobArchive(
            job,
            gameName,
            installDirectory,
            jobDirectory,
            temporaryJobDirectory,
            luaFilePath);
    }

    public void Commit(PreparedJobArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        var metadataDirectory = Path.GetDirectoryName(archive.JobDirectory)!;
        Directory.CreateDirectory(metadataDirectory);

        var backupJobDirectory = Directory.Exists(archive.JobDirectory)
            ? Path.Combine(metadataDirectory, $"job-backup-{Guid.NewGuid():N}")
            : null;
        var installedNewJobDirectory = false;

        try
        {
            if (backupJobDirectory is not null)
            {
                Directory.Move(archive.JobDirectory, backupJobDirectory);
            }

            Directory.Move(archive.TemporaryJobDirectory, archive.JobDirectory);
            installedNewJobDirectory = true;
        }
        catch
        {
            if (!installedNewJobDirectory && Directory.Exists(archive.JobDirectory))
            {
                DeleteDirectoryBestEffort(archive.JobDirectory);
            }

            if (!installedNewJobDirectory &&
                backupJobDirectory is not null &&
                Directory.Exists(backupJobDirectory) &&
                !Directory.Exists(archive.JobDirectory))
            {
                Directory.Move(backupJobDirectory, archive.JobDirectory);
            }

            throw;
        }

        if (backupJobDirectory is not null && Directory.Exists(backupJobDirectory))
        {
            try
            {
                Directory.Delete(backupJobDirectory, recursive: true);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"[Feil] Imported job backup cleanup failed for {backupJobDirectory}: {ex.Message}");
            }
        }
    }

    public static string ResolveInstallDirectory(string installBaseDirectory, string gameName, int appId)
    {
        var resolvedGameName = string.IsNullOrWhiteSpace(gameName) ? $"App {appId}" : gameName;
        var safeGameName = string.Join("_", resolvedGameName.Split(Path.GetInvalidFileNameChars()));

        return Path.Combine(installBaseDirectory, safeGameName);
    }

    private static string GetImportedJobDirectory(string installDirectory) =>
        Path.Combine(installDirectory, MetadataDirectoryName, ImportedJobDirectoryName);

    private static string GetExtractedEntryPath(string temporaryJobDirectory, string entryName)
    {
        var pathParts = entryName.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return Path.Combine([temporaryJobDirectory, .. pathParts]);
    }

    private static async Task<string> ResolveGameNameAsync(int appId)
    {
        var gameName = await SteamAppInfoService.GetGameNameAsync(appId);
        return string.IsNullOrWhiteSpace(gameName) ? $"App {appId}" : gameName;
    }

    public static void DeleteDirectoryBestEffort(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Feil] Could not delete temporary job directory {directory}: {ex.Message}");
        }
    }
}

public sealed record PreparedJobArchive(
    DownloadJob Job,
    string GameName,
    string InstallDirectory,
    string JobDirectory,
    string TemporaryJobDirectory,
    string LuaFilePath);
