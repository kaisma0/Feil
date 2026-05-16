using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Feil.Core;

namespace Feil.Services.Steam;

public sealed record SteamAppMetadata(string? Name, string? HeaderImageUrl);

public static class SteamAppInfoService
{
    private static readonly ConcurrentDictionary<int, SteamAppMetadata> _metadataCache = new();
    private static readonly ConcurrentDictionary<int, JsonElement> _steamCmdCache = new();
    private static readonly ConcurrentDictionary<int, Dictionary<int, DepotMetadata>> _depotMetadataCache = new();

    public sealed record DepotMetadata(string? Name, string? OsList);

    public static string GetDefaultHeaderImageUrl(int appId) =>
        $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg";

    public static async Task<SteamAppMetadata?> GetMetadataAsync(int appId)
    {
        if (_metadataCache.TryGetValue(appId, out var cachedMetadata))
        {
            return cachedMetadata;
        }

        try
        {
            using var client = HttpClientFactory.CreateHttpClient();
            client.Timeout = TimeSpan.FromSeconds(5);

            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}";
            var response = await client.GetStringAsync(url);

            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;

            if (!root.TryGetProperty(appId.ToString(), out var appElement))
                return null;

            if (!appElement.TryGetProperty("success", out var successElement) || !successElement.GetBoolean())
                return null;

            if (!appElement.TryGetProperty("data", out var dataElement))
                return null;

            string? name = null;
            if (dataElement.TryGetProperty("name", out var nameElement))
                name = nameElement.GetString();

            string? headerImageUrl = null;
            if (dataElement.TryGetProperty("header_image", out var headerImageElement))
                headerImageUrl = headerImageElement.GetString();

            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(headerImageUrl))
                return null;

            var metadata = new SteamAppMetadata(
                string.IsNullOrWhiteSpace(name) ? null : name,
                string.IsNullOrWhiteSpace(headerImageUrl) ? null : headerImageUrl);

            _metadataCache[appId] = metadata;
            return metadata;
        }
        catch
        {
            // Ignore errors (e.g. network failure, rate limiting) and return null
        }

        return null;
    }

    private static async Task<JsonElement?> GetSteamCmdAppInfoAsync(int appId)
    {
        if (_steamCmdCache.TryGetValue(appId, out var cached))
            return cached;

        try
        {
            using var client = HttpClientFactory.CreateHttpClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var url = $"https://api.steamcmd.net/v1/info/{appId}";
            var response = await client.GetStringAsync(url);
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;

            if (root.TryGetProperty("data", out var data) && data.TryGetProperty(appId.ToString(), out var appElement))
            {
                var clone = appElement.Clone();
                _steamCmdCache[appId] = clone;
                return clone;
            }
        }
        catch { }

        return null;
    }

    public static async Task<string?> GetGameNameAsync(int appId)
    {
        var name = (await GetMetadataAsync(appId))?.Name;
        if (!string.IsNullOrWhiteSpace(name)) return name;

        var appElement = await GetSteamCmdAppInfoAsync(appId);
        if (appElement.HasValue &&
            appElement.Value.TryGetProperty("common", out var commonElement) &&
            commonElement.TryGetProperty("name", out var nameElement))
        {
            var fallbackName = nameElement.GetString();
            if (!string.IsNullOrWhiteSpace(fallbackName))
            {
                if (_metadataCache.TryGetValue(appId, out var existing))
                    _metadataCache[appId] = existing with { Name = fallbackName };
                else
                    _metadataCache[appId] = new SteamAppMetadata(fallbackName, null);

                return fallbackName;
            }
        }

        return null;
    }

    public static async Task<string?> GetHeaderImageUrlAsync(int appId)
    {
        return (await GetMetadataAsync(appId))?.HeaderImageUrl;
    }

    public static async Task<Dictionary<int, DepotMetadata>> GetDepotMetadataAsync(int appId)
    {
        if (_depotMetadataCache.TryGetValue(appId, out var cached))
            return cached;

        var result = new Dictionary<int, DepotMetadata>();
        var appElement = await GetSteamCmdAppInfoAsync(appId);

        if (appElement.HasValue &&
            appElement.Value.TryGetProperty("depots", out var depotsElement) &&
            depotsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var depotProp in depotsElement.EnumerateObject())
            {
                if (!int.TryParse(depotProp.Name, out var depotId)) continue;

                string? name = null;
                if (depotProp.Value.ValueKind == JsonValueKind.Object && depotProp.Value.TryGetProperty("name", out var nameElement))
                    name = nameElement.GetString();

                string? oslist = null;
                if (depotProp.Value.ValueKind == JsonValueKind.Object &&
                    depotProp.Value.TryGetProperty("config", out var configElement) &&
                    configElement.ValueKind == JsonValueKind.Object &&
                    configElement.TryGetProperty("oslist", out var oslistElement))
                {
                    oslist = oslistElement.GetString();
                }

                result[depotId] = new DepotMetadata(name, oslist);
            }
        }

        _depotMetadataCache[appId] = result;
        return result;
    }
}
