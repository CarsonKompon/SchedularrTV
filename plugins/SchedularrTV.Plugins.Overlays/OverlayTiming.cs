using System.Text.Json.Nodes;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.Overlays;

/// <summary>
/// ErsatzTV's timing for a graphics layer: it fades in, holds and fades out once every <see cref="FrequencyMs"/>,
/// first <see cref="PhaseMs"/> into the item (or past the wall-clock period, for <see cref="Wall"/>).
/// </summary>
public sealed record GraphicsTiming(bool Wall, long FrequencyMs, long PhaseMs, long FadeMs, long HoldMs)
{
    /// <summary>How long it's seen each time, fades included.</summary>
    public long VisibleMs => 2 * FadeMs + HoldMs;

    public JsonObject ToJson() => new()
    {
        ["timing_type"] = "periodic",
        ["clock"] = Wall ? "wall" : "content",
        ["frequency_ms"] = FrequencyMs,
        ["phase_offset_ms"] = PhaseMs,
        ["fade_ms"] = FadeMs,
        ["hold_ms"] = HoldMs
    };
}

/// <summary>An overlay's place on one entry: shown throughout (<see cref="Timing"/> null), or as timed.</summary>
public sealed record OverlayShowing(GraphicsTiming? Timing);

/// <summary>
/// How part of a design is timed within each of the overlay's appearances: coming in <see cref="InSeconds"/> after it,
/// going <see cref="OutSeconds"/> before it ends, and fading or not (a video brings itself in and out).
/// </summary>
public readonly record struct LayerTiming(double InSeconds, double OutSeconds, bool Fades = true)
{
    public static readonly LayerTiming Whole = new(0, 0);

    public static LayerTiming Of(OverlayLayer layer) =>
        new(Math.Max(0, layer.InSeconds), Math.Max(0, layer.OutSeconds), layer.Kind != OverlayLayerKinds.Video);

    public bool IsWhole => InSeconds <= 0 && OutSeconds <= 0 && Fades;
}

/// <summary>
/// Works out when an overlay shows on one entry. Times are measured through the whole programme, so a programme split
/// by mid-roll breaks gets each appearance on whichever part it falls in, at the right point in that part.
/// </summary>
public static class OverlayTiming
{
    /// <summary>Appearances shorter than this (cut down by a break) are left out.</summary>
    private const long ShortestMs = 750;

    /// <summary>
    /// Null when the overlay doesn't show on this entry at all (or, with <paramref name="part"/>, when that part of it
    /// doesn't: its window is too short once its in and out are taken off).
    /// </summary>
    public static OverlayShowing? For(Overlay overlay, ScheduleEntry entry, LayerTiming? part = null)
    {
        if (!AppliesTo(overlay, entry))
        {
            return null;
        }

        LayerTiming layer = part ?? LayerTiming.Whole;
        long late = Ms(layer.InSeconds);
        long early = Ms(layer.OutSeconds);

        long partStart = (long)entry.InPoint.TotalMilliseconds;
        long partLength = (long)entry.Duration.TotalMilliseconds;
        if (partLength <= 0)
        {
            return null;
        }

        // The whole programme's length; a part of it starts InPoint into it.
        long programme = entry.Item is { Duration.TotalMilliseconds: > 0 } item
            ? Math.Max((long)item.Duration.TotalMilliseconds, partStart + partLength)
            : partStart + partLength;
        long fade = layer.Fades ? Ms(overlay.FadeSeconds) : 0;
        long duration = Math.Max(Ms(overlay.DurationSeconds), 1000);
        long offset = Ms(overlay.OffsetSeconds);

        // A layer's own window: the overlay's, starting later and ending sooner.
        long shown = duration - late - early;
        if (shown < ShortestMs && overlay.When != OverlayWhen.Always)
        {
            return null;
        }

        return overlay.When switch
        {
            OverlayWhen.Start => Once(offset + late, shown, fade, partStart, partLength),
            OverlayWhen.End => Once(programme - offset - duration + late, shown, fade, partStart, partLength),
            OverlayWhen.Percent => Once((long)(programme * Math.Clamp(overlay.Percent, 0, 100) / 100) + late, shown, fade, partStart, partLength),
            OverlayWhen.Every => Repeating(wall: false, offset - partStart + late, shown, fade, overlay.EveryMinutes),
            OverlayWhen.Clock => Repeating(wall: true, offset + late, shown, fade, overlay.EveryMinutes),
            _ => fade <= 0 && late <= 0 && early <= 0
                ? new OverlayShowing(null)
                : Once(partStart + late, partLength - late - early, fade, partStart, partLength)
        };
    }

    public static bool AppliesTo(Overlay overlay, ScheduleEntry entry) =>
        entry.Kind != ScheduleEntryKind.Padding && entry.Item is not null
        && (overlay.AppliesTo == OverlayAppliesTo.Everything
            || (entry.Kind == ScheduleEntryKind.Program && !entry.IsSlotIntro && !entry.IsSlotOutro));

    /// <summary>One appearance at <paramref name="start"/> (programme time), cut to the part of the programme this entry plays.</summary>
    private static OverlayShowing? Once(long start, long length, long fade, long partStart, long partLength)
    {
        long from = Math.Max(start, partStart);
        long to = Math.Min(start + length, partStart + partLength);
        long visible = to - from;
        if (visible < ShortestMs)
        {
            return null;
        }

        // ErsatzTV wants each fade no longer than the hold between them.
        fade = Math.Min(fade, visible / 3);
        long hold = visible - 2 * fade;
        long phase = from - partStart;
        return new OverlayShowing(new GraphicsTiming(Wall: false, FrequencyMs: Math.Max(partLength + 1000, visible + 1), phase, fade, hold));
    }

    /// <summary>
    /// Every <paramref name="everyMinutes"/>: through the programme (the cycle placed so it stays in step across its
    /// parts), or by the clock.
    /// </summary>
    private static OverlayShowing? Repeating(bool wall, long phase, long length, long fade, double everyMinutes)
    {
        long every = Math.Max(Ms(everyMinutes * 60), 10_000);
        long visible = Math.Min(length, every);
        fade = Math.Min(fade, visible / 3);
        long hold = visible - 2 * fade;
        phase = ((phase % every) + every) % every;
        return new OverlayShowing(new GraphicsTiming(wall, every, phase, fade, hold));
    }

    private static long Ms(double seconds) => (long)Math.Round(Math.Max(seconds, 0) * 1000);
}
