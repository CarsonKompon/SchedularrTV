using System.Globalization;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;

namespace SchedularrTV.Core.Ordering;

/// <summary>Where and when a build is happening, for working out which <see cref="PlaybackOrder.Drop"/> episodes are out.</summary>
/// <param name="Zone">The channel's time zone; drop days and times are local to it.</param>
/// <param name="Now">The start of the build. Drops without a configured start begin from here.</param>
public sealed record DropContext(TimeZoneInfo Zone, DateTimeOffset Now)
{
    public static DropContext Utc(DateTimeOffset now) => new(TimeZoneInfo.Utc, now);
}

/// <summary>Where a drop show is at some moment.</summary>
/// <param name="Cycle">How many times the show has started over (<see cref="DropEndAction.Restart"/>); 0 the first time through.</param>
/// <param name="Released">Episodes out, in release order, in this cycle.</param>
/// <param name="BeforeLatest">Episodes that were out before the latest drop (the rest are its new ones).</param>
/// <param name="LatestDrop">When the latest drop happened; null before the first.</param>
public readonly record struct DropRelease(int Cycle, int Released, int BeforeLatest, DateTimeOffset? LatestDrop);

/// <summary>
/// A drop cadence pinned to a first drop. Answers "how many episodes are out at this moment?" as a pure
/// function of time, so a rebuild or replan can never release an episode early.
/// </summary>
/// <remarks>
/// A plain cadence is a sum. With a season break, a restart or a manual "drop now" reset, the drops are walked one
/// at a time from the first (lazily, and only as far as asked), since when each happens depends on the seasons.
/// </remarks>
public sealed class DropPlan
{
    private const int MaxEvents = 200_000;

    private readonly HashSet<DayOfWeek> _days;
    private readonly int _baseOut;
    private readonly bool _immediate;
    private readonly int[] _seasonEnds;

    private List<DropEvent>? _events;
    private IEnumerator<DropEvent>? _source;
    private bool _exhausted;

    private readonly record struct DropEvent(DateTimeOffset At, int Cycle, int Before, int Released);

    private DropPlan(DropConfig config, TimeZoneInfo zone, DateTimeOffset origin, DateTimeOffset now,
        int baseOut, bool immediate, int total, int[] seasonEnds, DropReset? reset)
    {
        Config = config;
        Zone = zone;
        Origin = origin;
        Now = now;
        _days = [.. config.DropDays];
        _baseOut = Math.Max(0, baseOut);
        _immediate = immediate;
        Total = total;
        _seasonEnds = seasonEnds;
        Reset = reset;
    }

    public DropConfig Config { get; }

    public TimeZoneInfo Zone { get; }

    /// <summary>The first drop. Drops happen at or after it.</summary>
    public DateTimeOffset Origin { get; }

    /// <summary>When the build that created this plan started.</summary>
    public DateTimeOffset Now { get; }

    /// <summary>Episodes the show has; <see cref="int.MaxValue"/> when the plan wasn't given them.</summary>
    public int Total { get; }

    /// <summary>The manual reset the plan counts from, when one applies.</summary>
    public DropReset? Reset { get; }

    /// <summary>
    /// Episode counts (in release order) at which a season ends, the last being <see cref="Total"/>. Empty when the plan
    /// wasn't given the episodes.
    /// </summary>
    public IReadOnlyList<int> SeasonEnds => _seasonEnds;

    /// <summary>True when <see cref="Origin"/> came from the config rather than from the first build or a reset.</summary>
    public bool HasConfiguredStart => Reset is null && ParseStart(Config.Start) is not null;

    /// <summary>The origin worth saving for the next build: one the first build picked, not one from config or a reset.</summary>
    public DateTimeOffset? PersistedOrigin => Reset is null && ParseStart(Config.Start) is null ? Origin : null;

    public int PerDrop => Math.Max(1, Config.Count);

    /// <summary>Episodes out before the first drop.</summary>
    public int BaseOut => _baseOut;

    private bool ByDays => _days.Count > 0;

    private bool KnowsTotal => Total != int.MaxValue;

    /// <summary>The drops have to be walked one by one rather than counted.</summary>
    private bool Walks => _immediate || Config.HasSeasonBreak || Config.AtEnd == DropEndAction.Restart;

