using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Feil.Services.Achievements;

// Persisted Steam login state (username + refresh token).
// Stored alongside the main settings in ~/.Feil/steam_credentials.json.
public sealed class SteamCredentials
{
    public string? Username { get; set; }
    public string? RefreshToken { get; set; }
}

[JsonSerializable(typeof(SteamCredentials))]
internal sealed partial class SteamCredentialsJsonContext : JsonSerializerContext { }

public static class SteamCredentialStore
{
    private static readonly string CredentialPath = Path.Combine(
        AppEnvironmentService.GetAppDataFolder(), "steam_credentials.json");

    public static SteamCredentials? Load()
    {
        try
        {
            if (!File.Exists(CredentialPath)) return null;
            var json = File.ReadAllText(CredentialPath);
            var creds = JsonSerializer.Deserialize(json, SteamCredentialsJsonContext.Default.SteamCredentials);
            // Only return if we have at least a username
            return string.IsNullOrWhiteSpace(creds?.Username) ? null : creds;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(SteamCredentials credentials)
    {
        try
        {
            var dir = Path.GetDirectoryName(CredentialPath);
            if (dir != null) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(credentials, SteamCredentialsJsonContext.Default.SteamCredentials);
            File.WriteAllText(CredentialPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"[Feil] Could not save Steam credentials: {ex.Message}");
        }
    }

    public static void Delete()
    {
        try
        {
            if (File.Exists(CredentialPath)) File.Delete(CredentialPath);
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}
