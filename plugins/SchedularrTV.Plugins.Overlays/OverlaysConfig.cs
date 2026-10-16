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

/// <summary>Independent appearances, plus an optional automatic cycle. Null on old overlays until edited.</summary>
public sealed class OverlaySchedule
{
    public bool Always { get; set; }
    public List<OverlaySlot> Slots { get; set; } = [];
    public OverlayAutomatic Automatic { get; set; } = new();

    public OverlaySchedule Copy() => new()
    {
        Always = Always,
        Slots = [.. Slots.Select(slot => slot.Copy())],
        Automatic = Automatic.Copy()
    };

    public static OverlaySchedule FromLegacy(Overlay overlay) => new()
    {
        Always = overlay.When == OverlayWhen.Always,
        Slots = overlay.When is OverlayWhen.Start or OverlayWhen.End or OverlayWhen.Percent
            ? [new OverlaySlot { When = overlay.When, OffsetSeconds = overlay.OffsetSeconds, Percent = overlay.Percent, DurationSeconds = overlay.DurationSeconds }]
            : [],
        Automatic = new OverlayAutomatic
        {
            Enabled = overlay.When is OverlayWhen.Every or OverlayWhen.Clock,
            ByClock = overlay.When == OverlayWhen.Clock,
            EveryMinutes = overlay.EveryMinutes,
            OffsetSeconds = overlay.OffsetSeconds,
            DurationSeconds = overlay.DurationSeconds
        }
    };
}

public sealed class OverlaySlot
{
    /// <summary>Start, end (the slot ends this far before the programme ends), or percent.</summary>
    public string When { get; set; } = OverlayWhen.Start;
    public double OffsetSeconds { get; set; }
    public double Percent { get; set; } = 50;
    public double DurationSeconds { get; set; } = 8;
    public OverlaySlot Copy() => (OverlaySlot)MemberwiseClone();
}

public sealed class OverlayAutomatic
{
    public bool Enabled { get; set; }
    public bool ByClock { get; set; }
    public double EveryMinutes { get; set; } = 10;
    public double OffsetSeconds { get; set; }
    public double DurationSeconds { get; set; } = 8;
    public OverlayAutomatic Copy() => (OverlayAutomatic)MemberwiseClone();
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

    /// <summary>Named variable image used instead of File for a screenbug.</summary>
    public string? VariableImage { get; set; }

    /// <summary>Show selector: watching, next, next2…next10. Null uses the variable's usual context.</summary>
    public string? VariableImageShow { get; set; }

    /// <summary>
    /// Show-dependent images used by the file screenbug and design image layers. Each picks one of the schedule's shared
    /// images (<c>settings.plugins.generated-promos.shared_images</c>), the same ones generated promos use.
    /// </summary>
    public List<GeneratedVariableImage> VariableImages { get; set; } = [];

    /// <summary>Only in an exported package: the shared images its variable images use, so it works on its own.</summary>
    public List<GeneratedSharedImage>? SharedImages { get; set; }

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

    /// <summary>Multiple timing rules. When absent, the legacy single-rule fields below still apply.</summary>
    public OverlaySchedule? Schedule { get; set; }

    /// <summary><see cref="OverlayWhen"/>.</summary>
    public string When { get; set; } = OverlayWhen.Always;

    public double OffsetSeconds { get; set; }

    public double Percent { get; set; } = 50;

    /// <summary>How long it shows each time, fades included (not used for <see cref="OverlayWhen.Always"/>).</summary>
    public double DurationSeconds { get; set; } = 8;

    public double EveryMinutes { get; set; } = 10;

    /// <summary>Fade in and fade out, each.</summary>
    public double FadeSeconds { get; set; } = 1;

    /// <summary>
    /// For a design shown all the way through with keyframed layers: how long its animation runs from the start of each
    /// part of the programme, after which it stays as it is at the end. Null is <see cref="DefaultAnimationSeconds"/>.
    /// Timed overlays animate over each appearance instead.
    /// </summary>
    public double? AnimationSeconds { get; set; }

