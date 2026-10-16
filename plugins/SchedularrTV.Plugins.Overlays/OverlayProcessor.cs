using System.Globalization;
using System.Text.Json.Nodes;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Publishing;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Plugins.Playout;
using SchedularrTV.Service.Writing;

namespace SchedularrTV.Overlays;

/// <summary>
/// Puts each channel's overlays on its entries as they go to ErsatzTV: for every entry an overlay shows on, a graphics
/// layer (the drawn PNG or the file, where it sits, when it fades in and out) in the entry's <c>ersatztv.graphics</c>
/// annotation, which the ErsatzTV output writes into the playout.
/// </summary>
public sealed class OverlayProcessor : IPlayoutProcessor
{
    /// <summary>The ErsatzTV output's annotation for graphics layers.</summary>
    public const string GraphicsAnnotation = "ersatztv.graphics";

    private readonly OverlayRenderer renderer;
    private readonly ILogger<OverlayProcessor> logger;
    private readonly AiTextWriter? writer;
    private readonly OverlayAnimations? animations;
    private readonly SchedularrTV.Service.Images.AutomaticImages? automaticImages;

    private readonly OverlayPreparationSettings? preparation;
    private readonly OverlayPreparationMonitor? preparationMonitor;
    private readonly IChannelViewers? viewers;
    private readonly TimeProvider time;

    /// <param name="writer">Writes AI-written text layers; without it they show their usual text.</param>
    /// <param name="republisher">Publishes again when AI-written captions (or animated designs' videos) are ready, so they're used.</param>
    /// <param name="animations">Makes animated designs into videos; without it they show standing still.</param>
    public OverlayProcessor(OverlayRenderer renderer, ILogger<OverlayProcessor> logger, AiTextWriter? writer = null,
        IScheduleRepublisher? republisher = null, OverlayAnimations? animations = null,
        SchedularrTV.Service.Images.AutomaticImages? automaticImages = null, OverlayPreparationSettings? preparation = null,
        OverlayPreparationMonitor? preparationMonitor = null, IChannelViewers? viewers = null, TimeProvider? timeProvider = null)
    {
        this.renderer = renderer;
        this.logger = logger;
        this.writer = writer;
        this.animations = animations;
        this.automaticImages = automaticImages;
        this.preparation = preparation;
        this.preparationMonitor = preparationMonitor;
        this.viewers = viewers;
        time = timeProvider ?? TimeProvider.System;
        if (animations is not null && republisher is not null)
        {
            animations.Rendered += republisher.RequestRepublish;
        }

        if (automaticImages is not null && republisher is not null)
        {
            automaticImages.Fetched += _ => republisher.RequestRepublish();
        }

        if (writer is not null && republisher is not null)
        {
            // Captions are written in the background (each publish uses what's ready); a finished one is put on air
            // with the next publish, and requests close together are combined.
            writer.Written += (job, result) =>
            {
                if (job.Source == OverlayWriting.Source && result.Written)
                {
                    republisher.RequestRepublish();
                }
            };
        }
    }

    public string Name => OverlayResolver.PluginId;

    // After the processors that change what's in breaks, so overlays go on what actually airs.
    public int Order => 100;

