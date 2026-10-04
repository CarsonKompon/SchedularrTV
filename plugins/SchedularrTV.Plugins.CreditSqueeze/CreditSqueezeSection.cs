namespace SchedularrTV.CreditSqueeze;

/// <summary>Where the credits go.</summary>
public enum CreditsSide
{
    Left,
    Right,
    Top,
    Bottom
}

public enum SqueezeStyle
{
    /// <summary>
    /// The classic squeeze: the credits shrink to one edge (left, right, top or bottom) and the rest of the picture says
    /// what's next; at the end they grow back to fill it.
    /// </summary>
    SideBySide,

    /// <summary>Two pictures on a background: the credits in a framed box, the next programme (or a promo) beside it.</summary>
    Framed
}

/// <summary>What plays in the panel's picture, chosen from what the schedule airs nearby.</summary>
public enum SqueezePromo
{
    /// <summary>The next programme's own Up Next promo (from the break after the credits, or the ident before it).</summary>
    UpNext,

    /// <summary>A Coming Up promo for something later today.</summary>
    ComingUp,

    /// <summary>A trailer for a programme later in the week.</summary>
    Eventually,

    /// <summary>The next programme's "You're watching" ident.</summary>
    NowPlaying
}

/// <summary>How a video is fitted into its part of the squeeze.</summary>
public enum SqueezeFit
{
    /// <summary>Keeps its shape inside the part, with bars (or the background) around it.</summary>
    Letterbox,

    /// <summary>Stretched to fill the part exactly.</summary>
    Squeeze,

    /// <summary>Keeps its shape and fills the part, trimming its edges.</summary>
    Crop
}

/// <summary>
/// How a squeeze is laid out: its style, which side the credits go to, how much of the picture they take, and for each
/// side, how its video is fitted and whether there's text around it.
/// </summary>
public sealed class SqueezeLayoutChoice
{
    public SqueezeStyle Style { get; set; } = SqueezeStyle.SideBySide;

    public CreditsSide Side { get; set; } = CreditsSide.Left;

    /// <summary>How much of the picture the credits take (of its width, or of its height for top and bottom), 30 to 80 (percent).</summary>
    public int SplitPercent { get; set; } = 60;

    public SqueezeFit CreditsFit { get; set; } = SqueezeFit.Letterbox;

    /// <summary>Text with the credits: what's ending ("You've been watching …") and the channel's own line.</summary>
    public bool CreditsText { get; set; }

    public SqueezeFit PanelFit { get; set; } = SqueezeFit.Letterbox;

    /// <summary>The up-next text around the panel's video; off, the panel is just the video on the background.</summary>
    public bool PanelText { get; set; } = true;

    public SqueezeLayoutChoice Copy() => (SqueezeLayoutChoice)MemberwiseClone();
}

/// <summary>
/// A channel's or block's credit squeeze settings (<c>plugins: credit-squeeze:</c> in the schedule). Channels opt in;
/// a block without its own uses the channel's. The schedule-wide section (under <c>settings</c>) only holds layouts for
/// particular shows on every channel.
/// </summary>
public sealed class CreditSqueezeSection
{
    public const string PluginId = "credit-squeeze";

    /// <summary>On when the section is in the schedule (a channel is only squeezed once it's turned on there).</summary>
    public bool Enabled { get; set; } = true;

    public SqueezeStyle Style { get; set; } = SqueezeStyle.SideBySide;

    /// <summary>Which side the credits go to; the panel takes the rest.</summary>
    public CreditsSide Side { get; set; } = CreditsSide.Left;

    /// <summary>How much of the picture the credits take (of its width, or of its height for top and bottom), 30 to 80 (percent).</summary>
    public int SplitPercent { get; set; } = 60;

    public SqueezeFit CreditsFit { get; set; } = SqueezeFit.Letterbox;

    public bool CreditsText { get; set; }

    public SqueezeFit PanelFit { get; set; } = SqueezeFit.Letterbox;

    public bool PanelText { get; set; } = true;

    /// <summary>Over the ending show's name, when the credits have text.</summary>
    public string CreditsCaption { get; set; } = "You've been watching";

    /// <summary>A line of the channel's own under the ending show's name, if any.</summary>
    public string? CreditsLine { get; set; }

    /// <summary>An installed font for the headings.</summary>
    public string HeadingFont { get; set; } = "Arial";

    /// <summary>An installed font for the rest of the text.</summary>
    public string TextFont { get; set; } = "Arial";

