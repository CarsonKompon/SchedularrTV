using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SchedularrTV.Radio;

/// <summary>One line of lyrics; <see cref="At"/> in milliseconds from the song's start, or null when they aren't timed.</summary>
public sealed record LyricLine(string Text, long? At);

/// <summary>A song as the page shows it.</summary>
/// <param name="Art">The cover's address (the plugin's own for library songs), or null.</param>
/// <param name="DurationMs">Null for a radio station's song, whose length isn't known.</param>
public sealed record SongInfo(string Title, string Artist, string? Album, int? Year, string? Art, long? DurationMs, IReadOnlyList<LyricLine> Lyrics)
{
    public bool Synced => Lyrics.Count > 0 && Lyrics.All(line => line.At is not null);
}

/// <summary>What a station is playing: the song, when it started (server clock), and what's next.</summary>
public sealed record NowPlayingState(SongInfo? Song, DateTimeOffset StartedAt, SongInfo? Next, string? Problem = null);

/// <summary>What every station on air is playing, shared by its sound (which sets it) and its page (which shows it).</summary>
public sealed class RadioPlayers
{
    private readonly ConcurrentDictionary<string, NowPlayingState> _playing = new(StringComparer.OrdinalIgnoreCase);

    public NowPlayingState? Get(string stationId) => _playing.GetValueOrDefault(stationId);

    public void Set(string stationId, NowPlayingState state) => _playing[stationId] = state;

    /// <summary>Changes what's playing with <paramref name="change"/>, if anything is.</summary>
    public void Update(string stationId, Func<NowPlayingState, NowPlayingState> change)
    {
        if (_playing.TryGetValue(stationId, out NowPlayingState? current))
        {
            _playing[stationId] = change(current);
        }
    }

    public void Clear(string stationId) => _playing.TryRemove(stationId, out _);
}

/// <summary>Reads timed (LRC) and plain lyrics.</summary>
public static partial class Lyrics
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex Stamp();

    /// <summary>
    /// <c>[01:23.45]A line</c> becomes a line at 83.45 seconds; a line with several stamps appears at each. Lines without
    /// a stamp, and tags such as <c>[ar:…]</c>, are left out when any line is timed; otherwise every line is kept, untimed.
    /// </summary>
    public static IReadOnlyList<LyricLine> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        List<LyricLine> timed = [];
        List<LyricLine> plain = [];
        foreach (string raw in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            MatchCollection stamps = Stamp().Matches(raw);
            if (stamps.Count == 0)
            {
                string line = raw.Trim();
                if (!(line.StartsWith('[') && line.EndsWith(']')))
                {
                    plain.Add(new LyricLine(line, null));
                }

                continue;
            }

            string words = Stamp().Replace(raw, string.Empty).Trim();
            foreach (Match stamp in stamps)
            {
                long minutes = long.Parse(stamp.Groups[1].Value, CultureInfo.InvariantCulture);
                long seconds = long.Parse(stamp.Groups[2].Value, CultureInfo.InvariantCulture);
                string fraction = stamp.Groups[3].Success ? stamp.Groups[3].Value : "0";
                long milliseconds = long.Parse(fraction.PadRight(3, '0')[..3], CultureInfo.InvariantCulture);
                timed.Add(new LyricLine(words, (minutes * 60 + seconds) * 1000 + milliseconds));
            }
        }

        return timed.Count > 0
            ? [.. timed.OrderBy(line => line.At)]
            : [.. Trim(plain)];
    }

    // Blank lines at either end of plain lyrics carry nothing.
    private static IEnumerable<LyricLine> Trim(List<LyricLine> lines)
    {
        int first = lines.FindIndex(line => line.Text.Length > 0);
        int last = lines.FindLastIndex(line => line.Text.Length > 0);
        return first < 0 ? [] : lines.Skip(first).Take(last - first + 1);
    }
}

/// <summary>An internet station's "now playing" title, as stations send it: usually "Artist - Title".</summary>
public static partial class StreamTitle
{
    [GeneratedRegex("StreamTitle='(.*?)';", RegexOptions.Singleline)]
    private static partial Regex Field();

    /// <summary>The StreamTitle from an ICY metadata block, or null when there's none.</summary>
    public static string? FromMetadata(string metadata) =>
        Field().Match(metadata) is { Success: true } match ? match.Groups[1].Value.Trim() : null;

    /// <summary>Splits "Artist - Title" (at the first " - "); without one it's all title.</summary>
    public static (string Artist, string Title) Split(string streamTitle)
    {
        int dash = streamTitle.IndexOf(" - ", StringComparison.Ordinal);
        return dash > 0
            ? (streamTitle[..dash].Trim(), streamTitle[(dash + 3)..].Trim())
            : (string.Empty, streamTitle.Trim());
    }
}
