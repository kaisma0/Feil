using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using Feil.ViewModels;
using Feil.Views;
using Ursa.Controls;

namespace Feil.Services.Achievements;

// Orchestrates Steam achievement schema generation.
// Attempts silent auth with saved credentials first, falls back to UI dialog.
public static class StatsSchemaService
{
    private const int MaxNoSchemaStreak = 6;
    private const int OwnerQueryDelayMs = 200;

    // Single entry point for triggering schema generation.
    // Handles silent auth and dialog fallback. Safe to call fire-and-forget.
    public static async Task TriggerAsync(uint appId)
    {
        try
        {
            await GenerateForAppAsync(appId,
                showSignInDialog: () => ShowSignInDialogAsync(appId));
        }
        catch (Exception ex)
        {
            Trace.TraceError($"[Feil] Schema generation trigger failed for {appId}: {ex.Message}");
        }
    }

    // Core generation logic — tries silent auth, then falls back to the provided dialog callback.
    private static async Task GenerateForAppAsync(
        uint appId,
        Func<Task>? showSignInDialog = null)
    {
        var statsDir = ResolveSteamStatsDirectory();
        if (statsDir == null)
        {
            Trace.TraceWarning(
                "[Feil] Cannot resolve Steam stats directory — Steam installation not found");
            return;
        }

        // Try silent auth with saved credentials first
        var creds = SteamCredentialStore.Load();
        if (creds?.RefreshToken != null)
        {
            var silentResult = await TrySilentGenerateAsync(
                appId, statsDir, creds.Username!, creds.RefreshToken);
            if (silentResult)
            {
                Trace.TraceInformation(
                    $"[Feil] Schema generated silently for app {appId}");
                return;
            }

            // Token expired — delete stale credentials
            SteamCredentialStore.Delete();
        }

        // Need UI — show sign-in dialog if callback is available.
        // The dialog handles auth + schema generation internally.
        if (showSignInDialog == null)
        {
            Trace.TraceWarning(
                "[Feil] No sign-in dialog available for schema generation");
            return;
        }

        await showSignInDialog();
    }

    // Attempt to connect silently with a saved refresh token and generate the schema.
    private static async Task<bool> TrySilentGenerateAsync(
        uint appId, string statsDir, string username, string refreshToken)
    {
        using var client = new SteamStatsClient();
        try
        {
            bool connected = await client.ConnectAndLogOnAsync(
                username, null, refreshToken);
            if (!connected) return false;

            return await FetchAndSaveSchemaAsync(appId, statsDir, client);
        }
        catch
        {
            return false;
        }
        finally
        {
            client.Disconnect();
        }
    }

    // Iterate through owner IDs to find a valid schema, then save it.
    internal static async Task<bool> FetchAndSaveSchemaAsync(
        uint appId, string statsDir, SteamStatsClient client,
        IProgress<SchemaProgress>? progress = null)
    {
        var owners = TopOwnerIds.Ids;
        int noSchemaCount = 0;

        for (int i = 0; i < owners.Length; i++)
        {
            ulong ownerId = owners[i];
            progress?.Report(new SchemaProgress(i, owners.Length, noSchemaCount));

            var response = await client.GetStatsSchemaAsync(appId, ownerId);
            if (response == null) continue;

            if (response.eresult == 2 && response.crc_stats == 0)
            {
                noSchemaCount++;
                if (noSchemaCount >= MaxNoSchemaStreak)
                {
                    Trace.TraceWarning(
                        $"[Feil] No schema available for app {appId} " +
                        $"({MaxNoSchemaStreak} consecutive 'no schema' responses)");
                    return false;
                }
            }
            else if (response.schema is { Length: > 0 })
            {
                var accountId = client.CurrentUser?.AccountID ?? 0;
                SaveSchemaFile(statsDir, appId, response.schema, accountId);
                Trace.TraceInformation(
                    $"[Feil] Found schema for app {appId} using owner " +
                    $"{ownerId} ({i + 1}/{owners.Length})");
                return true;
            }
            else
            {
                noSchemaCount = 0;
            }

            await Task.Delay(OwnerQueryDelayMs);
        }

        Trace.TraceWarning(
            $"[Feil] No schema found for app {appId} after checking {owners.Length} owners");
        return false;
    }

    private static void SaveSchemaFile(string statsDir, uint appId, byte[] schemaData, uint accountId)
    {
        Directory.CreateDirectory(statsDir);

        // 1. Save the schema file
        string schemaPath = Path.Combine(statsDir, $"UserGameStatsSchema_{appId}.bin");
        File.WriteAllBytes(schemaPath, schemaData);
        Trace.TraceInformation(
            $"[Feil] Saved schema to {schemaPath} ({schemaData.Length} bytes)");

        // 2. Use settings override if configured
        var settings = SettingsService.Load();
        if (settings.SteamAccountId != 0)
            accountId = settings.SteamAccountId;

        // 3. Create the UserGameStats file if it doesn't already exist
        if (accountId != 0)
        {
            string statsFilePath = Path.Combine(statsDir, $"UserGameStats_{accountId}_{appId}.bin");
            if (!File.Exists(statsFilePath))
            {
                // Minimal valid stats file
                byte[] minimalStats =
                [
                    0x00, 0x63, 0x61, 0x63, 0x68, 0x65, 0x00,             
                    0x02, 0x63, 0x72, 0x63, 0x00, 0x00, 0x00, 0x00, 0x00, 
                    0x02, 0x50, 0x65, 0x6E, 0x64, 0x69, 0x6E, 0x67,       
                    0x43, 0x68, 0x61, 0x6E, 0x67, 0x65, 0x73, 0x00,       
                    0x00, 0x00, 0x00, 0x00,                               
                    0x08, 0x08,                                           
                ];
                File.WriteAllBytes(statsFilePath, minimalStats);
                Trace.TraceInformation(
                    $"[Feil] Created stats file: {statsFilePath}");
            }
        }
    }

    internal static string? ResolveSteamStatsDirectory()
    {
        return DefaultInstallPathService.GetSteamStatsPath();
    }

    private static async Task ShowSignInDialogAsync(uint appId)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var vm = new SteamSignInDialogViewModel(appId);
            var view = new SteamSignInDialog { DataContext = vm };
            await OverlayDialog.ShowCustomAsync<bool>(view, vm);
        });
    }
}

// Progress report during schema fetching.
public readonly record struct SchemaProgress(
    int CheckedOwners,
    int TotalOwners,
    int NoSchemaStreak);

