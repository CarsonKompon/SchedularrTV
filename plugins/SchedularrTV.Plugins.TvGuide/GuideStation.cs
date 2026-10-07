using System.Text.Json.Serialization;

namespace SchedularrTV.TvGuide;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GuideTheme
{
    /// <summary>The 1990s Prevue Channel: a blue scrolling list, a channel box and the programmes after it.</summary>
    Prevue,

    /// <summary>The 2000s TV Guide Channel: a glossy grid with half-hour columns.</summary>
    TvGuide,

    /// <summary>
    /// An early-2000s interactive cable-box guide: an orange promo box for what's on later, the picture beside it, a cream
    /// time bar with a running clock, red channel badges and rounded cream listings, a cursor hopping between them.
    /// </summary>
    Interactive,

    /// <summary>
    /// The turn-of-the-century listings channel: a blue promo panel, the picture beside it, yellow times in boxes, and
    /// rows coloured by kind (movies in maroon) with chevrons where a programme runs on.
    /// </summary>
    Classic
}

/// <summary>What each look is called, says about itself, and its own colours.</summary>
public static class GuideThemes
{
    public sealed record Look(GuideTheme Theme, string Name, string Blurb, string Background, string Row, string Text, string Accent);

    public static readonly IReadOnlyList<Look> All =
    [
        new(GuideTheme.Prevue, "Prevue (1990s)", "A blue scrolling list, a channel box and the programmes after it.", "#0b1a6e", "#1d3aa8", "#ffffff", "#ffd400"),
        new(GuideTheme.TvGuide, "TV Guide Channel (2000s)", "A glossy grid with half-hour columns.", "#1b1464", "#2f45b8", "#ffffff", "#ffcc33"),
        new(GuideTheme.Interactive, "Cable box guide (2000s)", "Orange promo box, cream rounded listings, red channel badges, a hopping cursor.", "#fdf1b8", "#f8e08c", "#3a2200", "#b5303c"),
        new(GuideTheme.Classic, "Listings channel (1999)", "Blue promo panel, yellow times in boxes, rows coloured by kind, chevrons.", "#0a1258", "#1f6fc4", "#ffffff", "#ffe94a")
    ];

    public static Look Of(GuideTheme theme) => All.FirstOrDefault(look => look.Theme == theme) ?? All[0];

