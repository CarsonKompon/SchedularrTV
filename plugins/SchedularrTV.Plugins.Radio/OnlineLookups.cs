using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SchedularrTV.Radio;

/// <summary>What was found online for a song: a cover and lyrics, either possibly missing.</summary>
public sealed record OnlineSong(string? Art, string? Album, IReadOnlyList<LyricLine> Lyrics);

/// <summary>
/// Covers from the iTunes Search API and lyrics from LRCLIB (timed where it has them): both free and keyless. Answers
/// are kept for the plugin's lifetime, so each song is looked up once.
/// </summary>
public sealed class OnlineLookups(IHttpClientFactory httpClients, ILogger<OnlineLookups> logger)
{
    public const string ClientName = "radio-lookups";

    private readonly ConcurrentDictionary<string, Task<OnlineSong>> _songs = new(StringComparer.OrdinalIgnoreCase);

    public Task<OnlineSong> FindAsync(string artist, string title, TimeSpan? duration, CancellationToken cancellationToken) =>
        _songs.GetOrAdd($"{artist}\n{title}", _ => LookUpAsync(artist, title, duration, CancellationToken.None)).WaitAsync(cancellationToken);

    /// <summary>Only the lyrics, for a library song that has none of its own.</summary>
    public async Task<IReadOnlyList<LyricLine>> LyricsAsync(string artist, string title, string? album, TimeSpan? duration, CancellationToken cancellationToken)
    {
        try
        {
            using HttpClient http = httpClients.CreateClient(ClientName);
            Dictionary<string, string?> query = new()
            {
                ["artist_name"] = artist,
                ["track_name"] = title,
                ["album_name"] = album,
                ["duration"] = duration is { TotalSeconds: > 0 } length ? ((int)Math.Round(length.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture) : null
            };

            // An exact match first (needs the length); else the best search result.
            string url = "https://lrclib.net/api/get?" + Query(query);
            LrcLibTrack? track = query["duration"] is not null ? await GetAsync<LrcLibTrack>(http, url, cancellationToken).ConfigureAwait(false) : null;
            if (track is null)
            {
                List<LrcLibTrack>? found = await GetAsync<List<LrcLibTrack>>(http,
                    "https://lrclib.net/api/search?" + Query(new() { ["artist_name"] = artist, ["track_name"] = title }), cancellationToken).ConfigureAwait(false);
                track = found?.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.SyncedLyrics))
                        ?? found?.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.PlainLyrics));
            }

            return track is null ? [] : Lyrics.Parse(!string.IsNullOrWhiteSpace(track.SyncedLyrics) ? track.SyncedLyrics : track.PlainLyrics);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogDebug("No lyrics found online for {Artist} – {Title}: {Message}", artist, title, ex.Message);
            return [];
        }
    }

    private async Task<OnlineSong> LookUpAsync(string artist, string title, TimeSpan? duration, CancellationToken cancellationToken)
    {
        string? art = null;
        string? album = null;
        try
        {
            using HttpClient http = httpClients.CreateClient(ClientName);
            ItunesResults? results = await GetAsync<ItunesResults>(http,
                "https://itunes.apple.com/search?" + Query(new() { ["term"] = $"{artist} {title}".Trim(), ["entity"] = "song", ["limit"] = "5" }),
                cancellationToken).ConfigureAwait(false);
            ItunesSong? song = results?.Results.FirstOrDefault(candidate => Same(candidate.TrackName, title)) ?? results?.Results.FirstOrDefault();
            if (song?.ArtworkUrl100 is { Length: > 0 } small)
            {
                // The same picture in a size worth showing.
                art = small.Replace("100x100", "600x600", StringComparison.Ordinal);
                album = song.CollectionName;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogDebug("No cover found online for {Artist} – {Title}: {Message}", artist, title, ex.Message);
        }

        IReadOnlyList<LyricLine> lyrics = await LyricsAsync(artist, title, album, duration, cancellationToken).ConfigureAwait(false);
        return new OnlineSong(art, album, lyrics);
    }

    private static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Query(Dictionary<string, string?> values) =>
        string.Join('&', values.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)).Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value!)}"));

    private static async Task<T?> GetAsync<T>(HttpClient http, string url, CancellationToken cancellationToken) where T : class
    {
        using HttpResponseMessage response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(body, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private sealed class ItunesResults
    {
        [JsonPropertyName("results")]
        public List<ItunesSong> Results { get; set; } = [];
    }

    private sealed class ItunesSong
    {
        [JsonPropertyName("trackName")]
        public string? TrackName { get; set; }

        [JsonPropertyName("collectionName")]
        public string? CollectionName { get; set; }

        [JsonPropertyName("artworkUrl100")]
        public string? ArtworkUrl100 { get; set; }
    }

    private sealed class LrcLibTrack
    {
        [JsonPropertyName("plainLyrics")]
        public string? PlainLyrics { get; set; }

        [JsonPropertyName("syncedLyrics")]
        public string? SyncedLyrics { get; set; }
    }
}
