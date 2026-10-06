using System.Globalization;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.Overlays;

/// <summary>When an overlay shows, in words and as times on a sample programme, for the editor.</summary>
public static class OverlayDescriptions
{
    public static string When(Overlay overlay)
    {
        string seconds = Seconds(overlay.DurationSeconds);
        string scope = overlay.AppliesTo == OverlayAppliesTo.Everything ? ", breaks too" : string.Empty;
        return overlay.When switch
        {
            OverlayWhen.Start => $"{seconds} from {Seconds(overlay.OffsetSeconds)} into each programme{scope}",
            OverlayWhen.End => $"{seconds}, ending {Seconds(overlay.OffsetSeconds)} before each programme ends{scope}",
            OverlayWhen.Percent => $"{seconds} from {overlay.Percent:0.#}% through each programme{scope}",
            OverlayWhen.Every => $"{seconds} every {Minutes(overlay.EveryMinutes)} through each programme{scope}",
            OverlayWhen.Clock => $"{seconds} every {Minutes(overlay.EveryMinutes)} by the clock{scope}",
            _ => $"All the way through{scope}"
        };
    }

    /// <summary>When it would show on a programme of <paramref name="length"/>, as (start, end) pairs from its start.</summary>
    public static IReadOnlyList<(TimeSpan From, TimeSpan To)> Sample(Overlay overlay, TimeSpan length)
    {
        var entry = new ScheduleEntry
        {
            Start = DateTimeOffset.UnixEpoch,
            Duration = length,
            Kind = ScheduleEntryKind.Program,
            Item = new MediaItem { Id = "sample", Title = "Sample", Kind = MediaKind.Episode, Duration = length }
        };

        if (OverlayTiming.For(overlay, entry) is not { } showing)
        {
            return [];
        }

        if (showing.Timing is not { } timing)
        {
            return [(TimeSpan.Zero, length)];
        }

        List<(TimeSpan, TimeSpan)> windows = [];
        long total = (long)length.TotalMilliseconds;
        for (long at = timing.PhaseMs; at < total && windows.Count < 200; at += timing.FrequencyMs)
        {
            windows.Add((TimeSpan.FromMilliseconds(at), TimeSpan.FromMilliseconds(Math.Min(total, at + timing.VisibleMs))));
        }

        return windows;
    }

    /// <summary>When a layer is seen within one appearance of a timed overlay, in seconds from its start; empty when never.</summary>
    public static (double From, double To) Within(Overlay overlay, OverlayLayer layer)
    {
        double length = Math.Max(1, overlay.DurationSeconds);
        double from = Math.Clamp(layer.InSeconds, 0, length);
        double to = Math.Clamp(length - Math.Max(0, layer.OutSeconds), 0, length);
        return to - from < 0.75 ? (from, from) : (from, to);
    }

    private static string Seconds(double seconds) =>
        seconds >= 60 && seconds % 60 == 0
            ? $"{(seconds / 60).ToString("0.#", CultureInfo.InvariantCulture)} min"
            : $"{seconds.ToString("0.#", CultureInfo.InvariantCulture)}s";

    private static string Minutes(double minutes) => minutes == 1 ? "minute" : $"{minutes.ToString("0.#", CultureInfo.InvariantCulture)} minutes";
}
