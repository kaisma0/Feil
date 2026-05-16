using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Feil.Models;

namespace Feil.Services;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PersistedQueueState))]
internal sealed partial class QueueStateJsonContext : JsonSerializerContext { }

public static class QueueStateService
{
    private const int CurrentSchemaVersion = 1;

    private static readonly string QueuePath = Path.Combine(
        AppEnvironmentService.GetAppDataFolder(), "queue.json");

    public static PersistedQueueState? Load()
    {
        try
        {
            if (!File.Exists(QueuePath)) return null;

            var json = File.ReadAllText(QueuePath);
            var state = JsonSerializer.Deserialize(json, QueueStateJsonContext.Default.PersistedQueueState);
            return state?.SchemaVersion == CurrentSchemaVersion ? state : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(PersistedQueueState state)
    {
        var directory = Path.GetDirectoryName(QueuePath)!;
        Directory.CreateDirectory(directory);

        state.SchemaVersion = CurrentSchemaVersion;
        state.SavedAt = DateTimeOffset.UtcNow;

        var tempPath = QueuePath + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, QueueStateJsonContext.Default.PersistedQueueState);

        using (var stream = new FileStream(
                   tempPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 4096,
                   FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, QueuePath, overwrite: true);
    }
}