    public const double DefaultAnimationSeconds = 10;

    /// <summary>
    /// Frames a second for the editor's timeline and an animated design's video, so keyframes land on the same frames as
    /// its videos'; null is 30.
    /// </summary>
    public double? Fps { get; set; }

    /// <summary><see cref="OverlayAppliesTo"/>.</summary>
    public string AppliesTo { get; set; } = OverlayAppliesTo.Programmes;

    /// <summary>
    /// For a file: other pictures shown instead of <see cref="File"/> when their conditions are met (a rating badge for
    /// TV-Y shows, a pumpkin bug through Halloween). The first that matches wins; with none, <see cref="File"/> shows,
    /// and with no <see cref="File"/> either, nothing does.
    /// </summary>
    public List<OverlayVariant> Variants { get; set; } = [];

    /// <summary>When the overlay may show at all: new episodes only, films, Up Next promos… Null shows it wherever its timing does.</summary>
    public OverlayConditions? Conditions { get; set; }

    /// <summary>
    /// True: always shows, alongside whatever else is on screen. False: where it would be on screen at the same time as
    /// other non-additive overlays, one of them is picked at random and the rest stand down. Null (overlays saved before
    /// this setting) counts as additive, so they keep stacking as they did.
    /// </summary>
    public bool? Additive { get; set; }

    public Overlay Copy()
    {
        var copy = (Overlay)MemberwiseClone();
        copy.Conditions = Conditions?.Copy();
        copy.Layers = [.. Layers.Select(layer => layer.Copy())];
        copy.Variants = [.. Variants.Select(variant => variant.Copy())];
        copy.Schedule = Schedule?.Copy();
        // Shared images stay linked: the copy names the same ones.
        copy.VariableImages = [.. VariableImages.Select(image => image.Copy())];
        copy.SharedImages = SharedImages?.Select(image => new GeneratedSharedImage
        {
            Id = image.Id, Name = image.Name, DefaultPath = image.DefaultPath, Automatic = image.Automatic?.Copy(),
            Shows = [.. image.Shows.Select(show => new GeneratedShowImage { Show = show.Show, Block = show.Block, Path = show.Path })]
        }).ToList();
        return copy;
    }
}

/// <summary>
/// Another picture for a file overlay, and when it's used instead. Every condition that's set must hold (an empty one
/// isn't checked); they're judged at the start of each entry, in the channel's time zone.
/// </summary>
public sealed class OverlayVariant
{
    public string Name { get; set; } = "Variant";

    public bool Enabled { get; set; } = true;

    public string? File { get; set; }

    /// <summary>The programme's content rating is one of these (TV-Y, TV-PG, PG-13…), ignoring case and dashes.</summary>
    public List<string> Ratings { get; set; } = [];

    /// <summary>The programme matches this library query (<c>show:'Rugrats'</c>, <c>tag:'Halloween'</c>).</summary>
    public string? Query { get; set; }

    /// <summary>Within one of these holidays, as set up under Settings (Christmas, Halloween…).</summary>
    public List<string> Holidays { get; set; } = [];

    /// <summary>In one of these seasons: Winter, Spring, Summer, Fall.</summary>
    public List<string> Seasons { get; set; } = [];

    /// <summary>In one of these months, 1 to 12.</summary>
    public List<int> Months { get; set; } = [];

    /// <summary>On or after this day of the year, <c>MM-DD</c> (with <see cref="DateTo"/>, a range that may wrap the new year).</summary>
    public string? DateFrom { get; set; }

    /// <summary>On or before this day of the year, <c>MM-DD</c>.</summary>
    public string? DateTo { get; set; }

    /// <summary>On one of these days of the week.</summary>
    public List<DayOfWeek> Days { get; set; } = [];

    /// <summary>At or after this time of day (with <see cref="TimeTo"/>, a range that may cross midnight).</summary>
    public TimeSpan? TimeFrom { get; set; }

    /// <summary>Before this time of day.</summary>
    public TimeSpan? TimeTo { get; set; }

