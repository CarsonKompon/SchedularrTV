using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace SchedularrTV.Radio;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RadioSource
{
    /// <summary>Songs from the Jellyfin music library.</summary>
    Jellyfin,

    /// <summary>An internet radio station's stream.</summary>
    Internet
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MusicPick
{
    /// <summary>Every song.</summary>
    All,
    Playlist,
    Genres,
    Artists
}

/// <summary>The look: where the cover, the words and the visualizer go.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RadioPreset
{
    /// <summary>Big cover on the left, the song and its lyrics on the right, the visualizer along the bottom.</summary>
    NowPlaying,

    /// <summary>The lyrics, big and centred, over the blurred cover.</summary>
    Karaoke,

    /// <summary>A spinning record with the cover as its label.</summary>
    Vinyl,

    /// <summary>A hi-fi tuner's display: station, frequency readout, spectrum and a scrolling title.</summary>
    RetroFm,

    /// <summary>The cover filling the screen, softly, with the song in a corner.</summary>
    Minimal
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RadioVisualizer
{
    None,

    /// <summary>Frequency bars.</summary>
    Bars,

    /// <summary>The waveform.</summary>
    Wave,

    /// <summary>A colourful musical spectrum (constant-Q).</summary>
    Spectrum,

    /// <summary>A stereo oscilloscope.</summary>
    Scope
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RadioBackground
{
    /// <summary>The cover, blurred and darkened.</summary>
    BlurredArt,

    /// <summary>A slowly moving gradient of the accent colour.</summary>
    Gradient,

    Solid
}

/// <summary>One radio station: where its music comes from, how it looks, and the lengths it can be scheduled for.</summary>
public sealed class RadioStation
{
    /// <summary>Stable id, used in item ids and addresses; never changes once created.</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    // --- Music ---

    public RadioSource Source { get; set; } = RadioSource.Jellyfin;

    public MusicPick Pick { get; set; } = MusicPick.All;

    public string? PlaylistId { get; set; }

    public string? PlaylistName { get; set; }

    public List<string> Genres { get; set; } = [];

    public List<string> Artists { get; set; } = [];

    public bool Shuffle { get; set; } = true;

    /// <summary>The internet station: its stream, or an .m3u / .pls playlist that names it.</summary>
    public string? StreamUrl { get; set; }

    /// <summary>Look covers and lyrics up online (iTunes for covers, LRCLIB for lyrics) when the library or station has none.</summary>
    public bool LookUpOnline { get; set; } = true;

    // --- Look ---

    public RadioPreset Preset { get; set; } = RadioPreset.NowPlaying;

    /// <summary>A new station's: the blurred cover, or a plain colour on a Raspberry Pi (a full-screen blur every frame is heavy there).</summary>
    public RadioBackground Background { get; set; } = SchedularrTV.Core.Platform.HardwareProfile.Pick(RadioBackground.BlurredArt, RadioBackground.Solid);

    /// <summary>#rrggbb.</summary>
    public string AccentColor { get; set; } = "#ff5c8a";

    /// <summary>#rrggbb, for a solid background.</summary>
    public string BackgroundColor { get; set; } = "#101828";

    public bool ShowArt { get; set; } = true;

    public bool ShowLyrics { get; set; } = true;

    public bool ShowUpNext { get; set; } = true;

    public bool ShowClock { get; set; } = true;

    /// <summary>A line of text along the bottom, e.g. the station's slogan; empty for none.</summary>
    public string? Message { get; set; }

    public RadioVisualizer Visualizer { get; set; } = RadioVisualizer.Bars;

    /// <summary>Lyrics shown this much earlier (positive) or later, in seconds, to line them up with what's heard.</summary>
    public double LyricsOffset { get; set; }

    // --- Sound and scheduling ---

    /// <summary>0 to 1.</summary>
    public double Volume { get; set; } = 1;

    public List<int> Lengths { get; set; } = [3, 5, 30, 60];

    [JsonIgnore]
    public IReadOnlyList<int> ValidLengths => [.. Lengths.Where(minutes => minutes is > 0 and <= 1440).Distinct().Order()];

    /// <summary>Where the visualizer is drawn, by the stream, over the page; null when there's none.</summary>
    [JsonIgnore]
    public FrameRect? VisualizerRect => Visualizer == RadioVisualizer.None ? null : RadioLayout.VisualizerRect(Preset, Visualizer);

    public RadioStation Copy()
    {
        var copy = (RadioStation)MemberwiseClone();
        copy.Genres = [.. Genres];
        copy.Artists = [.. Artists];
        copy.Lengths = [.. Lengths];
        return copy;
    }

    /// <summary>A safe, lowercase id from a name: letters, digits and dashes.</summary>
    public static string Slug(string name)
    {
        var slug = new StringBuilder();
        foreach (char character in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                slug.Append(character);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        string result = slug.ToString().Trim('-');
        return result.Length == 0 ? "radio" : result.Length > 40 ? result[..40].TrimEnd('-') : result;
    }
}

/// <summary>Where each preset puts the visualizer, and the ffmpeg filters that draw it; shared by the page and the stream.</summary>
public static class RadioLayout
{
    public static FrameRect VisualizerRect(RadioPreset preset, RadioVisualizer visualizer)
    {
        FrameRect rect = preset switch
        {
            RadioPreset.Karaoke => new FrameRect(0, 600, 1280, 100),
            RadioPreset.Vinyl => new FrameRect(240, 580, 800, 110),
            RadioPreset.RetroFm => new FrameRect(120, 300, 1040, 220),
            RadioPreset.Minimal => new FrameRect(60, 470, 560, 120),
            _ => new FrameRect(0, 590, 1280, 130)
        };

        // The scope is round: a square, centred where the strip would be.
        if (visualizer == RadioVisualizer.Scope)
        {
            int side = rect.Height / 2 * 2;
            return new FrameRect(rect.X + (rect.Width - side) / 2 / 2 * 2, rect.Y, side, side);
        }

        return rect;
    }

    /// <summary>The ffmpeg filters that turn the sound into the visualizer's picture, sized to its box.</summary>
    /// <param name="frameRate">The stream's frame rate (drawing more frames than that is wasted work); 30 if not given.</param>
    public static string? Filter(RadioVisualizer visualizer, FrameRect rect, string accent, int? frameRate = null)
    {
        string color = "0x" + (IsColour(accent) ? accent[1..] : "ff5c8a");
        string size = $"{rect.Width.ToString(CultureInfo.InvariantCulture)}x{rect.Height.ToString(CultureInfo.InvariantCulture)}";
        int fps = frameRate ?? LiveStream.OutputFrameRate;
        return visualizer switch
        {
            RadioVisualizer.Bars => $"showfreqs=s={size}:mode=bar:ascale=log:fscale=log:win_size=2048:colors={color},fps={fps}",
            RadioVisualizer.Wave => $"showwaves=s={size}:mode=cline:rate={fps}:scale=sqrt:colors={color}",
            RadioVisualizer.Spectrum => $"showcqt=s={size}:r={fps}:sono_h=0:axis=0:bar_g=2",
            RadioVisualizer.Scope => $"avectorscope=s={size}:mode=lissajous_xy:draw=line:scale=sqrt:rate={fps}:{ScopeColour(accent)}",
            _ => null
        };
    }

    private static string ScopeColour(string accent)
    {
        string hex = IsColour(accent) ? accent[1..] : "ff5c8a";
        int Channel(int index) => int.Parse(hex.AsSpan(index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return $"rc={Channel(0)}:gc={Channel(2)}:bc={Channel(4)}:rf=12:gf=12:bf=12";
    }

    /// <summary>#rrggbb only, so nothing else reaches the page's styles or ffmpeg's filters.</summary>
    public static bool IsColour(string? value) => value is { Length: 7 } && value[0] == '#' && value[1..].All(Uri.IsHexDigit);
}
