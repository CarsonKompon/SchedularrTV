using System.Text.Json;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class GeneratedPromoVoiceTemplateTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly UpNextListing[] Repeated =
    [new("Rugrats", Noon, "First episode", Runtime: TimeSpan.FromMinutes(20)),
        new("Rugrats", Noon.AddMinutes(30), "Second episode", Runtime: TimeSpan.FromMinutes(20))];

    private static GeneratedPromoTemplate Template(params string[] scripts) => new()
    {
        Name = "Authored", Voice = new() { Voice = "en_us_006", Advanced = new() { Templates = [.. scripts] } },
        Layers = [new() { Kind = GeneratedLayerKind.UpNext, UpNext = new() { ShowsAhead = 1 } }]
    };

    private static ResolvedGeneratedPromo Resolve(GeneratedPromoTemplate template, IReadOnlyList<UpNextListing>? episodes = null) =>
        GeneratedPromoResolver.Resolve(template, "Nick", [Repeated[0]], TimeZoneInfo.Utc, Noon,
             new(null, episodes ?? Repeated))!;

    [Fact]
    public void Voice_uses_the_shared_upcoming_context_instead_of_a_separate_subject_list()
    {
        var template = Template("Now {up_next}, then {up_next_2}.");
        template.Voice!.Advanced = new() { Templates = ["Now {up_next}, then {up_next_2}."] };
        var subjects = new GeneratedImageSubjects(null, [Repeated[0], new("Doug", Noon.AddHours(1))]);
        var promo = GeneratedPromoResolver.Resolve(template, "Nick", [Repeated[0]], TimeZoneInfo.Utc, Noon, subjects)!;
        Assert.Equal("Now Rugrats, then Doug.", promo.Script);
    }

    [Theory]
    [InlineData(GeneratedPromoType.NowPlaying, true, GeneratedUpcomingGrouping.Timeslot)]
    [InlineData(GeneratedPromoType.NowPlaying, false, GeneratedUpcomingGrouping.Timeslot)]
    [InlineData(GeneratedPromoType.NowPlaying, true, GeneratedUpcomingGrouping.Show)]
    [InlineData(GeneratedPromoType.NowPlaying, false, GeneratedUpcomingGrouping.Show)]
    [InlineData(GeneratedPromoType.UpNext, true, GeneratedUpcomingGrouping.Timeslot)]
    [InlineData(GeneratedPromoType.UpNext, false, GeneratedUpcomingGrouping.Timeslot)]
    [InlineData(GeneratedPromoType.UpNext, true, GeneratedUpcomingGrouping.Show)]
    [InlineData(GeneratedPromoType.UpNext, false, GeneratedUpcomingGrouping.Show)]
    public void Text_images_voice_and_background_lookahead_share_slot_grouping(GeneratedPromoType type, bool packed, GeneratedUpcomingGrouping grouping)
    {
        var first = TestMedia.Episode("Regular Show", 1, 1, TimeSpan.FromMinutes(10));
        var schedule = new ChannelSchedule
        {
            ChannelId = "test", ChannelName = "Nick", Start = Noon.AddMinutes(-1), End = Noon.AddHours(1),
            Entries =
            [
                new() { Start = Noon, Duration = TimeSpan.FromMinutes(5), Kind = ScheduleEntryKind.Program, Item = first, SegmentNumber = 1 },
                new() { Start = Noon.AddMinutes(5), Duration = TimeSpan.FromMinutes(1), Kind = ScheduleEntryKind.Commercial },
                new() { Start = Noon.AddMinutes(6), Duration = TimeSpan.FromMinutes(5), Kind = ScheduleEntryKind.Program, Item = first, SegmentNumber = 2 },
                new() { Start = Noon.AddMinutes(11), Duration = TimeSpan.FromMinutes(11), Kind = ScheduleEntryKind.Program,
                    Item = TestMedia.Episode("Regular Show", 1, 2, TimeSpan.FromMinutes(11)), ContinuesSlot = packed },
                new() { Start = Noon.AddMinutes(22), Duration = TimeSpan.FromMinutes(11), Kind = ScheduleEntryKind.Program,
                    Item = TestMedia.Episode("Gumball", 1, 1, TimeSpan.FromMinutes(11)) }
            ]
        };
        var template = new GeneratedPromoTemplate
        {
            Type = type, Name = "Now and then", UpcomingGrouping = grouping,
            Voice = new() { Advanced = new() { Templates = ["Now, {up_next}. Then, {up_next_2}!"] } },
            VariableImages = [new() { Name = "variable_image_01", Shows =
                [new() { Show = "Regular Show", Path = "regular.png" }, new() { Show = "Gumball", Path = "gumball.png" }] }],
            Layers =
            [
                new() { Kind = GeneratedLayerKind.Image, VariableImage = "variable_image_01", VariableImageShow = "next" },
                new() { Kind = GeneratedLayerKind.Image, VariableImage = "variable_image_01", VariableImageShow = "next2" },
                new() { Kind = GeneratedLayerKind.UpNext, UpNext = new() { ShowsAhead = 2 } }
            ]
        };
        var at = Noon.AddSeconds(-1);
        var subjects = GeneratedImageSubjects.FromSchedule(schedule, at, Noon, template);
        bool repeat = !packed && grouping == GeneratedUpcomingGrouping.Timeslot;
        Assert.Equal(repeat ? 3 : 2, subjects.Upcoming.Count);
        Assert.Equal(Noon.AddMinutes(repeat ? 11 : 22), subjects.Upcoming[0].End);
        var promo = GeneratedPromoResolver.Resolve(template, "Nick", subjects.Upcoming, TimeZoneInfo.Utc, at, subjects)!;
        Assert.Equal("regular.png", promo.VariableImages["variable_image_01:next"]);
        Assert.Equal(repeat ? "regular.png" : "gumball.png", promo.VariableImages["variable_image_01:next2"]);
        Assert.Equal(repeat ? "Now, Regular Show. Then, more Regular Show!" : "Now, Regular Show. Then, Gumball!", promo.Script);
        Assert.Equal(new[] { "Regular Show", repeat ? "Regular Show" : "Gumball" }, promo.LayerLines[2]);
        var rule = new GeneratedBackgroundRule { Shows = ["Gumball"], NextShows = 2, OnlyWhenMatching = true };
        Assert.Equal(repeat ? 0 : 5, GeneratedPromoBackgrounds.Weight(rule, subjects.Upcoming, at));
        if (type == GeneratedPromoType.UpNext)
        {
            template.Generator = new() { Shows = 2, Arguments = ["{shows}"], PerShow = ["{title}"] };
            template.Voice!.Advanced = null;
            var generated = GeneratedPromoResolver.Resolve(template, "Nick", subjects.Upcoming, TimeZoneInfo.Utc, at, subjects)!;
            Assert.Equal(new[] { "Regular Show", repeat ? "Regular Show" : "Gumball" }, generated.GeneratorArguments);
            Assert.Contains(repeat ? "Regular Show" : "Gumball", generated.Script!);
        }
    }

    [Fact]
    public void Spoken_titles_omit_parenthesised_years_in_templates_but_keep_caption_titles()
    {
        var template = Template("{show}. {up_next}. {up_next_2:raw}. {up_next_2}!");
        UpNextListing[] shows = [new("Total Drama Island (2023)", Noon), new("Total Drama Island (2023)", Noon.AddMinutes(30))];
        var promo = GeneratedPromoResolver.Resolve(template, "Nick", shows, TimeZoneInfo.Utc, Noon)!;
        Assert.Equal("Total Drama Island. Total Drama Island. Total Drama Island. more Total Drama Island!", promo.Script);
        Assert.Equal(new[] { "Total Drama Island (2023)" }, promo.LayerLines[0]);
        Assert.Equal("Total Drama Island (2023)", shows[0].Title);
    }

    [Theory]
    [InlineData(GeneratedPromoType.UpNext)]
    [InlineData(GeneratedPromoType.NowPlaying)]
    [InlineData(GeneratedPromoType.ComingUp)]
    [InlineData(GeneratedPromoType.Eventually)]
    public void Automatic_and_AI_voice_inputs_use_titles_without_years(GeneratedPromoType type)
    {
        var template = GeneratedPromoTemplates.ForType(type);
        template.Voice!.Writer = new();
        var promo = GeneratedPromoResolver.Resolve(template, "Nick", [new("Total Drama Island (2023)", Noon)], TimeZoneInfo.Utc, Noon)!;
        Assert.Contains("Total Drama Island", promo.Script!);
        Assert.DoesNotContain("2023", promo.Script!);
        Assert.Equal(new[] { "Total Drama Island" }, promo.ScriptJob!.MustSay);
        Assert.DoesNotContain(promo.ScriptJob.Facts, fact => fact.Value.Contains("(2023)"));
    }

    [Theory]
    [InlineData("Total Drama Island (2023)", "Total Drama Island")]
    [InlineData("Doctor Who (1963) (UK)", "Doctor Who (UK)")]
    [InlineData("Show ( 2023 )", "Show")]
    [InlineData("1984", "1984")]
    [InlineData("The 100", "The 100")]
    [InlineData("Show (US)", "Show (US)")]
    [InlineData("Avatar: The Last Airbender", "Avatar The Last Airbender")]
    [InlineData("Spider-Man", "Spider Man")]
    [InlineData("Show - Subtitle (2023)", "Show Subtitle")]
    [InlineData("Show – Subtitle", "Show Subtitle")]
    [InlineData("Show—Subtitle", "Show Subtitle")]
    [InlineData("Spider‑Man", "Spider Man")]
    public void Spoken_title_cleanup_removes_metadata_years_and_pause_inducing_separators(string title, string expected) =>
        Assert.Equal(expected, UpNextPromo.SpokenTitle(title));

    [Fact]
    public void Title_separator_cleanup_preserves_script_punctuation_times_and_captions()
    {
        var template = Template("Now: {up_next} - then {up_next_2:raw} at {up_next_2:start_time}!");
        UpNextListing[] shows = [new("Avatar: The Last Airbender", Noon), new("Spider-Man", Noon.AddMinutes(30))];
        var promo = GeneratedPromoResolver.Resolve(template, "Nick", shows, TimeZoneInfo.Utc, Noon)!;
        Assert.Equal("Now: Avatar The Last Airbender - then Spider Man at 12:30!", promo.Script);
        Assert.Equal(new[] { "Avatar: The Last Airbender" }, promo.LayerLines[0]);
        template.Voice!.Advanced = null;
        template.Voice.Writer = new();
        promo = GeneratedPromoResolver.Resolve(template, "Nick", shows, TimeZoneInfo.Utc, Noon)!;
        Assert.Contains("Avatar The Last Airbender", promo.Script!);
        Assert.Contains("Avatar The Last Airbender", promo.ScriptJob!.MustSay);
        Assert.DoesNotContain("Avatar:", promo.Script!);
        Assert.Equal("Now: Avatar - at 12:30!", UpNextPromo.WithoutTitleYears("Now: Avatar (2023) - at 12:30!"));
    }

    [Fact]
    public void Authored_wording_is_exact_and_repeat_affixes_are_configurable()
    {
        var template = Template("Now, it's {up_next}. Crazy right? Then after that it's {up_next_2}!");
        template.Voice!.PreMessages = ["Automatic opener"];
        template.Voice.PostMessages = ["Automatic closer"];
        template.Voice.Writer = new();
        var promo = Resolve(template);
        Assert.Equal("Now, it's Rugrats. Crazy right? Then after that it's more Rugrats!", promo.Script);
        Assert.Null(promo.ScriptJob);
        Assert.Equal(new[] { "Rugrats" }, promo.LayerLines[0]);
        Assert.Equal(1, GeneratedPromoResolver.ListingsNeeded(template));
        template.Voice.Advanced!.RepeatPrefix = "";
        template.Voice.Advanced.RepeatSuffix = " again";
        Assert.Equal("Now, it's Rugrats. Crazy right? Then after that it's Rugrats again!", Resolve(template).Script);
        Assert.NotEqual(promo.Key, Resolve(template).Key);
    }

    [Fact]
    public void Non_repeats_are_unchanged_and_raw_tokens_bypass_repeat_affixes()
    {
        var template = Template("{up_next}, then {UP_NEXT_2}, or just {up_next_2:raw}.");
        Assert.Equal("Rugrats, then more Rugrats, or just Rugrats.", Resolve(template).Script);
        Assert.Equal("Rugrats, then Doug, or just Doug.", Resolve(template, [Repeated[0], Repeated[1] with { Title = "Doug" }]).Script);
    }

    [Fact]
    public void Openers_and_closers_are_independently_optional_and_never_invented()
    {
        var template = Template("It's {show} on {channel}!");
        var voice = template.Voice!;
        voice.Advanced!.IncludePreMessages = true;
        voice.Advanced.IncludePostMessages = true;
        Assert.Equal("It's Rugrats on Nick!", Resolve(template).Script);
        voice.PreMessages = ["Hello {channel}."];
        voice.PostMessages = ["Next, {up_next_2:raw}."];
        Assert.Equal("Hello Nick. It's Rugrats on Nick! Next, Rugrats.", Resolve(template).Script);
        voice.Advanced.IncludePreMessages = false;
        Assert.Equal("It's Rugrats on Nick! Next, Rugrats.", Resolve(template).Script);
        voice.Advanced.IncludePostMessages = false;
        Assert.Equal("It's Rugrats on Nick!", Resolve(template).Script);
    }

    [Fact]
    public void Missing_episodes_skip_templates_and_no_eligible_template_means_no_voice()
    {
        var template = Template("{up_next} and {up_next_2}", "Just {up_next}.");
        Assert.Equal("Just Rugrats.", Resolve(template, [Repeated[0]]).Script);
        template.Voice!.Advanced!.Templates.RemoveAt(1);
        Assert.Null(Resolve(template, [Repeated[0]]).Script);
        template.Voice.Advanced.Templates = ["Unknown {up_next_11}"];
        Assert.Null(Resolve(template).Script);
    }

    [Fact]
    public void Only_authored_variants_are_used_and_selection_is_repeatable()
    {
        var template = Template("One: {up_next}.", "Two: {up_next}.");
        Assert.Equal(Resolve(template).Script, Resolve(template).Script);
        Assert.Contains(Resolve(template).Script, new[] { "One: Rugrats.", "Two: Rugrats." });
    }

    [Fact]
    public void Subject_and_upcoming_details_are_distinct_and_times_use_the_channel_zone()
    {
        var template = Template("{show}: {episode}, {start_time}–{end_time}. Next {up_next_2:episode} at {up_next_2:start_time}–{up_next_2:end_time}.");
        template.Type = GeneratedPromoType.NowPlaying;
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(3), "Test", "Test");
        var promo = GeneratedPromoResolver.Resolve(template, "Nick", [new("Doug", Noon.AddMinutes(-30), "Current episode", Runtime: TimeSpan.FromMinutes(25))],
            zone, Noon, new(null, Repeated))!;
        Assert.Equal("Doug: Current episode, 2:30–2:55. Next Second episode at 3:30–3:50.", promo.Script);
    }

    [Fact]
    public void Schedule_context_keeps_slots_together_and_can_group_consecutive_slots_by_show()
    {
        var first = TestMedia.Episode("Rugrats", 1, 1, TimeSpan.FromMinutes(20));
        var second = TestMedia.Episode("Rugrats", 1, 2, TimeSpan.FromMinutes(20));
        var third = TestMedia.Episode("Rugrats", 1, 3, TimeSpan.FromMinutes(20));
        var schedule = new ChannelSchedule
        {
            ChannelId = "test", ChannelName = "Nick", Start = Noon, End = Noon.AddHours(2),
            Entries =
            [
                new() { Start = Noon, Duration = TimeSpan.FromSeconds(30), Kind = ScheduleEntryKind.Program, Item = first, IsSlotIntro = true },
                new() { Start = Noon.AddMinutes(1), Duration = TimeSpan.FromMinutes(10), Kind = ScheduleEntryKind.Program, Item = first, SegmentNumber = 1 },
                new() { Start = Noon.AddMinutes(11), Duration = TimeSpan.FromMinutes(5), Kind = ScheduleEntryKind.Commercial },
                new() { Start = Noon.AddMinutes(16), Duration = TimeSpan.FromMinutes(10), Kind = ScheduleEntryKind.Program, Item = first, SegmentNumber = 2 },
                new() { Start = Noon.AddMinutes(26), Duration = TimeSpan.FromMinutes(20), Kind = ScheduleEntryKind.Program, Item = second, ContinuesSlot = true },
                new() { Start = Noon.AddMinutes(46), Duration = TimeSpan.FromMinutes(20), Kind = ScheduleEntryKind.Program, Item = third }
            ]
        };
        var template = Template("{up_next}, {up_next_2}.");
        var subjects = GeneratedImageSubjects.FromSchedule(schedule, Noon, template: template);
        Assert.Equal(2, subjects.Upcoming.Count);
        Assert.Equal(Noon.AddMinutes(46), subjects.Upcoming[0].End);
        Assert.Equal(Noon.AddMinutes(66), subjects.Upcoming[1].End);
        var promo = GeneratedPromoResolver.Resolve(template, "Nick", subjects.Upcoming, TimeZoneInfo.Utc, Noon, subjects)!;
        Assert.Equal("Rugrats, more Rugrats.", promo.Script);
        Assert.Equal(Noon.AddMinutes(46), Assert.Single(UpNextPromo.Listings(schedule, Noon.AddMinutes(30), 1)).Start);
        template.UpcomingGrouping = GeneratedUpcomingGrouping.Show;
        subjects = GeneratedImageSubjects.FromSchedule(schedule, Noon, template: template);
        Assert.Single(subjects.Upcoming);
        Assert.Equal(Noon.AddMinutes(66), subjects.Upcoming[0].End);
    }

    [Fact]
    public void Shared_grouping_is_serialized_and_part_of_the_render_identity()
    {
        var template = Template("{up_next}");
        var original = Resolve(template);
        template.UpcomingGrouping = GeneratedUpcomingGrouping.Show;
        Assert.NotEqual(original.Key, Resolve(template).Key);
        var config = new SchedulerConfig { Channels = [new() { Id = "test", Name = "Nick", GeneratedPromos = new() { Templates = [template] } }] };
        var copy = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config)).Channels[0].GeneratedPromos!.Templates[0];
        Assert.Equal(GeneratedUpcomingGrouping.Show, copy.UpcomingGrouping);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void Legacy_voice_only_grouping_does_not_override_the_shared_timeslot_default(string oldValue)
    {
        string yaml = $$"""
            channels:
            - id: test
              name: Nick
              generated_promos:
                templates:
                - name: Legacy
                  voice:
                    advanced:
                      use_individual_episodes: {{oldValue}}
                      templates: ['Now {up_next}, then {up_next_2}.']
            """;
        var config = SchedulerConfigLoader.Parse(yaml);
        Assert.Equal(GeneratedUpcomingGrouping.Timeslot, config.Channels[0].GeneratedPromos!.Templates[0].UpcomingGrouping);
        Assert.DoesNotContain("use_individual_episodes", SchedulerConfigLoader.Serialize(config));
    }

    [Fact]
    public void Settings_round_trip_and_unknown_tokens_are_validated()
    {
        var template = Template("{up_next} then {up_next_2}!");
        template.Voice!.Advanced!.RepeatPrefix = "even more ";
        template.Voice.Advanced.RepeatSuffix = " again";
        template.Voice.Advanced.IncludePostMessages = true;
        var config = new SchedulerConfig { Channels = [new() { Id = "test", Name = "Nick", GeneratedPromos = new() { Templates = [template] } }] };
        var copy = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config)).Channels[0].GeneratedPromos!.Templates[0];
        Assert.Equal(template.Voice.Advanced.RepeatPrefix, copy.Voice!.Advanced!.RepeatPrefix);
        Assert.Equal(template.Voice.Advanced.RepeatSuffix, copy.Voice.Advanced.RepeatSuffix);
        Assert.True(copy.Voice.Advanced.IncludePostMessages);
        Assert.Equal(GeneratedUpcomingGrouping.Timeslot, copy.UpcomingGrouping);
        Assert.Equal(Resolve(template).Script, Resolve(copy).Script);
        template.Voice.Advanced.Templates = ["{up_next_11}"];
        Assert.Contains(SchedulerConfigLoader.Validate(config).Errors, error => error.Contains("unknown token '{up_next_11}'"));
        template.Voice.Advanced = null;
        Assert.DoesNotContain("Advanced", JsonSerializer.Serialize(template));
    }
}
