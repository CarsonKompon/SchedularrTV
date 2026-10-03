namespace SchedularrTV.Overlays;

/// <summary>Ready-made overlays to start from: each is an ordinary overlay, changed freely once added.</summary>
public static class OverlayPresets
{
    public sealed record Preset(string Key, string Name, string Description, Func<Overlay> Create);

    public static IReadOnlyList<Preset> All { get; } =
    [
        new("bug-file", "Screen bug (a logo file)", "The channel's logo in a corner, all the way through every programme.", () => new Overlay
        {
            Name = "Screen bug",
            Kind = OverlayKinds.File,
            Anchor = "bottom_right",
            SizePercent = 9,
            MarginXPercent = 3,
            MarginYPercent = 4,
            Opacity = 70,
            When = OverlayWhen.Always,
            FadeSeconds = 1
        }),
        new("bug-text", "Screen bug (the channel's name)", "The channel's name in a corner, softly, all the way through.", () => new Overlay
        {
            Name = "Name bug",
            Opacity = 75,
            When = OverlayWhen.Always,
            FadeSeconds = 1,
            Layers =
            [
                new OverlayLayer
                {
                    Name = "Channel", Kind = OverlayLayerKinds.Text, Text = "{channel}", X = 1460, Y = 950, Width = 400, Height = 70,
                    FontSize = 44, Align = "right", Bold = true
                }
            ]
        }),
        new("lower-third", "Lower third", "The show's name and episode across the bottom left, a few seconds after it starts.", () => new Overlay
        {
            Name = "Lower third",
            When = OverlayWhen.Start,
            OffsetSeconds = 10,
            DurationSeconds = 8,
            FadeSeconds = 0.6,
            Layers =
            [
                new OverlayLayer { Name = "Bar", Kind = OverlayLayerKinds.Rect, X = 80, Y = 830, Width = 940, Height = 140, Color = "#14142b", CornerRadius = 10, Opacity = 88 },
                new OverlayLayer { Name = "Stripe", Kind = OverlayLayerKinds.Rect, X = 80, Y = 830, Width = 14, Height = 140, Color = "#ff3d6e" },
                new OverlayLayer
                {
                    Name = "Show", Kind = OverlayLayerKinds.Text, Text = "{title}", X = 124, Y = 842, Width = 860, Height = 70, FontSize = 56, Bold = true
                },
                new OverlayLayer
                {
                    Name = "Episode", Kind = OverlayLayerKinds.Text, Text = "{season_episode}  {episode}", X = 124, Y = 912, Width = 860, Height = 46,
                    FontSize = 32, Bold = false, Color = "#d6d6ea", Shadow = false
                }
            ]
        }),
        new("up-next", "Coming up next", "What's on next, near the end of each programme.", () => new Overlay
        {
            Name = "Up next",
            When = OverlayWhen.End,
            OffsetSeconds = 45,
            DurationSeconds = 10,
            FadeSeconds = 0.6,
            Layers =
            [
                new OverlayLayer { Name = "Bar", Kind = OverlayLayerKinds.Rect, X = 1180, Y = 860, Width = 660, Height = 120, Color = "#000000", CornerRadius = 12, Opacity = 70 },
                new OverlayLayer
                {
                    Name = "Label", Kind = OverlayLayerKinds.Text, Text = "UP NEXT · {next_time}", X = 1210, Y = 872, Width = 600, Height = 40,
                    FontSize = 28, Color = "#ffd34d", Shadow = false
                },
                new OverlayLayer
                {
                    Name = "Next", Kind = OverlayLayerKinds.Text, Text = "{next}", X = 1210, Y = 912, Width = 600, Height = 56, FontSize = 46, Bold = true
                }
            ]
        }),
        new("blank", "Blank design", "An empty canvas to design on.", () => new Overlay { Name = "Overlay" })
    ];
}
