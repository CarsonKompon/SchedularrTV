namespace SchedularrTV.CreditSqueeze;

/// <summary>A box on the picture, in pixels.</summary>
public readonly record struct Rect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static readonly Rect None = new(0, 0, 0, 0);
}

/// <summary>
/// Where things go on a squeeze of a given size: the credits' box, the panel (the rest), the picture slot in the panel
/// (where a promo plays, over the next programme's picture), where the panel's words go (<see cref="Rect.None"/> when the
/// panel is just its video), and where the credits' own words go (<see cref="Rect.None"/> without them). When
/// <see cref="TextBeside"/>, the panel's words sit beside the picture (a wide, short panel) rather than under it. Every
/// size is even, as the video must be.
/// </summary>
public sealed record SqueezeLayout(int Width, int Height, SqueezeStyle Style, Rect Credits, Rect Panel, Rect Picture, Rect Text, bool TextBeside, Rect Caption)
{
    public const int MinSplit = 30;
    public const int MaxSplit = 80;

    private const int Margin = 48;
    private const int Gap = 40;

    // The credits' words: a band under them (or a column beside them, top and bottom).
    private const int CaptionHeight = 120;
    private const int CaptionWidth = 320;

    /// <summary>The strip the credits' side takes: their column (left and right) or band (top and bottom); framed, their box.</summary>
    public Rect Strip { get; init; }

    public static SqueezeLayout Of(SqueezeStyle style, CreditsSide side, int splitPercent, int width = 1280, int height = 720,
        SqueezeFit creditsFit = SqueezeFit.Letterbox, bool creditsText = false, bool panelText = true)
    {
        int split = Math.Clamp(splitPercent, MinSplit, MaxSplit);
        SqueezeLayout layout = style == SqueezeStyle.Framed
            ? Framed(side, split, creditsText, panelText)
            : SideBySide(side, split, creditsFit, creditsText, panelText);
        double scale = width / 1280.0;
        return layout with
        {
            Width = width,
            Height = height,
            Credits = Scale(layout.Credits, scale),
            Panel = Scale(layout.Panel, scale),
            Picture = Scale(layout.Picture, scale),
            Text = Scale(layout.Text, scale),
            Caption = Scale(layout.Caption, scale),
            Strip = Scale(layout.Strip, scale)
        };
    }

    /// <summary>Two boxes on a background, side by side (top and bottom count as left and right here).</summary>
    private static SqueezeLayout Framed(CreditsSide side, int split, bool creditsText, bool panelText)
    {
        bool left = side is CreditsSide.Left or CreditsSide.Top;
        int usable = 1280 - 2 * Margin - Gap;
        int creditsWidth = Even(usable * Math.Clamp(split, 40, 75) / 100.0);
        int creditsHeight = Even(creditsWidth * 9 / 16.0);
        int panelWidth = usable - creditsWidth;

        // A little below centre, leaving room above for the channel's name and below for what's later (or the credits' words).
        int below = creditsText ? 150 : 90;
        int creditsTop = Even(Math.Clamp((720 - creditsHeight) / 2.0 + 8, 110, Math.Max(110, 720 - creditsHeight - below)));
        int panelTop = Math.Min(creditsTop, 144);
        int creditsLeft = left ? Margin : Margin + panelWidth + Gap;
        int panelLeft = left ? Margin + creditsWidth + Gap : Margin;
        Rect credits = new(creditsLeft, creditsTop, creditsWidth, creditsHeight);
        Rect caption = creditsText ? new Rect(creditsLeft, credits.Bottom + 14, creditsWidth, Even(Math.Min(96, 712 - credits.Bottom - 14))) : Rect.None;

        Rect picture;
        Rect text;
        if (panelText)
        {
            picture = new Rect(panelLeft, panelTop + 40, panelWidth, Even(panelWidth * 9 / 16.0));
            text = new Rect(panelLeft, picture.Bottom + 12, panelWidth, 600 - picture.Bottom - 12);
        }
        else
        {
            // Just the two pictures, level with each other.
            picture = new Rect(panelLeft, creditsTop, panelWidth, creditsHeight);
            text = Rect.None;
        }

        return new SqueezeLayout(1280, 720, SqueezeStyle.Framed, credits, new Rect(panelLeft, panelTop, panelWidth, 600 - panelTop), picture, text, false, caption)
        {
            Strip = credits
        };
    }

