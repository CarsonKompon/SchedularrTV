using System.Text.Json;
using SkiaSharp;
using SchedularrTV.Core.Configuration;
using SchedularrTV.GeneratedPromos;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class GeneratedPromoKeyframeTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private static readonly PromoKeyframe[] Keys =
    [
        new(TimeSpan.FromSeconds(1), SKRect.Create(0, 0, 100, 50), 1),
        new(TimeSpan.FromSeconds(3), SKRect.Create(200, 100, 300, 150), 0.5f, PromoEasing.Hold),
        new(TimeSpan.FromSeconds(5), SKRect.Create(400, 100, 300, 150), 0)
    ];

    [Fact]
    public void Keyframes_hold_at_the_ends_and_move_in_between()
    {
        SKRect own = SKRect.Create(9, 9, 9, 9);

        Assert.Equal((Keys[0].Bounds, 1f), PromoAnimation.Pose(own, 1, Keys, TimeSpan.Zero));
        Assert.Equal((Keys[2].Bounds, 0f), PromoAnimation.Pose(own, 1, Keys, TimeSpan.FromSeconds(9)));
        Assert.Equal((own, 0.7f), PromoAnimation.Pose(own, 0.7f, null, TimeSpan.FromSeconds(2)));

        (SKRect middle, float opacity) = PromoAnimation.Pose(own, 1, Keys, TimeSpan.FromSeconds(2));
        Assert.Equal(SKRect.Create(100, 50, 200, 100), middle);
        Assert.Equal(0.75f, opacity, 3);

        // Hold stays on the keyframe until the next one is reached.
        Assert.Equal(Keys[1].Bounds, PromoAnimation.Pose(own, 1, Keys, TimeSpan.FromSeconds(4.9)).Bounds);
        Assert.Equal(Keys[2].Bounds, PromoAnimation.Pose(own, 1, Keys, TimeSpan.FromSeconds(5)).Bounds);
    }

    [Theory]
    [InlineData(PromoEasing.Linear, 0.25, 0.25)]
    [InlineData(PromoEasing.EaseIn, 0.5, 0.125)]
    [InlineData(PromoEasing.EaseOut, 0.5, 0.875)]
    [InlineData(PromoEasing.EaseInOut, 0.5, 0.5)]
    [InlineData(PromoEasing.Hold, 0.99, 0)]
    public void Easings_follow_their_curves(PromoEasing easing, double progress, double expected) =>
        Assert.Equal(expected, PromoAnimation.Curve(easing, progress), 3);

    [Fact]
    public void Animated_layers_draw_where_their_keyframes_put_them()
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext();
        GeneratedLayer panel = template.Layers[0];
        panel.TransitionIn = GeneratedTransition.None;
        panel.TransitionOut = GeneratedTransition.None;
        panel.Keyframes =
        [
            new GeneratedKeyframe { At = TimeSpan.FromSeconds(4), X = 500, Y = 0, Width = 100, Height = 100 },
            new GeneratedKeyframe { At = TimeSpan.FromSeconds(2), X = 100, Y = 0, Width = 100, Height = 100 }
        ];

        ResolvedGeneratedPromo promo = GeneratedPromoResolver.Resolve(template, "Channel", [new UpNextListing("Show", Noon.AddMinutes(30))], TimeZoneInfo.Utc, Noon)!;
        PromoLayer layer = GeneratedPromoResolver.Compose(promo).Layers[0];

        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) }, layer.Keyframes!.Select(key => key.At));
        PromoLayerFrame frame = PromoAnimation.Evaluate(layer, TimeSpan.FromSeconds(3), template.Duration, template.Height)!.Value;
        Assert.Equal(300, frame.Bounds.Left, 3);

        // Frames that only differ in position aren't taken for a held pose.
        Assert.NotEqual(frame, PromoAnimation.Evaluate(layer, TimeSpan.FromSeconds(3.5), template.Duration, template.Height)!.Value);
    }

    [Fact]
    public void Templates_without_keyframes_or_extra_backgrounds_keep_their_render_keys()
    {
        string json = JsonSerializer.Serialize(GeneratedPromoTemplates.UpNext());

        Assert.DoesNotContain("Keyframes", json);
        Assert.DoesNotContain("Backgrounds", json);
        Assert.DoesNotContain("BackgroundChoices", json);
    }

    [Fact]
    public void Editing_an_animated_layer_sets_the_keyframe_at_the_preview_time()
    {
        GeneratedLayer layer = new() { X = 10, Y = 20, Width = 300, Height = 100, Opacity = 0.8f };

        GeneratedPromoEditing.SetPose(layer, TimeSpan.FromSeconds(1), new LayerPose(50, 60, 300, 100, 1));
        Assert.Null(layer.Keyframes);
        Assert.Equal(50, layer.X);

        GeneratedPromoEditing.StartKeyframes(layer, TimeSpan.FromSeconds(1));
        GeneratedPromoEditing.SetPose(layer, TimeSpan.FromSeconds(3.00004), new LayerPose(250, 60, 300, 100, 0.5f));
        Assert.Equal(2, layer.Keyframes!.Count);
        Assert.Equal(TimeSpan.FromSeconds(3), layer.Keyframes[1].At);
        Assert.Equal(150, GeneratedPromoEditing.PoseAt(layer, TimeSpan.FromSeconds(2)).X, 3);

        // Changing it again at (nearly) the same time edits that keyframe rather than adding one.
        GeneratedPromoEditing.SetPose(layer, TimeSpan.FromSeconds(2.9999), new LayerPose(260, 60, 300, 100, 0.5f));
        Assert.Equal(2, layer.Keyframes.Count);
        Assert.Equal(260, layer.Keyframes[1].X);

        // ... but the next frame along, even at 60 fps, is a keyframe of its own.
        Assert.Null(GeneratedPromoEditing.KeyframeAt(layer, TimeSpan.FromSeconds(3 + 1 / 60.0)));

        // A new keyframe between two changes nothing until it's moved, and eases on like the one before it.
        layer.Keyframes[0].Easing = GeneratedEasing.EaseOut;
        LayerPose before = GeneratedPromoEditing.PoseAt(layer, TimeSpan.FromSeconds(2));
        GeneratedKeyframe added = GeneratedPromoEditing.AddKeyframe(layer, TimeSpan.FromSeconds(2));
        Assert.Equal(GeneratedEasing.EaseOut, added.Easing);
        Assert.Equal(MathF.Round(before.X), added.X);
        Assert.Same(added, layer.Keyframes[1]);

        GeneratedPromoEditing.MoveKeyframe(layer, added, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(10));
        Assert.Same(added, layer.Keyframes[^1]);

        GeneratedPromoEditing.StopKeyframes(layer, TimeSpan.FromSeconds(9));
        Assert.Null(layer.Keyframes);
        Assert.Equal(added.X, layer.X);
    }

    [Fact]
    public void Removing_the_last_keyframe_leaves_the_layer_where_it_was()
    {
        GeneratedLayer layer = new() { X = 10, Y = 20, Width = 300, Height = 100 };
        GeneratedKeyframe key = GeneratedPromoEditing.StartKeyframes(layer, TimeSpan.FromSeconds(1));
        key.X = 400;

        GeneratedPromoEditing.RemoveKeyframe(layer, key);

        Assert.Null(layer.Keyframes);
        Assert.Equal(400, layer.X);
    }

    [Fact]
    public void Resizing_and_offsetting_move_keyframes_too()
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext();
        GeneratedLayer layer = template.Layers[0];
        GeneratedKeyframe key = GeneratedPromoEditing.StartKeyframes(layer, TimeSpan.FromSeconds(1));
        float x = key.X;

        GeneratedPromoEditing.Offset(layer, 20, 10);
        Assert.Equal(x + 20, key.X);

        GeneratedPromoEditing.Resize(template, 960, 540);
        Assert.Equal(MathF.Round((x + 20) / 2), key.X);
    }

    [Fact]
    public void Text_layers_can_be_cased_and_aligned_up_and_down()
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext();
        string before = JsonSerializer.Serialize(template);
        Assert.DoesNotContain("VerticalAlign", before);
        Assert.DoesNotContain("TextCase", before);

        GeneratedLayer heading = template.Layers[2];
        heading.Text = "Up Next";
        heading.TextCase = GeneratedTextCase.Lower;
        heading.VerticalAlign = GeneratedVerticalAlign.Bottom;
        GeneratedLayer show = template.Layers[3];
        show.TextCase = GeneratedTextCase.Upper;

        ResolvedGeneratedPromo promo = GeneratedPromoResolver.Resolve(template, "Channel", [new UpNextListing("Hey Arnold!", Noon.AddMinutes(30))], TimeZoneInfo.Utc, Noon)!;
        PromoComposition composition = GeneratedPromoResolver.Compose(promo);
        var headingText = (PromoTextLayer)composition.Layers[2];
        var showText = (PromoTextLayer)composition.Layers[3];

        Assert.Equal(new[] { "up next" }, headingText.Lines);
        Assert.Equal(PromoVerticalAlign.Bottom, headingText.VerticalAlign);
        Assert.Equal(new[] { "HEY ARNOLD!" }, showText.Lines);
        Assert.Equal(PromoVerticalAlign.Top, showText.VerticalAlign);
    }

    [Fact]
    public void Up_next_text_can_carry_its_own_prefix_and_suffix()
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext();
        Assert.DoesNotContain("Prefix", JsonSerializer.Serialize(template));
        UpNextListing[] listings = [new UpNextListing("Rugrats", Noon.AddMinutes(30)), new UpNextListing("Doug", Noon.AddMinutes(60))];

        GeneratedLayer next = template.Layers[3];
        next.UpNext!.Prefix = "Up next: ";
        next.UpNext.Suffix = " on {channel}";
        GeneratedLayer later = template.Layers[4];
        later.UpNext!.Prefix = "LATER\n";

        ResolvedGeneratedPromo promo = GeneratedPromoResolver.Resolve(template, "Nick", listings, TimeZoneInfo.Utc, Noon)!;
        Assert.Equal(new[] { "Up next: Rugrats on Nick" }, promo.LayerLines[3]);
        Assert.Equal("LATER", promo.LayerLines[4][0]);
        Assert.EndsWith("Doug", promo.LayerLines[4][1]);

        // Nothing to list, nothing to add.
        ResolvedGeneratedPromo oneShow = GeneratedPromoResolver.Resolve(template, "Nick", listings[..1], TimeZoneInfo.Utc, Noon)!;
        Assert.Empty(oneShow.LayerLines[4]);
    }

    [Fact]
    public void Background_videos_play_their_sound_unless_it_is_turned_off()
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext();
        UpNextListing[] listings = [new UpNextListing("Show", Noon.AddMinutes(30))];
        string pictureKey = GeneratedPromoResolver.Resolve(template, "Channel", listings, TimeZoneInfo.Utc, Noon)!.Key;

        template.Background = "still.png";
        ResolvedGeneratedPromo picture = GeneratedPromoResolver.Resolve(template, "Channel", listings, TimeZoneInfo.Utc, Noon)!;
        Assert.Null(GeneratedPromoResolver.Compose(picture).BackgroundAudio);
        Assert.NotEqual(pictureKey, picture.Key);

        template.Background = "loop.mp4";
        ResolvedGeneratedPromo video = GeneratedPromoResolver.Resolve(template, "Channel", listings, TimeZoneInfo.Utc, Noon)!;
        PromoMusic sound = GeneratedPromoResolver.Compose(video).BackgroundAudio!;
        Assert.Equal("loop.mp4", sound.Path);
        Assert.Equal(1.0, sound.Volume);
        Assert.True(sound.DuckUnderVoice);
        Assert.Null(sound.Levels);

        template.BackgroundSound = new GeneratedBackgroundSoundConfig { Enabled = false };
        ResolvedGeneratedPromo silent = GeneratedPromoResolver.Resolve(template, "Channel", listings, TimeZoneInfo.Utc, Noon)!;
        Assert.Null(GeneratedPromoResolver.Compose(silent).BackgroundAudio);
        Assert.NotEqual(video.Key, silent.Key);
    }

    [Fact]
    public void Layers_can_be_placed_and_animated_differently_over_each_background()
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext();
        GeneratedPromoEditing.SetBackgrounds(template, ["red.mp4", "blue.mp4"]);
        string before = JsonSerializer.Serialize(template);
        Assert.DoesNotContain("BackgroundOverrides", before);
        Assert.DoesNotContain("\"Id\"", before);

        GeneratedLayer panel = template.Layers[0];
        GeneratedLayerOverride onBlue = GeneratedPromoEditing.Place(template, "blue.mp4", panel);
        Assert.NotNull(panel.Id);
        Assert.Equal(panel.X, onBlue.X);
        Assert.Same(onBlue, GeneratedPromoEditing.Place(template, "BLUE.mp4", panel));

        GeneratedPromoEditing.StartKeyframes(onBlue, TimeSpan.FromSeconds(1));
        GeneratedPromoEditing.SetPose(onBlue, TimeSpan.FromSeconds(3), new LayerPose(900, 50, 200, 100, 1));
        Assert.Null(panel.Keyframes);
        Assert.Equal(1, GeneratedPromoEditing.PlacedCount(template, "blue.mp4"));

        UpNextListing[] listings = [new UpNextListing("Show", Noon.AddMinutes(30))];
        PromoLayer Drawn(string background)
        {
            GeneratedPromoTemplate only = GeneratedPromoEditing.Copy(template);
            GeneratedPromoEditing.SetBackgrounds(only, [background]);
            return GeneratedPromoResolver.Compose(GeneratedPromoResolver.Resolve(only, "Channel", listings, TimeZoneInfo.Utc, Noon)!).Layers[0];
        }

        Assert.Equal(panel.X, Drawn("red.mp4").Bounds.Left);
        Assert.Null(Drawn("red.mp4").Keyframes);
        Assert.Equal(900, Drawn("blue.mp4").Keyframes![^1].Bounds.Left);

        // A swapped file keeps its placements; a removed one, or a deleted layer, takes them away.
        GeneratedPromoEditing.RenameBackground(template, "blue.mp4", "navy.mp4");
        Assert.NotNull(GeneratedPromoResolver.Placement(template, "navy.mp4", panel));
        GeneratedPromoEditing.Unplace(template, "navy.mp4", panel);
        Assert.Null(template.BackgroundOverrides);

        GeneratedPromoEditing.Place(template, "red.mp4", panel);
        GeneratedPromoEditing.ForgetLayer(template, panel);
        Assert.Null(template.BackgroundOverrides);
    }

    [Fact]
    public void Layers_can_be_coloured_differently_over_each_background_without_moving()
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext();
        GeneratedPromoEditing.SetBackgrounds(template, ["dark.mp4", "light.png"]);
        GeneratedLayer heading = template.Layers[2];
        string usual = heading.Color;

        GeneratedPromoEditing.Recolour(template, "light.png", heading, "#202020");
        Assert.Null(GeneratedPromoResolver.Placement(template, "light.png", heading));
        Assert.True(GeneratedPromoEditing.Customised(template, "light.png", heading));
        Assert.Equal(1, GeneratedPromoEditing.PlacedCount(template, "light.png"));

        // Moving the layer in the template still moves it over the light background.
        heading.X += 50;
        UpNextListing[] listings = [new UpNextListing("Show", Noon.AddMinutes(30))];
        PromoTextLayer Drawn(string background)
        {
            GeneratedPromoTemplate only = GeneratedPromoEditing.Copy(template);
            GeneratedPromoEditing.SetBackgrounds(only, [background]);
            return (PromoTextLayer)GeneratedPromoResolver.Compose(GeneratedPromoResolver.Resolve(only, "Channel", listings, TimeZoneInfo.Utc, Noon)!).Layers[2];
        }

        Assert.Equal(SKColor.Parse("#202020"), Drawn("light.png").Color);
        Assert.Equal(heading.X, Drawn("light.png").Bounds.Left);
        Assert.Equal(SKColor.Parse(usual), Drawn("dark.mp4").Color);

        // Choosing the layer's own colour again, or resetting it, takes the background's away.
        GeneratedPromoEditing.Recolour(template, "light.png", heading, usual);
        Assert.Null(template.BackgroundOverrides);
        GeneratedPromoEditing.Recolour(template, "light.png", heading, "#101010");
        GeneratedPromoEditing.Unplace(template, "light.png", heading);
        Assert.Null(template.BackgroundOverrides);
    }

    [Fact]
    public void Layers_can_be_timed_and_animated_in_differently_over_each_background()
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext();
        GeneratedPromoEditing.SetBackgrounds(template, ["slow.mp4", "fast.mp4"]);
        GeneratedLayer heading = template.Layers[2];

        GeneratedLayerTiming onFast = GeneratedPromoEditing.Retime(template, "fast.mp4", heading);
        Assert.Equal(heading.Start, onFast.Start);
        Assert.Equal(heading.TransitionIn, onFast.TransitionIn);
        onFast.Start = TimeSpan.FromSeconds(2);
        onFast.End = TimeSpan.FromSeconds(5);
        onFast.TransitionIn = GeneratedTransition.SlideLeft;
        onFast.TransitionDuration = TimeSpan.FromSeconds(1);
        Assert.Null(GeneratedPromoResolver.Placement(template, "fast.mp4", heading));

        UpNextListing[] listings = [new UpNextListing("Show", Noon.AddMinutes(30))];
        PromoLayer Drawn(string background)
        {
            GeneratedPromoTemplate only = GeneratedPromoEditing.Copy(template);
            GeneratedPromoEditing.SetBackgrounds(only, [background]);
            return GeneratedPromoResolver.Compose(GeneratedPromoResolver.Resolve(only, "Channel", listings, TimeZoneInfo.Utc, Noon)!).Layers[2];
        }

        PromoLayer fast = Drawn("fast.mp4");
        Assert.Equal(TimeSpan.FromSeconds(2), fast.Start);
        Assert.Equal(TimeSpan.FromSeconds(5), fast.End);
        Assert.Equal(PromoTransition.SlideLeft, fast.In);
        Assert.Equal(TimeSpan.FromSeconds(1), fast.TransitionDuration);
        Assert.Equal(heading.X, fast.Bounds.Left);

        PromoLayer slow = Drawn("slow.mp4");
        Assert.Equal(heading.Start, slow.Start);
        Assert.Equal(heading.End, slow.End);

        // Timing alone is reset without touching anything else the background sets.
        GeneratedPromoEditing.Recolour(template, "fast.mp4", heading, "#202020");
        GeneratedPromoEditing.Untime(template, "fast.mp4", heading);
        Assert.Null(GeneratedPromoResolver.Retiming(template, "fast.mp4", heading));
        Assert.NotNull(GeneratedPromoResolver.Recolour(template, "fast.mp4", heading));
        GeneratedPromoEditing.Unplace(template, "fast.mp4", heading);
        Assert.Null(template.BackgroundOverrides);
    }

    [Theory]
    [InlineData("08:30", 8, 30)]
    [InlineData("08:30:00", 8, 30)] // How Blazor passes a time input's value on.
    [InlineData("8:05", 8, 5)]
    [InlineData("23:59:59", 23, 59)]
    [InlineData("00:00:00", 0, 0)]
    public void Time_inputs_are_read_with_or_without_seconds(string text, int hours, int minutes)
    {
        Assert.True(GeneratedPromoEditing.TryParseTimeOfDay(text, out TimeSpan time));
        Assert.Equal(new TimeSpan(hours, minutes, 0), time);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("24:00")]
    [InlineData("8h")]
    public void Not_a_time_of_day_changes_nothing(string? text) =>
        Assert.False(GeneratedPromoEditing.TryParseTimeOfDay(text, out _));

    [Fact]
    public void Time_ranges_survive_saving_and_loading()
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext();
        GeneratedPromoEditing.SetBackgrounds(template, ["morning.mp4", "night.mp4"]);
        template.Availability = new() { TimeRange = new() { Start = new TimeSpan(8, 30, 0), End = TimeSpan.FromHours(14) } };
        template.BackgroundRules =
        [
            new() { Background = "night.mp4", MatchShows = false, TimeRange = new() { Start = TimeSpan.FromHours(20), End = new TimeSpan(5, 45, 0) } }
        ];
        var config = new SchedulerConfig
        {
            Channels = [new() { Id = "channel", Name = "Channel", Number = 1, BaseRotation = new() { Query = "*" },
                GeneratedPromos = new() { Templates = [template] } }]
        };

        // Saved the way the editor saves (merged with what's on disk, durations made friendly), then loaded again.
        string first = YamlCommentMerger.Merge(string.Empty, SchedulerConfigLoader.Serialize(config));
        string second = YamlCommentMerger.Merge(first, SchedulerConfigLoader.Serialize(SchedulerConfigLoader.Parse(first)));
        GeneratedPromoTemplate loaded = SchedulerConfigLoader.Parse(second).Channels[0].GeneratedPromos!.Templates[0];

        Assert.Equal(new TimeSpan(8, 30, 0), loaded.Availability!.TimeRange!.Start);
        Assert.Equal(TimeSpan.FromHours(14), loaded.Availability.TimeRange.End);
        Assert.Equal(TimeSpan.FromHours(20), loaded.BackgroundRules![0].TimeRange!.Start);
        Assert.Equal(new TimeSpan(5, 45, 0), loaded.BackgroundRules[0].TimeRange!.End);
    }

    [Fact]
    public void Each_promo_picks_one_of_the_backgrounds_and_keeps_it()
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext();
        GeneratedPromoEditing.SetBackgrounds(template, ["a.png", " ", "b.png", "A.PNG", "c.mp4"]);

        Assert.Equal("a.png", template.Background);
        Assert.Equal(new[] { "b.png", "c.mp4" }, template.Backgrounds!);
        Assert.Equal(new[] { "a.png", "b.png", "c.mp4" }, template.BackgroundChoices);

        HashSet<string?> picked = [.. Enumerable.Range(0, 60).Select(index => GeneratedPromoResolver.Background(template.BackgroundChoices, $"key-{index}"))];
        Assert.Equal(3, picked.Count);
        Assert.Equal(GeneratedPromoResolver.Background(template.BackgroundChoices, "same"), GeneratedPromoResolver.Background(template.BackgroundChoices, "same"));
        Assert.Null(GeneratedPromoResolver.Background([], "key"));

        GeneratedPromoEditing.SetBackgrounds(template, ["only.png"]);
        Assert.Null(template.Backgrounds);
        Assert.Equal("only.png", template.Background);
    }
}