    /// <summary>Whether any condition is set (one without any would always win).</summary>
    [YamlDotNet.Serialization.YamlIgnore]
    public bool HasConditions =>
        Ratings.Count > 0 || !string.IsNullOrWhiteSpace(Query) || Holidays.Count > 0 || Seasons.Count > 0 || Months.Count > 0
        || !string.IsNullOrWhiteSpace(DateFrom) || !string.IsNullOrWhiteSpace(DateTo) || Days.Count > 0 || TimeFrom is not null || TimeTo is not null;

    public OverlayVariant Copy()
    {
        var copy = (OverlayVariant)MemberwiseClone();
        copy.Ratings = [.. Ratings];
        copy.Holidays = [.. Holidays];
        copy.Seasons = [.. Seasons];
        copy.Months = [.. Months];
        copy.Days = [.. Days];
        return copy;
    }
}

/// <summary>Kinds of box on a design's canvas.</summary>
public static class OverlayLayerKinds
{
    public const string Text = "text";
    public const string Image = "image";
    public const string Rect = "rect";

    /// <summary>
    /// A video file (with an alpha channel, for an animated design to put text over). It isn't drawn into the design's
    /// picture: it goes to ErsatzTV as a graphics layer of its own, placed where its box is, between the layers drawn
    /// under and over it.
    /// </summary>
    public const string Video = "video";

    /// <summary>
    /// Draws nothing itself: layers whose <see cref="OverlayLayer.Parent"/> is this group are placed, turned, faded and
    /// timed inside it, so moving or animating the group carries them along while they keep their own animation.
    /// </summary>
    public const string Group = "group";
}

/// <summary>A layer's pose at one moment of an appearance; between keyframes it moves from one to the next.</summary>
public sealed class OverlayKeyframe
{
    /// <summary>From the start of the overlay's appearance (not of the layer).</summary>
    public TimeSpan At { get; set; }

    public float X { get; set; }

    public float Y { get; set; }

    public float Width { get; set; } = 400;

    public float Height { get; set; } = 80;

    /// <summary>0 to 100, like the layer's own.</summary>
    public double Opacity { get; set; } = 100;

    /// <summary>Degrees clockwise about the middle of the box.</summary>
    [YamlDotNet.Serialization.YamlMember(DefaultValuesHandling = YamlDotNet.Serialization.DefaultValuesHandling.OmitDefaults)]
    public float Rotation { get; set; }

    /// <summary>How it moves from here to the next keyframe.</summary>
    public GeneratedEasing Easing { get; set; } = GeneratedEasing.Linear;

    public OverlayKeyframe Copy() => (OverlayKeyframe)MemberwiseClone();
}

/// <summary>A box on a design's canvas: text, a picture, a shape, a video or a group, in canvas pixels.</summary>
public sealed class OverlayLayer
{
    /// <summary>Stable identity, for groups (<see cref="Parent"/>) and mask targets; given when needed.</summary>
    public string? Id { get; set; }

    /// <summary>
    /// The <see cref="Id"/> of the <see cref="OverlayLayerKinds.Group"/> this layer is inside; null at the top. Its
    /// position is then in the group's space (from the group's top-left corner). The editor keeps a group's layers
    /// right after it in the list.
    /// </summary>
    public string? Parent { get; set; }

    /// <summary>Locked in the editor: it can't be picked or moved on the canvas. Only for editing.</summary>
    [YamlDotNet.Serialization.YamlMember(DefaultValuesHandling = YamlDotNet.Serialization.DefaultValuesHandling.OmitDefaults)]
    public bool Locked { get; set; }

    /// <summary>When set, this shape or picture masks its target instead of being drawn.</summary>
    public GeneratedLayerMask? Mask { get; set; }

    /// <summary>A shape's outline; null is a (rounded) rectangle.</summary>
    public GeneratedShape? Shape { get; set; }

    /// <summary>A shape's feathered edge, in pixels; null is sharp.</summary>
    public float? SoftEdges { get; set; }

