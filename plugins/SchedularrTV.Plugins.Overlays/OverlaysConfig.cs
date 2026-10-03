using SchedularrTV.Core.Configuration;

namespace SchedularrTV.Overlays;

/// <summary>
/// The plugin's section of a channel or block in the schedule (<c>plugins: overlays:</c>): the overlays it shows, or
/// which channel it takes them from.
/// </summary>
/// <remarks>
/// On a block, no section means the channel's overlays; <c>enabled: false</c> means none in that block. <c>from</c>
/// follows another channel's own overlays live, so editing them there changes them here too.
/// </remarks>
public sealed class OverlaysSection
{
    public bool Enabled { get; set; } = true;

    /// <summary>A channel id whose overlays are shown instead of these, kept in step with it.</summary>
    public string? From { get; set; }

    public List<Overlay> Overlays { get; set; } = [];

    public OverlaysSection Copy() => new()
    {
        Enabled = Enabled,
        From = From,
        Overlays = [.. Overlays.Select(overlay => overlay.Copy())]
    };
}

/// <summary>Kinds of overlay.</summary>
public static class OverlayKinds
{
    /// <summary>Drawn here from boxes of text, images and colour, on a frame-sized canvas.</summary>
    public const string Design = "design";

    /// <summary>A picture or video file (a channel logo, an animated bug) placed at a corner or edge.</summary>
    public const string File = "file";
}

/// <summary>When an overlay shows, measured through each programme.</summary>
public static class OverlayWhen
{
    /// <summary>The whole programme (fading in and out around its breaks, when it fades).</summary>
    public const string Always = "always";

    /// <summary>Once, <see cref="Overlay.OffsetSeconds"/> after the programme starts.</summary>
    public const string Start = "start";

    /// <summary>Once, ending <see cref="Overlay.OffsetSeconds"/> before the programme ends.</summary>
    public const string End = "end";

    /// <summary>Once, starting <see cref="Overlay.Percent"/>% of the way through.</summary>
    public const string Percent = "percent";

    /// <summary>Every <see cref="Overlay.EveryMinutes"/> through the programme, first at <see cref="Overlay.OffsetSeconds"/>.</summary>
    public const string Every = "every";

    /// <summary>Every <see cref="Overlay.EveryMinutes"/> by the clock (e.g. on the hour and half hour), whatever is on.</summary>
    public const string Clock = "clock";

    public static readonly string[] All = [Always, Start, End, Percent, Every, Clock];
}

/// <summary>Which entries an overlay is on.</summary>
public static class OverlayAppliesTo
{
    /// <summary>Programmes only, not breaks (commercials, bumpers, promos).</summary>
    public const string Programmes = "programmes";

    /// <summary>Everything that airs, breaks included.</summary>
    public const string Everything = "everything";
}

/// <summary>Where a file overlay sits: ErsatzTV's nine anchors.</summary>
public static class OverlayAnchors
{
    public static readonly string[] All =
        ["top_left", "top_center", "top_right", "center_left", "center", "center_right", "bottom_left", "bottom_center", "bottom_right"];
}

/// <summary>One screen bug, lower third or other graphic, and when it shows.</summary>
public sealed class Overlay
{
    public string Name { get; set; } = "Overlay";

    public bool Enabled { get; set; } = true;

    /// <summary><see cref="OverlayKinds"/>.</summary>
    public string Kind { get; set; } = OverlayKinds.Design;

    // --- Design ---

    /// <summary>The canvas, matching the channel's picture shape (16:9 by default).</summary>
    public int Width { get; set; } = 1920;

    public int Height { get; set; } = 1080;

    /// <summary>Bottom first.</summary>
    public List<OverlayLayer> Layers { get; set; } = [];

    // --- File ---

    public string? File { get; set; }

    /// <summary><see cref="OverlayAnchors"/>.</summary>
    public string Anchor { get; set; } = "bottom_right";

    /// <summary>Width as a percent of the picture's.</summary>
    public double SizePercent { get; set; } = 12;

    public double MarginXPercent { get; set; } = 3;

    public double MarginYPercent { get; set; } = 4;

    /// <summary>Keeps the file inside the programme's picture when it's letterboxed or pillarboxed.</summary>
    public bool InsidePicture { get; set; } = true;

    // --- Both ---

    /// <summary>0 to 100.</summary>
    public double Opacity { get; set; } = 100;

    // --- When ---

    /// <summary><see cref="OverlayWhen"/>.</summary>
    public string When { get; set; } = OverlayWhen.Always;

    public double OffsetSeconds { get; set; }

    public double Percent { get; set; } = 50;

    /// <summary>How long it shows each time, fades included (not used for <see cref="OverlayWhen.Always"/>).</summary>
    public double DurationSeconds { get; set; } = 8;

