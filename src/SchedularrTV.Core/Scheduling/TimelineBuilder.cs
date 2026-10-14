using System.Globalization;
using SchedularrTV.Core.Configuration;

namespace SchedularrTV.Core.Scheduling;

/// <summary>One concrete occurrence of a block window on the timeline.</summary>
public sealed record BlockOccurrence(BlockConfig Block, DateTimeOffset Start, DateTimeOffset End, int Priority)
{
    /// <summary>False when the build window cuts into an already-running block.</summary>
    public bool StartsBlock { get; init; } = true;

    /// <summary>False when the build window stops before the block finishes.</summary>
    public bool EndsBlock { get; init; } = true;
}

/// <summary>One concrete firing of a time-locked anchor.</summary>
public sealed record AnchorOccurrence(AnchorConfig Anchor, DateTimeOffset Start, BlockConfig? Block, string Name);

/// <summary>
/// Expands recurrence rules into concrete occurrences and resolves overlaps by priority.
/// Pure and side-effect free so recurrence behaviour can be unit tested in isolation.
/// </summary>
public static class TimelineBuilder
{
    public static IReadOnlyList<BlockOccurrence> BuildBlockOccurrences(
        ChannelConfig channel,
        TimeZoneInfo timeZone,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        List<BlockOccurrence> occurrences = [];

        DateTime firstDay = TimeZoneInfo.ConvertTime(windowStart, timeZone).Date.AddDays(-1);
        DateTime lastDay = TimeZoneInfo.ConvertTime(windowEnd, timeZone).Date;

        for (DateTime day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            foreach (BlockConfig block in channel.Blocks)
            {
                if (block.FollowOnly || block.IsAnchorSlot || block.Time is null || block.EndTime is null)
                {
                    continue;
                }

                if (!MatchesDay(block, day))
                {
                    continue;
                }

                DateTimeOffset start = ToOffset(day + block.Time.Value, timeZone);
                DateTime endLocal = day + block.EndTime.Value;
                if (block.EndTime.Value <= block.Time.Value)
                {
                    endLocal = endLocal.AddDays(1); // crosses midnight, e.g. 21:00 -> 03:00
                }

                DateTimeOffset end = ToOffset(endLocal, timeZone);

                if (end <= windowStart || start >= windowEnd)
                {
                    continue;
                }

                occurrences.Add(new BlockOccurrence(block, start, end, block.EffectivePriority));
            }
        }

        return Flatten(occurrences, windowStart, windowEnd);
    }

    public static IReadOnlyList<AnchorOccurrence> BuildAnchorOccurrences(
        ChannelConfig channel,
        IReadOnlyList<BlockOccurrence> blocks,
        TimeZoneInfo timeZone,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        List<AnchorOccurrence> occurrences = [];

        DateTime firstDay = TimeZoneInfo.ConvertTime(windowStart, timeZone).Date.AddDays(-1);
        DateTime lastDay = TimeZoneInfo.ConvertTime(windowEnd, timeZone).Date.AddDays(1);

        for (DateTime day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            foreach (AnchorConfig anchor in channel.Anchors)
            {
                AddIfInWindow(occurrences, anchor, day, null, anchor.Name ?? "anchor", timeZone, windowStart, windowEnd);
            }

            // Blocks with a start time but no end time are channel-level anchor slots.
            foreach (BlockConfig block in channel.Blocks.Where(b => b.IsAnchorSlot))
            {
                if (!MatchesDay(block, day))
                {
                    continue;
                }

                var synthetic = new AnchorConfig
                {
                    Name = block.Name,
                    Time = block.Time!.Value,
                    Shows = block.Shows
                };

                AddIfInWindow(occurrences, synthetic, day, block, block.Name, timeZone, windowStart, windowEnd);
            }
        }

        // Nested block anchors only fire while their parent block window is active (spec 2.1.1).
        foreach (BlockOccurrence occurrence in blocks)
        {
            foreach (AnchorConfig anchor in occurrence.Block.Anchors)
            {
                DateTime startLocal = TimeZoneInfo.ConvertTime(occurrence.Start, timeZone).DateTime;
                for (DateTime day = startLocal.Date.AddDays(-1); day <= startLocal.Date.AddDays(2); day = day.AddDays(1))
                {
                    DateTimeOffset at = ToOffset(day + anchor.Time, timeZone);
                    if (at < occurrence.Start || at >= occurrence.End)
                    {
                        continue;
                    }

                    if (anchor.Days.Count > 0 && !anchor.Days.Contains(TimeZoneInfo.ConvertTime(at, timeZone).DayOfWeek))
                    {
                        continue;
                    }

                    occurrences.Add(new AnchorOccurrence(
                        anchor,
                        at,
                        occurrence.Block,
                        anchor.Name ?? $"{occurrence.Block.Name} anchor"));
                }
            }
        }

        return occurrences
            .Where(o => o.Start >= windowStart && o.Start < windowEnd)
            .DistinctBy(o => (o.Start, o.Name))
            .OrderBy(o => o.Start)
            .ToList();
    }

