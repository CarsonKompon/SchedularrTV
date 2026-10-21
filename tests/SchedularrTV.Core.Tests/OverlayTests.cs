using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Overlays;
using SchedularrTV.Plugins.Playout;
using SchedularrTV.Providers.ErsatzTv;
using SchedularrTV.Service.Writing;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class OverlayTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "overlay-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.Delete(_folder);

    private static readonly DateTimeOffset Eight = new(2026, 10, 3, 20, 0, 0, TimeSpan.Zero);

    /// <summary>A 22-minute episode, or the part of it from <paramref name="inPoint"/> lasting <paramref name="part"/> (split by a break).</summary>
    private static ScheduleEntry Episode(TimeSpan? inPoint = null, TimeSpan? part = null, string? block = null, ScheduleEntryKind kind = ScheduleEntryKind.Program,
        string title = "Episode", DateTimeOffset? start = null) => new()
    {
        Start = start ?? Eight,
        Duration = part ?? TimeSpan.FromMinutes(22),
        InPoint = inPoint ?? TimeSpan.Zero,
        Kind = kind,
        BlockName = block,
        Item = TestMedia.Episode("Toons", 1, 2, TimeSpan.FromMinutes(22), title: title)
    };

    // --- Timing ----------------------------------------------------------------------------------

    [Fact]
    public void All_the_way_through_without_a_fade_has_no_timing_and_with_one_fades_at_each_end()
    {
        Assert.Null(OverlayTiming.For(new Overlay { When = OverlayWhen.Always, FadeSeconds = 0 }, Episode())!.Timing);

        GraphicsTiming timing = OverlayTiming.For(new Overlay { When = OverlayWhen.Always, FadeSeconds = 1 }, Episode())!.Timing!;
        Assert.Equal((false, 0L, 1000L), (timing.Wall, timing.PhaseMs, timing.FadeMs));
        Assert.Equal(22 * 60_000L, timing.VisibleMs);
        Assert.True(timing.FrequencyMs > 22 * 60_000L);
    }

    [Fact]
    public void Near_the_start_shows_once_that_far_in()
    {
        GraphicsTiming timing = OverlayTiming.For(new Overlay { When = OverlayWhen.Start, OffsetSeconds = 10, DurationSeconds = 8, FadeSeconds = 1 }, Episode())!.Timing!;

        Assert.Equal(10_000, timing.PhaseMs);
        Assert.Equal(8_000, timing.VisibleMs);
        Assert.Equal(1_000, timing.FadeMs);
        Assert.Equal(6_000, timing.HoldMs);
    }

    [Fact]
    public void Near_the_end_lands_on_the_last_part_of_a_programme_split_by_a_break()
    {
        var overlay = new Overlay { When = OverlayWhen.End, OffsetSeconds = 30, DurationSeconds = 10, FadeSeconds = 0.5 };

        // 0–11 minutes, then 11–22 after a break.
        Assert.Null(OverlayTiming.For(overlay, Episode(TimeSpan.Zero, TimeSpan.FromMinutes(11))));
        GraphicsTiming timing = OverlayTiming.For(overlay, Episode(TimeSpan.FromMinutes(11), TimeSpan.FromMinutes(11)))!.Timing!;

        // Ends 30 seconds before 22:00, so it starts at 21:20: 10:20 into the second part.
        Assert.Equal((10 * 60 + 20) * 1000L, timing.PhaseMs);
        Assert.Equal(10_000, timing.VisibleMs);
    }

    [Fact]
    public void Halfway_through_lands_where_half_the_programme_is()
    {
        var overlay = new Overlay { When = OverlayWhen.Percent, Percent = 50, DurationSeconds = 6, FadeSeconds = 0.5 };

        GraphicsTiming timing = OverlayTiming.For(overlay, Episode(TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(14)))!.Timing!;

        Assert.Equal(3 * 60_000L, timing.PhaseMs);
    }

    [Fact]
    public void An_appearance_cut_by_a_break_shows_for_what_is_left_and_one_too_short_is_left_out()
    {
        var overlay = new Overlay { When = OverlayWhen.Start, OffsetSeconds = 0, DurationSeconds = 20, FadeSeconds = 2 };

        GraphicsTiming cut = OverlayTiming.For(overlay, Episode(TimeSpan.Zero, TimeSpan.FromSeconds(12)))!.Timing!;
        Assert.Equal(12_000, cut.VisibleMs);
        Assert.True(cut.FadeMs <= cut.HoldMs);

        Assert.Null(OverlayTiming.For(overlay, Episode(TimeSpan.Zero, TimeSpan.FromMilliseconds(500))));
    }

    [Fact]
    public void Every_few_minutes_stays_in_step_across_a_break_and_by_the_clock_uses_the_wall_clock()
    {
        var every = new Overlay { When = OverlayWhen.Every, EveryMinutes = 5, OffsetSeconds = 60, DurationSeconds = 6 };

        GraphicsTiming first = OverlayTiming.For(every, Episode(TimeSpan.Zero, TimeSpan.FromMinutes(11)))!.Timing!;
        GraphicsTiming second = OverlayTiming.For(every, Episode(TimeSpan.FromMinutes(11), TimeSpan.FromMinutes(11)))!.Timing!;
        Assert.Equal((300_000L, 60_000L), (first.FrequencyMs, first.PhaseMs));

        // Programme times 1, 6, 11, 16, 21 minutes: 16 is 5 minutes into the second part, so it's at 0 and 5 there.
        Assert.Equal(0, second.PhaseMs);

        GraphicsTiming clock = OverlayTiming.For(new Overlay { When = OverlayWhen.Clock, EveryMinutes = 30, OffsetSeconds = 0, DurationSeconds = 10 }, Episode())!.Timing!;
        Assert.True(clock.Wall);
        Assert.Equal(1_800_000, clock.FrequencyMs);
        Assert.Equal("wall", (string?)clock.ToJson()["clock"]);
    }

    [Fact]
    public void Programmes_only_leaves_breaks_and_title_sequences_alone()
    {
        var overlay = new Overlay();

        Assert.NotNull(OverlayTiming.For(overlay, Episode()));
        Assert.Null(OverlayTiming.For(overlay, Episode(kind: ScheduleEntryKind.Commercial)));
        Assert.Null(OverlayTiming.For(overlay, Episode() with { IsSlotIntro = true }));
        Assert.NotNull(OverlayTiming.For(new Overlay { AppliesTo = OverlayAppliesTo.Everything }, Episode(kind: ScheduleEntryKind.Commercial)));
        Assert.Null(OverlayTiming.For(new Overlay { AppliesTo = OverlayAppliesTo.Everything }, Episode(kind: ScheduleEntryKind.Padding)));
    }

    // --- Channels, blocks and links ---------------------------------------------------------------

    [Fact]
    public void Multiple_slots_keep_their_own_lengths_and_land_on_the_right_programme_part()
    {
        var overlay = new Overlay
        {
            Schedule = new OverlaySchedule
            {
                Slots =
                [
                    new OverlaySlot { OffsetSeconds = 10, DurationSeconds = 8 },
                    new OverlaySlot { When = OverlayWhen.Percent, Percent = 50, DurationSeconds = 12 },
                    new OverlaySlot { When = OverlayWhen.End, OffsetSeconds = 30, DurationSeconds = 20 }
                ]
            }
        };
        Overlay[] rules = OverlayTiming.Rules(overlay).ToArray();
        ScheduleEntry first = Episode(TimeSpan.Zero, TimeSpan.FromMinutes(11));
        ScheduleEntry second = Episode(TimeSpan.FromMinutes(11), TimeSpan.FromMinutes(11));
        Assert.Equal(8_000, OverlayTiming.For(rules[0], first)!.Timing!.VisibleMs);
        Assert.Null(OverlayTiming.For(rules[0], second));
        Assert.Null(OverlayTiming.For(rules[1], first));
        Assert.Equal((0L, 12_000L), (OverlayTiming.For(rules[1], second)!.Timing!.PhaseMs, OverlayTiming.For(rules[1], second)!.Timing!.VisibleMs));
        Assert.Null(OverlayTiming.For(rules[2], first));
        GraphicsTiming end = OverlayTiming.For(rules[2], second)!.Timing!;
        Assert.Equal((610_000L, 20_000L), (end.PhaseMs, end.VisibleMs));
        Assert.Equal(3, OverlayDescriptions.Sample(overlay, TimeSpan.FromMinutes(22)).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_and_automatic_slots_publish_together(bool byClock)
    {
        (SchedulerConfig config, ChannelConfig cartoons, _) = Channels();
        cartoons.Plugins = Section(new OverlaysSection
        {
            Overlays = [new Overlay
            {
                Kind = OverlayKinds.File, File = "logo.png", FadeSeconds = 0,
                Schedule = new OverlaySchedule
                {
                    Slots = [new OverlaySlot { OffsetSeconds = 10, DurationSeconds = 8 }, new OverlaySlot { OffsetSeconds = 60, DurationSeconds = 15 }],
                    Automatic = new OverlayAutomatic { Enabled = true, ByClock = byClock, EveryMinutes = 5, DurationSeconds = 6 }
                }
            }]
        });
        var schedule = new ChannelSchedule
        {
            ChannelId = "cartoons", ChannelName = "Cartoons", ChannelNumber = 1, Start = Eight, End = Eight.AddMinutes(22), Entries = [Episode()]
        };
        ChannelSchedule processed = await Processor().ProcessAsync(new PlayoutContext(config, cartoons, schedule), CancellationToken.None);
        JsonArray layers = (JsonArray)JsonNode.Parse(processed.Entries[0].Annotations![PlayoutPublisher.GraphicsAnnotation])!;
        Assert.Equal(3, layers.Count);
        Assert.Equal(10_000, (long)layers[0]!["timing"]!["phase_offset_ms"]!);
        Assert.Equal(8_000, (long)layers[0]!["timing"]!["hold_ms"]!);
        Assert.Equal(60_000, (long)layers[1]!["timing"]!["phase_offset_ms"]!);
        Assert.Equal(15_000, (long)layers[1]!["timing"]!["hold_ms"]!);
        Assert.Equal(byClock ? "wall" : "content", (string?)layers[2]!["timing"]!["clock"]);
        Assert.Equal(300_000, (long)layers[2]!["timing"]!["frequency_ms"]!);
        Assert.Equal(6_000, (long)layers[2]!["timing"]!["hold_ms"]!);
    }

    [Fact]
    public void Migrating_each_legacy_timing_preserves_its_appearances()
    {
        foreach (string when in OverlayWhen.All)
        {
            var overlay = new Overlay { When = when, OffsetSeconds = 30, Percent = 70, DurationSeconds = 12, EveryMinutes = 5 };
            var expected = OverlayDescriptions.Sample(overlay, TimeSpan.FromMinutes(22));
            overlay.Schedule = OverlaySchedule.FromLegacy(overlay);
            Assert.Equal(expected, OverlayDescriptions.Sample(overlay, TimeSpan.FromMinutes(22)));
        }
    }

    [Fact]
    public void Empty_schedules_show_nothing_and_always_visible_overrides_saved_slots()
    {
        var overlay = new Overlay { Schedule = new OverlaySchedule() };
        Assert.Empty(OverlayTiming.Rules(overlay));
        Assert.Empty(OverlayDescriptions.Sample(overlay, TimeSpan.FromMinutes(22)));
        overlay.Schedule.Slots.Add(new OverlaySlot());
        overlay.Schedule.Automatic.Enabled = true;
        overlay.Schedule.Always = true;
        Assert.Equal(OverlayWhen.Always, Assert.Single(OverlayTiming.Rules(overlay)).When);
        Assert.Equal((TimeSpan.Zero, TimeSpan.FromMinutes(22)), Assert.Single(OverlayDescriptions.Sample(overlay, TimeSpan.FromMinutes(22))));
    }

    [Fact]
    public void Slot_lengths_control_layer_windows_and_copies_are_independent()
    {
        var overlay = new Overlay
        {
            Schedule = new OverlaySchedule
            {
                Slots = [new OverlaySlot { DurationSeconds = 8 }, new OverlaySlot { OffsetSeconds = 60, DurationSeconds = 20 }]
            }
        };
        var timings = OverlayTiming.Rules(overlay).Select(rule => OverlayTiming.For(rule, Episode(), new LayerTiming(2, 1))!.Timing!).ToArray();
        Assert.Equal((2_000L, 5_000L), (timings[0].PhaseMs, timings[0].VisibleMs));
        Assert.Equal((62_000L, 17_000L), (timings[1].PhaseMs, timings[1].VisibleMs));
        Overlay copy = overlay.Copy();
        copy.Schedule!.Slots[0].DurationSeconds = 40;
        copy.Schedule.Automatic.Enabled = true;
        Assert.Equal(8, overlay.Schedule.Slots[0].DurationSeconds);
        Assert.False(overlay.Schedule.Automatic.Enabled);
    }

    [Fact]
    public void Multiple_slot_settings_round_trip_through_yaml()
    {
        var overlay = new Overlay
        {
            Schedule = new OverlaySchedule
            {
                Slots = [new OverlaySlot { OffsetSeconds = 15, DurationSeconds = 10 }, new OverlaySlot { When = OverlayWhen.Percent, Percent = 75, DurationSeconds = 20 }],
                Automatic = new OverlayAutomatic { Enabled = true, ByClock = true, EveryMinutes = 15, DurationSeconds = 5 }
            }
        };
        var serializer = new YamlDotNet.Serialization.SerializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance).Build();
        var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance).Build();
        Overlay restored = deserializer.Deserialize<Overlay>(serializer.Serialize(overlay));
        Assert.Equal(OverlayDescriptions.Sample(overlay, TimeSpan.FromMinutes(22)), OverlayDescriptions.Sample(restored, TimeSpan.FromMinutes(22)));
        Assert.Equal(2, restored.Schedule!.Slots.Count);
        Assert.True(restored.Schedule.Automatic.ByClock);
    }

    private static Dictionary<string, object?> Section(OverlaysSection section) => new(StringComparer.OrdinalIgnoreCase) { ["overlays"] = section };

    private static (SchedulerConfig Config, ChannelConfig Cartoons, ChannelConfig AdultSwim) Channels()
    {
        var adultSwim = new ChannelConfig
        {
            Id = "adult-swim", Name = "Adult Swim", Number = 2,
            Plugins = Section(new OverlaysSection { Overlays = [new Overlay { Name = "[as] bug" }] })
        };
        var cartoons = new ChannelConfig
        {
            Id = "cartoons", Name = "Cartoons", Number = 1,
            Plugins = Section(new OverlaysSection { Overlays = [new Overlay { Name = "CN bug" }] }),
            Blocks =
            [
                new BlockConfig { Name = "Adult Swim", Plugins = Section(new OverlaysSection { From = "adult-swim" }) },
                new BlockConfig { Name = "Late", Plugins = Section(new OverlaysSection { Enabled = false }) },
                new BlockConfig { Name = "Daytime" }
            ]
        };
        return (new SchedulerConfig { Channels = [cartoons, adultSwim] }, cartoons, adultSwim);
    }

    [Fact]
    public void A_block_shows_the_channels_overlays_its_own_another_channels_or_none()
    {
        (SchedulerConfig config, ChannelConfig cartoons, _) = Channels();

        Assert.Equal("CN bug", Assert.Single(OverlayResolver.Resolve(config, cartoons, cartoons.Blocks[2])!.Overlays).Name);
        Assert.Null(OverlayResolver.Resolve(config, cartoons, cartoons.Blocks[1]));

        ResolvedOverlays linked = OverlayResolver.Resolve(config, cartoons, cartoons.Blocks[0])!;
        Assert.Equal("[as] bug", Assert.Single(linked.Overlays).Name);
        Assert.Equal("Adult Swim", linked.Owner.Name);
    }

    [Fact]
    public void A_linked_block_follows_later_changes_to_the_channel_it_matches()
    {
        (SchedulerConfig config, ChannelConfig cartoons, ChannelConfig adultSwim) = Channels();

        adultSwim.Plugins = Section(new OverlaysSection { Overlays = [new Overlay { Name = "[as] bug" }, new Overlay { Name = "[as] up next" }] });

        Assert.Equal(["[as] bug", "[as] up next"], OverlayResolver.Resolve(config, cartoons, cartoons.Blocks[0])!.Overlays.Select(overlay => overlay.Name));
    }

    [Fact]
    public void Links_round_in_a_loop_or_to_a_missing_channel_show_nothing_and_say_why()
    {
        (SchedulerConfig config, ChannelConfig cartoons, ChannelConfig adultSwim) = Channels();
        adultSwim.Plugins = Section(new OverlaysSection { From = "cartoons" });
        cartoons.Plugins = Section(new OverlaysSection { From = "adult-swim" });

        Assert.Null(OverlayResolver.Resolve(config, cartoons, null));
        Assert.Contains("loop", OverlayResolver.LinkProblem(config, cartoons, OverlayResolver.Read(cartoons.Plugins)));
        Assert.Contains("no channel", OverlayResolver.LinkProblem(config, cartoons, new OverlaysSection { From = "gone" }));
    }

    // --- Publishing ------------------------------------------------------------------------------

    private OverlayProcessor Processor() => new(new OverlayRenderer(_folder), NullLogger<OverlayProcessor>.Instance);

    [Fact]
    public async Task Overlays_reach_the_playout_as_graphics_layers()
    {
        (SchedulerConfig config, ChannelConfig cartoons, _) = Channels();
        string logo = Path.Combine(_folder, "logo.png");
        cartoons.Plugins = Section(new OverlaysSection
        {
            Overlays =
            [
                new Overlay { Name = "Bug", Kind = OverlayKinds.File, File = logo, Anchor = "top_right", SizePercent = 8, Opacity = 70, FadeSeconds = 0 },
                new Overlay
                {
                    Name = "Lower third", When = OverlayWhen.Start, OffsetSeconds = 10, DurationSeconds = 8,
                    Layers = [new OverlayLayer { Kind = OverlayLayerKinds.Text, Text = "{title}: next {next}", X = 100, Y = 900, Width = 900, Height = 80 }]
                }
            ]
        });
        var schedule = new ChannelSchedule
        {
            ChannelId = "cartoons", ChannelName = "Cartoons", ChannelNumber = 1, Start = Eight, End = Eight.AddMinutes(44),
            Entries = [Episode(block: "Daytime"), Episode(block: "Daytime", title: "Later", start: Eight.AddMinutes(22)) with { Item = TestMedia.Episode("Rugrats", 1, 1, TimeSpan.FromMinutes(22)) }]
        };

        ChannelSchedule processed = await Processor().ProcessAsync(new PlayoutContext(config, cartoons, schedule), CancellationToken.None);
        JsonArray layers = (JsonArray)JsonNode.Parse(processed.Entries[0].Annotations![PlayoutPublisher.GraphicsAnnotation])!;

        Assert.Equal(2, layers.Count);
        Assert.Equal(logo, (string?)layers[0]!["source"]!["path"]);
        Assert.Equal("top_right", (string?)layers[0]!["location"]);
        Assert.Equal(70, (double)layers[0]!["opacity_percent"]!);
        Assert.Null(layers[0]!["timing"]);

        string drawn = (string)layers[1]!["source"]!["path"]!;
        Assert.True(File.Exists(drawn));
        Assert.Equal(100, (int)layers[1]!["width_percent"]!);
        Assert.Equal(10_000, (long)layers[1]!["timing"]!["phase_offset_ms"]!);

        // Through to ErsatzTV's playout.
        string json = JsonSerializer.Serialize(PlayoutPublisher.Build(processed, new ErsatzTvOptions()), PlayoutPublisher.JsonOptions);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement graphics = document.RootElement.GetProperty("items")[0].GetProperty("graphics");
        Assert.Equal(2, graphics.GetArrayLength());
        Assert.Equal("periodic", graphics[1].GetProperty("timing").GetProperty("timing_type").GetString());
    }

    [Fact]
    public async Task A_file_shows_the_first_picture_whose_conditions_hold_else_its_own()
    {
        (SchedulerConfig config, ChannelConfig cartoons, _) = Channels();
        cartoons.Plugins = Section(new OverlaysSection
        {
            Overlays =
            [
                new Overlay
                {
                    Name = "Bug", Kind = OverlayKinds.File, File = "default.png", FadeSeconds = 0,
                    Variants =
                    [
                        new OverlayVariant { Name = "Kids", File = "kids.png", Ratings = ["TVY"] },
                        new OverlayVariant { Name = "Off", File = "never.png", Ratings = ["TV-Y"], Enabled = false },
                        new OverlayVariant { Name = "Spooky nights", File = "pumpkin.png", Holidays = ["Halloween"], TimeFrom = TimeSpan.FromHours(19) },
                        new OverlayVariant { Name = "Mondays", File = "monday.png", Days = [DayOfWeek.Monday] }
                    ]
                },
                // Only its variants: nothing on a programme none of them suits.
                new Overlay { Name = "Rating", Kind = OverlayKinds.File, FadeSeconds = 0, Variants = [new OverlayVariant { File = "tv-y.png", Ratings = ["TV-Y"] }] }
            ]
        });
        MediaItem kids = new() { Id = "kids", Title = "Kids", Kind = MediaKind.Episode, Duration = TimeSpan.FromMinutes(22), SeriesName = "Kids", Rating = "TV-Y" };
        var schedule = new ChannelSchedule
        {
            ChannelId = "cartoons", ChannelName = "Cartoons", ChannelNumber = 1, Start = Eight, End = Eight.AddDays(2),
            Entries =
            [
                Episode() with { Item = kids },                      // Saturday 3 October, 8pm: rated TV-Y
                Episode(start: Eight.AddMinutes(22)),                // unrated, in Halloween, after 7pm
                Episode(start: Eight.AddDays(2).AddHours(-10))       // Monday 5 October, 10am
            ]
        };

        ChannelSchedule processed = await Processor().ProcessAsync(new PlayoutContext(config, cartoons, schedule), CancellationToken.None);
        string[] Files(int index) =>
            [.. ((JsonArray)JsonNode.Parse(processed.Entries[index].Annotations![PlayoutPublisher.GraphicsAnnotation])!).Select(layer => (string)layer!["source"]!["path"]!)];

        Assert.Equal(new[] { "kids.png", "tv-y.png" }, Files(0));
        Assert.Equal(new[] { "pumpkin.png" }, Files(1));
        Assert.Equal(new[] { "monday.png" }, Files(2));
    }

    [Fact]
    public void Variant_dates_and_times_can_wrap_and_queries_and_seasons_are_checked()
    {
        OverlayMoment At(DateTimeOffset when) => OverlayMoment.At(when, new SchedulerConfig(), new ChannelConfig { Id = "c", Name = "C" });
        DateTimeOffset newYearsEve = new(2026, 12, 31, 23, 30, 0, TimeSpan.Zero);

        Assert.True(OverlayVariants.Matches(new OverlayVariant { DateFrom = "12-20", DateTo = "01-05" }, null, At(newYearsEve)));
        Assert.False(OverlayVariants.Matches(new OverlayVariant { DateFrom = "12-20", DateTo = "01-05" }, null, At(newYearsEve.AddDays(-15))));
        Assert.True(OverlayVariants.Matches(new OverlayVariant { TimeFrom = TimeSpan.FromHours(22), TimeTo = TimeSpan.FromHours(6) }, null, At(newYearsEve)));
        Assert.False(OverlayVariants.Matches(new OverlayVariant { TimeFrom = TimeSpan.FromHours(22), TimeTo = TimeSpan.FromHours(6) }, null, At(newYearsEve.AddHours(-12))));
        Assert.True(OverlayVariants.Matches(new OverlayVariant { Seasons = ["winter"], Months = [12], Holidays = ["New Year"] }, null, At(newYearsEve)));
        Assert.False(OverlayVariants.Matches(new OverlayVariant { Seasons = ["Summer"] }, null, At(newYearsEve)));

        MediaItem rugrats = TestMedia.Episode("Rugrats", 1, 1, TimeSpan.FromMinutes(22));
        Assert.True(OverlayVariants.Matches(new OverlayVariant { Query = "show:'Rugrats'" }, rugrats, At(newYearsEve)));
        Assert.False(OverlayVariants.Matches(new OverlayVariant { Query = "show:'Doug'" }, rugrats, At(newYearsEve)));
        Assert.False(OverlayVariants.Matches(new OverlayVariant { Query = "show:(" }, rugrats, At(newYearsEve)));
        Assert.False(OverlayVariants.Matches(new OverlayVariant { Ratings = ["TV-Y"] }, rugrats, At(newYearsEve)));
    }

    [Fact]
    public async Task Text_is_filled_in_per_programme_so_each_show_gets_its_own_drawing()
    {
        (SchedulerConfig config, ChannelConfig cartoons, _) = Channels();
        cartoons.Plugins = Section(new OverlaysSection
        {
            Overlays = [new Overlay { Layers = [new OverlayLayer { Kind = OverlayLayerKinds.Text, Text = "{title}" }] }]
        });
        var schedule = new ChannelSchedule
        {
            ChannelId = "cartoons", ChannelName = "Cartoons", ChannelNumber = 1, Start = Eight, End = Eight.AddMinutes(66),
            Entries =
            [
                Episode(),
                Episode(start: Eight.AddMinutes(22)) with { Item = TestMedia.Episode("Rugrats", 1, 1, TimeSpan.FromMinutes(22)) },
                Episode(start: Eight.AddMinutes(44))
            ]
        };

        ChannelSchedule processed = await Processor().ProcessAsync(new PlayoutContext(config, cartoons, schedule), CancellationToken.None);
        string Drawn(int index) => (string)JsonNode.Parse(processed.Entries[index].Annotations![PlayoutPublisher.GraphicsAnnotation])![0]!["source"]!["path"]!;

        Assert.NotEqual(Drawn(0), Drawn(1));
        Assert.Equal(Drawn(0), Drawn(2));
    }

    [Fact]
    public void Tokens_read_as_the_show_the_episode_the_channel_and_whats_next()
    {
        var owner = new ChannelConfig { Id = "adult-swim", Name = "Adult Swim" };
        Dictionary<string, string> tokens = OverlayProcessor.Tokens(Episode(title: "Pilot"), Episode() with { Item = TestMedia.Episode("Rugrats", 1, 1, TimeSpan.FromMinutes(22)) }, owner);

        Assert.Equal("Toons", tokens["title"]);
        Assert.Equal("Pilot", tokens["episode"]);
        Assert.Equal("S01E02", tokens["season_episode"]);
        Assert.Equal("Adult Swim", tokens["channel"]);
        Assert.Equal("Rugrats", tokens["next"]);
        Assert.Equal("Up next: Rugrats", OverlayRenderer.Fill("Up next: {next}{unknown}", tokens).Replace("{unknown}", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_overlay_is_placed_where_ErsatzTV_anchors_it()
    {
        var overlay = new Overlay { Kind = OverlayKinds.File, Anchor = "bottom_right", SizePercent = 10, MarginXPercent = 5, MarginYPercent = 10 };

        SkiaSharp.SKRect box = OverlayRenderer.FileBox(overlay, 1000, 500, aspect: 0.5f);

        Assert.Equal((850f, 400f, 100f, 50f), (box.Left, box.Top, box.Width, box.Height));
    }

    [Fact]
    public async Task A_file_overlay_set_to_fill_the_screen_covers_the_whole_frame()
    {
        var overlay = new Overlay
        {
            Name = "Full-frame bug", Kind = OverlayKinds.File, File = "bug-1080p.mov", FillScreen = true, FadeSeconds = 0,
            Anchor = "bottom_right", SizePercent = 10, MarginXPercent = 5, MarginYPercent = 10, InsidePicture = true
        };

        SkiaSharp.SKRect box = OverlayRenderer.FileBox(overlay, 1920, 1080, aspect: 9f / 16f);
        Assert.Equal((0f, 0f, 1920f, 1080f), (box.Left, box.Top, box.Width, box.Height));

        (SchedulerConfig config, ChannelConfig cartoons, _) = Channels();
        cartoons.Plugins = Section(new OverlaysSection { Overlays = [overlay] });
        var schedule = new ChannelSchedule
        {
            ChannelId = "cartoons", ChannelName = "Cartoons", ChannelNumber = 1, Start = Eight, End = Eight.AddMinutes(22),
            Entries = [Episode()]
        };

        ChannelSchedule processed = await Processor().ProcessAsync(new PlayoutContext(config, cartoons, schedule), CancellationToken.None);
        JsonNode layer = ((JsonArray)JsonNode.Parse(processed.Entries[0].Annotations![PlayoutPublisher.GraphicsAnnotation])!)[0]!;

        Assert.Equal("top_left", (string?)layer["location"]);
        Assert.Equal(100, (double)layer["width_percent"]!);
        Assert.Equal(0, (double)layer["horizontal_margin_percent"]!);
        Assert.Equal(0, (double)layer["vertical_margin_percent"]!);
        Assert.False((bool)layer["within_source_content"]!);
    }

    [Fact]
    public void A_video_played_once_runs_for_its_length_then_its_last_frame_holds_for_the_rest()
    {
        // An 8-second appearance 10 seconds in, fading for a second, of a 3-second video.
        var appearance = new OverlayShowing(new GraphicsTiming(Wall: false, FrequencyMs: 23 * 60_000, PhaseMs: 10_000, FadeMs: 1000, HoldMs: 6000));
        (OverlayShowing video, OverlayShowing? still) = OverlayTiming.PlayOnce(appearance, 22 * 60_000, 3000);

        Assert.Equal((10_000L, 1000L, 1000L), (video.Timing!.PhaseMs, video.Timing.FadeMs, video.Timing.HoldMs));
        // Fully in as the video starts fading out (at 12s), gone when the appearance ends (at 18s).
        Assert.Equal((11_000L, 1000L, 5000L), (still!.Timing!.PhaseMs, still.Timing.FadeMs, still.Timing.HoldMs));
        Assert.Equal(18_000L, still.Timing.PhaseMs + still.Timing.VisibleMs);

        // All the way through, unfaded: the video, then the still for the rest of the part.
        (video, still) = OverlayTiming.PlayOnce(new OverlayShowing(null), 60_000, 4000);
        Assert.Equal((0L, 0L, 4000L), (video.Timing!.PhaseMs, video.Timing.FadeMs, video.Timing.HoldMs));
        Assert.Equal((4000L, 0L, 56_000L), (still!.Timing!.PhaseMs, still.Timing.FadeMs, still.Timing.HoldMs));

        // A video that outlasts the appearance is all there is.
        (video, still) = OverlayTiming.PlayOnce(appearance, 22 * 60_000, 9000);
        Assert.Same(appearance, video);
        Assert.Null(still);
    }

    [Fact]
    public async Task A_file_overlay_that_plays_once_airs_as_its_video_then_its_last_frame()
    {
        Directory.CreateDirectory(_folder);
        string bug = Path.Combine(_folder, "bug.webm");
        await File.WriteAllBytesAsync(bug, [0]);
        string key = OverlayAnimations.HeldKey(bug);
        string last = Path.Combine(_folder, key + ".png");
        await File.WriteAllBytesAsync(last, [0]);
        await File.WriteAllTextAsync(Path.Combine(_folder, key + ".seconds"), "5");

        (SchedulerConfig config, ChannelConfig cartoons, _) = Channels();
        cartoons.Plugins = Section(new OverlaysSection
        {
            Overlays = [new Overlay { Name = "Bug", Kind = OverlayKinds.File, File = bug, Loop = false, FadeSeconds = 0 }]
        });
        var schedule = new ChannelSchedule
        {
            ChannelId = "cartoons", ChannelName = "Cartoons", ChannelNumber = 1, Start = Eight, End = Eight.AddMinutes(22),
            Entries = [Episode()]
        };

        using var animations = new OverlayAnimations(() => _folder, () => "ffmpeg");
        var processor = new OverlayProcessor(new OverlayRenderer(_folder), NullLogger<OverlayProcessor>.Instance, animations: animations);
        ChannelSchedule processed = await processor.ProcessAsync(new PlayoutContext(config, cartoons, schedule), CancellationToken.None);
        JsonArray layers = (JsonArray)JsonNode.Parse(processed.Entries[0].Annotations![PlayoutPublisher.GraphicsAnnotation])!;

        Assert.Equal(2, layers.Count);
        Assert.Equal(bug, (string?)layers[0]!["source"]!["path"]);
        Assert.Equal(5000, (long)layers[0]!["timing"]!["hold_ms"]!);
        Assert.Equal(last, (string?)layers[1]!["source"]!["path"]);
        Assert.Equal(5000, (long)layers[1]!["timing"]!["phase_offset_ms"]!);
        Assert.Equal(22 * 60_000 - 5000, (long)layers[1]!["timing"]!["hold_ms"]!);
        Assert.Equal((string?)layers[0]!["location"], (string?)layers[1]!["location"]);

        // Looping, it's the video alone, always on.
        cartoons.Plugins = Section(new OverlaysSection
        {
            Overlays = [new Overlay { Name = "Bug", Kind = OverlayKinds.File, File = bug, FadeSeconds = 0 }]
        });
        processed = await processor.ProcessAsync(new PlayoutContext(config, cartoons, schedule), CancellationToken.None);
        layers = (JsonArray)JsonNode.Parse(processed.Entries[0].Annotations![PlayoutPublisher.GraphicsAnnotation])!;
        Assert.Single(layers);
        Assert.Null(layers[0]!["timing"]);
    }

    [Fact]
    public async Task An_AI_written_caption_shows_its_usual_text_until_it_is_written_and_then_its_own()
    {
        (SchedulerConfig config, ChannelConfig cartoons, _) = Channels();
        var layer = new OverlayLayer { Kind = OverlayLayerKinds.Text, Text = "{title}", Writer = new AiTextConfig() };
        var overlay = new Overlay { Name = "Caption", Layers = [layer] };
        cartoons.Plugins = Section(new OverlaysSection { Overlays = [overlay] });
        var schedule = new ChannelSchedule
        {
            ChannelId = "cartoons", ChannelName = "Cartoons", ChannelNumber = 1, Start = Eight, End = Eight.AddMinutes(22),
            Entries = [Episode()]
        };
        var generator = new AiWritingTests.FakeGenerator(_ => "Toons is on, stay tuned");
        var writer = new AiTextWriter(new TextGeneratorCatalog([generator]), Path.Combine(_folder, "ai"), TimeProvider.System,
            NullLogger<AiTextWriter>.Instance);
        var processor = new OverlayProcessor(new OverlayRenderer(_folder), NullLogger<OverlayProcessor>.Instance, writer);
        string Drawn(ChannelSchedule processed) =>
            (string)JsonNode.Parse(processed.Entries[0].Annotations![PlayoutPublisher.GraphicsAnnotation])![0]!["source"]!["path"]!;

        string usual = Drawn(await processor.ProcessAsync(new PlayoutContext(config, cartoons, schedule), CancellationToken.None));

        ScheduleEntry entry = schedule.Entries[0];
        AiTextResult written = await writer.WriteAsync(OverlayWriting.Job(overlay, layer, OverlayProcessor.Tokens(entry, null, cartoons),
            entry.Item!.Overview, entry.Item.Id, entry.Start));
        Assert.True(written.Written, written.Problem);

        string own = Drawn(await processor.ProcessAsync(new PlayoutContext(config, cartoons, schedule), CancellationToken.None));
        Assert.NotEqual(usual, own);
        Assert.Equal(1, generator.Calls);
    }

    // --- Videos in designs, and parts timed on their own ---------------------------------------------

    [Fact]
    public void A_layer_comes_in_late_and_goes_early_within_the_overlay_and_a_video_does_not_fade()
    {
        var overlay = new Overlay { When = OverlayWhen.Start, OffsetSeconds = 10, DurationSeconds = 8, FadeSeconds = 1 };

        GraphicsTiming text = OverlayTiming.For(overlay, Episode(), new LayerTiming(2, 1))!.Timing!;
        Assert.Equal((12_000L, 5_000L, 1_000L), (text.PhaseMs, text.VisibleMs, text.FadeMs));

        GraphicsTiming video = OverlayTiming.For(overlay, Episode(), LayerTiming.Of(new OverlayLayer { Kind = OverlayLayerKinds.Video }))!.Timing!;
        Assert.Equal((10_000L, 8_000L, 0L), (video.PhaseMs, video.VisibleMs, video.FadeMs));

        // Nothing left of its window: it doesn't show.
        Assert.Null(OverlayTiming.For(overlay, Episode(), new LayerTiming(4, 4)));

        // Every few minutes keeps the cycle, starting each one later.
        GraphicsTiming every = OverlayTiming.For(new Overlay { When = OverlayWhen.Every, EveryMinutes = 5, OffsetSeconds = 60, DurationSeconds = 10 },
            Episode(), new LayerTiming(1.5, 1.5))!.Timing!;
        Assert.Equal((300_000L, 61_500L, 7_000L), (every.FrequencyMs, every.PhaseMs, every.VisibleMs));

        // All the way through: from each part's start and end.
        GraphicsTiming always = OverlayTiming.For(new Overlay { When = OverlayWhen.Always, FadeSeconds = 0 }, Episode(), new LayerTiming(3, 2))!.Timing!;
        Assert.Equal((3_000L, 22 * 60_000L - 5_000L), (always.PhaseMs, always.VisibleMs));
    }

    [Fact]
    public void A_design_splits_into_its_videos_and_runs_of_drawn_layers_that_come_and_go_together()
    {
        var overlay = new Overlay
        {
            Layers =
            [
                new OverlayLayer { Name = "Bar video", Kind = OverlayLayerKinds.Video, Path = "bar.mov" },
                new OverlayLayer { Name = "Show", Kind = OverlayLayerKinds.Text, InSeconds = 1.5, OutSeconds = 1.5 },
                new OverlayLayer { Name = "Episode", Kind = OverlayLayerKinds.Text, InSeconds = 1.5, OutSeconds = 1.5 },
                new OverlayLayer { Name = "Logo", Kind = OverlayLayerKinds.Image }
            ]
        };

        var groups = OverlayRenderer.Groups(overlay);

        Assert.Equal(3, groups.Count);
        Assert.Equal(["Bar video"], groups[0].Layers.Select(layer => layer.Name));
        Assert.False(groups[0].Timing.Fades);
        Assert.Equal(["Show", "Episode"], groups[1].Layers.Select(layer => layer.Name));
        Assert.Equal(["Logo"], groups[2].Layers.Select(layer => layer.Name));
        Assert.True(groups[2].Timing.IsWhole);
    }

    [Fact]
    public async Task A_design_with_a_video_reaches_ErsatzTV_as_the_video_then_its_text_timed_inside_it()
    {
        (SchedulerConfig config, ChannelConfig cartoons, _) = Channels();
        string video = Path.Combine(_folder, "lower-third.mov");
        cartoons.Plugins = Section(new OverlaysSection
        {
            Overlays =
            [
                new Overlay
                {
                    Name = "Animated lower third", When = OverlayWhen.Start, OffsetSeconds = 10, DurationSeconds = 8, FadeSeconds = 0.5,
                    Layers =
                    [
                        new OverlayLayer { Kind = OverlayLayerKinds.Video, Path = video, X = 96, Y = 810, Width = 960, Height = 180 },
                        new OverlayLayer { Kind = OverlayLayerKinds.Text, Text = "{title}", X = 140, Y = 850, Width = 860, Height = 70, InSeconds = 1.5, OutSeconds = 1 }
                    ]
                }
            ]
        });
        var schedule = new ChannelSchedule
        {
            ChannelId = "cartoons", ChannelName = "Cartoons", ChannelNumber = 1, Start = Eight, End = Eight.AddMinutes(22),
            Entries = [Episode()]
        };

        ChannelSchedule processed = await Processor().ProcessAsync(new PlayoutContext(config, cartoons, schedule), CancellationToken.None);
        JsonArray layers = (JsonArray)JsonNode.Parse(processed.Entries[0].Annotations![PlayoutPublisher.GraphicsAnnotation])!;

        Assert.Equal(2, layers.Count);

        // The video: where its box is, as percents of the canvas, on the whole frame; it plays the whole time, unfaded.
        Assert.Equal(video, (string?)layers[0]!["source"]!["path"]);
        Assert.Equal("top_left", (string?)layers[0]!["location"]);
        Assert.Equal(50, (double)layers[0]!["width_percent"]!);
        Assert.Equal(5, (double)layers[0]!["horizontal_margin_percent"]!);
        Assert.Equal(75, (double)layers[0]!["vertical_margin_percent"]!);
        Assert.False((bool)layers[0]!["within_source_content"]!);
        Assert.Equal(10_000, (long)layers[0]!["timing"]!["phase_offset_ms"]!);
        Assert.Equal(0, (long)layers[0]!["timing"]!["fade_ms"]!);
        Assert.Equal(8_000, (long)layers[0]!["timing"]!["hold_ms"]!);

        // The text: drawn without the video, 1.5s later and gone 1s sooner, fading as the overlay does.
        string drawn = (string)layers[1]!["source"]!["path"]!;
        Assert.True(File.Exists(drawn));
        Assert.Equal(11_500, (long)layers[1]!["timing"]!["phase_offset_ms"]!);
        Assert.Equal(500, (long)layers[1]!["timing"]!["fade_ms"]!);
        Assert.Equal(4_500, (long)layers[1]!["timing"]!["hold_ms"]!);
    }

    [Fact]
    public void Ffprobe_says_how_long_a_video_runs_and_its_size()
    {
        OverlayVideoInfo info = OverlayVideos.Parse("width=1920\nheight=1080\nduration=6.506000\n")!;

        Assert.Equal((1920, 1080), (info.Width, info.Height));
        Assert.Equal(6.506, info.Duration!.Value.TotalSeconds, 3);
        Assert.Equal(0.5625, info.Aspect!.Value, 4);
        Assert.Null(OverlayVideos.Parse("nonsense"));
    }

    [Fact]
    public void Every_preset_starts_from_something_that_shows()
    {
        foreach (OverlayPresets.Preset preset in OverlayPresets.All)
        {
            Overlay overlay = preset.Create();
            Assert.NotNull(OverlayTiming.For(overlay, Episode()));
            Assert.NotEmpty(OverlayDescriptions.Sample(overlay, TimeSpan.FromMinutes(22)));
        }
    }
}
