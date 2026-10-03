using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Overlays;
using SchedularrTV.Plugins.Playout;
using SchedularrTV.Providers.ErsatzTv;
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
