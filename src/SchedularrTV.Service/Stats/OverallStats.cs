namespace SchedularrTV.Service.Stats;

/// <summary>A channel as the Stats page knows it: its name and number, from the current schedules.</summary>
public sealed record StatsChannel(string Id, string Name, int Number);

/// <summary>How one channel did over the period.</summary>
public sealed record ChannelRank(
    string ChannelId,
    string Name,
    int? Number,
    bool Removed,
    int AiredMinutes,
    int WatchedMinutes,
    int ViewerMinutes,
    int Viewers,
    int Peak)
{
    /// <summary>The share of its airtime someone was watching, 0 to 1.</summary>
    public double Share => AiredMinutes == 0 ? 0 : (double)WatchedMinutes / AiredMinutes;
}

/// <summary>How a show did across every channel that aired it.</summary>
public sealed record OverallShowStat(string Show, int Airings, int Channels, int AiredMinutes, int WatchedMinutes, int ViewerMinutes, int Viewers, int Peak)
{
    public double Share => AiredMinutes == 0 ? 0 : (double)WatchedMinutes / AiredMinutes;
}

/// <summary>How an episode or film did across every channel that aired it.</summary>
public sealed record OverallTitleStat(string Show, string? Title, int Airings, int AiredMinutes, int WatchedMinutes, int ViewerMinutes, int Viewers)
{
    public double Share => AiredMinutes == 0 ? 0 : (double)WatchedMinutes / AiredMinutes;
}

/// <summary>A viewer across every channel: how long, how many channels, and their favourite.</summary>
public sealed record OverallWhoStat(string Name, string Via, int Minutes, int Channels, string? Favourite, DateTime LastSeen);

/// <summary>Everything the Stats page shows across all channels for one period.</summary>
public sealed record OverallStatsReport
{
    public required DateTime From { get; init; }
    public required DateTime To { get; init; }
    public DateTime? HistoryStart { get; init; }

    /// <summary>Airtime summed over every channel.</summary>
    public int AiredMinutes { get; init; }

    /// <summary>Channel-minutes someone was watching.</summary>
    public int WatchedMinutes { get; init; }
    public int ViewerMinutes { get; init; }

    /// <summary>Most viewers at once, over all channels together.</summary>
    public int PeakViewers { get; init; }
    public DateTime? PeakAt { get; init; }

    public int UniqueViewers { get; init; }
    public int ChannelsWatched { get; init; }
    public int ChannelsTotal { get; init; }

    public double ProgramShare { get; init; }
    public double BreakShare { get; init; }

    public IReadOnlyList<TimeBar> Bars { get; init; } = [];
    public bool BarsAreWeeks { get; init; }
    public IReadOnlyList<HeatCell> Heat { get; init; } = [];

    public IReadOnlyList<ChannelRank> Channels { get; init; } = [];
    public IReadOnlyList<OverallShowStat> MostWatchedShows { get; init; } = [];
    public IReadOnlyList<OverallShowStat> LeastWatchedShows { get; init; } = [];
    public int NeverWatchedShows { get; init; }
    public IReadOnlyList<OverallTitleStat> MostWatchedTitles { get; init; } = [];
    public IReadOnlyList<OverallTitleStat> LeastWatchedTitles { get; init; } = [];
    public IReadOnlyList<OverallWhoStat> Who { get; init; } = [];
    public IReadOnlyDictionary<string, int> MinutesByVia { get; init; } = new Dictionary<string, int>();

    public double WatchedShare => AiredMinutes == 0 ? 0 : (double)WatchedMinutes / AiredMinutes;
}

/// <summary>Works out stats across every channel from the viewing history.</summary>
public static class OverallStats
{
    /// <summary>Channels need at least this much airtime to count among the least watched.</summary>
    public const int LeastWatchedChannelMinutes = 60;

    /// <summary>Episodes and films need at least this much airtime to count among the least watched.</summary>
    public const int LeastWatchedTitleMinutes = 20;

    public const int ListSize = 10;

    public static OverallStatsReport Build(ViewingStore store, IReadOnlyList<StatsChannel> channels, StatsPeriod period, DateTimeOffset now, TimeZoneInfo zone)
    {
        long? first = store.FirstMinute();
        long to = ViewingStore.ToMinute(now);
        long from = period.Span is { } span ? ViewingStore.ToMinute(now - span) : first ?? to;
        return Build(store.AllAirings(from), store.AllWatched(from), store.AllViewers(from), channels, from, to, first, zone);
    }

