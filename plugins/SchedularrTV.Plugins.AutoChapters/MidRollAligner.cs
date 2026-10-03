using System.Collections.Concurrent;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Plugins.Playout;

namespace SchedularrTV.AutoChapters;

/// <summary>How the mid-roll breaks coming up on a channel are placed.</summary>
public sealed record MidRollSummary(int AtBreakPoints, int WaitingForAnalysis, int NoneFound, int NoPointNearby, int OwnChapters)
{
    public static readonly MidRollSummary Empty = new(0, 0, 0, 0, 0);

    public int Total => AtBreakPoints + WaitingForAnalysis + NoneFound + NoPointNearby + OwnChapters;

    public MidRollSummary Plus(MidRollSummary other) => new(
        AtBreakPoints + other.AtBreakPoints, WaitingForAnalysis + other.WaitingForAnalysis, NoneFound + other.NoneFound,
        NoPointNearby + other.NoPointNearby, OwnChapters + other.OwnChapters);
}

/// <summary>
/// Moves mid-roll breaks onto the break points found in the file, as the schedule goes out.
/// </summary>
/// <remarks>
/// <para>
/// A programme is analysed once it's in the schedule, but by then its airing has already been planned (and kept: later
/// builds only add to the end), so its breaks were split on the fixed interval. Rather than replanning the guide, each
/// publish slides the breaks inside such a programme to the nearest break point: the first part gets shorter and the
/// next one longer by the same amount, and the break between moves with them. Where the programme starts and ends,
/// and so everything around it, stays exactly as planned.
/// </para>
/// <para>
/// A programme already on air is left as it was last sent, so ErsatzTV never sees a programme change under it.
/// </para>
/// </remarks>
public sealed class MidRollAligner : IPlayoutProcessor
{
    /// <summary>The furthest a break is moved to reach a break point.</summary>
    public static readonly TimeSpan Reach = TimeSpan.FromMinutes(5);

    /// <summary>No part of a programme is made shorter than this.</summary>
    public static readonly TimeSpan ShortestPart = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(1);

    private readonly ChapterStore _store;
    private readonly AutoChaptersSettings _settings;
    private readonly TimeProvider _clock;

    // Whether each airing (channel, programme, start) was aligned the last time it went out, so one on air stays put.
    private readonly ConcurrentDictionary<string, (bool Aligned, DateTimeOffset Start)> _decided = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MidRollSummary> _summaries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HashSet<string>> _upcomingPaths = new(StringComparer.OrdinalIgnoreCase);

    public MidRollAligner(ChapterStore store, AutoChaptersSettings settings, IScheduleRepublisher? republisher = null, TimeProvider? clock = null)
    {
        _store = store;
        _settings = settings;
        _clock = clock ?? TimeProvider.System;
        if (republisher is not null)
        {
            // Break points found for a programme coming up go out with the next publish, not the next rebuild.
            store.Saved += record =>
            {
                if (record.Error is null && record.Markers.Count > 0 && _settings.UseForMidRolls
                    && _upcomingPaths.Values.Any(paths => { lock (paths) return paths.Contains(record.Path); }))
                {
                    republisher.RequestRepublish();
                }
            };
        }
    }

    public string Name => "auto-chapters.mid-rolls";

    // Before everything else, so overlays, promos and the outputs all see the breaks where they finally are.
    public int Order => -100;

    /// <summary>Raised after a channel's breaks are looked at.</summary>
    public event Action? Changed;

    /// <summary>How the mid-roll breaks in the next two days are placed, across every channel.</summary>
    public MidRollSummary Summary => _summaries.Values.Aggregate(MidRollSummary.Empty, (sum, channel) => sum.Plus(channel));

