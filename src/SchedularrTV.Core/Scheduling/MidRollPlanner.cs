using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;

namespace SchedularrTV.Core.Scheduling;

/// <summary>A contiguous slice of a media item, expressed as an offset and a length.</summary>
public sealed record MediaSegment(TimeSpan InPoint, TimeSpan Duration)
{
    public TimeSpan OutPoint => InPoint + Duration;
}

/// <summary>
/// Decides where a programme should be interrupted for advert breaks.
/// </summary>
/// <remarks>
/// Breaks are placed on a cadence (<see cref="MidRollConfig.TargetSegment"/>) rather than by count,
/// so a long film gets proportionally more breaks instead of longer parts. Each cadence point snaps
/// to the nearest chapter marker within tolerance; <see cref="MidRollMode.Auto"/> falls back to the
/// cadence point itself when no marker is close enough.
/// </remarks>
public static class MidRollPlanner
{
    public static IReadOnlyList<MediaSegment> SplitStack(IReadOnlyList<MediaItem> items, MidRollConfig? config, TimeSpan breakBudget)
    {
        if (items.Count == 1) return Split(items[0], config, breakBudget);
        TimeSpan offset = TimeSpan.Zero;
        List<TimeSpan> chapters = [];
        foreach (MediaItem item in items)
        {
            chapters.Add(offset);
            chapters.AddRange(item.Chapters.Where(chapter => chapter > TimeSpan.Zero && chapter < item.Duration)
                .Select(chapter => offset + chapter));
            offset += item.Duration;
        }
        return Split(new MediaItem { Id = "stack", Title = "Stack", Duration = offset, Chapters = chapters }, config, breakBudget);
    }

    public static IReadOnlyList<MediaSegment> Split(MediaItem item, MidRollConfig? config, TimeSpan breakBudget)
    {
        var whole = new MediaSegment(TimeSpan.Zero, item.Duration);

        if (config is null || config.Mode == MidRollMode.None || breakBudget < config.MinBreak)
        {
            return [whole];
        }

        TimeSpan target = config.TargetSegment > TimeSpan.Zero ? config.TargetSegment : TimeSpan.FromMinutes(15);
        TimeSpan minSegment = config.MinSegment > TimeSpan.Zero ? config.MinSegment : TimeSpan.FromMinutes(1);
        int maxBreaks = config.MaxBreaks > 0 ? config.MaxBreaks : int.MaxValue;

        if (item.Duration < target + minSegment)
        {
            return [whole];
        }

        List<TimeSpan> chapters = item.Chapters
            .Where(c => c > TimeSpan.Zero && c < item.Duration)
            .Distinct()
            .OrderBy(c => c)
            .ToList();

        TimeSpan latestCut = item.Duration - minSegment;
        List<TimeSpan> cuts = [];
        TimeSpan previous = TimeSpan.Zero;
        TimeSpan ideal = target;

        while (cuts.Count < maxBreaks && ideal <= latestCut)
        {
            TimeSpan earliest = previous + minSegment;
            if (earliest > latestCut)
            {
                break;
            }

            // Tolerance only matters when there is an alternative. In chapters mode the nearest
            // marker is the only option, so take it rather than skipping the break entirely.
            TimeSpan? tolerance = config.Mode == MidRollMode.Auto ? config.ChapterTolerance : null;
            TimeSpan? cut = FindChapterNear(chapters, ideal, earliest, latestCut, tolerance);

            if (cut is null && config.Mode == MidRollMode.Auto)
            {
                TimeSpan candidate = ideal < earliest ? earliest : ideal;
                if (candidate <= latestCut)
                {
                    cut = candidate;
                }
            }

            if (cut is { } chosen && chosen > previous)
            {
                cuts.Add(chosen);
                previous = chosen;

                // Measure the next break from where this one actually landed, so snapping to a
                // chapter does not compress or stretch the rest of the cadence.
                ideal = chosen + target;
            }
            else
            {
                ideal += target;
            }
        }

        if (cuts.Count == 0)
        {
            return [whole];
        }

        List<MediaSegment> segments = new(cuts.Count + 1);
        TimeSpan start = TimeSpan.Zero;

        foreach (TimeSpan cut in cuts)
        {
            segments.Add(new MediaSegment(start, cut - start));
            start = cut;
        }

        segments.Add(new MediaSegment(start, item.Duration - start));
        return segments;
    }

    private static TimeSpan? FindChapterNear(
        List<TimeSpan> chapters,
        TimeSpan ideal,
        TimeSpan earliest,
        TimeSpan latest,
        TimeSpan? tolerance)
    {
        TimeSpan? best = null;
        TimeSpan bestDistance = TimeSpan.MaxValue;

        foreach (TimeSpan chapter in chapters)
        {
            if (chapter < earliest || chapter > latest)
            {
                continue;
            }

            TimeSpan distance = (chapter - ideal).Duration();
            if (tolerance is { } limit && distance > limit)
            {
                continue;
            }

            if (distance < bestDistance)
            {
                best = chapter;
                bestDistance = distance;
            }
        }

        return best;
    }
}
