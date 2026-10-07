namespace SchedularrTV.Service.Stats;

/// <summary>A period the Stats tab can show.</summary>
public sealed record StatsPeriod(string Key, string Label, TimeSpan? Span)
{
    public static readonly IReadOnlyList<StatsPeriod> All =
    [
        new("day", "Last 24 hours", TimeSpan.FromDays(1)),
        new("week", "Last 7 days", TimeSpan.FromDays(7)),
        new("month", "Last 30 days", TimeSpan.FromDays(30)),
        new("quarter", "Last 90 days", TimeSpan.FromDays(90)),
        new("year", "Last year", TimeSpan.FromDays(365)),
        new("all", "Everything", null)
    ];

    public static StatsPeriod Find(string? key) => All.FirstOrDefault(period => period.Key == key) ?? All[1];
}

/// <summary>One bar of the watch-time chart: a day (or a week, over long periods).</summary>
public sealed record TimeBar(DateTime Start, string Label, int WatchedMinutes, int ViewerMinutes, int AiredMinutes);

/// <summary>A weekday-and-hour cell of the heat map: how many were watching on average, then.</summary>
/// <param name="Day">0 Monday to 6 Sunday.</param>
public sealed record HeatCell(int Day, int Hour, double AverageViewers, int WatchedMinutes);

/// <summary>A point of the recent-viewers line.</summary>
public sealed record ViewerPoint(DateTime At, double Viewers);

/// <summary>How a show did: how much of its airtime was watched, by how many.</summary>
public sealed record ShowStat(string Show, int Airings, int AiredMinutes, int WatchedMinutes, int ViewerMinutes, int Peak)
{
    /// <summary>The share of its airtime someone was watching, 0 to 1.</summary>
    public double Share => AiredMinutes == 0 ? 0 : (double)WatchedMinutes / AiredMinutes;
}

/// <summary>An episode or movie and its viewer-minutes.</summary>
public sealed record TitleStat(string Show, string? Title, int ViewerMinutes, int Airings);

/// <summary>A viewer and their minutes on the channel.</summary>
public sealed record WhoStat(string Name, string Via, int Minutes, DateTime LastSeen);

/// <summary>Everything the Stats tab shows for one channel and period.</summary>
public sealed record ChannelStatsReport
{
    public required DateTime From { get; init; }
    public required DateTime To { get; init; }

    /// <summary>When this channel's history begins; null when nothing has been recorded.</summary>
    public DateTime? HistoryStart { get; init; }

    public int AiredMinutes { get; init; }
    public int WatchedMinutes { get; init; }
    public int ViewerMinutes { get; init; }

    public int PeakViewers { get; init; }
    public DateTime? PeakAt { get; init; }
    public string? PeakShow { get; init; }

    public int UniqueViewers { get; init; }
    public int Sessions { get; init; }
    public double AverageSessionMinutes { get; init; }
    public int LongestSessionMinutes { get; init; }

    /// <summary>Sessions that ended during a break: tuning away at the ads.</summary>
    public int SessionsEndingInBreaks { get; init; }

    /// <summary>The share of programme time someone was watching, and of break time.</summary>
    public double ProgramShare { get; init; }
    public double BreakShare { get; init; }

    public IReadOnlyList<TimeBar> Bars { get; init; } = [];
    public bool BarsAreWeeks { get; init; }
    public IReadOnlyList<HeatCell> Heat { get; init; } = [];
    public IReadOnlyList<ViewerPoint> Recent { get; init; } = [];
    public IReadOnlyList<ShowStat> MostWatched { get; init; } = [];
    public IReadOnlyList<ShowStat> LeastWatched { get; init; } = [];
    public int NeverWatchedShows { get; init; }
    public IReadOnlyList<TitleStat> TopTitles { get; init; } = [];
    public IReadOnlyList<WhoStat> Who { get; init; } = [];
    public IReadOnlyDictionary<string, int> MinutesByVia { get; init; } = new Dictionary<string, int>();

    public double WatchedShare => AiredMinutes == 0 ? 0 : (double)WatchedMinutes / AiredMinutes;
}

/// <summary>Works out a channel's stats from its viewing history.</summary>
public static class ChannelStats
{
    /// <summary>A gap longer than this ends a viewing session.</summary>
    public const int SessionGapMinutes = 3;

    /// <summary>Shows need at least this much airtime to count among the least watched.</summary>
    public const int LeastWatchedMinimumMinutes = 30;

    public static ChannelStatsReport Build(ViewingStore store, string channelId, StatsPeriod period, DateTimeOffset now, TimeZoneInfo zone)
    {
        long? first = store.FirstMinute(channelId);
        long to = ViewingStore.ToMinute(now);
        long from = period.Span is { } span ? ViewingStore.ToMinute(now - span) : first ?? to;
        return Build(store.Airings(channelId, from), store.Watched(channelId, from), store.Viewers(channelId, from), from, to, first, zone);
    }