    public double EveryMinutes { get; set; } = 10;

    /// <summary>Fade in and fade out, each.</summary>
    public double FadeSeconds { get; set; } = 1;

    /// <summary><see cref="OverlayAppliesTo"/>.</summary>
    public string AppliesTo { get; set; } = OverlayAppliesTo.Programmes;

    public Overlay Copy()
    {
        var copy = (Overlay)MemberwiseClone();
        copy.Layers = [.. Layers.Select(layer => layer.Copy())];
        return copy;
    }
}

/// <summary>Kinds of box on a design's canvas.</summary>
public static class OverlayLayerKinds
{
    public const string Text = "text";
    public const string Image = "image";
    public const string Rect = "rect";
}

/// <summary>A box on a design's canvas: text, a picture or a block of colour, in canvas pixels.</summary>
public sealed class OverlayLayer
{
    public string Name { get; set; } = "Layer";

    /// <summary><see cref="OverlayLayerKinds"/>.</summary>
    public string Kind { get; set; } = OverlayLayerKinds.Text;

    public float X { get; set; }

    public float Y { get; set; }

    public float Width { get; set; } = 400;

    public float Height { get; set; } = 80;

    /// <summary>For text: may hold {title}, {episode}, {season_episode}, {year}, {channel}, {next} and {next_time}.</summary>
    public string? Text { get; set; }

    /// <summary>For an image.</summary>
    public string? Path { get; set; }

    /// <summary>#rrggbb: the text's colour, or the block's.</summary>
    public string Color { get; set; } = "#ffffff";

    public string FontFamily { get; set; } = "Arial";

    /// <summary>The largest size; long text shrinks to fit the box.</summary>
    public float FontSize { get; set; } = 48;

    public bool Bold { get; set; } = true;

    /// <summary>left, center or right.</summary>
    public string Align { get; set; } = "left";

    public bool Shadow { get; set; } = true;

    public float CornerRadius { get; set; }

    /// <summary>0 to 100.</summary>
    public double Opacity { get; set; } = 100;

    public OverlayLayer Copy() => (OverlayLayer)MemberwiseClone();
}

/// <summary>The overlays a channel or block shows, after following blocks to channels and links to other channels.</summary>
/// <param name="Owner">The channel they're set up on (another channel's, when linked): its name is <c>{channel}</c>.</param>
public sealed record ResolvedOverlays(IReadOnlyList<Overlay> Overlays, ChannelConfig Owner);

public static class OverlayResolver
{
    public const string PluginId = "overlays";

    public static OverlaysSection? Read(IReadOnlyDictionary<string, object?>? sections) =>
        PluginSettings.Read<OverlaysSection>(sections, PluginId);

    /// <summary>
    /// The block's section, else the channel's; then, while it names another channel, that channel's own section.
    /// Null when it's off, empty, or a link is missing or goes round in a loop.
    /// </summary>
    public static ResolvedOverlays? Resolve(SchedulerConfig config, ChannelConfig channel, BlockConfig? block)
    {
        OverlaysSection? section = (block is null ? null : Read(block.Plugins)) ?? Read(channel.Plugins);
        ChannelConfig owner = channel;
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase) { channel.Id };
        while (section is { Enabled: true, From: { Length: > 0 } from })
        {
            ChannelConfig? source = config.Channels.FirstOrDefault(candidate => string.Equals(candidate.Id, from, StringComparison.OrdinalIgnoreCase));
            if (source is null || !visited.Add(source.Id))
            {
                return null;
            }

            owner = source;
            section = Read(source.Plugins);
        }

        if (section is not { Enabled: true } || section.Overlays.Count == 0)
        {
            return null;
        }

        List<Overlay> overlays = [.. section.Overlays.Where(overlay => overlay.Enabled)];
        return overlays.Count == 0 ? null : new ResolvedOverlays(overlays, owner);
    }

    /// <summary>Why a link can't be followed (a missing channel, or a loop), or null when it can.</summary>
    public static string? LinkProblem(SchedulerConfig config, ChannelConfig channel, OverlaysSection? section)
    {
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase) { channel.Id };
        while (section is { Enabled: true, From: { Length: > 0 } from })
        {
            ChannelConfig? source = config.Channels.FirstOrDefault(candidate => string.Equals(candidate.Id, from, StringComparison.OrdinalIgnoreCase));
            if (source is null)
            {
                return $"There's no channel '{from}' to take overlays from.";
            }

            if (!visited.Add(source.Id))
            {
                return $"{source.Name} takes its overlays from this one, round in a loop, so neither shows any.";
            }

            section = Read(source.Plugins);
        }

        return null;
    }
}