    /// <summary>The credits against one edge, the panel filling the rest of the picture.</summary>
    private static SqueezeLayout SideBySide(CreditsSide side, int split, SqueezeFit fit, bool creditsText, bool panelText)
    {
        bool letterbox = fit == SqueezeFit.Letterbox;
        if (side is CreditsSide.Left or CreditsSide.Right)
        {
            int columnWidth = Even(1280 * split / 100.0);
            Rect column = new(side == CreditsSide.Left ? 0 : 1280 - columnWidth, 0, columnWidth, 720);
            Rect credits;
            Rect caption = Rect.None;
            if (letterbox)
            {
                // 16:9 in the column, centred along with its words.
                int creditsHeight = Even(Math.Min(columnWidth * 9 / 16.0, 720 - (creditsText ? CaptionHeight : 0)));
                int creditsWidth = Even(creditsHeight * 16 / 9.0);
                int group = creditsHeight + (creditsText ? CaptionHeight : 0);
                int top = Even((720 - group) / 2.0);
                credits = new Rect(Even(column.X + (columnWidth - creditsWidth) / 2.0), top, creditsWidth, creditsHeight);
                if (creditsText)
                {
                    caption = new Rect(column.X + 24, credits.Bottom + 14, columnWidth - 48, CaptionHeight - 20);
                }
            }
            else
            {
                credits = creditsText ? column with { Height = 720 - CaptionHeight } : column;
                if (creditsText)
                {
                    caption = new Rect(column.X + 24, credits.Bottom + 14, columnWidth - 48, CaptionHeight - 20);
                }
            }

            Rect panel = new(side == CreditsSide.Left ? columnWidth : 0, 0, 1280 - columnWidth, 720);
            if (!panelText)
            {
                return new SqueezeLayout(1280, 720, SqueezeStyle.SideBySide, credits, panel, panel, Rect.None, false, caption) { Strip = column };
            }

            int inset = Math.Min(40, panel.Width / 10);
            int pictureWidth = Even(panel.Width - 2 * inset);
            Rect picture = new(Even(panel.X + inset), 96, pictureWidth, Even(pictureWidth * 9 / 16.0));
            return new SqueezeLayout(1280, 720, SqueezeStyle.SideBySide, credits, panel, picture,
                new Rect(picture.X, picture.Bottom + 12, pictureWidth, 720 - picture.Bottom - 40), false, caption) { Strip = column };
        }

        int bandHeight = Even(720 * split / 100.0);
        Rect band = new(0, side == CreditsSide.Top ? 0 : 720 - bandHeight, 1280, bandHeight);
        Rect bandCredits;
        Rect bandCaption = Rect.None;
        if (letterbox)
        {
            // 16:9 in the band, centred along with its words beside it.
            int creditsWidth = Even(Math.Min(bandHeight * 16 / 9.0, 1280 - (creditsText ? CaptionWidth : 0)));
            int creditsHeight = Even(creditsWidth * 9 / 16.0);
            int group = creditsWidth + (creditsText ? CaptionWidth : 0);
            int left = Even((1280 - group) / 2.0);
            bandCredits = new Rect(left, Even(band.Y + (bandHeight - creditsHeight) / 2.0), creditsWidth, creditsHeight);
            if (creditsText)
            {
                bandCaption = new Rect(bandCredits.Right + 24, bandCredits.Y, CaptionWidth - 48, creditsHeight);
            }
        }
        else
        {
            bandCredits = creditsText ? band with { Width = 1280 - CaptionWidth } : band;
            if (creditsText)
            {
                bandCaption = new Rect(bandCredits.Right + 24, band.Y + 16, CaptionWidth - 48, bandHeight - 32);
            }
        }

        Rect rest = new(0, side == CreditsSide.Top ? bandHeight : 0, 1280, 720 - bandHeight);
        if (!panelText)
        {
            return new SqueezeLayout(1280, 720, SqueezeStyle.SideBySide, bandCredits, rest, rest, Rect.None, true, bandCaption) { Strip = band };
        }

        int pictureHeight = Even(Math.Max(60, rest.Height - 64));
        Rect slot = new(Margin, Even(rest.Y + (rest.Height - pictureHeight) / 2.0), Even(pictureHeight * 16 / 9.0), pictureHeight);
        return new SqueezeLayout(1280, 720, SqueezeStyle.SideBySide, bandCredits, rest, slot,
            new Rect(slot.Right + 32, slot.Y, 1280 - slot.Right - 32 - Margin, pictureHeight), true, bandCaption) { Strip = band };
    }

    private static Rect Scale(Rect rect, double scale) =>
        rect.IsEmpty ? Rect.None : new(Even(rect.X * scale), Even(rect.Y * scale), Even(rect.Width * scale), Even(rect.Height * scale));

    public static int Even(double value) => (int)Math.Round(value / 2) * 2;
}
