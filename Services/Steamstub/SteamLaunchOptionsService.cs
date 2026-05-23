using Feil.Services;
using Serilog;
using SteamKit2;

namespace Feil.Services.Steamstub;

// Reads and writes Steam launch options in localconfig.vdf.
// Handles native installs, XDG overrides, and Flatpak.
public static class SteamLaunchOptionsService
{
    private const string WinmmOverride = "WINEDLLOVERRIDES=\"winmm=n,b\"";
    private const string WinmmDll = "winmm=n,b";

    // Sets the winmm WINEDLLOVERRIDES launch option for appId
    // in every localconfig.vdf found on this system.
    public static void SetWinmmOverride(int appId)
    {
        var paths = DefaultInstallPathService.EnumerateLocalConfigPaths().ToList();

        if (paths.Count == 0)
        {
            Log.Warning("No localconfig.vdf found — cannot set launch options for AppId {AppId}", appId);
            return;
        }

        foreach (var path in paths)
        {
            try
            {
                ApplyToFile(path, appId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to update launch options in {Path}", path);
            }
        }
    }

    // ── VDF edit ─────────────────────────────────────────────────────────────

    private static void ApplyToFile(string path, int appId)
    {
        var kv = new KeyValue();
        if (!kv.ReadFileAsText(path))
        {
            Log.Warning("Could not parse localconfig.vdf at {Path}", path);
            return;
        }

        var appsNode = kv["Software"]["Valve"]["Steam"]["Apps"];
        if (appsNode == KeyValue.Invalid)
        {
            Log.Warning("Could not locate Apps node in localconfig.vdf at {Path}", path);
            return;
        }

        var appIdStr = appId.ToString();
        var appNode = appsNode.Children.FirstOrDefault(
            c => string.Equals(c.Name, appIdStr, StringComparison.OrdinalIgnoreCase));

        if (appNode is null)
        {
            appNode = new KeyValue(appIdStr);
            appsNode.Children.Add(appNode);
        }

        var launchOpt = appNode.Children.FirstOrDefault(
            c => string.Equals(c.Name, "LaunchOptions", StringComparison.OrdinalIgnoreCase));

        if (launchOpt is null)
        {
            launchOpt = new KeyValue("LaunchOptions");
            appNode.Children.Add(launchOpt);
        }

        var existing = launchOpt.Value ?? string.Empty;
        var merged = MergeLaunchOption(existing);

        if (merged == existing)
        {
            Log.Debug("Launch option already set in {Path} for AppId {AppId}", path, appId);
            return;
        }

        launchOpt.Value = merged;
        kv.SaveToFile(path, asBinary: false);
        Log.Information("Updated Steam launch options in {Path} for AppId {AppId}: {Merged}", path, appId, merged);
    }

    // ── Merge logic ───────────────────────────────────────────────────────────

    // Smart-merges the winmm WINEDLLOVERRIDES env var into an existing launch
    // options string, handling all common Steam launch option forms.
    internal static string MergeLaunchOption(string existing)
    {
        existing = existing.Trim();

        if (string.IsNullOrEmpty(existing))
            return $"{WinmmOverride} %command%";

        if (existing.Contains(WinmmOverride, StringComparison.Ordinal))
            return existing;

        if (TryInjectIntoDllOverrides(existing, out var injected))
            return injected;

        int cmdIdx = existing.IndexOf("%command%", StringComparison.Ordinal);
        if (cmdIdx >= 0)
        {
            var before = existing[..cmdIdx].TrimEnd();
            var rest = existing[cmdIdx..];
            return string.IsNullOrEmpty(before)
                ? $"{WinmmOverride} {rest}"
                : $"{before} {WinmmOverride} {rest}";
        }

        return $"{WinmmOverride} %command% {existing}".TrimEnd();
    }

    // Attempts to inject winmm=n,b into an existing WINEDLLOVERRIDES= value in existing.
    // Returns true if a WINEDLLOVERRIDES token was found (whether or not injection
    // was needed); false if no such token exists.
    // When returning true, result holds the (possibly unchanged) string.
    private static bool TryInjectIntoDllOverrides(string existing, out string result)
    {
        result = existing;

        const string prefix = "WINEDLLOVERRIDES=";
        int start = existing.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return false;

        int valueStart = start + prefix.Length;
        bool quoted = valueStart < existing.Length && existing[valueStart] == '"';

        int valueEnd;
        string rawValue;

        if (quoted)
        {
            valueEnd = existing.IndexOf('"', valueStart + 1);
            if (valueEnd < 0) return false;
            rawValue = existing[(valueStart + 1)..valueEnd];
            valueEnd++;
        }
        else
        {
            valueEnd = existing.IndexOf(' ', valueStart);
            if (valueEnd < 0) valueEnd = existing.Length;
            rawValue = existing[valueStart..valueEnd];
        }

        if (rawValue.Contains("winmm", StringComparison.OrdinalIgnoreCase))
            return true;

        var newValue = string.IsNullOrEmpty(rawValue) ? WinmmDll : $"{WinmmDll};{rawValue}";

        result = existing[..start]
               + prefix
               + $"\"{newValue}\""
               + existing[valueEnd..];
        return true;
    }
}