    public ValueTask<ChannelSchedule> ProcessAsync(PlayoutContext context, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        List<ScheduleEntry> entries = [.. context.Schedule.Entries];
        bool changed = false;
        int atPoints = 0, waiting = 0, none = 0, notNear = 0, own = 0;
        HashSet<string> upcoming = new(StringComparer.Ordinal);

        for (int index = 0; index < entries.Count; index++)
        {
            ScheduleEntry first = entries[index];
            if (first is not { Kind: ScheduleEntryKind.Program, SegmentNumber: 1, SegmentCount: > 1, Item: { } item })
            {
                continue;
            }

            List<int> parts = Parts(entries, index);
            if (parts.Count < 2)
            {
                continue;
            }

            bool soon = entries[parts[^1]].End > now && first.Start < now + UpcomingProgrammes.Horizon;
            if (soon && item.Path is { Length: > 0 } path)
            {
                upcoming.Add(path);
            }

            // Its own chapter markers were used when it was planned.
            if (!ChapterEnricher.Wants(item))
            {
                if (soon) own++;
                continue;
            }

            ChapterRecord? record = _store.Find(item.Path);
            if (record is not { Error: null, Markers.Count: > 0 } || !_settings.UseForMidRolls)
            {
                if (soon)
                {
                    if (record is null) waiting++;
                    else none++;
                }

                continue;
            }

            string key = $"{context.Schedule.ChannelId}|{item.Id}|{first.Start.UtcTicks}";
            bool started = first.Start <= now;
            if (started && !(_decided.TryGetValue(key, out var decided) && decided.Aligned))
            {
                // On air already as it was sent without break points; it stays that way until it ends.
                _decided.TryAdd(key, (false, first.Start));
                if (soon) notNear++;
                continue;
            }

            (bool moved, bool allAtPoints) = Align(entries, parts, record.MarkerTimes);
            changed |= moved;
            _decided[key] = (true, first.Start);
            if (soon)
            {
                if (allAtPoints) atPoints++;
                else notNear++;
            }
        }

        _summaries[context.Schedule.ChannelId] = new MidRollSummary(atPoints, waiting, none, notNear, own);
        _upcomingPaths[context.Schedule.ChannelId] = upcoming;
        Forget(now);
        Changed?.Invoke();
        return ValueTask.FromResult(changed ? context.Schedule with { Entries = entries } : context.Schedule);
    }

    /// <summary>The indexes of a split programme's parts, in order, starting from its first.</summary>
    private static List<int> Parts(List<ScheduleEntry> entries, int first)
    {
        ScheduleEntry start = entries[first];
        List<int> parts = [first];
        int expected = 2;
        for (int index = first + 1; index < entries.Count && expected <= start.SegmentCount; index++)
        {
            ScheduleEntry entry = entries[index];
            if (entry.Kind != ScheduleEntryKind.Program)
            {
                continue;
            }

            if (entry.Item?.Id != start.Item!.Id || entry.SegmentNumber != expected)
            {
                break;
            }

            parts.Add(index);
            expected++;
        }

        return parts;
    }

    /// <summary>
    /// Slides each break between the parts to the nearest break point within reach. Returns whether anything moved, and
    /// whether every break now sits on a break point.
    /// </summary>
    internal static (bool Moved, bool AllAtPoints) Align(List<ScheduleEntry> entries, IReadOnlyList<int> parts, IReadOnlyList<TimeSpan> markers)
    {
        bool moved = false;
        bool all = true;
        for (int split = 0; split + 1 < parts.Count; split++)
        {
            int before = parts[split];
            int after = parts[split + 1];
            ScheduleEntry earlier = entries[before];
            ScheduleEntry later = entries[after];
            TimeSpan at = later.InPoint;
            TimeSpan lowest = earlier.InPoint + ShortestPart;
            TimeSpan highest = later.InPoint + later.Duration - ShortestPart;

            TimeSpan? best = null;
            foreach (TimeSpan marker in markers)
            {
                if (marker > lowest && marker < highest && (marker - at).Duration() <= Reach
                    && (best is null || (marker - at).Duration() < (best.Value - at).Duration()))
                {
                    best = marker;
                }
            }

            if (best is not { } point)
            {
                all = false;
                continue;
            }

            TimeSpan shift = at - point;
            if (shift.Duration() <= Tolerance)
            {
                continue;
            }

            // Earlier when the break point comes before the planned split (shift > 0), later when after it.
            entries[before] = earlier with { Duration = earlier.Duration - shift };
            for (int index = before + 1; index < after; index++)
            {
                entries[index] = entries[index] with { Start = entries[index].Start - shift };
            }

            entries[after] = later with { Start = later.Start - shift, InPoint = point, Duration = later.Duration + shift };
            moved = true;
        }

        return (moved, all);
    }

    private void Forget(DateTimeOffset now)
    {
        foreach (KeyValuePair<string, (bool Aligned, DateTimeOffset Start)> airing in _decided)
        {
            if (now - airing.Value.Start > TimeSpan.FromDays(1))
            {
                _decided.TryRemove(airing.Key, out _);
            }
        }
    }
}