    public ValueTask<ChannelSchedule> ProcessAsync(PlayoutContext context, CancellationToken cancellationToken)
    {
        if (context.Channel is not { } channel)
        {
            return ValueTask.FromResult(context.Schedule);
        }

        // Each block's overlays (and the channel's) are worked out once per publish, not once per entry.
        Dictionary<string, ResolvedOverlays?> byBlock = new(StringComparer.Ordinal);
        ResolvedOverlays? For(string? blockName)
        {
            string key = blockName ?? string.Empty;
            if (!byBlock.TryGetValue(key, out ResolvedOverlays? resolved))
            {
                BlockConfig? block = blockName is null ? null : channel.Blocks.FirstOrDefault(candidate => candidate.Name == blockName);
                byBlock[key] = resolved = OverlayResolver.Resolve(context.Config, channel, block);
            }

            return resolved;
        }

        List<ScheduleEntry> ordered = [.. context.Schedule.Entries.OrderBy(entry => entry.Start)];
        List<ScheduleEntry> result = new(ordered.Count);
        bool changed = false;
        DateTimeOffset now = time.GetUtcNow();
        List<DeferredOverlay> deferred = [];
        for (int index = 0; index < ordered.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScheduleEntry entry = ordered[index];
            if (For(entry.BlockName) is not { } resolved)
            {
                result.Add(entry);
                continue;
            }

            var layers = new JsonArray();
            Dictionary<string, string>? tokens = null;
            OverlayMoment? moment = null;
            (OverlayImageSubject? Current, IReadOnlyList<OverlayImageSubject> Upcoming)? imageSubjects = null;
            ScheduleEntry? next = Next(ordered, index);
            ScheduleEntry? programme = Programme(ordered, index);

            // Each overlay whose conditions hold and that shows on this entry, with its rules' appearances…
            List<OverlayContention.Candidate> candidates = [];
            foreach (Overlay candidate in resolved.Overlays)
            {
                if (!OverlayConditionMatcher.Matches(candidate, entry, programme, next))
                {
                    continue;
                }

                // A file none of whose pictures suits this entry shows nothing, so it can't win a turn either.
                if (candidate.Kind == OverlayKinds.File && candidate.Variants.Count > 0 && string.IsNullOrWhiteSpace(candidate.VariableImage)
                    && candidate.VariableImages.Count == 0)
                {
                    moment ??= OverlayMoment.At(entry.Start, context.Config, channel);
                    if (OverlayVariants.FileFor(candidate, entry.Item, moment) is null)
                    {
                        continue;
                    }
                }

                List<(Overlay Rule, OverlayShowing Showing)> rules = [];
                foreach (Overlay rule in OverlayTiming.Rules(candidate))
                {
                    if (OverlayTiming.For(rule, entry) is { } appearance)
                    {
                        rules.Add((rule, appearance));
                    }
                }

                if (rules.Count > 0)
                {
                    candidates.Add(new OverlayContention.Candidate(candidate, rules));
                }
            }

            // …then the ones that aren't additive take turns where they'd be on screen together.
            string contentionSeed = $"{context.Config.Settings.Seed}|{channel.Id}|{ProgrammeKey(ordered, index)}";
            foreach ((Overlay overlay, OverlayShowing showing) in OverlayContention.Resolve(candidates, entry, contentionSeed)
                         .SelectMany(candidate => candidate.Rules))
            {
                try
                {
                    bool prepare = preparation?.MayPrepare(channel.Number, entry, showing, now, viewers) ?? true;
                    if (!prepare && OverlayPreparationSettings.NextAppearance(entry, showing, now) is not null)
                        deferred.Add(new(entry, showing));
                    // A file with variants shows whichever picture this entry calls for (or nothing, when none does and it has no default).
                    Overlay shown = overlay;
                    if (overlay.VariableImages.Count > 0 || !string.IsNullOrWhiteSpace(overlay.VariableImage)
                        || overlay.Layers.Any(layer => !string.IsNullOrWhiteSpace(layer.VariableImage)))
                    {
                        // Variable images' automatic pictures show once found; programmes coming up soon have theirs looked
                        // for in the background, and the schedule is published again when they're found.
                        imageSubjects ??= OverlayImages.Programmes(ordered, index);
                        shown = OverlayImages.ResolveFor(overlay, imageSubjects.Value.Current, imageSubjects.Value.Upcoming,
                            context.Config.Settings.GeneratedPromosSharedImages, automaticImages?.Lookup(queue: prepare));
                    }
                    if (overlay.Kind == OverlayKinds.File && overlay.Variants.Count > 0)
                    {
                        moment ??= OverlayMoment.At(entry.Start, context.Config, channel);
                        string? file = OverlayVariants.FileFor(shown, entry.Item, moment);
                        if (file is null)
                        {
                            continue;
                        }

                        if (!string.Equals(file, shown.File, StringComparison.Ordinal))
                        {
                            shown = shown.Copy();
                            shown.File = file;
                        }
                    }

                    tokens ??= Tokens(entry, next, resolved.Owner);
                    foreach (JsonObject layer in Layers(Written(shown, entry, tokens, prepare), entry, showing, tokens, prepare: prepare))
                    {
                        layers.Add(layer);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Overlay {Overlay} on {Channel} couldn't be drawn; it's left off.", overlay.Name, channel.Name);
                }
            }

            if (layers.Count == 0)
            {
                result.Add(entry);
                continue;
            }

            result.Add(entry with { Annotations = WithGraphics(entry.Annotations, layers) });
            changed = true;
        }

        preparationMonitor?.Track(channel.Number, deferred);
        return ValueTask.FromResult(changed ? context.Schedule with { Entries = result } : context.Schedule);
    }

    /// <summary>
    /// An overlay's graphics layers on one entry, bottom first. A file is one; so is a design that's all drawn and comes
    /// and goes as a whole. A design with videos, or with parts timed on their own, is split: each video goes as its own
    /// layer, and the drawn parts between them as full-frame PNGs, each timed within the overlay's appearance.
    /// </summary>
    internal IEnumerable<JsonObject> Layers(Overlay overlay, ScheduleEntry entry, OverlayShowing showing, IReadOnlyDictionary<string, string> tokens,
        bool allowAnimation = true, bool prepare = true)
    {
        if (overlay.Kind == OverlayKinds.File)
        {
            if (Layer(overlay, showing, tokens, prepare) is { } file)
            {
                yield return file;
            }

            yield break;
        }

        if (allowAnimation && OverlayAnimation.IsAnimated(overlay))
        {
            foreach (JsonObject animated in Animated(overlay, entry, showing, tokens, prepare))
            {
                yield return animated;
            }

            yield break;
        }

        IReadOnlyList<(LayerTiming Timing, IReadOnlyList<OverlayLayer> Layers)> groups = OverlayRenderer.Groups(overlay);
        if (groups.Count == 1 && groups[0].Timing.IsWhole && groups[0].Layers.All(layer => layer.Kind != OverlayLayerKinds.Video))
        {
            if (Layer(overlay, showing, tokens, prepare) is { } whole)
            {
                yield return whole;
            }

            yield break;
        }

        foreach ((LayerTiming timing, IReadOnlyList<OverlayLayer> parts) in groups)
        {
            OverlayShowing? own = timing.IsWhole ? showing : OverlayTiming.For(overlay, entry, timing);
            if (own is null)
            {
                continue;
            }

            JsonObject? layer = parts.Count == 1 && parts[0].Kind == OverlayLayerKinds.Video
                ? VideoLayer(overlay, parts[0], own)
                : Layer(OverlayRenderer.Part(overlay, parts), own, tokens, prepare);
            if (layer is not null)
            {
                yield return layer;
            }
        }
    }

    /// <summary>
    /// An animated design's layers on one entry. Its drawn layers (each run of them between its videos) go as a video of
    /// one appearance, timed as the overlay is; a whole-programme one plays its animation as each part of the programme
    /// starts and then stays as it ends up. Until a video is made, the design shows standing still.
    /// </summary>
    private IEnumerable<JsonObject> Animated(Overlay overlay, ScheduleEntry entry, OverlayShowing showing, IReadOnlyDictionary<string, string> tokens, bool prepare)
    {
        bool whole = overlay.When == OverlayWhen.Always;
        double length = OverlayAnimation.Length(overlay);
        foreach (var (_, parts) in OverlayRenderer.Groups(overlay, byTiming: false))
        {
            if (parts.Count == 1 && parts[0].Kind == OverlayLayerKinds.Video)
            {
                if (OverlayTiming.For(overlay, entry, OverlayHierarchy.EffectiveTiming(overlay, parts[0])) is { } own && VideoLayer(overlay, parts[0], own) is { } video)
                {
                    yield return video;
                }

                continue;
            }

            Overlay part = OverlayRenderer.Part(overlay, parts);
            string? clip = animations?.Find(part, tokens, length, whole, queue: prepare);
            if (!whole)
            {
                JsonObject? layer = clip is not null
                    ? Placed(overlay, clip, showing)
                    // Standing still, as it is half way through, until its video is made.
                    : Layer(OverlayAnimation.Freeze(part, length / 2), showing, tokens, prepare);
                if (layer is not null)
                {
                    yield return layer;
                }

                continue;
            }

            // All the way through: the animation, then the design as it ends up, for the rest of each part. The still
            // comes in as the animation's last moments play, so there's no gap between them.
            Overlay held = OverlayAnimation.Freeze(part, length);
            if (clip is not null)
            {
                if (OverlayTiming.Opening(overlay, entry, length) is { } opening && Placed(overlay, clip, opening) is { } video)
                {
                    yield return video;
                }

                double from = Math.Max(0, length - Math.Max(0, overlay.FadeSeconds));
                foreach (OverlayLayer layer in held.Layers.Where(OverlayRenderer.IsDrawn))
                {
                    layer.InSeconds = Math.Max(layer.InSeconds, from);
                }
            }

            foreach (JsonObject still in Layers(held, entry, showing, tokens, allowAnimation: false, prepare: prepare))
            {
                yield return still;
            }
        }
    }

    /// <summary>A frame-sized picture or video of the design, covering the frame exactly.</summary>
    private static JsonObject Placed(Overlay overlay, string path, OverlayShowing showing)
    {
        var layer = new JsonObject
        {
            ["source"] = new JsonObject { ["source_type"] = "local", ["path"] = path },
            ["location"] = "top_left",
            ["width_percent"] = 100,
            ["horizontal_margin_percent"] = 0,
            ["vertical_margin_percent"] = 0
        };

        if (overlay.Opacity < 100)
        {
            layer["opacity_percent"] = Math.Clamp(overlay.Opacity, 0, 100);
        }

        if (showing.Timing is { } timing)
        {
            layer["timing"] = timing.ToJson();
        }

        return layer;
    }

    /// <summary>
    /// A design's video as ErsatzTV places a file: from its box's top-left, as wide as its box (ErsatzTV keeps the video's
    /// shape), on the whole frame rather than inside the programme's picture, the way the rest of the design is.
    /// </summary>
    private static JsonObject? VideoLayer(Overlay overlay, OverlayLayer video, OverlayShowing showing)
    {
        if (video.Path is not { Length: > 0 } path || video.Opacity <= 0)
        {
            return null;
        }

        double width = Math.Max(1, overlay.Width);
        double height = Math.Max(1, overlay.Height);
        // Inside a group, where the group puts it (ErsatzTV can't turn it, so only its place counts).
        OverlayBox box = OverlayHierarchy.World(overlay, video, 0);
        double left = box.CenterX - box.Width / 2;
        double top = box.CenterY - box.Height / 2;
        var layer = new JsonObject
        {
            ["source"] = new JsonObject { ["source_type"] = "local", ["path"] = path },
            ["location"] = "top_left",
            ["width_percent"] = Round(Math.Clamp(box.Width / width * 100, 1, 100)),
            ["horizontal_margin_percent"] = Round(Math.Clamp(left / width * 100, 0, 100)),
            ["vertical_margin_percent"] = Round(Math.Clamp(top / height * 100, 0, 100)),
            ["within_source_content"] = false
        };

        double opacity = Math.Clamp(overlay.Opacity, 0, 100) * Math.Clamp(video.Opacity, 0, 100) / 100;
        foreach (OverlayLayer group in OverlayHierarchy.Ancestors(overlay, video))
        {
            opacity *= Math.Clamp(group.Opacity, 0, 100) / 100;
        }
        if (opacity < 100)
        {
            layer["opacity_percent"] = Round(opacity);
        }

        if (showing.Timing is { } timing)
        {
            layer["timing"] = timing.ToJson();
        }

        return layer;
    }

    private static double Round(double value) => Math.Round(value, 3);

    /// <summary>One graphics layer in ErsatzTV's format: a design as a full-frame PNG, a file where it's anchored.</summary>
    internal JsonObject? Layer(Overlay overlay, OverlayShowing showing, IReadOnlyDictionary<string, string> tokens, bool prepare = true)
    {
        JsonObject layer;
        if (overlay.Kind == OverlayKinds.File)
        {
            if (overlay.File is not { Length: > 0 } file)
            {
                return null;
            }

            // An animated picture (a GIF) goes as a video made from it, once made (its still picture until then).
            if (SchedularrTV.Service.Images.AnimatedImages.IsAnimated(file) && animations?.Converted(file, queue: prepare) is { } video)
            {
                file = video;
            }

            layer = new JsonObject
            {
                ["source"] = new JsonObject { ["source_type"] = "local", ["path"] = file },
                ["location"] = OverlayAnchors.All.Contains(overlay.Anchor) ? overlay.Anchor : "bottom_right",
                ["width_percent"] = Math.Clamp(overlay.SizePercent, 1, 100),
                ["horizontal_margin_percent"] = Math.Clamp(overlay.MarginXPercent, 0, 100),
                ["vertical_margin_percent"] = Math.Clamp(overlay.MarginYPercent, 0, 100),
                ["within_source_content"] = overlay.InsidePicture
            };
        }
        else
        {
            if (overlay.Layers.Count == 0)
            {
                return null;
            }

            // Drawn on a frame-sized canvas, so it covers the frame exactly and everything sits where it was placed.
            string? picture = prepare ? renderer.Render(overlay, tokens) : renderer.Cached(overlay, tokens);
            if (picture is null) return null;
            layer = new JsonObject
            {
                ["source"] = new JsonObject { ["source_type"] = "local", ["path"] = picture },
                ["location"] = "top_left",
                ["width_percent"] = 100,
                ["horizontal_margin_percent"] = 0,
                ["vertical_margin_percent"] = 0
            };
        }

        if (overlay.Opacity < 100)
        {
            layer["opacity_percent"] = Math.Clamp(overlay.Opacity, 0, 100);
        }

        if (showing.Timing is { } timing)
        {
            layer["timing"] = timing.ToJson();
        }

        return layer;
    }

    /// <summary>
    /// The overlay with its AI-written text layers filled in where they're written; the rest keep their usual text
    /// (and are queued, for programmes coming up soon).
    /// </summary>
    private Overlay Written(Overlay overlay, ScheduleEntry entry, IReadOnlyDictionary<string, string> tokens, bool prepare)
    {
        if (writer is null || overlay.Kind == OverlayKinds.File || !overlay.Layers.Any(OverlayWriting.Writes))
        {
            return overlay;
        }

        Overlay copy = overlay.Copy();
        string identity = entry.Item?.Id ?? tokens.GetValueOrDefault("title") ?? string.Empty;
        foreach (OverlayLayer layer in copy.Layers.Where(OverlayWriting.Writes))
        {
            AiTextJob job = OverlayWriting.Job(overlay, layer, tokens, entry.Item?.Overview, identity, entry.Start);
            AiTextResult result = writer.Peek(job, queue: prepare);

            // Written text has no {tokens} left in it; the usual text is filled in as the renderer would.
            layer.Text = result.Text;
        }

        return copy;
    }

    private static Dictionary<string, string> WithGraphics(IReadOnlyDictionary<string, string>? annotations, JsonArray layers)
    {
        Dictionary<string, string> result = annotations is null ? [] : new Dictionary<string, string>(annotations);

        // Another plugin's layers stay, under these.
        if (result.TryGetValue(GraphicsAnnotation, out string? existing) && JsonNode.Parse(existing) is JsonArray earlier)
        {
            foreach (JsonNode? layer in layers.ToList())
            {
                layers.Remove(layer);
                earlier.Add(layer);
            }

            layers = earlier;
        }

        result[GraphicsAnnotation] = layers.ToJsonString();
        return result;
    }

    /// <summary>The programme after this entry: the next one that isn't more of the same.</summary>
    private static ScheduleEntry? Next(List<ScheduleEntry> ordered, int index)
    {
        string? current = ordered[index].Item?.Id;
        for (int next = index + 1; next < ordered.Count; next++)
        {
            ScheduleEntry candidate = ordered[next];
            if (candidate.Kind == ScheduleEntryKind.Program && candidate.Item is { } item && item.Id != current && !candidate.IsSlotIntro && !candidate.IsSlotOutro)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The programme on now: this entry if it is one, else the one the break interrupts or follows.</summary>
    private static ScheduleEntry? Programme(List<ScheduleEntry> ordered, int index)
    {
        for (int at = index; at >= 0; at--)
        {
            ScheduleEntry candidate = ordered[at];
            if (candidate.Kind == ScheduleEntryKind.Program && candidate.Item is not null && !candidate.IsSlotIntro && !candidate.IsSlotOutro)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Identifies the airing an entry belongs to, the same for every part of a programme split by breaks (and for the
    /// breaks between them), so a random pick between overlays holds for the whole programme.
    /// </summary>
    private static string ProgrammeKey(List<ScheduleEntry> ordered, int index)
    {
        ScheduleEntry entry = ordered[index];
        if (entry.Kind != ScheduleEntryKind.Program || entry.IsSlotIntro || entry.IsSlotOutro)
        {
            return entry.Start.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        }

        // Walk back to the programme's first part.
        DateTimeOffset start = entry.Start;
        for (int at = index - 1; at >= 0 && entry.SegmentNumber > 1; at--)
        {
            ScheduleEntry earlier = ordered[at];
            if (earlier.Kind == ScheduleEntryKind.Program && !earlier.IsSlotIntro && !earlier.IsSlotOutro)
            {
                if (earlier.Item?.Id != entry.Item?.Id)
                {
                    break;
                }

                start = earlier.Start;
                if (earlier.SegmentNumber <= 1)
                {
                    break;
                }
            }
        }

        return $"{entry.Item?.Id}|{start.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}";
    }

    /// <summary>What {tokens} read as on this entry.</summary>
    public static Dictionary<string, string> Tokens(ScheduleEntry entry, ScheduleEntry? next, ChannelConfig owner)
    {
        MediaItem? item = entry.Item;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["title"] = Title(item),
            ["episode"] = item?.SeriesName is { Length: > 0 } ? item.Title : string.Empty,
            ["season_episode"] = item is { SeasonNumber: > 0, EpisodeNumber: > 0 } ? $"S{item.SeasonNumber:D2}E{item.EpisodeNumber:D2}" : string.Empty,
            ["year"] = (item?.PremiereDate?.Year ?? item?.SeriesYear)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            ["channel"] = owner.Name,
            ["next"] = Title(next?.Item),
            ["next_time"] = next is null ? string.Empty : OverlayRenderer.ClockTime(next.Start),
            ["new"] = entry.IsPremiere ? "NEW" : string.Empty,
            ["next_new"] = next?.IsPremiere == true ? "NEW" : string.Empty
        };
    }

    private static string Title(MediaItem? item) => item is null ? string.Empty : item.SeriesName is { Length: > 0 } series ? series : item.Title;

    /// <summary>Sample tokens for the editor's preview.</summary>
    public static readonly IReadOnlyDictionary<string, string> SampleTokens = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["title"] = "The Show",
        ["episode"] = "The One With the Overlay",
        ["season_episode"] = "S02E05",
        ["year"] = "1998",
        ["channel"] = "Channel",
        ["next"] = "Another Show",
        ["next_time"] = "8:30 PM",
        ["new"] = "NEW",
        ["next_new"] = "NEW"
    };
}
