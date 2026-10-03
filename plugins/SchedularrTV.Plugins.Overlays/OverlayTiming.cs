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
/// Works out when an overlay shows on one entry. Times are measured through the whole programme, so a programme split
/// by mid-roll breaks gets each appearance on whichever part it falls in, at the right point in that part.
/// </summary>
public static class OverlayTiming
{
    /// <summary>Appearances shorter than this (cut down by a break) are left out.</summary>
    private const long ShortestMs = 750;

    /// <summary>Null when the overlay doesn't show on this entry at all.</summary>
    public static OverlayShowing? For(Overlay overlay, ScheduleEntry entry)
    {
        if (!AppliesTo(overlay, entry))
        {
            return null;
        }

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
        long fade = Ms(overlay.FadeSeconds);
        long duration = Math.Max(Ms(overlay.DurationSeconds), 1000);
        long offset = Ms(overlay.OffsetSeconds);

        return overlay.When switch
        {
            OverlayWhen.Start => Once(offset, duration, fade, partStart, partLength),
            OverlayWhen.End => Once(programme - offset - duration, duration, fade, partStart, partLength),
            OverlayWhen.Percent => Once((long)(programme * Math.Clamp(overlay.Percent, 0, 100) / 100), duration, fade, partStart, partLength),
            OverlayWhen.Every => Repeating(wall: false, offset - partStart, duration, fade, overlay.EveryMinutes),
            OverlayWhen.Clock => Repeating(wall: true, offset, duration, fade, overlay.EveryMinutes),
            _ => fade <= 0
                ? new OverlayShowing(null)
                : Once(partStart, partLength, fade, partStart, partLength)
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
