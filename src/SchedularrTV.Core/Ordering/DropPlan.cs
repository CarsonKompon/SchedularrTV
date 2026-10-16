using System.Globalization;
using SchedularrTV.Core.Configuration;

namespace SchedularrTV.Core.Ordering;

/// <summary>Where and when a build is happening, for working out which <see cref="PlaybackOrder.Drop"/> episodes are out.</summary>
/// <param name="Zone">The channel's time zone; drop days and times are local to it.</param>
/// <param name="Now">The start of the build. Drops without a configured start begin from here.</param>
public sealed record DropContext(TimeZoneInfo Zone, DateTimeOffset Now)
{
    public static DropContext Utc(DateTimeOffset now) => new(TimeZoneInfo.Utc, now);
}

/// <summary>
/// A drop cadence pinned to a first drop. Answers "how many episodes are out at this moment?" as a pure
/// function of time, so a rebuild or replan can never release an episode early.
/// </summary>
public sealed class DropPlan
{
    private readonly HashSet<DayOfWeek> _days;

    private DropPlan(DropConfig config, TimeZoneInfo zone, DateTimeOffset origin, DateTimeOffset now)
    {
        Config = config;
        Zone = zone;
        Origin = origin;
        Now = now;
        _days = [.. config.DropDays];
    }

    public DropConfig Config { get; }

    public TimeZoneInfo Zone { get; }

    /// <summary>The first drop. Drops happen at or after it.</summary>
    public DateTimeOffset Origin { get; }

    /// <summary>When the build that created this plan started.</summary>
    public DateTimeOffset Now { get; }

    /// <summary>True when <see cref="Origin"/> came from the config rather than from the first build.</summary>
    public bool HasConfiguredStart => ParseStart(Config.Start) is not null;

    public int PerDrop => Math.Max(1, Config.Count);

    /// <summary>
    /// Pins <paramref name="config"/> to its configured start, else the origin saved by an earlier build,
    /// else the most recent drop time at or before <see cref="DropContext.Now"/>.
    /// </summary>
    public static DropPlan Create(DropConfig config, DropContext context, DateTimeOffset? savedOrigin)
    {
        DateTimeOffset origin = ParseStart(config.Start) is { } start
            ? Local(start, config.TimeOfDay, context.Zone)
            : savedOrigin ?? LatestAtOrBefore(config, context.Zone, context.Now);
        return new DropPlan(config, context.Zone, origin, context.Now);
    }

    /// <summary>Episodes out by <paramref name="at"/>: those already out before the first drop plus every drop since.</summary>
    public int ReleasedAt(DateTimeOffset at) =>
        (int)Math.Min(int.MaxValue, Math.Max(0, Config.AlreadyOut) + (long)PerDrop * DropsThrough(at));

    /// <summary>How many drops have happened at or before <paramref name="at"/>.</summary>
    public long DropsThrough(DateTimeOffset at)
    {
        if (at < Origin)
        {
            return 0;
        }

        if (_days.Count == 0)
        {
            return (long)((at - Origin).Ticks / Config.Interval.Ticks) + 1;
        }

        DateOnly first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Origin, Zone).DateTime);
        DateOnly last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, Zone).DateTime);
        int span = last.DayNumber - first.DayNumber;
        long weeks = span / 7;
        long drops = weeks * _days.Count;

        // Whole weeks hold every weekday once; walk the few days left over one at a time.
        for (DateOnly day = first.AddDays((int)(weeks * 7)); day <= last; day = day.AddDays(1))
        {
            if (!_days.Contains(day.DayOfWeek))
            {
                continue;
            }

            DateTimeOffset instant = Local(day, Config.TimeOfDay, Zone);
            if (instant >= Origin && instant <= at)
            {
                drops++;
            }
        }

        // The origin's own day counts only from the origin itself.
        if (weeks > 0 && _days.Contains(first.DayOfWeek) && Local(first, Config.TimeOfDay, Zone) < Origin)
        {
            drops--;
        }

        return drops;
    }

    /// <summary>The first drop strictly after <paramref name="at"/>.</summary>
    public DateTimeOffset NextDropAfter(DateTimeOffset at)
    {
        if (at < Origin)
        {
            return Origin;
        }

        if (_days.Count == 0)
        {
            long passed = (at - Origin).Ticks / Config.Interval.Ticks + 1;
            return Origin + TimeSpan.FromTicks(Config.Interval.Ticks * passed);
        }

        DateOnly day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, Zone).DateTime);
        for (int i = 0; i <= 8; i++, day = day.AddDays(1))
        {
            if (!_days.Contains(day.DayOfWeek))
            {
                continue;
            }

            DateTimeOffset instant = Local(day, Config.TimeOfDay, Zone);
            if (instant > at)
            {
                return instant;
            }
        }

        return at + TimeSpan.FromDays(7);
    }

    /// <summary>The most recent drop time at or before <paramref name="at"/>, ignoring any configured start.</summary>
    public static DateTimeOffset LatestAtOrBefore(DropConfig config, TimeZoneInfo zone, DateTimeOffset at)
    {
        DateOnly day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
        HashSet<DayOfWeek> days = [.. config.DropDays];

        for (int i = 0; i <= 8; i++, day = day.AddDays(-1))
        {
            if (days.Count > 0 && !days.Contains(day.DayOfWeek))
            {
                continue;
            }

            DateTimeOffset instant = Local(day, config.TimeOfDay, zone);
            if (instant <= at)
            {
                return instant;
            }
        }

        return at;
    }

    public static DateOnly? ParseStart(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && DateOnly.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date)
            ? date
            : null;

    private static DateTimeOffset Local(DateOnly day, TimeSpan time, TimeZoneInfo zone)
    {
        DateTime local = day.ToDateTime(TimeOnly.FromTimeSpan(time), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            // Skipped by a clock change: the drop happens once the clocks have moved on.
            local = local.AddHours(1);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
