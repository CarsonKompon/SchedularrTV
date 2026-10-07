using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;

namespace SchedularrTV.News;

/// <summary>
/// The map's tiles, fetched through the plugin: OpenStreetMap's (kept on disk for a week, as its tile policy asks, and
/// fetched with a proper User-Agent), RainViewer's latest radar, and TomTom's traffic flow (so the key never reaches the
/// page). Radar and traffic are kept in memory for a few minutes.
/// </summary>
public sealed class NewsTiles(NewsSources sources, NewsSettings settings, IHttpClientFactory httpClients, string dataDirectory, ILogger<NewsTiles> logger)
{
    private readonly string _folder = Path.Combine(dataDirectory, "tiles");
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, byte[] Image)> _memory = new(StringComparer.Ordinal);

    public async Task<IResult> ServeAsync(string layer, int z, int x, int y, CancellationToken cancellationToken)
    {
        int count = 1 << Math.Clamp(z, 0, 19);
        if (z is < 1 or > 18 || x < 0 || y < 0 || x >= count || y >= count)
        {
            return Results.NotFound();
        }

        try
        {
            byte[]? image = layer switch
            {
                "osm" => await StreetsAsync(z, x, y, cancellationToken).ConfigureAwait(false),
                "radar" => await RemembersAsync($"radar|{z}/{x}/{y}", async () =>
                    (await sources.RadarTemplateAsync(cancellationToken).ConfigureAwait(false))
                        .Replace("{z}", $"{z}").Replace("{x}", $"{x}").Replace("{y}", $"{y}"), cancellationToken).ConfigureAwait(false),
                "traffic" when settings.TomTomKey is { } key => await RemembersAsync($"traffic|{z}/{x}/{y}", () => Task.FromResult(
                    $"https://api.tomtom.com/traffic/map/4/tile/flow/relative0/{z}/{x}/{y}.png?tileSize=256&key={Uri.EscapeDataString(key)}"), cancellationToken).ConfigureAwait(false),
                _ => null
            };
            return image is null ? Results.NotFound() : Results.File(image, "image/png");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException or System.Text.Json.JsonException)
        {
            logger.LogDebug("News: couldn't get the {Layer} tile {Z}/{X}/{Y}: {Message}", layer, z, x, y, ex.Message);
            return Results.NotFound();
        }
    }

    private async Task<byte[]> StreetsAsync(int z, int x, int y, CancellationToken cancellationToken)
    {
        string path = Path.Combine(_folder, "osm", $"{z}", $"{x}", $"{y}.png");
        if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromDays(7))
        {
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }

        using HttpClient http = httpClients.CreateClient(NewsSources.ClientName);
        byte[] image = await http.GetByteArrayAsync($"https://tile.openstreetmap.org/{z}/{x}/{y}.png", cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, image, cancellationToken).ConfigureAwait(false);
        return image;
    }

    private async Task<byte[]> RemembersAsync(string key, Func<Task<string>> address, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (_memory.TryGetValue(key, out var kept) && now - kept.At < TimeSpan.FromMinutes(5))
        {
            return kept.Image;
        }

        using HttpClient http = httpClients.CreateClient(NewsSources.ClientName);
        byte[] image = await http.GetByteArrayAsync(await address().ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        _memory[key] = (now, image);
        if (_memory.Count > 400)
        {
            foreach (var stale in _memory.Where(entry => now - entry.Value.At > TimeSpan.FromMinutes(10)).ToList())
            {
                _memory.TryRemove(stale.Key, out _);
            }
        }

        return image;
    }
}
