namespace SchedularrTV.CreditSqueeze;

/// <summary>A box on the picture, in pixels.</summary>
public readonly record struct Rect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;
}

/// <summary>
/// Where things go on a squeeze of a given size: the credits' box, the panel (the rest), the picture slot in the panel
/// (where a promo plays, over the next programme's picture), and where the panel's words start. When
/// <see cref="TextBeside"/>, the words sit beside the picture (a wide, short panel) rather than under it. Every size is
/// even, as the video must be.
/// </summary>
public sealed record SqueezeLayout(int Width, int Height, SqueezeStyle Style, Rect Credits, Rect Panel, Rect Picture, Rect Text, bool TextBeside)
{
    public const int MinSplit = 30;
    public const int MaxSplit = 80;

    private const int Margin = 48;
    private const int Gap = 40;

    public static SqueezeLayout Of(SqueezeStyle style, CreditsSide side, int splitPercent, int width = 1280, int height = 720)
    {
        int split = Math.Clamp(splitPercent, MinSplit, MaxSplit);
        SqueezeLayout layout = style == SqueezeStyle.Framed ? Framed(side, split) : SideBySide(side, split);
        double scale = width / 1280.0;
        return layout with
        {
            Width = width,
            Height = height,
            Credits = Scale(layout.Credits, scale),
            Panel = Scale(layout.Panel, scale),
            Picture = Scale(layout.Picture, scale),
            Text = Scale(layout.Text, scale)
        };
    }

    /// <summary>Two boxes on a background, side by side (top and bottom count as left and right here).</summary>
    private static SqueezeLayout Framed(CreditsSide side, int split)
    {
        bool left = side is CreditsSide.Left or CreditsSide.Top;
        int usable = 1280 - 2 * Margin - Gap;
        int creditsWidth = Even(usable * Math.Clamp(split, 40, 75) / 100.0);
        int creditsHeight = Even(creditsWidth * 9 / 16.0);
        int panelWidth = usable - creditsWidth;

        // A little below centre, leaving room above for the channel's name and below for what's later.
        int creditsTop = Even(Math.Clamp((720 - creditsHeight) / 2.0 + 8, 110, 720 - creditsHeight - 90));
        int panelTop = Math.Min(creditsTop, 144);
        int creditsLeft = left ? Margin : Margin + panelWidth + Gap;
        int panelLeft = left ? Margin + creditsWidth + Gap : Margin;
        Rect picture = new(panelLeft, panelTop + 40, panelWidth, Even(panelWidth * 9 / 16.0));
        return new SqueezeLayout(1280, 720, SqueezeStyle.Framed,
            new Rect(creditsLeft, creditsTop, creditsWidth, creditsHeight),
            new Rect(panelLeft, panelTop, panelWidth, 600 - panelTop),
            picture,
            new Rect(panelLeft, picture.Bottom + 12, panelWidth, 600 - picture.Bottom - 12),
            false);
    }

    /// <summary>The credits against one edge, the panel filling the rest of the picture.</summary>
    private static SqueezeLayout SideBySide(CreditsSide side, int split)
    {
        if (side is CreditsSide.Left or CreditsSide.Right)
        {
            int creditsWidth = Even(1280 * split / 100.0);
            int creditsHeight = Even(creditsWidth * 9 / 16.0);
            Rect credits = new(side == CreditsSide.Left ? 0 : 1280 - creditsWidth, Even((720 - creditsHeight) / 2.0), creditsWidth, creditsHeight);
            Rect panel = new(side == CreditsSide.Left ? creditsWidth : 0, 0, 1280 - creditsWidth, 720);
            int inset = Math.Min(40, panel.Width / 10);
            int pictureWidth = Even(panel.Width - 2 * inset);
            Rect picture = new(Even(panel.X + inset), 96, pictureWidth, Even(pictureWidth * 9 / 16.0));
            return new SqueezeLayout(1280, 720, SqueezeStyle.SideBySide, credits, panel, picture,
                new Rect(picture.X, picture.Bottom + 12, pictureWidth, 720 - picture.Bottom - 40), false);
        }

        int bandHeight = Even(720 * split / 100.0);
        int bandWidth = Even(bandHeight * 16 / 9.0);
        Rect band = new(Even((1280 - bandWidth) / 2.0), side == CreditsSide.Top ? 0 : 720 - bandHeight, bandWidth, bandHeight);
        Rect rest = new(0, side == CreditsSide.Top ? bandHeight : 0, 1280, 720 - bandHeight);
        int pictureHeight = Even(Math.Max(60, rest.Height - 64));
        Rect slot = new(Margin, Even(rest.Y + (rest.Height - pictureHeight) / 2.0), Even(pictureHeight * 16 / 9.0), pictureHeight);
        return new SqueezeLayout(1280, 720, SqueezeStyle.SideBySide, band, rest, slot,
            new Rect(slot.Right + 32, slot.Y, 1280 - slot.Right - 32 - Margin, pictureHeight), true);
    }

    private static Rect Scale(Rect rect, double scale) =>
        new(Even(rect.X * scale), Even(rect.Y * scale), Even(rect.Width * scale), Even(rect.Height * scale));

    public static int Even(double value) => (int)Math.Round(value / 2) * 2;
}
