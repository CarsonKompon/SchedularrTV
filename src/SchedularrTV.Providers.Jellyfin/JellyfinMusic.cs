using System.Text.Json.Serialization;

namespace SchedularrTV.Providers.Jellyfin;

/// <summary>One song in the Jellyfin library.</summary>
public sealed record JellyfinTrack(
    string Id,
    string Title,
    IReadOnlyList<string> Artists,
    string? Album,
    string? AlbumId,
    string? AlbumArtist,
    TimeSpan Duration,
    int? Year,
    IReadOnlyList<string> Genres)
{
    public string Artist => Artists.Count > 0 ? string.Join(", ", Artists) : AlbumArtist ?? string.Empty;

    /// <summary>The item whose picture is the cover: the album's, else the song's own.</summary>
    public string ArtItemId => AlbumId ?? Id;
}

/// <summary>A named thing in the music library (a playlist, a genre or an artist), for pickers.</summary>
public sealed record JellyfinMusicChoice(string Id, string Name, int? Count = null);

/// <summary>One line of a song's lyrics; <see cref="Start"/> is null for unsynced lyrics.</summary>
public sealed record JellyfinLyricLine(string Text, TimeSpan? Start);

/// <summary>Which songs to take from the library: every one, or a playlist's, or some genres' or artists'.</summary>
/// <param name="PlaylistId">A playlist's songs, in its order unless shuffled.</param>
/// <param name="Genres">Songs in any of these genres.</param>
/// <param name="Artists">Songs by any of these artists.</param>
public sealed record JellyfinMusicQuery(string? PlaylistId = null, IReadOnlyList<string>? Genres = null, IReadOnlyList<string>? Artists = null, int Limit = 2000);

public sealed partial class JellyfinClient
{
    private const string TrackFields = "Genres";

    /// <summary>Songs matching the query, in random order (or a playlist's own order).</summary>
    public async Task<IReadOnlyList<JellyfinTrack>> GetTracksAsync(JellyfinMusicQuery query, bool shuffle, CancellationToken cancellationToken)
    {
        string? userId = _options.UserId ?? await FirstUserIdAsync(cancellationToken).ConfigureAwait(false);
        var parameters = new Dictionary<string, string?>
        {
            ["recursive"] = "true",
            ["includeItemTypes"] = "Audio",
            ["fields"] = TrackFields,
            ["enableImages"] = "false",
            ["isMissing"] = "false",
            ["limit"] = Math.Clamp(query.Limit, 1, 10000).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["sortBy"] = shuffle ? "Random" : query.PlaylistId is null ? "AlbumArtist,Album,ParentIndexNumber,IndexNumber,SortName" : null,
            ["parentId"] = query.PlaylistId,
            ["genres"] = query.Genres is { Count: > 0 } genres ? string.Join('|', genres) : null,
            ["artists"] = query.Artists is { Count: > 0 } artists ? string.Join('|', artists) : null,
            ["userId"] = userId
        };

        JellyfinAudioResponse? response = await GetAsync<JellyfinAudioResponse>(BuildUrl("/Items", parameters), cancellationToken).ConfigureAwait(false);
        return [.. (response?.Items ?? [])
            .Where(item => item.RunTimeTicks is > 0)
            .Select(item => new JellyfinTrack(
                item.Id,
                item.Name ?? "Untitled",
                item.Artists ?? [],
                item.Album,
                item.AlbumId,
                item.AlbumArtist,
                TimeSpan.FromTicks(item.RunTimeTicks ?? 0),
                item.ProductionYear,
                item.Genres ?? []))];
    }

    /// <summary>How many songs the library holds, without fetching them (Jellyfin counts them itself).</summary>
    public async Task<int> CountTracksAsync(CancellationToken cancellationToken)
    {
        string? userId = _options.UserId ?? await FirstUserIdAsync(cancellationToken).ConfigureAwait(false);
        var parameters = new Dictionary<string, string?>
        {
            ["recursive"] = "true",
            ["includeItemTypes"] = "Audio",
            ["isMissing"] = "false",
            ["enableImages"] = "false",
            ["enableTotalRecordCount"] = "true",
            ["limit"] = "0",
            ["userId"] = userId
        };

        JellyfinItemsResponse? response = await GetAsync<JellyfinItemsResponse>(BuildUrl("/Items", parameters), cancellationToken).ConfigureAwait(false);
        return response?.TotalRecordCount ?? 0;
    }

