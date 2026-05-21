using System;
using Serilog;
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
            if (string.IsNullOrWhiteSpace(creds?.Username)) return null;
            
            Log.Information("Successfully loaded Steam credentials for user {Username}", creds.Username);
            return creds;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load Steam credentials");
            return null;
        }
    }

    public static void Save(SteamCredentials credentials)
    {
        try
        {
            var dir = Path.GetDirectoryName(CredentialPath);
            if (dir != null) Directory.CreateDirectory(dir);
            
            Log.Information("Saving Steam credentials for user {Username}", credentials.Username);
            var json = JsonSerializer.Serialize(credentials, SteamCredentialsJsonContext.Default.SteamCredentials);
            File.WriteAllText(CredentialPath, json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not save Steam credentials");
        }
    }

    public static void Delete()
    {
        try
        {
            Log.Information("Deleting saved Steam credentials.");
            if (File.Exists(CredentialPath)) File.Delete(CredentialPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete Steam credentials");
        }
    }
}
