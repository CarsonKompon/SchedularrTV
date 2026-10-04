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

/// <summary>How a squeeze is laid out: its style, which side the credits go to, and how much of the picture they take.</summary>
public sealed class SqueezeLayoutChoice
{
    public SqueezeStyle Style { get; set; } = SqueezeStyle.SideBySide;

    public CreditsSide Side { get; set; } = CreditsSide.Left;

    /// <summary>How much of the picture the credits take (of its width, or of its height for top and bottom), 30 to 80 (percent).</summary>
    public int SplitPercent { get; set; } = 60;

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

    /// <summary>Credits longer than this play as they are (a long credits roll is rarely just credits).</summary>
    public TimeSpan LongestCredits { get; set; } = TimeSpan.FromMinutes(4);

    public string Heading { get; set; } = "Up next";

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
        ShowLayout(show) ?? everywhere?.ShowLayout(show) ?? new SqueezeLayoutChoice { Style = Style, Side = Side, SplitPercent = SplitPercent };

    public CreditSqueezeSection Copy()
    {
        var copy = (CreditSqueezeSection)MemberwiseClone();
        copy.Outros = new Dictionary<string, double>(Outros, StringComparer.OrdinalIgnoreCase);
        copy.Shows = Shows.ToDictionary(pair => pair.Key, pair => pair.Value.Copy(), StringComparer.OrdinalIgnoreCase);
        copy.Promos = [.. Promos];
        return copy;
    }
}
