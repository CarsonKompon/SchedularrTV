using System.Text.Json;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Media;

namespace SchedularrTV.Providers.Jellyfin;

// What Jellyfin offers the app besides its items and pictures, each through the generic contract (SchedularrTV.Core.Media),
// so nothing outside this project needs to know it's Jellyfin.

/// <summary>A Jellyfin show's (or movie's) special features, for intros and outros.</summary>
public sealed class JellyfinExtras(JellyfinClient client, IOptions<JellyfinOptions> options) : IExtrasSource
{
    public bool Owns(string id) => JellyfinArtworkSource.IsJellyfinId(id);

    public async ValueTask<IReadOnlyList<MediaExtra>> GetExtrasAsync(string id, CancellationToken cancellationToken = default) =>
        options.Value.IsConfigured
            ? [.. (await client.GetSpecialFeaturesAsync(id, cancellationToken).ConfigureAwait(false)).Select(extra => new MediaExtra(extra.Name, extra.Path, extra.Duration))]
            : [];
}

/// <summary>Where Jellyfin's media segments (10.10 and newer, from a provider such as Intro Skipper) say an episode's credits start.</summary>
public sealed class JellyfinCredits(JellyfinClient client, IOptions<JellyfinOptions> options) : ICreditsSource
{
    public string Name => "Jellyfin";

    public bool IsAvailable => options.Value.IsConfigured;

    public bool Owns(string itemId) => JellyfinArtworkSource.IsJellyfinId(itemId);

    public async ValueTask<TimeSpan?> GetCreditsStartAsync(string itemId, CancellationToken cancellationToken = default) =>
        IsAvailable && await client.GetCreditsSegmentAsync(itemId, cancellationToken).ConfigureAwait(false) is { } segment ? segment.Start : null;
}

/// <summary>Who is watching Jellyfin's Live TV, for viewing stats.</summary>
public sealed class JellyfinLiveViewers(JellyfinClient client, IOptions<JellyfinOptions> options) : ILiveViewerSource
{
    public string Name => "Jellyfin";

    public bool IsConfigured => options.Value.IsConfigured;

    public async Task<IReadOnlyList<LiveViewerSession>> GetSessionsAsync(CancellationToken cancellationToken = default) =>
        IsConfigured ? Parse(await client.GetSessionsAsync(cancellationToken).ConfigureAwait(false)) : [];

    /// <summary>
    /// Jellyfin's sessions on Live TV: the item playing is the channel itself (<c>TvChannel</c>, with its <c>Number</c>
    /// or <c>ChannelNumber</c>), or a programme on one (with <c>ChannelNumber</c> / <c>ChannelName</c>).
    /// </summary>
    public static IReadOnlyList<LiveViewerSession> Parse(JsonElement sessions)
    {
        List<LiveViewerSession> found = [];
        if (sessions.ValueKind != JsonValueKind.Array)
        {
            return found;
        }

        foreach (JsonElement session in sessions.EnumerateArray())
        {
            if (!session.TryGetProperty("NowPlayingItem", out JsonElement item) || item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? type = Text(item, "Type");
            bool channel = type == "TvChannel";
            bool onChannel = channel || type is "Program" or "LiveTvProgram" || Text(item, "ChannelId") is not null;
            if (!onChannel)
            {
                continue;
            }

            int? number = LiveChannelNumbers.Read(Text(item, "ChannelNumber")) ?? (channel ? LiveChannelNumbers.Read(Text(item, "Number")) : null);
            string? name = channel ? Text(item, "Name") ?? Text(item, "ChannelName") : Text(item, "ChannelName");
            found.Add(new LiveViewerSession(number, name, Text(session, "UserName") ?? "Someone", "Jellyfin", Text(session, "DeviceName") ?? Text(session, "Client")));
        }

        return found;
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            }
            : null;
}

/// <summary>Jellyfin's music library, for radio channels.</summary>
public sealed class JellyfinMusicLibrary(JellyfinClient client, IOptions<JellyfinOptions> options) : IMusicLibrary
{
    public const string LibraryId = "jellyfin";

    public string Id => LibraryId;

    public string Name => "Jellyfin";

    public bool IsConfigured => options.Value.IsConfigured;

    public async Task<IReadOnlyList<MusicTrack>> GetTracksAsync(MusicQuery query, bool shuffle, CancellationToken cancellationToken = default) =>
        [.. (await client.GetTracksAsync(new JellyfinMusicQuery(query.PlaylistId, query.Genres, query.Artists, query.Limit), shuffle, cancellationToken).ConfigureAwait(false))
            .Select(track => new MusicTrack(track.Id, track.Title, track.Artists, track.Album, track.AlbumArtist, track.Duration, track.Year, track.Genres, track.ArtItemId))];

    public async Task<IReadOnlyList<MusicChoice>> GetPlaylistsAsync(CancellationToken cancellationToken = default) =>
        Choices(await client.GetMusicPlaylistsAsync(cancellationToken).ConfigureAwait(false));

    public async Task<IReadOnlyList<MusicChoice>> GetGenresAsync(CancellationToken cancellationToken = default) =>
        Choices(await client.GetMusicGenresAsync(cancellationToken).ConfigureAwait(false));

    public async Task<IReadOnlyList<MusicChoice>> GetArtistsAsync(CancellationToken cancellationToken = default) =>
        Choices(await client.GetMusicArtistsAsync(cancellationToken).ConfigureAwait(false));

    public async Task<IReadOnlyList<MusicLyricLine>> GetLyricsAsync(string trackId, CancellationToken cancellationToken = default) =>
        [.. (await client.GetLyricsAsync(trackId, cancellationToken).ConfigureAwait(false)).Select(line => new MusicLyricLine(line.Text, line.Start))];

    public ValueTask<MusicStream?> GetStreamAsync(string trackId, CancellationToken cancellationToken = default)
    {
        (string url, string authorization) = client.GetAudioStream(trackId);
        return ValueTask.FromResult<MusicStream?>(new MusicStream(url, authorization));
    }

    public async ValueTask<ArtworkImage?> GetArtAsync(string artId, int width, CancellationToken cancellationToken = default) =>
        await client.GetImageAsync(artId, "Primary", width, cancellationToken).ConfigureAwait(false) is { } image
            ? new ArtworkImage(image.Content, image.ContentType)
            : null;

    private static IReadOnlyList<MusicChoice> Choices(IReadOnlyList<JellyfinMusicChoice> choices) =>
        [.. choices.Select(choice => new MusicChoice(choice.Id, choice.Name, choice.Count))];
}

public sealed partial class JellyfinClient
{
    /// <summary>The sessions active in the last few minutes, as Jellyfin sends them.</summary>
    public async Task<JsonElement> GetSessionsAsync(CancellationToken cancellationToken) =>
        await GetAsync<JsonElement>(BuildUrl("/Sessions", new Dictionary<string, string?> { ["ActiveWithinSeconds"] = "150" }), cancellationToken).ConfigureAwait(false);
}
