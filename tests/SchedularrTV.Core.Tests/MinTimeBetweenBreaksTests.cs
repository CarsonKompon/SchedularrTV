using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>Short programmes back to back break every so often, not after each one.</summary>
public sealed class MinTimeBetweenBreaksTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);

    private static List<MediaItem> Videos() =>
        [.. Enumerable.Range(1, 80).Select(number => TestMedia.Episode("Music Videos", 1, number, TimeSpan.FromMinutes(4), "Music"))];

    private static SchedulerConfig Config(TimeSpan? spacing)
    {
        SchedulerConfig config = new() { Settings = new SchedulerSettings { TimeZone = "UTC" } };
        config.Settings.Promos.Enabled = false;
        config.Channels.Add(new ChannelConfig
        {
            Id = "mtv",
            Name = "MTV",
            Number = 1,
            TimeZone = "UTC",
            AutoTemporalTags = false,
            PadToNearest = TimeSpan.FromMinutes(5),
            MinTimeBetweenBreaks = spacing,
            BaseRotation = new ContentSelector { Query = "kind == 'Episode'", Order = PlaybackOrder.Chronological }
        });
        return config;
    }

    /// <summary>Where each break between programmes starts and ends: the gaps between one programme and the next.</summary>
    private static List<(DateTimeOffset From, DateTimeOffset To)> Breaks(ChannelSchedule schedule)
    {
        List<ScheduleEntry> programmes = [.. schedule.Entries.Where(entry => entry.Kind == ScheduleEntryKind.Program).OrderBy(entry => entry.Start)];
        return [.. programmes.Zip(programmes.Skip(1))
            .Where(pair => pair.Second.Start > pair.First.End)
            .Select(pair => (pair.First.End, pair.Second.Start))];
    }

    [Fact]
    public async Task Without_it_every_short_programme_gets_a_break()
    {
        var engine = new SchedulingEngine(new FakeMediaLibrary(Videos()), new InMemoryPlaybackStateStore());
        SchedulerConfig config = Config(null);

        ChannelSchedule schedule = await engine.BuildAsync(config, config.Channels[0], Start, Start.AddHours(2));

        int programmes = schedule.Entries.Count(entry => entry.Kind == ScheduleEntryKind.Program);
        Assert.True(Breaks(schedule).Count >= programmes - 2);
    }

    [Fact]
    public async Task With_it_breaks_are_at_least_that_far_apart_and_land_back_on_the_grid()
    {
        var engine = new SchedulingEngine(new FakeMediaLibrary(Videos()), new InMemoryPlaybackStateStore());
        SchedulerConfig config = Config(TimeSpan.FromMinutes(15));

        ChannelSchedule schedule = await engine.BuildAsync(config, config.Channels[0], Start, Start.AddHours(2));
        List<(DateTimeOffset From, DateTimeOffset To)> breaks = Breaks(schedule);

        Assert.NotEmpty(breaks);
        DateTimeOffset lastEnd = Start;
        foreach ((DateTimeOffset from, DateTimeOffset to) in breaks)
        {
            Assert.True(from - lastEnd >= TimeSpan.FromMinutes(15), $"A break at {from:t} came {(from - lastEnd).TotalMinutes} min after the last.");
            // The break that does come rounds up to the 5-minute grid.
            Assert.Equal(0, to.TimeOfDay.Ticks % TimeSpan.FromMinutes(5).Ticks);
            lastEnd = to;
        }

        // Four-minute videos: three or four between breaks, so far fewer breaks than videos.
        int programmes = schedule.Entries.Count(entry => entry.Kind == ScheduleEntryKind.Program);
        Assert.True(breaks.Count * 3 <= programmes, $"{breaks.Count} breaks for {programmes} videos.");
    }

    [Fact]
    public void The_setting_survives_the_schedule_file()
    {
        SchedulerConfig config = Config(TimeSpan.FromMinutes(15));

        SchedulerConfig read = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config));

        Assert.Equal(TimeSpan.FromMinutes(15), read.Channels[0].MinTimeBetweenBreaks);
    }
}