    /// <summary>Looks with the picture on the right of the top panel, filling its height (the promo goes on the left).</summary>
    public static bool WindowOnRight(GuideTheme theme) => theme is GuideTheme.Interactive or GuideTheme.Classic;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TopPanel
{
    /// <summary>The clock, the date, a message and what's on now.</summary>
    Info,

    /// <summary>Commercials from the library, round the clock, in the video window.</summary>
    Ads,

    /// <summary>Another channel's picture in the video window.</summary>
    Channel
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GuideSound
{
    Music,

    /// <summary>A channel's sound, like the old cable "barker" channels.</summary>
    Channel,

    Silence,

    /// <summary>The top panel's own sound: the commercials', or the channel's (or channels') in the window.</summary>
    Window
}

/// <summary>One guide channel: how it looks, what's up top, what it plays, and the lengths it can be scheduled for.</summary>
public sealed class GuideStation
{
    /// <summary>Stable id, used in item ids and addresses; never changes once created.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The show's title, e.g. "Program Guide".</summary>
    public string Name { get; set; } = "Program Guide";

    public GuideTheme Theme { get; set; } = GuideTheme.Prevue;

    // --- Listings ---

    /// <summary>How far ahead the listings reach.</summary>
    public double HoursShown { get; set; } = 1.5;

    /// <summary>Scrolling speed, in pixels a second.</summary>
    public double ScrollSpeed { get; set; } = 40;

    /// <summary>Height of one channel's row, in pixels.</summary>
    public int RowHeight { get; set; } = 54;

    /// <summary>Text size, 1 being the theme's own.</summary>
    public double FontScale { get; set; } = 1;

    /// <summary>The lettering: an installed font or a web font (<see cref="LiveFonts.Web"/>); null for the look's own.</summary>
    public string? FontFamily { get; set; }

    public bool ShowLogos { get; set; } = true;

    public bool TwentyFourHourClock { get; set; }

    /// <summary>Channel numbers left out of the listings (usually the guide channel itself).</summary>
    public List<int> HiddenChannels { get; set; } = [];

    // --- Colours: null keeps the theme's own ---

    public string? Background { get; set; }

    public string? RowColor { get; set; }

    public string? TextColor { get; set; }

    public string? AccentColor { get; set; }

    // --- Top panel ---

    public TopPanel Top { get; set; } = TopPanel.Info;

    /// <summary>Share of the screen the top panel takes, in percent; 0 hides it.</summary>
    public int TopPercent { get; set; } = 45;

    /// <summary>Shown in the top panel; <c>{channel}</c> is the guide's name.</summary>
    public string Message { get; set; } = "Welcome! Here's what's on.";

    /// <summary>Which library items play as ads, when the top panel shows ads.</summary>
    public string AdsQuery { get; set; } = "tag:\"Commercials\"";

    /// <summary>The channel whose picture is shown, when the top panel shows a channel (and doesn't cycle).</summary>
    public int? WindowChannel { get; set; }

    /// <summary>Show a different channel, picked at random, every <see cref="CycleMinutes"/>, instead of <see cref="WindowChannel"/>.</summary>
    public bool CycleChannels { get; set; }

    /// <summary>How long each channel stays in the window when cycling.</summary>
    public int CycleMinutes { get; set; } = 5;

    /// <summary>The channels to cycle through; empty means every listed channel.</summary>
    public List<int> CycleChannelNumbers { get; set; } = [];

    // --- Sound ---

    public GuideSound Sound { get; set; } = GuideSound.Music;

    public string? MusicFolder { get; set; }

    /// <summary>0 to 1.</summary>
    public double Volume { get; set; } = 0.6;

    /// <summary>The channel to listen to, when the sound is a channel's; empty uses the window's channel.</summary>
    public int? SoundChannel { get; set; }

    /// <summary>The lengths, in minutes, this guide can be scheduled for; each becomes an "episode".</summary>
    public List<int> Lengths { get; set; } = [2, 5, 30, 60];

    public GuideStation Copy()
    {
        var copy = (GuideStation)MemberwiseClone();
        copy.HiddenChannels = [.. HiddenChannels];
        copy.CycleChannelNumbers = [.. CycleChannelNumbers];
        copy.Lengths = [.. Lengths];
        return copy;
    }

    [JsonIgnore]
    public IReadOnlyList<int> ValidLengths => [.. Lengths.Where(minutes => minutes is > 0 and <= 1440).Distinct().Order()];

    [JsonIgnore]
    public int TopHeight => Math.Clamp(TopPercent, 0, 70) * LiveStream.Height / 100 / 2 * 2;

    /// <summary>
    /// Where the video window sits in the top panel, 16:9: on the left with a margin, or (for the cable box and listings
    /// channel looks) on the right, filling the panel's height. The page draws its frame and the stream lays the picture
    /// over exactly this, so both use this one calculation.
    /// </summary>
    [JsonIgnore]
    public FrameRect? Window
    {
        get
        {
            bool right = GuideThemes.WindowOnRight(Theme);
            int margin = right ? 0 : 22;
            int height = (TopHeight - (2 * margin)) / 2 * 2;
            if (Top == TopPanel.Info || height < 90)
            {
                return null;
            }

            int width = Math.Min(height * 16 / 9 / 2 * 2, LiveStream.Width * 2 / 3 / 2 * 2);
            return new FrameRect(right ? LiveStream.Width - width : margin, margin, width, height);
        }
    }

    /// <summary>
    /// Whether the window plays the plugin's own feed (commercials one after another, or channels in turn) rather
    /// than one channel's stream.
    /// </summary>
    [JsonIgnore]
    public bool WindowIsFeed => Top == TopPanel.Ads || (Top == TopPanel.Channel && CycleChannels);

    /// <summary>A safe, lowercase id from a name: letters, digits and dashes.</summary>
    public static string Slug(string name)
    {
        var slug = new System.Text.StringBuilder();
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
        return result.Length == 0 ? "guide" : result.Length > 40 ? result[..40].TrimEnd('-') : result;
    }
}