    /// <summary>
    /// Shows whose end credits want a layout of their own (credits that run in a corner, say), by show name. They win over
    /// the schedule-wide ones for the same show.
    /// </summary>
    public Dictionary<string, SqueezeLayoutChoice> Shows { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The kinds of promo that may play in the panel, one picked at random from those the schedule airs nearby (library
    /// clips and generated promos alike). None, or none nearby: the panel shows the next programme's picture.
    /// </summary>
    public List<SqueezePromo> Promos { get; set; } = [];

    /// <summary>The odds (0–1) an episode whose end credits are marked gets them squeezed.</summary>
    public double CreditsChance { get; set; } = 1;

    /// <summary>The odds (0–1) a show's own outro (an end card played after its episode) is squeezed, for shows not in <see cref="Outros"/>.</summary>
    public double OutroChance { get; set; } = 1;

    /// <summary>Each show's own odds for its outro, by show name.</summary>
    public Dictionary<string, double> Outros { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// For episodes whose credits aren't known, how many seconds of their end to squeeze anyway: null for the plugin's
    /// setting, zero to leave them be.
    /// </summary>
    public double? AssumedCreditsSeconds { get; set; }

    /// <summary>How much of the end of an episode without known credits is squeezed (zero: none).</summary>
    public TimeSpan AssumedCredits(TimeSpan pluginDefault) =>
        AssumedCreditsSeconds is { } seconds ? TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 600)) : pluginDefault;

    /// <summary>Credits longer than this play as they are (a long credits roll is rarely just credits).</summary>
    public TimeSpan LongestCredits { get; set; } = TimeSpan.FromMinutes(4);

    /// <summary>Over the panel when it's about the next programme (an Up Next promo, or no promo).</summary>
    public string Heading { get; set; } = "Up next";

    /// <summary>Over the panel while a Coming Up promo plays there.</summary>
    public string ComingUpHeading { get; set; } = "Coming up";

    /// <summary>Over the panel while an Eventually promo (a trailer for later in the week) plays there.</summary>
    public string EventuallyHeading { get; set; } = "Eventually";

    /// <summary>Over the panel while the next programme's Now Playing ident plays there.</summary>
    public string NowPlayingHeading { get; set; } = "Next";

    /// <summary>The panel's heading for the kind of promo in it (none: the next programme's).</summary>
    public string HeadingFor(SqueezePromo? kind)
    {
        string? heading = kind switch
        {
            SqueezePromo.ComingUp => ComingUpHeading,
            SqueezePromo.Eventually => EventuallyHeading,
            SqueezePromo.NowPlaying => NowPlayingHeading,
            _ => Heading
        };
        return string.IsNullOrWhiteSpace(heading) ? Heading : heading.Trim();
    }

    public string AccentColor { get; set; } = "#ff5c8a";

    public string BackgroundColor { get; set; } = "#0b0d14";

    /// <summary>A picture or video behind everything (a video loops); empty for <see cref="BackgroundColor"/>.</summary>
    public string? BackgroundPath { get; set; }

    /// <summary>The credits' own sound, 0 (silent) to 1.</summary>
    public double CreditsVolume { get; set; } = 1;

    /// <summary>Lists the two programmes after the next one too.</summary>
    public bool ShowLater { get; set; } = true;

    /// <summary>The credits shrink into place (and, side by side, grow back at the end), rather than start there.</summary>
    public bool Animate { get; set; } = true;

    /// <summary>The odds for a show's outro: its own, else <see cref="OutroChance"/>.</summary>
    public double OutroChanceFor(string? show)
    {
        if (show is not null)
        {
            foreach ((string name, double chance) in Outros)
            {
                if (string.Equals(name, show, StringComparison.OrdinalIgnoreCase))
                {
                    return chance;
                }
            }
        }

        return OutroChance;
    }

    /// <summary>A show's layout here, if it has its own.</summary>
    public SqueezeLayoutChoice? ShowLayout(string? show)
    {
        if (show is not null)
        {
            foreach ((string name, SqueezeLayoutChoice choice) in Shows)
            {
                if (string.Equals(name, show, StringComparison.OrdinalIgnoreCase))
                {
                    return choice;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The layout for a show's credits: its own here, else its own on every channel (<paramref name="everywhere"/>), else
    /// this section's.
    /// </summary>
    public SqueezeLayoutChoice LayoutFor(string? show, CreditSqueezeSection? everywhere = null) =>
        ShowLayout(show) ?? everywhere?.ShowLayout(show) ?? OwnLayout();

    /// <summary>This section's own layout (for every show), as a copy.</summary>
    public SqueezeLayoutChoice OwnLayout() => new()
    {
        Style = Style, Side = Side, SplitPercent = SplitPercent, CreditsFit = CreditsFit, CreditsText = CreditsText, PanelFit = PanelFit, PanelText = PanelText
    };

    /// <summary>Makes <paramref name="layout"/> this section's own layout.</summary>
    public void UseLayout(SqueezeLayoutChoice layout)
    {
        Style = layout.Style;
        Side = layout.Side;
        SplitPercent = layout.SplitPercent;
        CreditsFit = layout.CreditsFit;
        CreditsText = layout.CreditsText;
        PanelFit = layout.PanelFit;
        PanelText = layout.PanelText;
    }

    public CreditSqueezeSection Copy()
    {
        var copy = (CreditSqueezeSection)MemberwiseClone();
        copy.Outros = new Dictionary<string, double>(Outros, StringComparer.OrdinalIgnoreCase);
        copy.Shows = Shows.ToDictionary(pair => pair.Key, pair => pair.Value.Copy(), StringComparer.OrdinalIgnoreCase);
        copy.Promos = [.. Promos];
        return copy;
    }
}
