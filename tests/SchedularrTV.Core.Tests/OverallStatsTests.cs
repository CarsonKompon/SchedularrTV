using SchedularrTV.Service.Stats;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class OverallStatsTests
{
    private const long M = 29_000_000;

    private static OverallStatsReport Report()
    {
        AiringRow[] airings =
        [
            new("a", M * 60, "program", "Space Show", "S01E01 Pilot", "e1", 30, 20, 30, 2),
            new("b", M * 60, "program", "Big Movie", null, "m1", 90, 0, 0, 0),
            new("old", M * 60, "program", "Space Show", "S01E01 Pilot", "e9", 60, 0, 0, 0)
        ];

        List<(string, WatchedMinute)> watched = [];
        List<(string, ViewerMinute)> viewers = [];
        for (long minute = M; minute < M + 20; minute++)
        {
            bool both = minute < M + 10;
            watched.Add(("a", new WatchedMinute(minute, both ? 2 : 1)));
            viewers.Add(("a", new ViewerMinute(minute, "carson", "Jellyfin")));
            if (both)
            {
                viewers.Add(("a", new ViewerMinute(minute, "sam", "Plex")));
            }
        }

        StatsChannel[] channels = [new("a", "Alpha", 1), new("b", "Beta", 2), new("c", "Gamma", 3)];
        return OverallStats.Build(airings, watched, viewers, channels, M, M + 24 * 60, M, TimeZoneInfo.Utc);
    }

    [Fact]
    public void Totals_add_up_over_every_channel()
    {
        OverallStatsReport report = Report();

        Assert.Equal(180, report.AiredMinutes);
        Assert.Equal(20, report.WatchedMinutes);
        Assert.Equal(30, report.ViewerMinutes);
        Assert.Equal(2, report.PeakViewers);
        Assert.Equal(2, report.UniqueViewers);
        Assert.Equal(1, report.ChannelsWatched);
        Assert.Equal(3, report.ChannelsTotal);
    }

    [Fact]
    public void Channels_are_ranked_and_removed_ones_are_kept()
    {
        OverallStatsReport report = Report();

        ChannelRank top = report.Channels[0];
        Assert.Equal("a", top.ChannelId);
        Assert.Equal(2, top.Viewers);
        Assert.Equal(30, top.ViewerMinutes);

        Assert.Contains(report.Channels, channel => channel.ChannelId == "c" && channel.AiredMinutes == 0);
        Assert.True(Assert.Single(report.Channels, channel => channel.ChannelId == "old").Removed);
    }

    [Fact]
    public void Shows_and_titles_combine_channels()
    {
        OverallStatsReport report = Report();

        OverallShowStat show = Assert.Single(report.MostWatchedShows);
        Assert.Equal("Space Show", show.Show);
        Assert.Equal(2, show.Channels);
        Assert.Equal(2, show.Airings);
        Assert.Equal(2, show.Viewers);

        Assert.Equal("Big Movie", report.LeastWatchedShows[0].Show);
        Assert.Equal(1, report.NeverWatchedShows);

        OverallTitleStat title = Assert.Single(report.MostWatchedTitles);
        Assert.Equal("S01E01 Pilot", title.Title);
        Assert.Equal(90, title.AiredMinutes);
        Assert.Equal("Big Movie", report.LeastWatchedTitles[0].Show);
    }

    [Fact]
    public void Viewers_show_their_favourite_channel()
    {
        OverallWhoStat carson = Report().Who[0];

        Assert.Equal("carson", carson.Name);
        Assert.Equal(20, carson.Minutes);
        Assert.Equal(1, carson.Channels);
        Assert.Equal("Alpha", carson.Favourite);
    }
}
