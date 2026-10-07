using System.Text.Json;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Service.Stats;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class StatsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "stats-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    // --- Audience sources ----------------------------------------------------------------------------------------

    [Fact]
    public void Jellyfin_sessions_on_live_tv_are_read_and_others_ignored()
    {
        using JsonDocument json = JsonDocument.Parse("""
            [
              { "UserName": "carson", "DeviceName": "Living room", "NowPlayingItem": { "Type": "TvChannel", "Name": "Cartoon Toons", "ChannelNumber": "5" } },
              { "UserName": "sam", "Client": "Jellyfin Web", "NowPlayingItem": { "Type": "Program", "ChannelName": "Movie Night", "ChannelId": "abc" } },
              { "UserName": "alex", "NowPlayingItem": { "Type": "Episode", "Name": "Pilot" } },
              { "UserName": "idle" }
            ]
            """);

        IReadOnlyList<LiveSession> sessions = AudienceReader.ReadJellyfin(json.RootElement);

        Assert.Equal(2, sessions.Count);
        Assert.Equal(new LiveSession(5, "Cartoon Toons", "carson", "Jellyfin", "Living room"), sessions[0]);
        Assert.Equal(new LiveSession(null, "Movie Night", "sam", "Jellyfin", "Jellyfin Web"), sessions[1]);
    }

    [Fact]
    public void Plex_live_sessions_are_read_by_channel_number_or_title()
    {
        using JsonDocument json = JsonDocument.Parse("""
            { "MediaContainer": { "Metadata": [
              { "live": "1", "User": { "title": "carson" }, "Player": { "title": "Shield" }, "Media": [ { "channelVcn": "7", "channelTitle": "Retro TV" } ] },
              { "channelCallSign": "Movie Night", "User": { "title": "sam" } },
              { "type": "episode", "User": { "title": "alex" } }
            ] } }
            """);

        IReadOnlyList<LiveSession> sessions = AudienceReader.ReadPlex(json.RootElement);

        Assert.Equal(2, sessions.Count);
        Assert.Equal(new LiveSession(7, "Retro TV", "carson", "Plex", "Shield"), sessions[0]);
        Assert.Equal(new LiveSession(null, "Movie Night", "sam", "Plex", null), sessions[1]);
    }

    [Fact]
    public void Sessions_match_channels_by_number_then_name()
    {
        AudienceChannel[] channels = [new("toons", 5, "Cartoon Toons"), new("movies", 9, "Movie Night")];

        Assert.Equal("toons", AudienceReader.Match(channels, new LiveSession(5, "Something else", "u", "Jellyfin", null))?.Id);
        Assert.Equal("movies", AudienceReader.Match(channels, new LiveSession(null, " movie night ", "u", "Plex", null))?.Id);
        Assert.Equal("movies", AudienceReader.Match(channels, new LiveSession(42, "Movie Night", "u", "Plex", null))?.Id);
        Assert.Null(AudienceReader.Match(channels, new LiveSession(42, "Nope", "u", "Plex", null)));
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("5.1", 5)]
    [InlineData("ch12", 12)]
    [InlineData("", null)]
    [InlineData("abc", null)]
    public void Channel_numbers_are_read_from_text(string text, int? expected) =>
        Assert.Equal(expected, AudienceReader.Number(text));

    // --- Describing what aired -----------------------------------------------------------------------------------

    [Fact]
    public void Airings_are_described_by_show_and_episode()
    {
        DateTimeOffset start = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);
        var episode = new ScheduleEntry
        {
            Start = start, Duration = TimeSpan.FromMinutes(22), Kind = ScheduleEntryKind.Program,
            Item = new MediaItem { Id = "e1", Title = "The Pilot", SeriesName = "Space Show", SeasonNumber = 1, EpisodeNumber = 2 }
        };
        var movie = episode with { Item = new MediaItem { Id = "m1", Title = "Big Movie" } };
        var advert = episode with { Kind = ScheduleEntryKind.Commercial, Item = new MediaItem { Id = "a1", Title = "Soda ad" } };

        Assert.Equal(("program", "Space Show", "S01E02 The Pilot"), ViewingRecorder.Describe(episode));
        Assert.Equal(("program", "Big Movie", (string?)null), ViewingRecorder.Describe(movie));
        Assert.Equal(("break", "Breaks", "Soda ad"), ViewingRecorder.Describe(advert));
    }

    // --- The store -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_minute_recorded_twice_counts_once()
    {
        var store = new ViewingStore(Path.Combine(_folder, "viewing.db"));
        MinuteSample sample = Sample(1000, 59_000, "Space Show", [new Viewer("carson", "Jellyfin")]);

        store.Record([sample]);
        store.Record([sample]);
        store.Record([sample with { Minute = 1001 }]);

        AiringRow airing = Assert.Single(store.Airings("toons", 0));
        Assert.Equal(2, airing.AiredMinutes);
        Assert.Equal(2, airing.WatchedMinutes);
        Assert.Equal(2, airing.ViewerMinutes);
        Assert.Equal(2, store.Watched("toons", 0).Count);
        Assert.Equal(2, store.Viewers("toons", 0).Count);
    }

    [Fact]
    public void Unwatched_minutes_only_count_in_their_airing()
    {
        var store = new ViewingStore(Path.Combine(_folder, "viewing.db"));
        store.Record([Sample(1000, 59_000, "Space Show", []), Sample(1001, 59_000, "Space Show", [])]);

        Assert.Equal(2, Assert.Single(store.Airings("toons", 0)).AiredMinutes);
        Assert.Empty(store.Watched("toons", 0));
        Assert.Equal(59_000 / 60, store.FirstMinute("toons"));

        store.Clear("toons");
        Assert.Null(store.FirstMinute("toons"));
    }

    // --- The report ----------------------------------------------------------------------------------------------

    [Fact]
    public void Report_finds_peak_sessions_and_most_and_least_watched()
    {
        // 60 minutes of Space Show (watched 30 of them, two people for 10), then a 5 minute break (left during it),
        // then 60 minutes of Dull Show nobody watched.
        long start = 28_000_000;
        List<AiringRow> airings =
        [
            new("toons", start * 60, "program", "Space Show", "S01E01", "e1", 60, 30, 40, 2),
            new("toons", (start + 60) * 60, "break", "Breaks", "Soda ad", "a1", 5, 2, 2, 1),
            new("toons", (start + 65) * 60, "program", "Dull Show", null, "d1", 60, 0, 0, 0)
        ];
        List<WatchedMinute> watched = [];
        List<ViewerMinute> viewers = [];
        for (long minute = start + 30; minute < start + 62; minute++)
        {
            int count = minute < start + 40 ? 2 : 1;
            watched.Add(new WatchedMinute(minute, count));
            viewers.Add(new ViewerMinute(minute, "carson", "Jellyfin"));
            if (count == 2)
            {
                viewers.Add(new ViewerMinute(minute, "sam", "Plex"));
            }
        }

        ChannelStatsReport report = ChannelStats.Build(airings, watched, viewers, start, start + 125, start, TimeZoneInfo.Utc);

        Assert.Equal(125, report.AiredMinutes);
        Assert.Equal(32, report.WatchedMinutes);
        Assert.Equal(2, report.PeakViewers);
        Assert.Equal("Space Show", report.PeakShow);
        Assert.Equal(2, report.UniqueViewers);
        Assert.Equal(1, report.Sessions);
        Assert.Equal(32, report.LongestSessionMinutes);
        Assert.Equal(1, report.SessionsEndingInBreaks);
        Assert.Equal("Space Show", Assert.Single(report.MostWatched).Show);
        Assert.Equal("Dull Show", report.LeastWatched[0].Show);
        Assert.Equal(1, report.NeverWatchedShows);
        Assert.Equal(0.25, report.ProgramShare, 3);
        Assert.Equal(0.4, report.BreakShare, 3);
        Assert.Equal(new[] { "Jellyfin", "Plex" }, report.Who.Select(who => who.Via));
        Assert.Equal(7 * 24, report.Heat.Count);
    }

    [Fact]
    public void A_programme_split_by_breaks_counts_as_one_airing()
    {
        long start = 28_000_000;
        List<AiringRow> airings =
        [
            new("toons", start * 60, "program", "Space Show", "S01E01", "e1", 20, 0, 0, 0),
            new("toons", (start + 20) * 60, "break", "Breaks", "Ad", "a1", 2, 0, 0, 0),
            new("toons", (start + 22) * 60, "program", "Space Show", "S01E01", "e1", 20, 0, 0, 0)
        ];

        ChannelStatsReport report = ChannelStats.Build(airings, [], [], start, start + 42, start, TimeZoneInfo.Utc);

        ShowStat show = Assert.Single(report.LeastWatched);
        Assert.Equal(1, show.Airings);
        Assert.Equal(40, show.AiredMinutes);
    }

    [Fact]
    public void Long_periods_chart_by_week()
    {
        long now = 28_000_000;
        ChannelStatsReport report = ChannelStats.Build([], [], [], now - 200 * 24 * 60, now, null, TimeZoneInfo.Utc);

        Assert.True(report.BarsAreWeeks);
        Assert.InRange(report.Bars.Count, 28, 31);
        Assert.Null(report.HistoryStart);
    }

    private static MinuteSample Sample(long minute, long airingStart, string show, IReadOnlyList<Viewer> who) =>
        new(minute, "toons", airingStart, "program", show, null, "e1", who.Count, who);
}
