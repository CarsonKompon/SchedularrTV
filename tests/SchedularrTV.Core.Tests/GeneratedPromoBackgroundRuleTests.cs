using System.Text.Json;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class GeneratedPromoBackgroundRuleTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly UpNextListing[] Upcoming = [new("Doug", Noon.AddMinutes(1)), new("The Powerpuff Girls", Noon.AddMinutes(30))];

    private static GeneratedPromoTemplate Template(bool fallback = true) => new()
    {
        Name = "Conditional", Background = "special.png", Backgrounds = fallback ? ["ordinary.png"] : null,
        Layers = [new() { Kind = GeneratedLayerKind.UpNext, UpNext = new() { ShowsAhead = 1 } }],
        Voice = new(),
        BackgroundRules = [new() { Background = "special.png", Shows = ["Rugrats", "Powerpuff Girls"], OnlyWhenMatching = true }]
    };

    [Fact]
    public void Daily_ranges_use_channel_time_with_inclusive_start_and_exclusive_end()
    {
        var rule = new GeneratedBackgroundRule
        {
            MatchShows = false,
            TimeRange = new() { Start = TimeSpan.FromHours(6), End = TimeSpan.FromHours(12) }
        };
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(-6), "Test", "Test");
        Assert.Equal(0, GeneratedPromoBackgrounds.Weight(rule, [], Noon.AddSeconds(-1), zone: zone));
        Assert.Equal(1, GeneratedPromoBackgrounds.Weight(rule, [], Noon, zone: zone));
        Assert.Equal(1, GeneratedPromoBackgrounds.Weight(rule, [], Noon.AddHours(6).AddSeconds(-1), zone: zone));
        Assert.Equal(0, GeneratedPromoBackgrounds.Weight(rule, [], Noon.AddHours(6), zone: zone));
    }

    [Theory]
    [InlineData(21, 59, 0)]
    [InlineData(22, 0, 1)]
    [InlineData(0, 0, 1)]
    [InlineData(5, 59, 1)]
    [InlineData(6, 0, 0)]
    public void Overnight_ranges_cross_midnight(int hour, int minute, double expected)
    {
        var rule = new GeneratedBackgroundRule
        {
            MatchShows = false,
            TimeRange = new() { Start = TimeSpan.FromHours(22), End = TimeSpan.FromHours(6) }
        };
        var at = new DateTimeOffset(2026, 10, 15, hour, minute, 0, TimeSpan.Zero);
        Assert.Equal(expected, GeneratedPromoBackgrounds.Weight(rule, [], at));
    }

    [Fact]
    public void Matching_show_cannot_bypass_time_range_and_resolver_uses_its_zone()
    {
        var template = Template(fallback: false);
        template.Type = GeneratedPromoType.NowPlaying;
        template.BackgroundRules![0].TimeRange = new() { Start = TimeSpan.FromHours(6), End = TimeSpan.FromHours(12) };
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(-6), "Test", "Test");
        UpNextListing[] subject = [new("Rugrats", Noon.AddHours(-1))];
        var morning = GeneratedPromoResolver.Resolve(template, "Channel", subject, zone, Noon)!;
        var afternoon = GeneratedPromoResolver.Resolve(template, "Channel", subject, zone, Noon.AddHours(6))!;
        Assert.Equal("special.png", GeneratedPromoResolver.Compose(morning).BackgroundPath);
        Assert.Null(GeneratedPromoResolver.Compose(afternoon).BackgroundPath);
        Assert.NotEqual(morning.Key, afternoon.Key);
    }

    [Fact]
    public void Time_only_rules_round_trip_without_requiring_show_names_and_invalid_times_are_rejected()
    {
        var template = Template();
        var rule = template.BackgroundRules![0];
        rule.MatchShows = false;
        rule.Shows = [];
        rule.TimeRange = new() { Start = TimeSpan.FromHours(22), End = TimeSpan.FromHours(6) };
        var config = new SchedulerConfig { Channels = [new() { Id = "test", Name = "Test", GeneratedPromos = new() { Templates = [template] } }] };
        Assert.DoesNotContain(SchedulerConfigLoader.Validate(config).Errors, error => error.Contains("background_rules"));
        var copy = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config)).Channels[0].GeneratedPromos!.Templates[0];
        Assert.False(copy.BackgroundRules![0].MatchShows);
        Assert.Equal(TimeSpan.FromHours(22), copy.BackgroundRules[0].TimeRange!.Start);
        Assert.Equal(TimeSpan.FromHours(6), copy.BackgroundRules[0].TimeRange!.End);
        rule.TimeRange.End = rule.TimeRange.Start;
        Assert.Contains(SchedulerConfigLoader.Validate(config).Errors, error => error.Contains("background_rules.time_range"));
        rule.TimeRange.End = TimeSpan.FromDays(1);
        Assert.Contains(SchedulerConfigLoader.Validate(config).Errors, error => error.Contains("background_rules.time_range"));
    }

    [Fact]
    public void Restriction_matches_any_named_show_only_inside_the_requested_show_count()
    {
        var rule = Template().BackgroundRules![0];
        Assert.Equal(0, GeneratedPromoBackgrounds.Weight(rule, Upcoming, Noon));
        rule.NextShows = 2;
        Assert.Equal(5, GeneratedPromoBackgrounds.Weight(rule, Upcoming, Noon));
        Assert.Equal(0, GeneratedPromoBackgrounds.Weight(rule, [], Noon));
        Assert.Equal(5, GeneratedPromoBackgrounds.Weight(rule, [new("RUGRATS", Noon)], Noon));
        Assert.Equal(0, GeneratedPromoBackgrounds.Weight(rule, [new("Rugrats", Noon.AddMinutes(-1))], Noon));
    }

    [Fact]
    public void Time_window_uses_airtime_and_includes_the_boundary()
    {
        var rule = Template().BackgroundRules![0];
        rule.Within = TimeSpan.FromMinutes(29);
        Assert.Equal(0, GeneratedPromoBackgrounds.Weight(rule, Upcoming, Noon));
        rule.Within = TimeSpan.FromMinutes(30);
        Assert.Equal(5, GeneratedPromoBackgrounds.Weight(rule, Upcoming, Noon));
    }

    [Fact]
    public void Preferred_background_remains_eligible_and_is_weighted_on_a_match()
    {
        var template = Template();
        var rule = template.BackgroundRules![0];
        rule.OnlyWhenMatching = false;
        rule.NextShows = 2;
        rule.MatchWeight = 9;
        Assert.Equal(1, GeneratedPromoBackgrounds.Weight(rule, [], Noon));
        int special = Enumerable.Range(0, 1000).Count(seed =>
            GeneratedPromoBackgrounds.Select(template, Upcoming, Noon, seed.ToString()) == "special.png");
        Assert.InRange(special, 850, 950);
        string? picked = GeneratedPromoBackgrounds.Select(template, Upcoming, Noon, "repeat");
        Assert.Equal(picked, GeneratedPromoBackgrounds.Select(template, Upcoming, Noon, "repeat"));
    }

    [Fact]
    public void No_match_uses_an_unrestricted_background_or_solid_colour_never_a_restricted_one()
    {
        var template = Template();
        var promo = GeneratedPromoResolver.Resolve(template, "Channel", Upcoming, TimeZoneInfo.Utc, Noon)!;
        Assert.Equal("ordinary.png", GeneratedPromoResolver.Compose(promo).BackgroundPath);
        template.Backgrounds = null;
        promo = GeneratedPromoResolver.Resolve(template, "Channel", Upcoming, TimeZoneInfo.Utc, Noon)!;
        Assert.Null(GeneratedPromoResolver.Compose(promo).BackgroundPath);
    }

    [Fact]
    public void Schedule_context_changes_the_background_and_key_without_changing_spoken_or_visible_shows()
    {
        var template = Template(fallback: false);
        template.BackgroundRules![0].NextShows = 2;
        UpNextListing[] promoted = [Upcoming[0]];
        var matching = GeneratedPromoResolver.Resolve(template, "Channel", promoted, TimeZoneInfo.Utc, Noon,
            new(null, Upcoming))!;
        var absent = GeneratedPromoResolver.Resolve(template, "Channel", promoted, TimeZoneInfo.Utc, Noon,
            new(null, promoted))!;
        Assert.Equal("special.png", GeneratedPromoResolver.Compose(matching).BackgroundPath);
        Assert.Null(GeneratedPromoResolver.Compose(absent).BackgroundPath);
        Assert.NotEqual(matching.Key, absent.Key);
        Assert.Equal(matching.Script, absent.Script);
        Assert.DoesNotContain("Powerpuff", matching.Script!);
        Assert.Equal(new[] { "Doug" }, matching.LayerLines[0]);
        Assert.Equal(1, GeneratedPromoResolver.ListingsNeeded(template));
    }

    [Fact]
    public void Now_playing_matches_its_subject_even_after_it_started_and_ignores_upcoming_matches()
    {
        var template = Template(fallback: false);
        template.Type = GeneratedPromoType.NowPlaying;
        // A saved lookahead should not affect Now Playing, including if the subject began earlier.
        template.BackgroundRules![0].Within = TimeSpan.FromMinutes(1);
        var matching = GeneratedPromoResolver.Resolve(template, "Channel", [new("Rugrats", Noon.AddMinutes(-20))],
            TimeZoneInfo.Utc, Noon, new(new("Doug", Noon.AddMinutes(-30)), [new("Doug", Noon.AddMinutes(30))]))!;
        Assert.Equal("special.png", GeneratedPromoResolver.Compose(matching).BackgroundPath);
        var absent = GeneratedPromoResolver.Resolve(template, "Channel", [new("Doug", Noon.AddMinutes(-20))],
            TimeZoneInfo.Utc, Noon, new(new("Rugrats", Noon.AddMinutes(-30)), [new("Rugrats", Noon)]))!;
        Assert.Null(GeneratedPromoResolver.Compose(absent).BackgroundPath);
        var withoutContext = GeneratedPromoResolver.Resolve(template, "Channel", [new("Rugrats", Noon.AddMinutes(-20))],
            TimeZoneInfo.Utc, Noon)!;
        Assert.Equal(GeneratedPromoResolver.Compose(matching).BackgroundPath, GeneratedPromoResolver.Compose(withoutContext).BackgroundPath);
    }

    [Fact]
    public void Now_playing_preference_weights_the_current_subject_without_requiring_a_future_start()
    {
        var rule = Template().BackgroundRules![0];
        rule.OnlyWhenMatching = false;
        rule.MatchWeight = 9;
        Assert.Equal(9, GeneratedPromoBackgrounds.Weight(rule, [new("Rugrats", Noon.AddMinutes(-20))], Noon, matchCurrentShow: true));
        Assert.Equal(1, GeneratedPromoBackgrounds.Weight(rule, [new("Doug", Noon.AddMinutes(-20))], Noon, matchCurrentShow: true));
    }

    [Fact]
    public void Other_promo_types_match_actual_upcoming_shows_not_the_promoted_subject()
    {
        var template = Template(fallback: false);
        template.Type = GeneratedPromoType.Eventually;
        UpNextListing[] promoted = [new("Rugrats", Noon.AddDays(3))];
        Assert.Null(GeneratedPromoResolver.Compose(GeneratedPromoResolver.Resolve(template, "Channel", promoted,
            TimeZoneInfo.Utc, Noon, new(null, Upcoming))!).BackgroundPath);
        Assert.Equal("special.png", GeneratedPromoResolver.Compose(GeneratedPromoResolver.Resolve(template, "Channel", promoted,
            TimeZoneInfo.Utc, Noon, new(null, [new("Rugrats", Noon.AddMinutes(1))]))!).BackgroundPath);
    }

    [Fact]
    public void Time_lookahead_fetches_beyond_the_ten_image_subjects()
    {
        var template = Template();
        template.BackgroundRules![0].Within = TimeSpan.FromHours(3);
        var schedule = new ChannelSchedule
        {
            ChannelId = "test", ChannelName = "Test", Start = Noon, End = Noon.AddHours(4),
            Entries = [.. Enumerable.Range(0, 20).Select(index => new ScheduleEntry
            {
                Start = Noon.AddMinutes(10 * index), Duration = TimeSpan.FromMinutes(10), Kind = ScheduleEntryKind.Program,
                Item = TestMedia.Episode(index == 15 ? "Rugrats" : $"Show {index}", 1, 1, TimeSpan.FromMinutes(10))
            })]
        };
        var subjects = GeneratedImageSubjects.FromSchedule(schedule, Noon, template: template);
        Assert.Contains(subjects.Upcoming, show => show.Title == "Rugrats");
        Assert.Equal(5, GeneratedPromoBackgrounds.Weight(template.BackgroundRules[0], subjects.Upcoming, Noon));
        Assert.Equal(10, GeneratedImageSubjects.FromSchedule(schedule, Noon).Upcoming.Count);
    }

    [Fact]
    public async Task Rules_follow_file_renames_removal_and_package_path_mapping()
    {
        var template = Template();
        GeneratedPromoEditing.RenameBackground(template, "special.png", "renamed.png");
        Assert.Equal("renamed.png", template.BackgroundRules![0].Background);
        template.Background = "renamed.png";
        await GeneratedPromoPackages.MapPathsAsync(template, path => Task.FromResult(path is null ? null : "package/" + path));
        Assert.Equal("package/renamed.png", template.BackgroundRules[0].Background);
        Assert.Equal(template.Background, template.BackgroundRules[0].Background);
        GeneratedPromoEditing.RenameBackground(template, template.Background!, null);
        Assert.Null(template.BackgroundRules);
    }

    [Fact]
    public void Rules_round_trip_in_yaml_and_legacy_templates_keep_their_selection()
    {
        var template = Template();
        template.BackgroundRules![0].Within = TimeSpan.FromMinutes(90);
        var config = new SchedulerConfig { Channels = [new() { Id = "test", Name = "Test", GeneratedPromos = new() { Templates = [template] } }] };
        var copy = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config)).Channels[0].GeneratedPromos!.Templates[0];
        var rule = Assert.Single(copy.BackgroundRules!);
        Assert.Equal(TimeSpan.FromMinutes(90), rule.Within);
        Assert.Equal(new[] { "Rugrats", "Powerpuff Girls" }, rule.Shows);
        Assert.True(rule.OnlyWhenMatching);
        template.BackgroundRules = null;
        Assert.DoesNotContain("BackgroundRules", JsonSerializer.Serialize(template));
        var promo = GeneratedPromoResolver.Resolve(template, "Channel", Upcoming, TimeZoneInfo.Utc, Noon)!;
        Assert.Equal(GeneratedPromoResolver.Background(template.BackgroundChoices, promo.Key), GeneratedPromoResolver.Compose(promo).BackgroundPath);
    }
}
