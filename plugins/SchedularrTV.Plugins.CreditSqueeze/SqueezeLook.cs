namespace SchedularrTV.CreditSqueeze;

/// <summary>What plays in the panel beside the credits.</summary>
public enum SqueezePanel
{
    /// <summary>A promo of a ticked kind (else the next programme's picture).</summary>
    Promo,

    /// <summary>Ordinary commercials, as many as fit, as in a break.</summary>
    Commercials,

    /// <summary>A promo first, then commercials for the rest of the credits.</summary>
    PromoThenCommercials
}

/// <summary>A clip to play in the panel, with how long it runs.</summary>
public sealed record SqueezeClip(string Path, TimeSpan Length);

/// <summary>
/// How a squeeze looks: its layout, what plays in the panel, the headings, fonts, colours and background. A channel (or
/// block) has one of its own, and may save more as templates, one of which is picked at random for each squeeze.
/// </summary>
public class SqueezeLook
{
    /// <summary>Use the canvas designs instead of a preset layout.</summary>
    public bool UseCanvas { get; set; }
    public List<SqueezeCanvas> Canvases { get; set; } = [];

    public SqueezeStyle Style { get; set; } = SqueezeStyle.SideBySide;

    /// <summary>Which side the credits go to; the panel takes the rest.</summary>
    public CreditsSide Side { get; set; } = CreditsSide.Left;

    /// <summary>How much of the picture the credits take (of its width, or of its height for top and bottom), 30 to 80 (percent).</summary>
    public int SplitPercent { get; set; } = 60;

    public SqueezeFit CreditsFit { get; set; } = SqueezeFit.Letterbox;

    public bool CreditsText { get; set; }

    public SqueezeFit PanelFit { get; set; } = SqueezeFit.Letterbox;

    public bool PanelText { get; set; } = true;

    /// <summary>
    /// The kinds of promo that may play in the panel, one picked at random from those the schedule airs nearby (library
    /// clips and generated promos alike). None, or none nearby: the panel shows the next programme's picture.
    /// </summary>
    public List<SqueezePromo> Promos { get; set; } = [];

    /// <summary>Promos, commercials, or a promo then commercials.</summary>
    public SqueezePanel Panel { get; set; } = SqueezePanel.Promo;

    /// <summary>Over the ending show's name, when the credits have text.</summary>
    public string CreditsCaption { get; set; } = "You've been watching";

    /// <summary>A line of the channel's own under the ending show's name, if any.</summary>
    public string? CreditsLine { get; set; }

    /// <summary>An installed font for the headings.</summary>
    public string HeadingFont { get; set; } = "Arial";

    /// <summary>An installed font for the rest of the text.</summary>
    public string TextFont { get; set; } = "Arial";

    /// <summary>Over the panel when it's about the next programme (an Up Next promo, or no promo).</summary>
    public string Heading { get; set; } = "Up next";

    /// <summary>Over the panel while a Coming Up promo plays there.</summary>
    public string ComingUpHeading { get; set; } = "Coming up";

    /// <summary>Over the panel while an Eventually promo (a trailer for later in the week) plays there.</summary>
    public string EventuallyHeading { get; set; } = "Eventually";

    /// <summary>Over the panel while the next programme's Now Playing ident plays there.</summary>
    public string NowPlayingHeading { get; set; } = "Next";

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

    /// <summary>How likely this look is picked against the others (relative; 0 never).</summary>
    public double Weight { get; set; } = 1;

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

    /// <summary>This look's layout, as a copy.</summary>
    public SqueezeLayoutChoice OwnLayout() => new()
    {
        Style = Style, Side = Side, SplitPercent = SplitPercent, CreditsFit = CreditsFit, CreditsText = CreditsText, PanelFit = PanelFit, PanelText = PanelText
    };

    /// <summary>Makes <paramref name="layout"/> this look's layout.</summary>
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

    /// <summary>Copies everything about how this looks onto <paramref name="target"/>.</summary>
    public void CopyLookTo(SqueezeLook target)
    {
        target.UseCanvas = UseCanvas;
        target.Canvases = [.. Canvases.Select(canvas => canvas.Copy())];
        target.UseLayout(OwnLayout());
        target.Promos = [.. Promos];
        target.Panel = Panel;
        target.CreditsCaption = CreditsCaption;
        target.CreditsLine = CreditsLine;
        target.HeadingFont = HeadingFont;
        target.TextFont = TextFont;
        target.Heading = Heading;
        target.ComingUpHeading = ComingUpHeading;
        target.EventuallyHeading = EventuallyHeading;
        target.NowPlayingHeading = NowPlayingHeading;
        target.AccentColor = AccentColor;
        target.BackgroundColor = BackgroundColor;
        target.BackgroundPath = BackgroundPath;
        target.CreditsVolume = CreditsVolume;
        target.ShowLater = ShowLater;
        target.Animate = Animate;
        target.Weight = Weight;
    }
}

/// <summary>A saved look of a channel's (or block's), picked at random among its others for each squeeze.</summary>
public sealed class SqueezeTemplate : SqueezeLook
{
    public string Name { get; set; } = "Template";

    /// <summary>Off, it's kept but never picked.</summary>
    public bool Enabled { get; set; } = true;

    public SqueezeTemplate Copy()
    {
        var copy = new SqueezeTemplate { Name = Name, Enabled = Enabled };
        CopyLookTo(copy);
        return copy;
    }

    /// <summary>A template of <paramref name="look"/> as it is now.</summary>
    public static SqueezeTemplate Of(SqueezeLook look, string name)
    {
        var template = new SqueezeTemplate { Name = name };
        look.CopyLookTo(template);
        return template;
    }
}
