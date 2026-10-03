namespace SchedularrTV.Streamers;

public enum PredictionKind
{
    /// <summary>The streamer announced it (YouTube's upcoming stream, Twitch's schedule).</summary>
    Scheduled,

    /// <summary>They usually stream on this weekday around this time.</summary>
    Weekly,

    /// <summary>They stream most days around this time.</summary>
    Daily,

    /// <summary>Only a rough guess from how far apart their streams usually are.</summary>
    Interval
}

/// <summary>When a streamer is expected to go live next, how sure that is (0–1), and roughly for how long.</summary>
public sealed record StreamPrediction(DateTimeOffset At, TimeSpan? Length, double Confidence, PredictionKind Kind);

/// <summary>
/// Predicts a streamer's next stream from when they've streamed before. Streams are grouped by weekday and time of day
/// (recent weeks count more), and the soonest regular slot that hasn't been used yet is the prediction; failing a weekly
/// habit, a daily one; failing that, the usual gap since their last stream.
/// </summary>
public static class StreamPredictor
{
    /// <summary>A predicted start this long past is still "any minute now" rather than missed.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(45);

    // Streams within this much of each other (by time of day) are the same habit.
    private const int SlotMinutes = 75;

    // A week's streams count half as much as those three weeks newer.
    private const double HalfLifeDays = 21;

    // Days run from 4am, so a stream at 12:30am counts with the evening it carries on from.
    private static readonly TimeSpan DayStart = TimeSpan.FromHours(4);

    private const double MinimumConfidence = 0.3;

    public static StreamPrediction? Next(IReadOnlyList<StreamSession> history, DateTimeOffset now, TimeZoneInfo zone, DateTimeOffset? scheduled = null)
    {
        List<StreamSession> past = [.. history.Where(session => session.Start <= now).OrderBy(session => session.Start)];
        TimeSpan? usualLength = Median(past.Select(session => session.Length).Where(length => length > TimeSpan.Zero));

        if (scheduled is { } announced && announced > now - Grace)
        {
            return new StreamPrediction(announced, usualLength, 1, PredictionKind.Scheduled);
        }

        if (past.Count < 2)
        {
            return null;
        }

        List<Slot> starts = [.. past.Select(session => Slot.Of(session, now, zone))];
        double spanDays = Math.Max(7, (now - past[0].Start).TotalDays);

        return Habit(starts, past, now, zone, spanDays, weekly: true)
               ?? (IsDaily(starts, spanDays) ? Habit(starts, past, now, zone, spanDays, weekly: false) : null)
               ?? Interval(past, now, usualLength);
    }

    /// <summary>Streams on most days of the span (at least half of them).</summary>
    private static bool IsDaily(List<Slot> starts, double spanDays) =>
        starts.Select(slot => slot.Day).Distinct().Count() >= Math.Max(3, spanDays * 0.5);

    /// <summary>The soonest regular slot from now (weekly: by weekday and time; daily: by time alone).</summary>
    private static StreamPrediction? Habit(List<Slot> starts, List<StreamSession> past, DateTimeOffset now, TimeZoneInfo zone, double spanDays, bool weekly)
    {
        // What a habit would weigh if it had been kept every week (or every day) of the span.
        double period = weekly ? 7 : 1;
        double perfect = 0;
        for (double age = 0; age < spanDays; age += period)
        {
            perfect += Weight(age);
        }

        DateOnly today = DateOnly.FromDateTime(Local(now, zone).Date);
        StreamPrediction? best = null;
        for (int offset = -1; offset <= 14 && best is null; offset++)
        {
            DateOnly day = today.AddDays(offset);
            List<Slot> candidates = weekly ? [.. starts.Where(slot => slot.Weekday == day.DayOfWeek)] : starts;
            foreach (Habitual habit in Habits(candidates).OrderBy(habit => habit.Minute))
            {
                double confidence = Math.Min(1, habit.Weight / perfect);
                if (confidence < MinimumConfidence || habit.Count < 2)
                {
                    continue;
                }

                DateTimeOffset at = At(day, habit.Minute, zone);

                // Gone by, or already streamed in that slot (early, say).
                if (at <= now - Grace || past.Any(session => (session.Start - at).Duration() <= TimeSpan.FromHours(3)))
                {
                    continue;
                }

                best = new StreamPrediction(at, habit.Length, confidence, weekly ? PredictionKind.Weekly : PredictionKind.Daily);
                break;
            }
        }

        return best;
    }

