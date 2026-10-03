using System.Globalization;
using System.Text.Json.Nodes;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Plugins.Playout;

namespace SchedularrTV.Overlays;

/// <summary>
/// Puts each channel's overlays on its entries as they go to ErsatzTV: for every entry an overlay shows on, a graphics
/// layer (the drawn PNG or the file, where it sits, when it fades in and out) in the entry's <c>ersatztv.graphics</c>
/// annotation, which the ErsatzTV output writes into the playout.
/// </summary>
public sealed class OverlayProcessor(OverlayRenderer renderer, ILogger<OverlayProcessor> logger) : IPlayoutProcessor
{
    /// <summary>The ErsatzTV output's annotation for graphics layers.</summary>
    public const string GraphicsAnnotation = "ersatztv.graphics";

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
        for (int index = 0; index < ordered.Count; index++)
        {
            ScheduleEntry entry = ordered[index];
            if (For(entry.BlockName) is not { } resolved)
            {
                result.Add(entry);
                continue;
            }

            var layers = new JsonArray();
            Dictionary<string, string>? tokens = null;
            foreach (Overlay overlay in resolved.Overlays)
            {
                if (OverlayTiming.For(overlay, entry) is not { } showing)
                {
                    continue;
                }

                try
                {
                    tokens ??= Tokens(entry, Next(ordered, index), resolved.Owner);
                    if (Layer(overlay, showing, tokens) is { } layer)
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

        return ValueTask.FromResult(changed ? context.Schedule with { Entries = result } : context.Schedule);
    }

    /// <summary>One graphics layer in ErsatzTV's format: a design as a full-frame PNG, a file where it's anchored.</summary>
    internal JsonObject? Layer(Overlay overlay, OverlayShowing showing, IReadOnlyDictionary<string, string> tokens)
    {
        JsonObject layer;
        if (overlay.Kind == OverlayKinds.File)
        {
            if (overlay.File is not { Length: > 0 } file)
            {
                return null;
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
            layer = new JsonObject
            {
                ["source"] = new JsonObject { ["source_type"] = "local", ["path"] = renderer.Render(overlay, tokens) },
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
            ["next_time"] = next is null ? string.Empty : OverlayRenderer.ClockTime(next.Start)
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
        ["next_time"] = "8:30 PM"
    };
}
