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
            FadeSeconds = 1,
            Additive = true
        }),
        new("rating", "Rating badge", "The programme's rating (TV-Y, TV-PG…) in the top corner as it starts: choose a picture for each rating.", () => new Overlay
        {
            Name = "Rating",
            Kind = OverlayKinds.File,
            Anchor = "top_left",
            SizePercent = 7,
            MarginXPercent = 3,
            MarginYPercent = 4,
            When = OverlayWhen.Start,
            OffsetSeconds = 2,
            DurationSeconds = 15,
            FadeSeconds = 0.6,
            Additive = true,
            Variants =
            [
                .. new[] { "TV-Y", "TV-Y7", "TV-G", "TV-PG", "TV-14", "TV-MA" }
                    .Select(rating => new OverlayVariant { Name = rating, Ratings = [rating] })
            ]
        }),
        new("bug-text", "Screen bug (the channel's name)", "The channel's name in a corner, softly, all the way through.", () => new Overlay
        {
            Name = "Name bug",
            Opacity = 75,
            When = OverlayWhen.Always,
            FadeSeconds = 1,
            Additive = true,
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
            Additive = false,
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
            Additive = false,
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
        new("new-up-next", "New episode up next", "\"All-new\" and the show's name near the end of the programme before a new episode.", () => new Overlay
        {
            Name = "New episode up next",
            Additive = false,
            When = OverlayWhen.End,
            OffsetSeconds = 45,
            DurationSeconds = 10,
            FadeSeconds = 0.6,
            Conditions = new OverlayConditions { Next = OverlayAiring.New },
            Layers =
            [
                new OverlayLayer { Name = "Bar", Kind = OverlayLayerKinds.Rect, X = 1180, Y = 860, Width = 660, Height = 120, Color = "#000000", CornerRadius = 12, Opacity = 70 },
                new OverlayLayer
                {
                    Name = "Label", Kind = OverlayLayerKinds.Text, Text = "ALL-NEW · UP NEXT · {next_time}", X = 1210, Y = 872, Width = 600, Height = 40,
                    FontSize = 28, Color = "#ff3d6e", Shadow = false
                },
                new OverlayLayer
                {
                    Name = "Next", Kind = OverlayLayerKinds.Text, Text = "{next}", X = 1210, Y = 912, Width = 600, Height = 56, FontSize = 46, Bold = true
                }
            ]
        }),
        new("new-bug", "New episode badge", "\"NEW\" in a corner for the first minute of a new episode.", () => new Overlay
        {
            Name = "New episode",
            Additive = true,
            When = OverlayWhen.Start,
            OffsetSeconds = 0,
            DurationSeconds = 60,
            FadeSeconds = 0.6,
            Conditions = new OverlayConditions { Episodes = OverlayAiring.New },
            Layers =
            [
                new OverlayLayer { Name = "Badge", Kind = OverlayLayerKinds.Rect, X = 80, Y = 60, Width = 180, Height = 70, Color = "#ff3d6e", CornerRadius = 10 },
                new OverlayLayer
                {
                    Name = "Label", Kind = OverlayLayerKinds.Text, Text = "NEW", X = 80, Y = 66, Width = 180, Height = 60, FontSize = 44, Bold = true, Align = "center"
                }
            ]
        }),
        new("blank", "Blank design", "An empty canvas to design on.", () => new Overlay { Name = "Overlay", Additive = false })
    ];
}
