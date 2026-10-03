using System.Collections.Concurrent;
using System.Text.Json;
using SchedularrTV.Providers.Jellyfin;

namespace SchedularrTV.Radio;

/// <summary>What the radio page needs to know about its station (also what the editor's live preview sends it).</summary>
public sealed record RadioPageConfig(
    string Id,
    string Name,
    string Preset,
    string Background,
    string Accent,
    string BackgroundColor,
    bool ShowArt,
    bool ShowLyrics,
    bool ShowUpNext,
    bool ShowClock,
    string? Message,
    FrameRect? Vis,
    long LyricsOffsetMs,
    bool Preview)
{
    public static RadioPageConfig For(RadioStation station, bool preview) => new(
        station.Id,
        station.Name,
        station.Preset.ToString(),
        station.Background.ToString(),
        RadioLayout.IsColour(station.AccentColor) ? station.AccentColor : "#ff5c8a",
        RadioLayout.IsColour(station.BackgroundColor) ? station.BackgroundColor : "#101828",
        station.ShowArt,
        station.ShowLyrics,
        station.ShowUpNext,
        station.ShowClock,
        station.Message,
        station.VisualizerRect,
        (long)(station.LyricsOffset * 1000),
        preview);
}

/// <summary>The radio page: one self-contained HTML file (in the plugin) with the station's settings written in.</summary>
public static class RadioPage
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly Lazy<string> Template = new(() =>
    {
        using Stream stream = typeof(RadioPage).Assembly.GetManifestResourceStream("radio.html")
            ?? throw new InvalidOperationException("The radio page is missing from the plugin.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>The page for a station. The settings go in as JSON, which escapes &lt; and &gt;, so nothing in them can end the script.</summary>
    public static string Html(RadioPageConfig config) =>
        Template.Value.Replace("__CONFIG__", JsonSerializer.Serialize(config, Json), StringComparison.Ordinal);
}

/// <summary>now.json: what's playing, when it started and the server's clock, so the page can time the lyrics.</summary>
public sealed record NowPlayingResponse(SongInfo? Song, long StartedAt, SongInfo? Next, string? Problem, long ServerTime);

/// <summary>
/// What the editor's preview shows while a station isn't on air: a song picked from its music (with its real cover
/// and lyrics), or an internet station's name. Kept a minute per station so the preview doesn't flicker.
/// </summary>
public sealed class RadioPreviews(RadioStore store, OnlineLookups online, IServiceProvider services, TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, NowPlayingState State)> _samples = new(StringComparer.OrdinalIgnoreCase);

    public async Task<NowPlayingState> SampleAsync(string stationId, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        if (_samples.TryGetValue(stationId, out var cached) && now - cached.At < TimeSpan.FromMinutes(1))
        {
            return cached.State;
        }

        RadioStation station = store.Find(stationId) ?? new RadioStation { Id = stationId, Name = "Radio" };
        NowPlayingState state;
        if (station.Source == RadioSource.Internet)
        {
            state = new NowPlayingState(new SongInfo("Song title", "Artist", null, null, null, null,
                [new LyricLine("Lyrics appear here when the station's song is found", null)]), now, null);
        }
        else if (services.GetService<JellyfinClient>() is { } jellyfin)
        {
            try
            {
                IReadOnlyList<JellyfinTrack> tracks = await jellyfin.GetTracksAsync(new JellyfinMusicQuery(Limit: 2), shuffle: true, cancellationToken).ConfigureAwait(false);
                if (tracks.Count == 0)
                {
                    state = new NowPlayingState(null, now, null, "No songs in the Jellyfin library.");
                }
                else
                {
                    JellyfinTrack track = tracks[0];
                    IReadOnlyList<LyricLine> lyrics = [.. (await jellyfin.GetLyricsAsync(track.Id, cancellationToken).ConfigureAwait(false))
                        .Select(line => new LyricLine(line.Text, line.Start is { } start ? (long)start.TotalMilliseconds : null))];
                    if (lyrics.Count == 0 && station.LookUpOnline && track.Artist.Length > 0)
                    {
                        lyrics = await online.LyricsAsync(track.Artist, track.Title, track.Album, track.Duration, cancellationToken).ConfigureAwait(false);
                    }

                    // Started a little while ago, so the preview shows lyrics part-way through.
                    state = new NowPlayingState(RadioSound.Library(track, lyrics), now - TimeSpan.FromSeconds(45),
                        tracks.Count > 1 ? RadioSound.Library(tracks[1], []) : null);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                state = new NowPlayingState(null, now, null, $"Couldn't reach Jellyfin: {ex.Message}");
            }
        }
        else
        {
            state = new NowPlayingState(null, now, null, "Jellyfin isn't set up.");
        }

        _samples[stationId] = (now, state);
        return state;
    }
}