    public static bool MatchesDay(BlockConfig block, DateTime day)
    {
        if (block.Schedule?.Date is { } dateSpec)
        {
            return MatchesDateSpec(dateSpec, block.Schedule.Repeat, day);
        }

        if (block.Recurrence == Recurrence.DaysOfWeek || block.Days.Count > 0)
        {
            return block.Days.Contains(day.DayOfWeek);
        }

        return true;
    }

    public static bool MatchesDateSpec(string dateSpec, RepeatModifier repeat, DateTime day)
    {
        string[] parts = dateSpec.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        switch (repeat)
        {
            case RepeatModifier.Monthly:
                // "DD" or the DD component of "MM-DD".
                return int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int dd)
                    && day.Day == dd;

            case RepeatModifier.Yearly:
                if (parts.Length < 2)
                {
                    return false;
                }

                return int.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mm)
                    && int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int d2)
                    && day.Month == mm && day.Day == d2;

            default:
                return DateTime.TryParseExact(
                        dateSpec,
                        ["yyyy-MM-dd", "MM-dd"],
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out DateTime exact)
                    && (dateSpec.Length <= 5
                        ? exact.Month == day.Month && exact.Day == day.Day
                        : exact.Date == day.Date);
        }
    }

    /// <summary>
    /// Resolves overlapping windows: the highest priority occurrence owns each instant, and
    /// lower priority windows are clipped (possibly split) around it.
    /// </summary>
    internal static List<BlockOccurrence> Flatten(List<BlockOccurrence> occurrences, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        List<BlockOccurrence> ordered = occurrences
            .OrderByDescending(o => o.Priority)
            .ThenBy(o => o.Start)
            .ToList();

        List<BlockOccurrence> accepted = [];

        foreach (BlockOccurrence candidate in ordered)
        {
            List<(DateTimeOffset Start, DateTimeOffset End)> remaining =
            [
                (Max(candidate.Start, windowStart), Min(candidate.End, windowEnd))
            ];

            foreach (BlockOccurrence existing in accepted)
            {
                List<(DateTimeOffset Start, DateTimeOffset End)> next = [];
                foreach ((DateTimeOffset s, DateTimeOffset e) in remaining)
                {
                    if (existing.End <= s || existing.Start >= e)
                    {
                        next.Add((s, e));
                        continue;
                    }

                    if (existing.Start > s)
                    {
                        next.Add((s, existing.Start));
                    }

                    if (existing.End < e)
                    {
                        next.Add((existing.End, e));
                    }
                }

                remaining = next;
            }

            foreach ((DateTimeOffset s, DateTimeOffset e) in remaining)
            {
                if (e > s)
                {
                    accepted.Add(candidate with
                    {
                        Start = s, End = e,
                        StartsBlock = s == candidate.Start ? candidate.StartsBlock : s > windowStart,
                        EndsBlock = e == candidate.End ? candidate.EndsBlock : e < windowEnd
                    });
                }
            }
        }

        return accepted.OrderBy(o => o.Start).ToList();
    }

    internal static DateTimeOffset ToOffset(DateTime local, TimeZoneInfo timeZone)
    {
        // DST spring-forward can produce an invalid local time; shift into the following valid hour.
        DateTime adjusted = timeZone.IsInvalidTime(local) ? local.AddHours(1) : local;
        return new DateTimeOffset(adjusted, timeZone.GetUtcOffset(adjusted));
    }

    private static void AddIfInWindow(
        List<AnchorOccurrence> occurrences,
        AnchorConfig anchor,
        DateTime day,
        BlockConfig? block,
        string name,
        TimeZoneInfo timeZone,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        if (anchor.Days.Count > 0 && !anchor.Days.Contains(day.DayOfWeek))
        {
            return;
        }

        DateTimeOffset at = ToOffset(day + anchor.Time, timeZone);
        if (at >= windowStart && at < windowEnd)
        {
            occurrences.Add(new AnchorOccurrence(anchor, at, block, name));
        }
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
