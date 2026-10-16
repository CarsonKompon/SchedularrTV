using System.Text.Json;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class GeneratedPromoAvailabilityTests
{
    private static GeneratedPromoTemplate Christmas(GeneratedPromoType type = GeneratedPromoType.NowPlaying) => new()
    {
        Name = "Christmas", Type = type, Duration = TimeSpan.FromSeconds(10),
        Availability = new() { DateRange = new() { Start = "12-25", End = "12-25" } }
    };

    private static ChannelConfig Channel(params GeneratedPromoTemplate[] templates) => new()
    {
        Id = "test", Name = "Test", Number = 1, TimeZone = "UTC", AutoTemporalTags = false,
        PadToNearest = TimeSpan.FromMinutes(30),
        BaseRotation = new() { Include = [new() { Show = "A" }, new() { Show = "B" }] },
        Promos = new() { BaseQuery = "tag:'Promo'" }, GeneratedPromos = new() { Templates = [.. templates] }
    };

    [Theory]
    [InlineData("2026-12-24T23:59:59Z", false)]
    [InlineData("2026-12-25T00:00:00Z", true)]
    [InlineData("2026-12-25T23:59:59Z", true)]
    [InlineData("2026-12-26T00:00:00Z", false)]
    [InlineData("2027-12-25T12:00:00Z", true)]
    public void A_single_day_repeats_every_year_with_both_endpoints_inclusive(string at, bool expected) =>
        Assert.Equal(expected, GeneratedPromoEligibility.IsAvailable(Christmas(), DateTimeOffset.Parse(at), TimeZoneInfo.Utc));

    [Theory]
    [InlineData("2026-12-19T12:00:00Z", false)]
    [InlineData("2026-12-20T00:00:00Z", true)]
    [InlineData("2027-01-05T23:59:59Z", true)]
    [InlineData("2027-01-06T00:00:00Z", false)]
    public void Date_ranges_can_cross_new_year(string at, bool expected)
    {
        var template = Christmas();
        template.Availability!.DateRange = new() { Start = "12-20", End = "01-05" };
        Assert.Equal(expected, GeneratedPromoEligibility.IsAvailable(template, DateTimeOffset.Parse(at), TimeZoneInfo.Utc));
    }

    [Fact]
    public void Time_and_date_restrictions_both_apply_in_local_time_including_overnight()
    {
        var template = Christmas();
        template.Availability!.TimeRange = new() { Start = TimeSpan.FromHours(22), End = TimeSpan.FromHours(6) };
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(-5), "Test", "Test");
        Assert.False(GeneratedPromoEligibility.IsAvailable(template, DateTimeOffset.Parse("2026-12-25T04:59:59Z"), zone)); // Dec 24 locally
        Assert.True(GeneratedPromoEligibility.IsAvailable(template, DateTimeOffset.Parse("2026-12-25T05:00:00Z"), zone));
        Assert.False(GeneratedPromoEligibility.IsAvailable(template, DateTimeOffset.Parse("2026-12-25T11:00:00Z"), zone)); // 06:00 excluded
        Assert.True(GeneratedPromoEligibility.IsAvailable(template, DateTimeOffset.Parse("2026-12-26T03:00:00Z"), zone)); // Dec 25 22:00
        Assert.False(GeneratedPromoEligibility.IsAvailable(template, DateTimeOffset.Parse("2026-12-26T05:00:00Z"), zone)); // Dec 26 excluded
    }

    [Fact]
    public void Leap_day_is_valid_but_not_shifted_to_february_28_or_march_1()
    {
        var template = Christmas();
        template.Availability!.DateRange = new() { Start = "02-29", End = "02-29" };
        Assert.True(GeneratedPromoEligibility.IsAvailable(template, DateTimeOffset.Parse("2028-02-29T12:00:00Z"), TimeZoneInfo.Utc));
        Assert.False(GeneratedPromoEligibility.IsAvailable(template, DateTimeOffset.Parse("2027-02-28T12:00:00Z"), TimeZoneInfo.Utc));
        Assert.False(GeneratedPromoEligibility.IsAvailable(template, DateTimeOffset.Parse("2027-03-01T12:00:00Z"), TimeZoneInfo.Utc));
    }

    [Fact]
    public void Planner_filters_templates_and_uses_the_airing_channels_zone_when_borrowing()
    {
        var template = Christmas();
        var owner = Channel(template);
        owner.Id = "owner";
        var channel = Channel();
        channel.TimeZone = "Eastern Standard Time";
        channel.GeneratedPromos = new() { From = owner.Id };
        var block = new BlockConfig { Name = "Any ordinary block" };
        channel.Blocks.Add(block);
        var config = new SchedulerConfig { Channels = [owner, channel] };
        GeneratedPromoTemplate? Pick(string at) => GeneratedPromoPlanner.Choose(config, channel, block,
            GeneratedPromoType.NowPlaying, false, TimeSpan.FromMinutes(1), DateTimeOffset.Parse(at));
        Assert.Null(Pick("2026-12-25T04:59:59Z"));
        Assert.Same(template, Pick("2026-12-25T05:00:00Z"));
        Assert.Same(template, Pick("2027-12-25T12:00:00Z"));
        Assert.Null(Pick("2026-12-26T05:00:00Z"));
    }

    [Fact]
    public void A_programme_query_limits_a_template_to_the_programmes_it_matches()
    {
        GeneratedPromoTemplate action = new()
        {
            Name = "Action", Type = GeneratedPromoType.UpNext, Duration = TimeSpan.FromSeconds(10),
            Availability = new() { Query = "genre:'Action'" }
        };
        ChannelConfig channel = Channel(action);
        SchedulerConfig config = new() { Channels = [channel] };
        DateTimeOffset at = DateTimeOffset.Parse("2026-06-01T12:00:00Z");
        MediaItem explosions = new() { Id = "1", Title = "Explosions", Kind = MediaKind.Movie, Genres = ["Action"] };
        MediaItem feelings = new() { Id = "2", Title = "Feelings", Kind = MediaKind.Movie, Genres = ["Drama"] };

        GeneratedPromoTemplate? Pick(MediaItem? subject, bool defer = false) => GeneratedPromoPlanner.Choose(config, channel, null,
            GeneratedPromoType.UpNext, false, TimeSpan.FromMinutes(1), at, subject: subject, deferSubject: defer);

        Assert.Same(action, Pick(explosions));
        Assert.Null(Pick(feelings));
        // Unknown programme: left out, unless the schedule gets to say when the promo is made.
        Assert.Null(Pick(null));
        Assert.Same(action, Pick(null, defer: true));

        Assert.True(GeneratedPromoEligibility.SuitsProgramme(action, explosions));
        Assert.False(GeneratedPromoEligibility.SuitsProgramme(action, feelings));
        Assert.True(GeneratedPromoEligibility.SuitsProgramme(new GeneratedPromoTemplate(), null));
    }

    [Fact]
    public void A_tail_promo_is_checked_at_its_start_not_the_following_programmes_start()
    {
        var template = Christmas();
        var channel = Channel(template);
        var config = new SchedulerConfig { Channels = [channel] };
        var end = DateTimeOffset.Parse("2026-12-26T00:00:00Z");
        Assert.Null(GeneratedPromoPlanner.Choose(config, channel, null, template.Type, false, TimeSpan.FromMinutes(1), end));
        Assert.Same(template, GeneratedPromoPlanner.Choose(config, channel, null, template.Type, false, TimeSpan.FromMinutes(1), end, endsAt: true));
    }

    [Theory]
    [InlineData(GeneratedPromoType.NowPlaying)]
    [InlineData(GeneratedPromoType.UpNext)]
    public async Task Packed_schedules_never_air_the_seasonal_template_outside_its_date_range(GeneratedPromoType type)
    {
        var template = Christmas(type);
        var channel = Channel(template);
        channel.Promos!.UpNext.SkipWhenSameSeries = false;
        channel.Promos.NowPlaying.SkipWhenSameSeries = false;
        var config = new SchedulerConfig { Channels = [channel] };
        List<MediaItem> library = [];
        for (int episode = 1; episode <= 10; episode++)
        {
            library.Add(TestMedia.Episode("A", 1, episode, TimeSpan.FromMinutes(22)));
            library.Add(TestMedia.Episode("B", 1, episode, TimeSpan.FromMinutes(22)));
            library.Add(TestMedia.Filler($"ad-{episode}", TimeSpan.FromSeconds(10), "Commercials"));
        }
        var at = DateTimeOffset.Parse("2026-12-25T23:00:00Z");
        var engine = new SchedulingEngine(new FakeMediaLibrary(library), new InMemoryPlaybackStateStore());
        var schedule = await engine.BuildAsync(config, channel, at, at.AddHours(3));
        var promos = schedule.Entries.Where(entry => entry.GeneratedTemplate is not null).ToList();
        Assert.True(promos.Count > 0, string.Join('\n', schedule.Entries.Where(entry => entry.Kind == ScheduleEntryKind.Program)
            .Select(entry => $"{entry.Start:O} {entry.Item?.Title}")));
        Assert.All(promos, entry => Assert.True(GeneratedPromoEligibility.IsAvailable(template, entry.Start, TimeZoneInfo.Utc)));
        Assert.Contains(schedule.Entries, entry => entry.Start >= at.AddHours(1) && entry.Kind == ScheduleEntryKind.Program);
    }

    [Fact]
    public async Task Plugin_maker_declines_out_of_season_before_attempting_a_render()
    {
        var template = Christmas();
        var channel = Channel(template);
        var at = DateTimeOffset.Parse("2026-09-30T12:00:00Z");
        var schedule = new ChannelSchedule
        {
            ChannelId = channel.Id, ChannelName = channel.Name, Start = at, End = at.AddHours(1),
            Entries = [new() { Start = at.AddSeconds(10), Duration = TimeSpan.FromMinutes(22), Kind = ScheduleEntryKind.Program,
                Item = TestMedia.Episode("A", 1, 1, TimeSpan.FromMinutes(22)) }]
        };
        var maker = new GeneratedPromoMaker(null!); // Any attempt to render would fail this test.
        Assert.Null(await maker.MakeAsync(new() { Channels = [channel] }, channel, schedule, null, template.Type, at));
    }

    [Fact]
    public void Availability_round_trips_and_invalid_dates_and_times_are_reported()
    {
        var template = Christmas();
        template.Availability!.TimeRange = new() { Start = TimeSpan.FromHours(6), End = TimeSpan.FromHours(12) };
        var config = new SchedulerConfig { Channels = [Channel(template)] };
        var copy = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config)).Channels[0].GeneratedPromos!.Templates[0];
        Assert.Equal("12-25", copy.Availability!.DateRange!.Start);
        Assert.Equal("12-25", copy.Availability.DateRange.End);
        Assert.Equal(TimeSpan.FromHours(6), copy.Availability.TimeRange!.Start);
        Assert.DoesNotContain(SchedulerConfigLoader.Validate(config).Errors, error => error.Contains("availability"));
        template.Availability.DateRange!.End = "02-30";
        template.Availability.TimeRange.End = template.Availability.TimeRange.Start;
        var errors = SchedulerConfigLoader.Validate(config).Errors;
        Assert.Contains(errors, error => error.Contains("availability.date_range"));
        Assert.Contains(errors, error => error.Contains("availability.time_range"));
        template.Availability = null;
        Assert.DoesNotContain("Availability", JsonSerializer.Serialize(template));
    }
}