    /// <summary>
    /// Pins <paramref name="config"/> to a manual <paramref name="reset"/> (while the config's start is the one it was
    /// made under), else its configured start, else the origin saved by an earlier build, else the most recent drop
    /// time at or before <see cref="DropContext.Now"/>.
    /// </summary>
    /// <param name="releaseOrder">The show's episodes in release order, for its seasons and its end.</param>
    public static DropPlan Create(DropConfig config, DropContext context, DateTimeOffset? savedOrigin,
        IReadOnlyList<MediaItem>? releaseOrder = null, DropReset? reset = null)
    {
        int total = releaseOrder?.Count ?? int.MaxValue;
        int[] seasonEnds = releaseOrder is null ? [] : SeasonEndsOf(releaseOrder);

        if (reset is not null && SameStart(reset.Start, config.Start))
        {
            // "Drop now" makes the reset itself the first drop. Otherwise drops carry on from the next drop time after
            // it: the next drop weekday, or a whole interval later.
            DateTimeOffset from = reset.Immediate ? reset.At
                : config.DropDays.Count > 0 ? reset.At.AddTicks(1)
                : reset.At + config.Interval;
            return new DropPlan(config, context.Zone, from, context.Now, reset.Out, reset.Immediate, total, seasonEnds, reset);
        }

        DateTimeOffset origin = ParseStart(config.Start) is { } start
            ? Local(start, config.TimeOfDay, context.Zone)
            : savedOrigin ?? LatestAtOrBefore(config, context.Zone, context.Now);
        return new DropPlan(config, context.Zone, origin, context.Now, config.AlreadyOut, false, total, seasonEnds, null);
    }

    /// <summary>Episodes out by <paramref name="at"/> (in the cycle then): those already out before the first drop plus every drop since.</summary>
    public int ReleasedAt(DateTimeOffset at) => StatusAt(at).Released;

    /// <summary>How many times the show has started over by <paramref name="at"/>.</summary>
    public int CycleAt(DateTimeOffset at) => StatusAt(at).Cycle;

    /// <summary>Where the show is at <paramref name="at"/>.</summary>
    public DropRelease StatusAt(DateTimeOffset at)
    {
        if (!Walks)
        {
            long drops = DropsThrough(at);
            int released = (int)Math.Min(int.MaxValue, _baseOut + (long)PerDrop * drops);
            int before = drops > 0 ? (int)Math.Min(int.MaxValue, _baseOut + (long)PerDrop * (drops - 1)) : released;
            return new DropRelease(0, released, before, drops > 0 ? LatestDropAtOrBefore(at) : null);
        }

        Extend(at);
        int index = LastEventAtOrBefore(at);
        if (index < 0)
        {
            int already = Math.Min(_baseOut, Total);
            return new DropRelease(0, already, already, null);
        }

        DropEvent latest = _events![index];
        return new DropRelease(latest.Cycle, latest.Released, latest.Before, latest.At);
    }

    /// <summary>
    /// How many episodes count as having had their new airing when a show first goes onto drops: everything out
    /// before the latest drop, so the latest drop's episodes are still new.
    /// </summary>
    public int PremieredOnFirstBuild(int poolCount)
    {
        DropRelease status = StatusAt(Now);
        int released = Math.Min(poolCount, status.Released);
        if (!Walks)
        {
            return Math.Max(Math.Min(released, _baseOut), released - PerDrop);
        }

        return Math.Clamp(status.BeforeLatest, 0, released);
    }

    /// <summary>The next drop that actually releases something, strictly after <paramref name="at"/>; null once there are no more.</summary>
    public DateTimeOffset? NextReleaseAfter(DateTimeOffset at)
    {
        if (!Walks)
        {
            return KnowsTotal && StatusAt(at).Released >= Total ? null : NextDropAfter(at);
        }

        Extend(at);
        int index = LastEventAtOrBefore(at) + 1;
        return index < _events!.Count ? _events[index].At : null;
    }

    /// <summary>How many drops have happened at or before <paramref name="at"/> on the plain cadence.</summary>
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

