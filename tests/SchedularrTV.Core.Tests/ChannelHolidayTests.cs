using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class ChannelHolidayTests
{
    // A Monday, in the week of Christmas 2026 (Sunday the 20th to Saturday the 26th).
    private static readonly DateTimeOffset ChristmasWeek = new(2026, 12, 21, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Spring = new(2026, 4, 13, 0, 0, 0, TimeSpan.Zero);

    // ---- When ----

    [Fact]
    public void The_week_of_a_day_is_the_whole_sunday_to_saturday_week_holding_it_that_year()
    {
        var christmas = new ChannelHoliday { Dates = HolidayDates.WeekOf, Date = "12-25", Query = "plot:'christmas'" };

        // Christmas 2026 is a Friday, 2027's a Saturday.
        Assert.Equal((new DateOnly(2026, 12, 20), new DateOnly(2026, 12, 26)), ChannelHolidays.In(christmas, 2026));
        Assert.Equal((new DateOnly(2027, 12, 19), new DateOnly(2027, 12, 25)), ChannelHolidays.In(christmas, 2027));
        Assert.True(ChannelHolidays.Contains(christmas, new DateOnly(2026, 12, 20)));
        Assert.False(ChannelHolidays.Contains(christmas, new DateOnly(2026, 12, 27)));

        christmas.WeeksBefore = 1;
        christmas.WeeksAfter = 1;
        Assert.Equal((new DateOnly(2026, 12, 13), new DateOnly(2027, 1, 2)), ChannelHolidays.In(christmas, 2026));
        // The week after reaches into the next year.
        Assert.True(ChannelHolidays.Contains(christmas, new DateOnly(2027, 1, 2)));
    }

    [Fact]
    public void A_range_of_days_can_run_into_the_new_year_and_leap_days_fall_back()
    {
        var newYear = new ChannelHoliday { Start = "12-26", End = "01-02", Query = "plot:'new year'" };
        Assert.True(ChannelHolidays.Contains(newYear, new DateOnly(2027, 1, 1)));
        Assert.True(ChannelHolidays.Contains(newYear, new DateOnly(2026, 12, 31)));
        Assert.False(ChannelHolidays.Contains(newYear, new DateOnly(2026, 6, 1)));

        var may = new ChannelHoliday { Start = "05-01", End = "05-05", WeeksAfter = 1, Query = "plot:'may'" };
        Assert.Equal((new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 12)), ChannelHolidays.In(may, 2026));

        var leap = new ChannelHoliday { Start = "02-29", End = "03-01", Query = "plot:'leap'" };
        Assert.Equal(new DateOnly(2026, 2, 28), ChannelHolidays.In(leap, 2026)!.Value.Start);
        Assert.Equal(new DateOnly(2028, 2, 29), ChannelHolidays.In(leap, 2028)!.Value.Start);
    }

    [Fact]
    public void Holidays_that_cant_be_used_are_reported()
    {
        ChannelConfig channel = Channel(new ChannelHoliday { Name = "Broken", Start = "13-01", End = "05-05", Query = "plot:'x'" },
            new ChannelHoliday { Name = "No query", Dates = HolidayDates.WeekOf, Date = "12-25" },
            new ChannelHoliday { Name = "Bad fallback", Dates = HolidayDates.WeekOf, Date = "12-25", Query = "plot:'x'", Fallback = "nowhere" });
        ConfigValidationResult result = SchedulerConfigLoader.Validate(Config(channel));

        Assert.Contains(result.Errors, error => error.StartsWith("channels.kids.holidays.Broken", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.StartsWith("channels.kids.holidays.No query", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("nowhere", StringComparison.Ordinal));
    }

    [Fact]
    public void Holidays_survive_the_schedule_file()
    {
        ChannelConfig channel = Channel(new ChannelHoliday
        {
            Name = "Christmas", Dates = HolidayDates.WeekOf, Date = "12-25", WeeksBefore = 2, Query = "plot:'christmas'", Global = true,
            Fallback = "specials", MinimumShows = 3, Order = PlaybackOrder.Random
        });

        ChannelHoliday read = Assert.Single(SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(Config(channel))).Channels[0].Holidays);
        Assert.Equal(("Christmas", HolidayDates.WeekOf, "12-25", 2), (read.Name, read.Dates, read.Date, read.WeeksBefore));
        Assert.Equal(("plot:'christmas'", true, "specials", 3, PlaybackOrder.Random),
            (read.Query, read.Global, read.Fallback, read.MinimumShows, read.Order));
    }

    // ---- What plays ----

    private static List<MediaItem> Library()
    {
        List<MediaItem> items = [];
        for (int episode = 1; episode <= 20; episode++)
        {
            string? festive = episode is 5 or 12 ? "The babies meet Santa at Christmas." : null;
            items.Add(TestMedia.Episode("Rugrats", 1, episode, TimeSpan.FromMinutes(22), "Kids", overview: festive ?? "The babies go on an adventure."));
            items.Add(TestMedia.Episode("Doug", 1, episode, TimeSpan.FromMinutes(22), "Kids",
                overview: episode == 7 ? "Doug's Christmas wish." : "Doug writes in his journal."));
            items.Add(TestMedia.Episode("Hey Arnold", 1, episode, TimeSpan.FromMinutes(22), "Kids", overview: "Arnold helps a friend."));
        }

        for (int episode = 1; episode <= 5; episode++)
        {
            items.Add(TestMedia.Episode("Winter Specials", 1, episode, TimeSpan.FromMinutes(22), "Specials", overview: "A snowy special."));
        }

        items.Add(TestMedia.Episode("Late Night", 1, 1, TimeSpan.FromMinutes(22), "Grown-ups", overview: "A Christmas party goes wrong."));
        return items;
    }

    private static ChannelConfig Channel(params ChannelHoliday[] holidays) => new()
    {
        Id = "kids",
        Name = "Kids",
        Number = 1,
        TimeZone = "UTC",
        AutoTemporalTags = false,
        BaseRotation = new ContentSelector { Collection = "kids", Order = PlaybackOrder.Shuffle },
        Holidays = [.. holidays]
    };

    private static SchedulerConfig Config(ChannelConfig channel) => new()
    {
        Settings = new SchedulerSettings { TimeZone = "UTC" },
        GlobalCollections = new Dictionary<string, ContentSelector>(StringComparer.OrdinalIgnoreCase)
        {
            ["kids"] = new() { Query = "collection:'Kids'", Order = PlaybackOrder.Shuffle },
            ["specials"] = new() { Query = "collection:'Specials'", Order = PlaybackOrder.Shuffle }
        },
        Channels = [channel]
    };

    private static async Task<List<MediaItem>> ProgrammesAsync(ChannelConfig channel, DateTimeOffset day)
    {
        var engine = new SchedulingEngine(new FakeMediaLibrary(Library()), new InMemoryPlaybackStateStore());
        ChannelSchedule schedule = await engine.BuildAsync(Config(channel), channel, day, day + TimeSpan.FromHours(12));
        return [.. schedule.Entries.Where(entry => entry.Kind == ScheduleEntryKind.Program && !entry.IsSlotIntro && !entry.IsSlotOutro)
            .Select(entry => entry.Item).OfType<MediaItem>()];
    }

    private static bool Festive(MediaItem item) => item.Overview?.Contains("christmas", StringComparison.OrdinalIgnoreCase) == true;

    private static ChannelHoliday Christmas() => new() { Name = "Christmas", Dates = HolidayDates.WeekOf, Date = "12-25", Query = "plot:'christmas'" };

    [Fact]
    public async Task On_a_holiday_only_its_episodes_of_what_the_channel_plays_air()
    {
        List<MediaItem> holiday = await ProgrammesAsync(Channel(Christmas()), ChristmasWeek);
        Assert.NotEmpty(holiday);
        Assert.All(holiday, item => Assert.True(Festive(item), item.Title));
        // Only the channel's own shows, not the grown-ups' Christmas episode.
        Assert.DoesNotContain(holiday, item => item.SeriesName == "Late Night");

        List<MediaItem> spring = await ProgrammesAsync(Channel(Christmas()), Spring);
        Assert.Contains(spring, item => !Festive(item));
    }

    [Fact]
    public async Task A_global_holiday_finds_its_episodes_anywhere_in_the_library()
    {
        ChannelHoliday holiday = Christmas();
        holiday.Global = true;
        List<MediaItem> played = await ProgrammesAsync(Channel(holiday), ChristmasWeek);

        Assert.All(played, item => Assert.True(Festive(item), item.Title));
        Assert.Contains(played, item => item.SeriesName == "Late Night");
    }

    [Fact]
    public async Task With_too_few_shows_the_rest_of_the_time_is_filled_in_with_the_usual_episodes()
    {
        // Two shows have Christmas episodes; four are wanted, so half the slots are the usual programming.
        ChannelHoliday holiday = Christmas();
        holiday.MinimumShows = 4;
        List<MediaItem> played = await ProgrammesAsync(Channel(holiday), ChristmasWeek);

        int festive = played.Count(Festive);
        Assert.InRange(festive, played.Count * 4 / 10, played.Count * 8 / 10);
        Assert.Contains(played, item => !Festive(item) && item.Collections.Contains("Kids"));
    }

    [Fact]
    public async Task With_too_few_shows_a_fallback_collection_fills_in()
    {
        ChannelHoliday holiday = Christmas();
        holiday.MinimumShows = 4;
        holiday.Fallback = "specials";
        List<MediaItem> played = await ProgrammesAsync(Channel(holiday), ChristmasWeek);

        Assert.Contains(played, Festive);
        Assert.Contains(played, item => item.SeriesName == "Winter Specials");
        Assert.All(played.Where(item => !Festive(item)), item => Assert.Equal("Winter Specials", item.SeriesName));
    }

    [Fact]
    public void Too_few_shows_get_their_share_of_turns_spread_evenly()
    {
        bool[] turns = [.. Enumerable.Range(0, 12).Select(turn => HolidayProgramming.Turn(turn, 1, 3))];
        Assert.Equal(4, turns.Count(turn => turn));
        Assert.Equal(new[] { false, false, true }, turns[..3]);
    }
}
