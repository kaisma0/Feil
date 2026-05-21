using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Feil.Models;

namespace Feil.Services;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PersistedHistoryState))]
internal sealed partial class HistoryStateJsonContext : JsonSerializerContext { }

public static class HistoryStateService
{
    private const int CurrentSchemaVersion = 1;
    private static readonly object _syncLock = new();

    private static readonly string HistoryPath = Path.Combine(
        AppEnvironmentService.GetAppDataFolder(), "history.json");

    public static PersistedHistoryState? Load()
    {
        lock (_syncLock)
        {
            try
            {
                if (!File.Exists(HistoryPath)) return null;

                var json = File.ReadAllText(HistoryPath);
                var state = JsonSerializer.Deserialize(json, HistoryStateJsonContext.Default.PersistedHistoryState);
                return state?.SchemaVersion == CurrentSchemaVersion ? state : null;
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Failed to load history state");
                return null;
            }
        }
    }

    public static void Save(PersistedHistoryState state)
    {
        lock (_syncLock)
        {
            try
            {
                var directory = Path.GetDirectoryName(HistoryPath)!;
                Directory.CreateDirectory(directory);

                state.SchemaVersion = CurrentSchemaVersion;
                state.SavedAt = DateTimeOffset.UtcNow;

                var tempPath = HistoryPath + ".tmp";
                var bytes = JsonSerializer.SerializeToUtf8Bytes(state, HistoryStateJsonContext.Default.PersistedHistoryState);

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

                File.Move(tempPath, HistoryPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed to save history state");
                // Fail silently on save error to avoid crashing the app
            }
        }
    }
}