    /// <summary>The music playlists, for the station editor.</summary>
    public Task<IReadOnlyList<JellyfinMusicChoice>> GetMusicPlaylistsAsync(CancellationToken cancellationToken) =>
        ChoicesAsync("/Items", new Dictionary<string, string?> { ["recursive"] = "true", ["includeItemTypes"] = "Playlist", ["mediaTypes"] = "Audio" }, cancellationToken);

    /// <summary>The genres songs are in, for the station editor.</summary>
    public Task<IReadOnlyList<JellyfinMusicChoice>> GetMusicGenresAsync(CancellationToken cancellationToken) =>
        ChoicesAsync("/Genres", new Dictionary<string, string?> { ["includeItemTypes"] = "Audio,MusicAlbum", ["recursive"] = "true" }, cancellationToken);

    /// <summary>The artists, for the station editor.</summary>
    public Task<IReadOnlyList<JellyfinMusicChoice>> GetMusicArtistsAsync(CancellationToken cancellationToken) =>
        ChoicesAsync("/Artists/AlbumArtists", new Dictionary<string, string?> { ["recursive"] = "true" }, cancellationToken);

    private async Task<IReadOnlyList<JellyfinMusicChoice>> ChoicesAsync(string path, Dictionary<string, string?> parameters, CancellationToken cancellationToken)
    {
        parameters["userId"] = _options.UserId ?? await FirstUserIdAsync(cancellationToken).ConfigureAwait(false);
        parameters["enableImages"] = "false";
        parameters["limit"] = "5000";
        parameters["sortBy"] = "SortName";
        JellyfinAudioResponse? response = await GetAsync<JellyfinAudioResponse>(BuildUrl(path, parameters), cancellationToken).ConfigureAwait(false);
        return [.. (response?.Items ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .Select(item => new JellyfinMusicChoice(item.Id, item.Name!, item.ChildCount))];
    }

    /// <summary>A song's lyrics from Jellyfin (10.9 and later): timed lines when the lyrics are synced. Empty when it has none.</summary>
    public async Task<IReadOnlyList<JellyfinLyricLine>> GetLyricsAsync(string trackId, CancellationToken cancellationToken)
    {
        try
        {
            // No lyrics answers 404, as does a server too old to have them.
            JellyfinLyricsResponse? response = await GetIfFoundAsync<JellyfinLyricsResponse>(
                BuildUrl($"/Audio/{Uri.EscapeDataString(trackId)}/Lyrics", []), cancellationToken).ConfigureAwait(false);
            return [.. (response?.Lyrics ?? [])
                .Select(line => new JellyfinLyricLine(line.Text ?? string.Empty, line.Start is { } ticks ? TimeSpan.FromTicks(ticks) : null))];
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Where a song's file can be read in full (as it is, not transcoded), and the header that authorises it: given to
    /// ffmpeg as <c>-headers</c>, so the API key stays out of the address.
    /// </summary>
    public (string Url, string AuthorizationHeader) GetAudioStream(string trackId) =>
        (BuildUrl($"/Audio/{Uri.EscapeDataString(trackId)}/stream", new Dictionary<string, string?> { ["static"] = "true" }),
         $"Authorization: {Authorization()}");

    private sealed class JellyfinAudioResponse
    {
        [JsonPropertyName("Items")]
        public List<JellyfinAudioItem> Items { get; set; } = [];
    }

    private sealed class JellyfinAudioItem
    {
        [JsonPropertyName("Id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("Name")]
        public string? Name { get; set; }

        [JsonPropertyName("Album")]
        public string? Album { get; set; }

        [JsonPropertyName("AlbumId")]
        public string? AlbumId { get; set; }

        [JsonPropertyName("AlbumArtist")]
        public string? AlbumArtist { get; set; }

        [JsonPropertyName("Artists")]
        public List<string>? Artists { get; set; }

        [JsonPropertyName("RunTimeTicks")]
        public long? RunTimeTicks { get; set; }

        [JsonPropertyName("ProductionYear")]
        public int? ProductionYear { get; set; }

        [JsonPropertyName("Genres")]
        public List<string>? Genres { get; set; }

        [JsonPropertyName("ChildCount")]
        public int? ChildCount { get; set; }
    }

    private sealed class JellyfinLyricsResponse
    {
        [JsonPropertyName("Lyrics")]
        public List<JellyfinLyricDto>? Lyrics { get; set; }
    }

    private sealed class JellyfinLyricDto
    {
        [JsonPropertyName("Text")]
        public string? Text { get; set; }

        [JsonPropertyName("Start")]
        public long? Start { get; set; }
    }
}
