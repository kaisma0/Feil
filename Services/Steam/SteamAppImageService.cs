using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Feil.Core;

namespace Feil.Services.Steam;

public static class SteamAppImageService
{
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> _imageCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static async Task<Bitmap?> GetGameImageAsync(int appId, string? imageUrl = null)
    {
        var resolvedUrl = string.IsNullOrWhiteSpace(imageUrl)
            ? SteamAppInfoService.GetDefaultHeaderImageUrl(appId)
            : imageUrl;

        if (string.IsNullOrWhiteSpace(resolvedUrl))
        {
            return null;
        }

        var imageTask = _imageCache.GetOrAdd(resolvedUrl, DownloadBitmapAsync);
        var bitmap = await imageTask;

        if (bitmap is null)
        {
            _imageCache.TryRemove(resolvedUrl, out _);
        }

        return bitmap;
    }

    private static async Task<Bitmap?> DownloadBitmapAsync(string imageUrl)
    {
        try
        {
            using var client = HttpClientFactory.CreateHttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            using var response = await client.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync();
            using var memoryStream = new MemoryStream();
            await responseStream.CopyToAsync(memoryStream);
            memoryStream.Position = 0;

            return new Bitmap(memoryStream);
        }
        catch
        {
            return null;
        }
    }
}
