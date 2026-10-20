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
public sealed class CreditSqueezeSection : SqueezeLook
{
    public const string PluginId = "credit-squeeze";

    /// <summary>The name of the section's own look among its templates.</summary>
    public const string MainLook = "Main";

    /// <summary>On when the section is in the schedule (a channel is only squeezed once it's turned on there).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// More looks saved alongside the section's own; each squeeze picks one of them (or the section's own) at random,
    /// by their weights, seeded by the airing.
    /// </summary>
    public List<SqueezeTemplate> Templates { get; set; } = [];

    /// <summary>The looks a squeeze may pick from: the section's own and its enabled templates, any with a weight.</summary>
    public IReadOnlyList<SqueezeLook> Looks()
    {
        List<SqueezeLook> looks = [.. new SqueezeLook[] { this }.Concat(Templates.Where(template => template.Enabled)).Where(look => look.Weight > 0)];
        return looks.Count > 0 ? looks : new List<SqueezeLook> { this };
    }

    /// <summary>The look for a squeeze airing at <paramref name="at"/> on the channel, picked by weight (always the same for the same airing).</summary>
    public SqueezeLook LookFor(string channelId, DateTimeOffset at)
    {
        IReadOnlyList<SqueezeLook> looks = Looks();
        if (looks.Count == 1)
        {
            return looks[0];
        }

        double total = looks.Sum(look => look.Weight);
        double roll = SchedularrTV.Core.Ordering.DeterministicRandom.FromSeedText(channelId, at.UtcDateTime.ToString("O"), "credit-squeeze", "look").Next(1_000_000)
                      / 1_000_000.0 * total;
        foreach (SqueezeLook look in looks)
        {
            roll -= look.Weight;
            if (roll < 0)
            {
                return look;
            }
        }

        return looks[^1];
    }

    /// <summary>A copy that only ever uses <paramref name="look"/> (for previewing one template).</summary>
    public CreditSqueezeSection WithOnly(SqueezeLook look)
    {
        CreditSqueezeSection copy = Copy();
        look.CopyLookTo(copy);
        copy.Weight = 1;
        copy.Templates = [];
        return copy;
    }

    /// <summary>
    /// Shows whose end credits want a layout of their own (credits that run in a corner, say), by show name. They win over
    /// the schedule-wide ones for the same show.
    /// </summary>
    public Dictionary<string, SqueezeLayoutChoice> Shows { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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
    /// the look's (<paramref name="look"/>, or this section's own).
    /// </summary>
    public SqueezeLayoutChoice LayoutFor(string? show, CreditSqueezeSection? everywhere = null, SqueezeLook? look = null) =>
        ShowLayout(show) ?? everywhere?.ShowLayout(show) ?? (look ?? this).OwnLayout();

    public CreditSqueezeSection Copy()
    {
        var copy = (CreditSqueezeSection)MemberwiseClone();
        copy.Outros = new Dictionary<string, double>(Outros, StringComparer.OrdinalIgnoreCase);
        copy.Shows = Shows.ToDictionary(pair => pair.Key, pair => pair.Value.Copy(), StringComparer.OrdinalIgnoreCase);
        copy.Promos = [.. Promos];
        copy.Canvases = [.. Canvases.Select(canvas => canvas.Copy())];
        copy.Templates = [.. Templates.Select(template => template.Copy())];
        return copy;
    }
}
