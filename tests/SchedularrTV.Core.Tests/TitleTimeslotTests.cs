using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>A title's timeslots limit when it's considered for a programme, without promising it airs then.</summary>
public sealed class TitleTimeslotTests
{
    // A Tuesday.
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    private static DateTime At(DayOfWeek day, int hour, int minute = 0)
    {
        DateTime date = new(2026, 10, 18); // Sunday
        return date.AddDays((int)day).AddHours(hour).AddMinutes(minute);
    }

    [Fact]
    public void A_slot_allows_only_its_days_and_hours()
    {
        var slot = new TitleTimeslot { Days = [DayOfWeek.Saturday, DayOfWeek.Sunday], Start = TimeSpan.FromHours(7), End = TimeSpan.FromHours(12) };

        Assert.True(slot.Allows(At(DayOfWeek.Saturday, 7)));
        Assert.True(slot.Allows(At(DayOfWeek.Sunday, 11, 59)));
        Assert.False(slot.Allows(At(DayOfWeek.Sunday, 12)));
        Assert.False(slot.Allows(At(DayOfWeek.Saturday, 6, 59)));
        Assert.False(slot.Allows(At(DayOfWeek.Monday, 9)));
    }

    [Fact]
    public void Days_alone_allow_the_whole_day_and_times_alone_allow_every_day()
    {
        var weekdays = new TitleTimeslot { Days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday] };
        Assert.True(weekdays.Allows(At(DayOfWeek.Friday, 23, 30)));
        Assert.False(weekdays.Allows(At(DayOfWeek.Saturday, 0)));
        Assert.Equal("Mon–Fri", weekdays.Describe());

        var evenings = new TitleTimeslot { Start = TimeSpan.FromHours(18) };
        Assert.True(evenings.Allows(At(DayOfWeek.Wednesday, 20)));
        Assert.False(evenings.Allows(At(DayOfWeek.Wednesday, 17)));
        Assert.Equal("Every day from 6pm", evenings.Describe());
    }

    [Fact]
    public void An_end_before_the_start_runs_past_midnight_into_the_next_day()
    {
        var late = new TitleTimeslot { Days = [DayOfWeek.Friday], Start = TimeSpan.FromHours(22), End = TimeSpan.FromHours(2) };

        Assert.True(late.Allows(At(DayOfWeek.Friday, 23)));
        Assert.True(late.Allows(At(DayOfWeek.Saturday, 1, 30)));
        Assert.False(late.Allows(At(DayOfWeek.Saturday, 2)));
        Assert.False(late.Allows(At(DayOfWeek.Friday, 1)));
        Assert.False(late.Allows(At(DayOfWeek.Saturday, 23)));
    }

    [Fact]
    public void No_slots_is_any_time()
    {
        Assert.True(TitleTimeslot.AnyAllows([], At(DayOfWeek.Monday, 3)));
    }

    [Fact]
    public void Timeslots_survive_the_schedule_file()
    {
        SchedulerConfig config = new();
        config.GlobalCollections["Kids"] = new ContentSelector
        {
            Include =
            [
                new ContentInclude
                {
                    Show = "Rugrats",
                    Timeslots =
                    [
                        new TitleTimeslot { Days = [DayOfWeek.Sunday, DayOfWeek.Saturday, DayOfWeek.Sunday], Start = TimeSpan.FromHours(7), End = TimeSpan.FromHours(12) },
                        new TitleTimeslot { Days = [DayOfWeek.Wednesday], Start = TimeSpan.FromHours(30) }
                    ]
                }
            ]
        };

        SchedulerConfig read = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config));
        List<TitleTimeslot> slots = read.GlobalCollections["Kids"].Include[0].Timeslots;

        Assert.Equal(2, slots.Count);
        Assert.Equal(new[] { DayOfWeek.Sunday, DayOfWeek.Saturday }, slots[0].Days);
        Assert.Equal(TimeSpan.FromHours(7), slots[0].Start);
        Assert.Equal(TimeSpan.FromHours(12), slots[0].End);
        // A time past the end of the day is dropped rather than misread.
        Assert.Null(slots[1].Start);
    }

    private static SchedulerConfig Config(params ContentInclude[] kids)
    {
        SchedulerConfig config = new() { Settings = new SchedulerSettings { TimeZone = "UTC" } };
        config.Settings.Promos.Enabled = false;
        config.GlobalCollections["Kids"] = new ContentSelector
        {
            Include = [.. kids], Order = PlaybackOrder.Chronological, TitleOrder = TitleOrder.Shuffle
        };
        config.Channels.Add(new ChannelConfig
        {
            Id = "kids",
            Name = "Kids",
            Number = 1,
            TimeZone = "UTC",
            AutoTemporalTags = false,
            PadToNearest = TimeSpan.FromMinutes(30),
            BaseRotation = new ContentSelector { Collection = "Kids" }
        });
        return config;
    }

    private static List<MediaItem> Library() =>
        [.. new[] { "Alpha", "Bravo" }.SelectMany(show => Enumerable.Range(1, 60)
            .Select(episode => TestMedia.Episode(show, 1, episode, TimeSpan.FromMinutes(22), "Kids")))];

    [Fact]
    public async Task A_slotted_show_is_only_scheduled_inside_its_slot()
    {
        var engine = new SchedulingEngine(new FakeMediaLibrary(Library()), new InMemoryPlaybackStateStore());
        SchedulerConfig config = Config(
            new ContentInclude { Show = "Alpha", Timeslots = [new TitleTimeslot { Start = TimeSpan.FromHours(18), End = TimeSpan.FromHours(22) }] },
            new ContentInclude { Show = "Bravo" });

        ChannelSchedule schedule = await engine.BuildAsync(config, config.Channels[0], Start, Start.AddDays(1));
        List<ScheduleEntry> alpha = [.. schedule.Entries.Where(entry => entry.Kind == ScheduleEntryKind.Program && entry.Item?.SeriesName == "Alpha")];

        Assert.NotEmpty(alpha);
        Assert.All(alpha, entry => Assert.InRange(entry.Start.UtcDateTime.Hour, 18, 21));
        // Bravo fills the rest of the day.
        Assert.Contains(schedule.Entries, entry => entry.Item?.SeriesName == "Bravo" && entry.Start.UtcDateTime.Hour < 18);
    }

    [Fact]
    public async Task When_nothing_is_in_its_slot_the_collection_still_plays()
    {
        var engine = new SchedulingEngine(new FakeMediaLibrary(Library()), new InMemoryPlaybackStateStore());
        // Saturdays only, scheduled on a Tuesday: rather than dead air, the restriction gives way.
        SchedulerConfig config = Config(new ContentInclude { Show = "Alpha", Timeslots = [new TitleTimeslot { Days = [DayOfWeek.Saturday] }] });

        ChannelSchedule schedule = await engine.BuildAsync(config, config.Channels[0], Start, Start.AddHours(6));

        Assert.Contains(schedule.Entries, entry => entry.Kind == ScheduleEntryKind.Program && entry.Item?.SeriesName == "Alpha");
    }
}