    /// <summary>The first drop strictly after <paramref name="at"/> on the plain cadence.</summary>
    public DateTimeOffset NextDropAfter(DateTimeOffset at)
    {
        if (at < Origin)
        {
            return ByDays ? DayDropAtOrAfter(Origin) : Origin;
        }

        if (_days.Count == 0)
        {
            long passed = (at - Origin).Ticks / Config.Interval.Ticks + 1;
            return Origin + TimeSpan.FromTicks(Config.Interval.Ticks * passed);
        }

        return DayDropAtOrAfter(at.AddTicks(1));
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

    /// <summary>
    /// Where seasons end in a show's release order: after how many episodes each season's last one has dropped. Specials
    /// (season 0) and episodes without a season belong to the season around them, so they don't split one.
    /// </summary>
    public static int[] SeasonEndsOf(IReadOnlyList<MediaItem> releaseOrder)
    {
        List<int> ends = [];
        int? current = null;
        for (int i = 0; i < releaseOrder.Count; i++)
        {
            int? season = releaseOrder[i].SeasonNumber is int number && number > 0 ? number : current;
            if (current is not null && season is not null && season != current)
            {
                ends.Add(i);
            }

            current = season ?? current;
        }

        if (releaseOrder.Count > 0)
        {
            ends.Add(releaseOrder.Count);
        }

        return [.. ends];
    }

    /// <summary>The end of the season that episode number <paramref name="released"/> + 1 belongs to.</summary>
    public int SeasonEndAfter(int released)
    {
        foreach (int end in _seasonEnds)
        {
            if (end > released)
            {
                return end;
            }
        }

        return Total;
    }

    // Every drop from the first, in order. Each releases up to PerDrop episodes, never past a season's end while there's a
    // season break; after a season's (or the show's) last episode the next drop waits out the break.
    private IEnumerable<DropEvent> Walk()
    {
        int cycle = 0;
        int released = Math.Min(_baseOut, Total);
        DateTimeOffset next = _immediate || !ByDays ? Origin : DayDropAtOrAfter(Origin);

        while (true)
        {
            if (released >= Total)
            {
                if (Config.AtEnd != DropEndAction.Restart || Total <= 0)
                {
                    yield break;
                }

                cycle++;
                released = 0;
            }

            int before = released;
            int limit = Config.HasSeasonBreak ? SeasonEndAfter(released) : Total;
            released = (int)Math.Min(limit, (long)released + PerDrop);
            yield return new DropEvent(next, cycle, before, released);

            bool finished = released >= Total;
            if (finished && Config.AtEnd != DropEndAction.Restart)
            {
                yield break;
            }

            bool breakNow = Config.HasSeasonBreak && (finished || released == limit);
            next = breakNow
                ? (ByDays ? DayDropAtOrAfter(next + Config.Break) : next + Config.Break)
                : (ByDays ? DayDropAtOrAfter(next.AddTicks(1)) : next + Config.Interval);
        }
    }

    private void Extend(DateTimeOffset through)
    {
        _events ??= [];
        _source ??= Walk().GetEnumerator();
        while (!_exhausted && (_events.Count == 0 || _events[^1].At <= through))
        {
            if (_events.Count >= MaxEvents || !_source.MoveNext())
            {
                _exhausted = true;
                break;
            }

            _events.Add(_source.Current);
        }
    }

    private int LastEventAtOrBefore(DateTimeOffset at)
    {
        int low = 0, high = _events!.Count - 1, found = -1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (_events[mid].At <= at)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return found;
    }

    /// <summary>The latest plain-cadence drop at or before <paramref name="at"/> (at least one has happened).</summary>
    private DateTimeOffset LatestDropAtOrBefore(DateTimeOffset at)
    {
        if (!ByDays)
        {
            long passed = (at - Origin).Ticks / Config.Interval.Ticks;
            return Origin + TimeSpan.FromTicks(Config.Interval.Ticks * passed);
        }

        DateTimeOffset latest = LatestAtOrBefore(Config, Zone, at);
        return latest < Origin ? Origin : latest;
    }

    /// <summary>The first weekday drop at or after <paramref name="at"/>.</summary>
    private DateTimeOffset DayDropAtOrAfter(DateTimeOffset at)
    {
        DateOnly day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, Zone).DateTime);
        for (int i = 0; i <= 8; i++, day = day.AddDays(1))
        {
            if (!_days.Contains(day.DayOfWeek))
            {
                continue;
            }

            DateTimeOffset instant = Local(day, Config.TimeOfDay, Zone);
            if (instant >= at)
            {
                return instant;
            }
        }

        return at + TimeSpan.FromDays(7);
    }

    private static bool SameStart(string? a, string? b) =>
        string.Equals(string.IsNullOrWhiteSpace(a) ? null : a.Trim(), string.IsNullOrWhiteSpace(b) ? null : b.Trim(), StringComparison.Ordinal);

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
