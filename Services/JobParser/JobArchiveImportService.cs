using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Feil.Models;
using Feil.Services.Steam;
using Serilog;

namespace Feil.Services.JobParser;

public sealed class JobArchiveImportService
{
    private const string MetadataDirectoryName = AppEnvironmentService.AppFolderName;
    private const string ImportedJobDirectoryName = "job";

    public async Task<PreparedJobArchive?> PrepareAsync(string zipPath, string installBaseDirectory)
    {
        Log.Information("Preparing job archive from {ZipPath}", zipPath);
        var parser = new LuaJobParser();

        using var archive = ZipFile.OpenRead(zipPath);
        var luaEntry = archive.Entries.FirstOrDefault(entry =>
            entry.FullName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));

        if (luaEntry is null)
        {
            Log.Warning("Job archive {ZipPath} contains no .lua file", zipPath);
            return null;
        }

        string[] lines;
        using (var reader = new StreamReader(luaEntry.Open()))
        {
            var content = await reader.ReadToEndAsync();
            lines = content.Split(["\r\n", "\n"], StringSplitOptions.None);
        }

        var job = parser.Parse(lines);
        Log.Debug("Parsed job from archive for AppId {AppId} with {DepotCount} depots", job.AppId, job.Depots.Count);
        
        var gameName = await ResolveGameNameAsync(job.AppId);
        var installDirectory = await ResolveInstallDirectoryAsync(installBaseDirectory, job.AppId, gameName);
        var jobDirectory = GetImportedJobDirectory(installDirectory);
        var metadataDirectory = Path.GetDirectoryName(jobDirectory)!;
        Directory.CreateDirectory(metadataDirectory);

        var temporaryJobDirectory = Path.Combine(metadataDirectory, $"job-import-{Guid.NewGuid():N}");
        try
        {
            ZipFile.ExtractToDirectory(zipPath, temporaryJobDirectory, overwriteFiles: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to extract job archive to {TemporaryJobDirectory}", temporaryJobDirectory);
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
        Log.Information("Committing job archive {JobDirectory}", archive.JobDirectory);

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
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to commit job archive {JobDirectory}, attempting rollback", archive.JobDirectory);
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
                Log.Warning(ex, "Imported job backup cleanup failed for {BackupJobDirectory}", backupJobDirectory);
            }
        }
    }

    public static async Task<string> ResolveInstallDirectoryAsync(
        string installBaseDirectory,
        int appId,
        string? fallbackDirectoryName = null)
    {
        Log.Debug("Resolving install directory for App {AppId}", appId);
        var installDirectoryName = await SteamAppInfoService.GetConfiguredInstallDirectoryNameAsync(appId);
        if (string.IsNullOrWhiteSpace(installDirectoryName))
        {
            installDirectoryName = string.IsNullOrWhiteSpace(fallbackDirectoryName)
                ? $"App {appId}"
                : fallbackDirectoryName;
        }

        return ResolveInstallDirectory(installBaseDirectory, installDirectoryName, appId);
    }

    public static string ResolveInstallDirectory(string installBaseDirectory, string directoryName, int appId)
    {
        var resolvedDirectoryName = string.IsNullOrWhiteSpace(directoryName) ? $"App {appId}" : directoryName;
        var safeDirectoryName = string.Join("_", resolvedDirectoryName.Split(Path.GetInvalidFileNameChars()));

        return Path.Combine(installBaseDirectory, safeDirectoryName);
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
            Log.Warning(ex, "Could not delete temporary job directory {Directory}", directory);
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