    public static OverallStatsReport Build(
        IReadOnlyList<AiringRow> airings,
        IReadOnlyList<(string ChannelId, WatchedMinute Minute)> watched,
        IReadOnlyList<(string ChannelId, ViewerMinute Minute)> viewers,
        IReadOnlyList<StatsChannel> channels,
        long fromMinute, long toMinute, long? firstMinute, TimeZoneInfo zone)
    {
        DateTime Local(long minute) => TimeZoneInfo.ConvertTime(ViewingStore.FromMinute(minute), zone).DateTime;

        // Each channel's airings, oldest first, to find what was on at a given minute.
        Dictionary<string, List<AiringRow>> byChannel = airings.GroupBy(airing => airing.ChannelId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(airing => airing.Start).ToList(), StringComparer.Ordinal);

        AiringRow? At(string channelId, long minute)
        {
            if (!byChannel.TryGetValue(channelId, out List<AiringRow>? rows) || rows.Count == 0)
            {
                return null;
            }

            int low = 0, high = rows.Count - 1, found = -1;
            while (low <= high)
            {
                int mid = (low + high) / 2;
                if (rows[mid].Start / 60 <= minute)
                {
                    found = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return found < 0 ? null : rows[found];
        }

        // A programme split by mid-roll breaks is several rows; only its first part counts as an airing.
        HashSet<AiringRow> continuations = [];
        foreach (List<AiringRow> rows in byChannel.Values)
        {
            AiringRow? previous = null;
            foreach (AiringRow row in rows.Where(row => row.Kind != "break"))
            {
                if (previous is not null && row.ItemId is { } item && previous.ItemId == item)
                {
                    continuations.Add(row);
                }

                previous = row;
            }
        }

        List<AiringRow> programmes = [.. airings.Where(airing => airing.Kind != "break")];

        // Viewers are counted as people: a media server's user by name, and every direct player as one.
        static (string Name, string Via) Person(ViewerMinute minute) => minute.Via == "Direct" ? ("Direct viewer", "Direct") : (minute.Name, minute.Via);

        // Who watched each airing, for unique viewers per show and title.
        Dictionary<AiringRow, HashSet<(string, string)>> audience = [];
        Dictionary<string, HashSet<(string, string)>> channelAudience = new(StringComparer.Ordinal);
        foreach ((string channelId, ViewerMinute minute) in viewers)
        {
            (string, string) person = Person(minute);
            (channelAudience.TryGetValue(channelId, out var set) ? set : channelAudience[channelId] = []).Add(person);
            if (At(channelId, minute.Minute) is { } airing)
            {
                (audience.TryGetValue(airing, out var watchers) ? watchers : audience[airing] = []).Add(person);
            }
        }

        int AudienceOf(IEnumerable<AiringRow> rows)
        {
            HashSet<(string, string)> people = [];
            foreach (AiringRow row in rows)
            {
                if (audience.TryGetValue(row, out var watchers))
                {
                    people.UnionWith(watchers);
                }
            }

            return people.Count;
        }

        // Concurrent viewers over every channel, minute by minute.
        Dictionary<long, int> concurrent = [];
        foreach (var (_, minute) in watched)
        {
            concurrent[minute.Minute] = (concurrent.TryGetValue(minute.Minute, out int sum) ? sum : 0) + minute.Viewers;
        }

        KeyValuePair<long, int>? peak = concurrent.Count == 0 ? null
            : concurrent.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).First();

        // Viewer time by day, or by week over long periods.
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

        foreach (var (_, minute) in watched)
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

        // The heat map: average viewers over all channels in each weekday-and-hour.
        int[,] viewerMinutes = new int[7, 24], watchedMinutes = new int[7, 24], hours = new int[7, 24];
        for (long hour = fromMinute / 60 * 60; hour < toMinute; hour += 60)
        {
            DateTime at = Local(Math.Max(hour, fromMinute));
            hours[((int)at.DayOfWeek + 6) % 7, at.Hour]++;
        }

        foreach (var (_, minute) in watched)
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

        // Channels: every current channel (watched or not), and any removed one that still has history in the period.
        Dictionary<string, StatsChannel> known = channels.GroupBy(channel => channel.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        Dictionary<string, (int Watched, int Viewers, int Peak)> watchedByChannel = new(StringComparer.Ordinal);
        foreach ((string channelId, WatchedMinute minute) in watched)
        {
            var value = watchedByChannel.TryGetValue(channelId, out var found) ? found : (0, 0, 0);
            watchedByChannel[channelId] = (value.Item1 + 1, value.Item2 + minute.Viewers, Math.Max(value.Item3, minute.Viewers));
        }

        List<ChannelRank> channelRanks = [.. known.Keys.Concat(byChannel.Keys).Concat(watchedByChannel.Keys).Distinct(StringComparer.Ordinal).Select(id =>
        {
            known.TryGetValue(id, out StatsChannel? channel);
            var seen = watchedByChannel.TryGetValue(id, out var value) ? value : (0, 0, 0);
            return new ChannelRank(id, channel?.Name ?? id, channel?.Number, channel is null,
                byChannel.TryGetValue(id, out List<AiringRow>? rows) ? rows.Sum(row => row.AiredMinutes) : 0,
                seen.Item1, seen.Item2, channelAudience.TryGetValue(id, out var people) ? people.Count : 0, seen.Item3);
        }).OrderByDescending(rank => rank.ViewerMinutes).ThenBy(rank => rank.Number ?? int.MaxValue)];

        // Shows, by name across channels.
        List<OverallShowStat> shows = [.. programmes.GroupBy(airing => airing.Show, StringComparer.OrdinalIgnoreCase).Select(group => new OverallShowStat(
            group.First().Show,
            group.Count(airing => !continuations.Contains(airing)),
            group.Select(airing => airing.ChannelId).Distinct(StringComparer.Ordinal).Count(),
            group.Sum(airing => airing.AiredMinutes),
            group.Sum(airing => airing.WatchedMinutes),
            group.Sum(airing => airing.ViewerMinutes),
            AudienceOf(group),
            group.Max(airing => airing.Peak)))];

        // Episodes and films: a series' episode by its title, a film (no episode title) by itself.
        List<OverallTitleStat> titles = [.. programmes.GroupBy(airing => (Show: airing.Show.ToUpperInvariant(), Title: airing.Title?.ToUpperInvariant())).Select(group => new OverallTitleStat(
            group.First().Show,
            group.First().Title,
            group.Count(airing => !continuations.Contains(airing)),
            group.Sum(airing => airing.AiredMinutes),
            group.Sum(airing => airing.WatchedMinutes),
            group.Sum(airing => airing.ViewerMinutes),
            AudienceOf(group)))];

        // Viewers across every channel, with the channel they spent longest on.
        string ChannelName(string id) => known.TryGetValue(id, out StatsChannel? channel) ? channel.Name : id;
        List<OverallWhoStat> who = [.. viewers.GroupBy(row => Person(row.Minute)).Select(group =>
        {
            var perChannel = group.GroupBy(row => row.ChannelId, StringComparer.Ordinal).Select(c => (Id: c.Key, Minutes: c.Count())).OrderByDescending(c => c.Minutes).ToList();
            return new OverallWhoStat(group.Key.Name, group.Key.Via, group.Count(), perChannel.Count,
                perChannel.Count == 0 ? null : ChannelName(perChannel[0].Id), Local(group.Max(row => row.Minute.Minute)));
        }).OrderByDescending(row => row.Minutes)];

        Dictionary<string, int> byVia = viewers.GroupBy(row => row.Minute.Via).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

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

        return new OverallStatsReport
        {
            From = Local(fromMinute),
            To = Local(toMinute),
            HistoryStart = firstMinute is { } start ? Local(start) : null,
            AiredMinutes = airings.Sum(airing => airing.AiredMinutes),
            WatchedMinutes = watched.Count,
            ViewerMinutes = watched.Sum(row => row.Minute.Viewers),
            PeakViewers = peak?.Value ?? 0,
            PeakAt = peak is { } p ? Local(p.Key) : null,
            UniqueViewers = who.Count,
            ChannelsWatched = channelRanks.Count(rank => rank.ViewerMinutes > 0),
            ChannelsTotal = channelRanks.Count(rank => !rank.Removed),
            ProgramShare = Share(programmes),
            BreakShare = Share(airings.Where(airing => airing.Kind == "break")),
            Bars = bars,
            BarsAreWeeks = weeks,
            Heat = heat,
            Channels = channelRanks,
            MostWatchedShows = [.. shows.Where(show => show.ViewerMinutes > 0).OrderByDescending(show => show.ViewerMinutes).ThenByDescending(show => show.Share).Take(ListSize)],
            LeastWatchedShows = [.. shows.Where(show => show.AiredMinutes >= ChannelStats.LeastWatchedMinimumMinutes)
                .OrderBy(show => show.Share).ThenByDescending(show => show.AiredMinutes).Take(ListSize)],
            NeverWatchedShows = shows.Count(show => show.WatchedMinutes == 0 && show.AiredMinutes >= ChannelStats.LeastWatchedMinimumMinutes),
            MostWatchedTitles = [.. titles.Where(title => title.ViewerMinutes > 0).OrderByDescending(title => title.ViewerMinutes).ThenByDescending(title => title.Viewers).Take(ListSize)],
            LeastWatchedTitles = [.. titles.Where(title => title.AiredMinutes >= LeastWatchedTitleMinutes)
                .OrderBy(title => title.Share).ThenByDescending(title => title.AiredMinutes).Take(ListSize)],
            Who = who,
            MinutesByVia = byVia
        };
    }
}