    /// <summary>Degrees clockwise about the middle of the box; null is 0. Keyframes animate it.</summary>
    public float? Rotation { get; set; }

    /// <summary>
    /// While there are any, they set position, size, turn and opacity over each appearance instead of the values here
    /// (kept for when animating is stopped).
    /// </summary>
    public List<OverlayKeyframe>? Keyframes { get; set; }

    public string Name { get; set; } = "Layer";

    /// <summary><see cref="OverlayLayerKinds"/>.</summary>
    public string Kind { get; set; } = OverlayLayerKinds.Text;

    public float X { get; set; }

    public float Y { get; set; }

    public float Width { get; set; } = 400;

    public float Height { get; set; } = 80;

    /// <summary>For text: may hold {title}, {episode}, {season_episode}, {year}, {channel}, {next} and {next_time}.</summary>
    public string? Text { get; set; }

    /// <summary>For an image or a video.</summary>
    public string? Path { get; set; }

    /// <summary>For an image: a named variable image instead of Path.</summary>
    public string? VariableImage { get; set; }

    /// <summary>Show selector for this layer: watching, next, next2…next10. Null uses the variable's usual context.</summary>
    public string? VariableImageShow { get; set; }
    /// <summary>
    /// Comes in this long after the overlay does (fading in then), so text can wait for a video's design to animate in.
    /// </summary>
    public double InSeconds { get; set; }

    /// <summary>Goes this long before the overlay does (fading out then), so text can clear before a video animates out.</summary>
    public double OutSeconds { get; set; }

    /// <summary>#rrggbb: the text's colour, or the block's.</summary>
    public string Color { get; set; } = "#ffffff";

    public string FontFamily { get; set; } = "Arial";

    /// <summary>The largest size; long text shrinks to fit the box.</summary>
    public float FontSize { get; set; } = 48;

    public bool Bold { get; set; } = true;

    /// <summary>left, center or right.</summary>
    public string Align { get; set; } = "left";

    /// <summary>
    /// Where the text sits up and down its box; null is the middle (where overlay text always sat), so older designs
    /// keep their look and their drawings.
    /// </summary>
    public GeneratedVerticalAlign? VerticalAlign { get; set; }

    /// <summary>Upper or lower cases every letter (filled-in tokens and AI-written text too); null keeps it as written.</summary>
    public GeneratedTextCase? TextCase { get; set; }

    /// <summary>Breaks long text between words (and at new lines) to fill the box before it shrinks; null is off.</summary>
    public bool? WordWrap { get; set; }

    /// <summary><see cref="Align"/> as the shared alignment pad reads it.</summary>
    [YamlDotNet.Serialization.YamlIgnore, System.Text.Json.Serialization.JsonIgnore]
    public GeneratedTextAlign HorizontalAlign
    {
        get => Align switch
        {
            "center" => GeneratedTextAlign.Center,
            "right" => GeneratedTextAlign.Right,
            _ => GeneratedTextAlign.Left
        };
        set => Align = value.ToString().ToLowerInvariant();
    }

    public bool Shadow { get; set; } = true;

    public float CornerRadius { get; set; }

    /// <summary>0 to 100.</summary>
    public double Opacity { get; set; } = 100;

    /// <summary>For text: has a language model write it for each programme, with <see cref="Text"/> as the fallback.</summary>
    public AiTextConfig? Writer { get; set; }

    public OverlayLayer Copy()
    {
        var copy = (OverlayLayer)MemberwiseClone();
        copy.Mask = Mask is null ? null : new GeneratedLayerMask { Mode = Mask.Mode, Target = Mask.Target };
        copy.Keyframes = Keyframes?.Select(key => key.Copy()).ToList();        copy.Writer = Writer is null ? null : new AiTextConfig
        {
            Generator = Writer.Generator,
            Prompt = Writer.Prompt,
            Style = Writer.Style,
            UseDescriptions = Writer.UseDescriptions,
            MaxWords = Writer.MaxWords,
            Chance = Writer.Chance
        };
        return copy;
    }
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