    /// <summary>Groups starts by time of day: the heaviest groups first, each start in one group only.</summary>
    private static List<Habitual> Habits(List<Slot> starts)
    {
        List<Habitual> habits = [];
        HashSet<int> used = [];
        List<(Slot Seed, List<int> Members, double Weight)> groups = [];
        for (int seed = 0; seed < starts.Count; seed++)
        {
            List<int> members = [.. Enumerable.Range(0, starts.Count).Where(index => Math.Abs(starts[index].Minute - starts[seed].Minute) <= SlotMinutes)];
            groups.Add((starts[seed], members, members.Sum(index => starts[index].Weight)));
        }

        foreach (var group in groups.OrderByDescending(group => group.Weight))
        {
            List<int> fresh = [.. group.Members.Where(index => !used.Contains(index))];
            if (fresh.Count == 0)
            {
                continue;
            }

            used.UnionWith(fresh);
            List<Slot> slots = [.. fresh.Select(index => starts[index])];
            habits.Add(new Habitual(
                WeightedMedian(slots),
                slots.Sum(slot => slot.Weight),
                slots.Count,
                Median(slots.Select(slot => slot.Length).Where(length => length > TimeSpan.Zero))));
        }

        return habits;
    }

    /// <summary>No habit to go on: the usual gap between streams, after the last one.</summary>
    private static StreamPrediction? Interval(List<StreamSession> past, DateTimeOffset now, TimeSpan? usualLength)
    {
        List<TimeSpan> gaps = [];
        for (int index = 1; index < past.Count; index++)
        {
            TimeSpan gap = past[index].Start - past[index - 1].Start;
            if (gap >= TimeSpan.FromHours(2))
            {
                gaps.Add(gap);
            }
        }

        if (Median(gaps) is not { } usual)
        {
            return null;
        }

        DateTimeOffset at = past[^1].Start + usual;
        for (int step = 0; at <= now - Grace && step < 60; step++)
        {
            at += usual;
        }

        return at <= now - Grace ? null : new StreamPrediction(at, usualLength, 0.15, PredictionKind.Interval);
    }

    private static double Weight(double ageDays) => Math.Pow(0.5, Math.Max(0, ageDays) / HalfLifeDays);

    private static DateTime Local(DateTimeOffset at, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(at, zone).DateTime;

    /// <summary>The moment a streamer day's minute falls on (minutes count from 4am, so may run past midnight).</summary>
    private static DateTimeOffset At(DateOnly day, int minute, TimeZoneInfo zone)
    {
        DateTime local = day.ToDateTime(TimeOnly.MinValue) + DayStart + TimeSpan.FromMinutes(minute);
        if (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private static int WeightedMedian(List<Slot> slots)
    {
        List<Slot> sorted = [.. slots.OrderBy(slot => slot.Minute)];
        double half = sorted.Sum(slot => slot.Weight) / 2;
        double running = 0;
        foreach (Slot slot in sorted)
        {
            running += slot.Weight;
            if (running >= half)
            {
                return slot.Minute;
            }
        }

        return sorted[^1].Minute;
    }

    private static TimeSpan? Median(IEnumerable<TimeSpan> values)
    {
        List<TimeSpan> sorted = [.. values.Order()];
        return sorted.Count == 0 ? null : sorted[sorted.Count / 2];
    }

    /// <summary>A past start, by streamer day (from 4am), weekday and minute of that day, weighted by how recent it is.</summary>
    private sealed record Slot(DateOnly Day, DayOfWeek Weekday, int Minute, double Weight, TimeSpan Length)
    {
        public static Slot Of(StreamSession session, DateTimeOffset now, TimeZoneInfo zone)
        {
            DateTime shifted = Local(session.Start, zone) - DayStart;
            return new Slot(DateOnly.FromDateTime(shifted.Date), shifted.DayOfWeek, (int)shifted.TimeOfDay.TotalMinutes,
                StreamPredictor.Weight((now - session.Start).TotalDays), session.Length);
        }
    }

    private sealed record Habitual(int Minute, double Weight, int Count, TimeSpan? Length);
}