    public static ChannelStatsReport Build(IReadOnlyList<AiringRow> airings, IReadOnlyList<WatchedMinute> watched, IReadOnlyList<ViewerMinute> viewers,
        long fromMinute, long toMinute, long? firstMinute, TimeZoneInfo zone)
    {
        DateTime Local(long minute) => TimeZoneInfo.ConvertTime(ViewingStore.FromMinute(minute), zone).DateTime;

        // The airing each watched minute belongs to (by its start), for the peak's show and where sessions end.
        Dictionary<long, AiringRow> byStart = airings.GroupBy(airing => airing.Start).ToDictionary(group => group.Key, group => group.First());
        List<AiringRow> programmes = [.. airings.Where(airing => airing.Kind != "break")];
        List<AiringRow> breaks = [.. airings.Where(airing => airing.Kind == "break")];

        // The peak: most viewers at once, the first time it was reached.
        WatchedMinute? peak = watched.OrderByDescending(minute => minute.Viewers).ThenBy(minute => minute.Minute).FirstOrDefault();
        string? peakShow = peak is null ? null : ShowAt(airings, peak.Minute);

        // Sessions: runs of watched minutes with no gap longer than SessionGapMinutes.
        List<(long Start, long End)> sessions = [];
        foreach (WatchedMinute minute in watched)
        {
            if (sessions.Count > 0 && minute.Minute - sessions[^1].End <= SessionGapMinutes)
            {
                sessions[^1] = (sessions[^1].Start, minute.Minute);
            }
            else
            {
                sessions.Add((minute.Minute, minute.Minute));
            }
        }

        int endingInBreaks = sessions.Count(session => ShowKindAt(airings, session.End) == "break");

        // Watch time by day, or by week over long periods.
        long spanMinutes = Math.Max(1, toMinute - fromMinute);
        bool weeks = spanMinutes > 92 * 24 * 60;
        DateTime Bucket(long minute)
        {
            DateTime day = Local(minute).Date;
            return weeks ? day.AddDays(-(((int)day.DayOfWeek + 6) % 7)) : day;
        }

        Dictionary<DateTime, (int Watched, int Viewers, int Aired)> buckets = [];
        for (DateTime day = Bucket(fromMinute), last = Bucket(toMinute); day <= last; day = day.AddDays(weeks ? 7 : 1))
        {
            buckets[day] = (0, 0, 0);
        }

        foreach (WatchedMinute minute in watched)
        {
            DateTime key = Bucket(minute.Minute);
            if (buckets.TryGetValue(key, out var value))
            {
                buckets[key] = (value.Watched + 1, value.Viewers + minute.Viewers, value.Aired);
            }
        }

        foreach (AiringRow airing in airings)
        {
            DateTime key = Bucket(airing.Start / 60);
            if (buckets.TryGetValue(key, out var value))
            {
                buckets[key] = (value.Watched, value.Viewers, value.Aired + airing.AiredMinutes);
            }
        }

        List<TimeBar> bars = [.. buckets.OrderBy(pair => pair.Key).Select(pair => new TimeBar(pair.Key,
            weeks ? pair.Key.ToString("MMM d") : pair.Key.ToString("ddd d"), pair.Value.Watched, pair.Value.Viewers, pair.Value.Aired))];

        // The heat map: average viewers in each weekday-and-hour over the period (the hours the period covers).
        int[,] viewerMinutes = new int[7, 24], watchedMinutes = new int[7, 24], hours = new int[7, 24];
        for (long hour = fromMinute / 60 * 60; hour < toMinute; hour += 60)
        {
            DateTime at = Local(Math.Max(hour, fromMinute));
            hours[((int)at.DayOfWeek + 6) % 7, at.Hour]++;
        }

        foreach (WatchedMinute minute in watched)
        {
            DateTime at = Local(minute.Minute);
            int day = ((int)at.DayOfWeek + 6) % 7;
            viewerMinutes[day, at.Hour] += minute.Viewers;
            watchedMinutes[day, at.Hour]++;
        }

        List<HeatCell> heat = [];
        for (int day = 0; day < 7; day++)
        {
            for (int hour = 0; hour < 24; hour++)
            {
                heat.Add(new HeatCell(day, hour, hours[day, hour] == 0 ? 0 : viewerMinutes[day, hour] / 60.0 / hours[day, hour], watchedMinutes[day, hour]));
            }
        }

        // The last 24 hours (of the period), in quarter hours.
        List<ViewerPoint> recent = [];
        long recentFrom = Math.Max(fromMinute, toMinute - 24 * 60) / 15 * 15;
        Dictionary<long, int> quarter = watched.Where(minute => minute.Minute >= recentFrom).GroupBy(minute => minute.Minute / 15 * 15)
            .ToDictionary(group => group.Key, group => group.Sum(minute => minute.Viewers));
        for (long slot = recentFrom; slot <= toMinute; slot += 15)
        {
            recent.Add(new ViewerPoint(Local(slot), (quarter.TryGetValue(slot, out int sum) ? sum : 0) / 15.0));
        }

        // A programme split by mid-roll breaks is several rows; only its first part counts as an airing.
        HashSet<AiringRow> continuations = [];
        for (int i = 1; i < programmes.Count; i++)
        {
            if (programmes[i].ItemId is { } item && programmes[i - 1].ItemId == item)
            {
                continuations.Add(programmes[i]);
            }
        }

        // Shows: programmes and short shows (not breaks), by show.
        List<ShowStat> shows = [.. programmes.GroupBy(airing => airing.Show, StringComparer.OrdinalIgnoreCase).Select(group => new ShowStat(
            group.First().Show, group.Count(airing => !continuations.Contains(airing)), group.Sum(airing => airing.AiredMinutes), group.Sum(airing => airing.WatchedMinutes),
            group.Sum(airing => airing.ViewerMinutes), group.Max(airing => airing.Peak)))];
        List<ShowStat> most = [.. shows.Where(show => show.ViewerMinutes > 0).OrderByDescending(show => show.ViewerMinutes).ThenByDescending(show => show.Share).Take(10)];
        List<ShowStat> least = [.. shows.Where(show => show.AiredMinutes >= LeastWatchedMinimumMinutes)
            .OrderBy(show => show.Share).ThenByDescending(show => show.AiredMinutes).Take(10)];

        List<TitleStat> titles = [.. programmes.Where(airing => airing.ViewerMinutes > 0)
            .GroupBy(airing => (airing.Show, airing.Title))
            .Select(group => new TitleStat(group.Key.Show, group.Key.Title, group.Sum(airing => airing.ViewerMinutes), group.Count(airing => !continuations.Contains(airing))))
            .OrderByDescending(title => title.ViewerMinutes).Take(8)];

        List<WhoStat> who = [.. viewers.GroupBy(viewer => (viewer.Name, viewer.Via))
            .Select(group => new WhoStat(group.Key.Name, group.Key.Via, group.Count(), Local(group.Max(viewer => viewer.Minute))))
            .OrderByDescending(viewer => viewer.Minutes)];

        Dictionary<string, int> byVia = viewers.GroupBy(viewer => viewer.Via).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        double Share(IEnumerable<AiringRow> rows)
        {
            int aired = 0, seen = 0;
            foreach (AiringRow row in rows)
            {
                aired += row.AiredMinutes;
                seen += row.WatchedMinutes;
            }

            return aired == 0 ? 0 : (double)seen / aired;
        }

        return new ChannelStatsReport
        {
            From = Local(fromMinute),
            To = Local(toMinute),
            HistoryStart = firstMinute is { } start ? Local(start) : null,
            AiredMinutes = airings.Sum(airing => airing.AiredMinutes),
            WatchedMinutes = watched.Count,
            ViewerMinutes = watched.Sum(minute => minute.Viewers),
            PeakViewers = peak?.Viewers ?? 0,
            PeakAt = peak is null ? null : Local(peak.Minute),
            PeakShow = peakShow,
            UniqueViewers = who.Select(viewer => (viewer.Name, viewer.Via)).Count(viewer => viewer.Via != "Direct") + (who.Any(viewer => viewer.Via == "Direct") ? 1 : 0),
            Sessions = sessions.Count,
            AverageSessionMinutes = sessions.Count == 0 ? 0 : sessions.Average(session => session.End - session.Start + 1),
            LongestSessionMinutes = sessions.Count == 0 ? 0 : (int)sessions.Max(session => session.End - session.Start + 1),
            SessionsEndingInBreaks = endingInBreaks,
            ProgramShare = Share(programmes),
            BreakShare = Share(breaks),
            Bars = bars,
            BarsAreWeeks = weeks,
            Heat = heat,
            Recent = recent,
            MostWatched = most,
            LeastWatched = least,
            NeverWatchedShows = shows.Count(show => show.WatchedMinutes == 0 && show.AiredMinutes >= LeastWatchedMinimumMinutes),
            TopTitles = titles,
            Who = who,
            MinutesByVia = byVia
        };

        // What was airing at a minute: the latest airing that started at or before it.
        static AiringRow? At(IReadOnlyList<AiringRow> rows, long minute)
        {
            AiringRow? found = null;
            foreach (AiringRow row in rows)
            {
                if (row.Start / 60 > minute)
                {
                    break;
                }

                found = row;
            }

            return found;
        }

        static string? ShowAt(IReadOnlyList<AiringRow> rows, long minute) => At(rows, minute) is { } row ? row.Kind == "break" ? "a break" : row.Show : null;

        static string? ShowKindAt(IReadOnlyList<AiringRow> rows, long minute) => At(rows, minute)?.Kind;
    }
}
