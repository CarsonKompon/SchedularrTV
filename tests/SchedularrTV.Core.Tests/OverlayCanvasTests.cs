using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Overlays;
using SchedularrTV.Plugins.Playout;
using SchedularrTV.Providers.ErsatzTv;
using SkiaSharp;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>The overlay canvas's groups, locking, shapes, masks and keyframes, as in the Generated promos editor.</summary>
public sealed class OverlayCanvasTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "overlay-canvas-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.Delete(_folder);

    private static readonly DateTimeOffset Eight = new(2026, 10, 3, 20, 0, 0, TimeSpan.Zero);

    private static ScheduleEntry Episode() => new()
    {
        Start = Eight,
        Duration = TimeSpan.FromMinutes(22),
        Kind = ScheduleEntryKind.Program,
        Item = TestMedia.Episode("Toons", 1, 2, TimeSpan.FromMinutes(22))
    };

    [Fact]
    public void Keyframes_ease_between_poses_and_hold_before_the_first_and_after_the_last()
    {
        var layer = new OverlayLayer
        {
            Keyframes =
            [
                new OverlayKeyframe { At = TimeSpan.FromSeconds(1), X = 0, Y = 0, Width = 100, Height = 50, Opacity = 0 },
                new OverlayKeyframe { At = TimeSpan.FromSeconds(3), X = 200, Y = 100, Width = 100, Height = 50, Opacity = 100, Rotation = 90 }
            ]
        };

        Assert.Equal(0, OverlayAnimation.PoseAt(layer, 0).X);
        OverlayPose middle = OverlayAnimation.PoseAt(layer, 2);
        Assert.Equal((100f, 50f, 50.0, 45f), (middle.X, middle.Y, middle.Opacity, middle.Rotation!.Value));
        Assert.Equal(200, OverlayAnimation.PoseAt(layer, 10).X);

        layer.Keyframes[0].Easing = GeneratedEasing.Hold;
        Assert.Equal(0, OverlayAnimation.PoseAt(layer, 2.9).X);
    }

    [Fact]
    public void Moving_an_animated_layer_sets_a_keyframe_at_the_playhead_and_stopping_leaves_it_there()
    {
        var layer = new OverlayLayer { X = 10, Y = 10, Width = 100, Height = 40 };
        OverlayAnimation.StartKeyframes(layer, 0);
        OverlayAnimation.SetPose(layer, 2, OverlayAnimation.PoseAt(layer, 2) with { X = 210 });

        Assert.Equal(2, layer.Keyframes!.Count);
        Assert.Equal(110, OverlayAnimation.PoseAt(layer, 1).X);

        OverlayAnimation.StopKeyframes(layer, 1);
        Assert.Null(layer.Keyframes);
        Assert.Equal(110, layer.X);
    }

    [Fact]
    public void Grouping_keeps_layers_where_they_are_and_the_group_carries_them()
    {
        var overlay = new Overlay
        {
            Layers =
            [
                new OverlayLayer { Name = "A", Kind = OverlayLayerKinds.Rect, X = 100, Y = 100, Width = 100, Height = 100 },
                new OverlayLayer { Name = "B", Kind = OverlayLayerKinds.Text, X = 300, Y = 100, Width = 100, Height = 100 }
            ]
        };

        OverlayLayer group = OverlayHierarchy.Group(overlay, overlay.Layers.ToList(), 0);

        Assert.Equal(OverlayLayerKinds.Group, overlay.Layers[0].Kind);
        Assert.Equal((100f, 100f, 300f, 100f), (group.X, group.Y, group.Width, group.Height));
        OverlayLayer b = overlay.Layers.Single(layer => layer.Name == "B");
        Assert.Equal(group.Id, b.Parent);
        Assert.Equal(200, b.X);
        Assert.Equal(350, OverlayHierarchy.World(overlay, b, 0).CenterX, 3);

        group.X += 50;
        Assert.Equal(400, OverlayHierarchy.World(overlay, b, 0).CenterX, 3);

        group.Locked = true;
        Assert.True(OverlayHierarchy.IsLocked(overlay, b));

        OverlayHierarchy.Ungroup(overlay, group, 0);
        Assert.Equal(2, overlay.Layers.Count);
        Assert.Null(b.Parent);
        Assert.Equal(350, b.X);
    }

    [Fact]
    public void Dragging_a_layer_into_a_group_and_above_another_keeps_the_list_tidy()
    {
        var overlay = new Overlay
        {
            Layers =
            [
                new OverlayLayer { Name = "Group", Kind = OverlayLayerKinds.Group, Id = "g", X = 0, Y = 0, Width = 10, Height = 10 },
                new OverlayLayer { Name = "Inside", Parent = "g" },
                new OverlayLayer { Name = "Top" }
            ]
        };

        OverlayLayer top = overlay.Layers[2];
        Assert.True(OverlayHierarchy.Move(overlay, [top], overlay.Layers[0], OverlayDrop.Into, 0));
        Assert.Equal(["Group", "Inside", "Top"], overlay.Layers.Select(layer => layer.Name));
        Assert.Equal("g", top.Parent);

        Assert.False(OverlayHierarchy.Move(overlay, [overlay.Layers[0]], top, OverlayDrop.Above, 0));
        Assert.Equal(["Top", "Inside"], OverlayHierarchy.Rows(overlay, _ => true).Skip(1).Select(row => row.Layer.Name));
    }

    [Fact]
    public void A_subtract_mask_cuts_its_target_and_draws_nothing_itself()
    {
        var overlay = new Overlay
        {
            Width = 100, Height = 100,
            Layers =
            [
                new OverlayLayer { Id = "box", Kind = OverlayLayerKinds.Rect, Color = "#ff0000", X = 0, Y = 0, Width = 100, Height = 100 },
                new OverlayLayer { Kind = OverlayLayerKinds.Rect, Color = "#ffffff", X = 0, Y = 0, Width = 50, Height = 100,
                    Mask = new GeneratedLayerMask { Mode = GeneratedMaskMode.Subtract, Target = "box" } }
            ]
        };

        using var bitmap = new SKBitmap(new SKImageInfo(100, 100, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        OverlayRenderer.DrawDesign(canvas, overlay, new Dictionary<string, string>(), null, null);

        Assert.Equal(0, bitmap.GetPixel(25, 50).Alpha);
        Assert.Equal(SKColors.Red, bitmap.GetPixel(75, 50));
    }

    [Fact]
    public void Old_designs_keep_their_drawings()
    {
        var plain = new Overlay { Layers = [new OverlayLayer { Kind = OverlayLayerKinds.Rect, X = 1, Y = 2 }] };
        var shaped = plain.Copy();
        shaped.Layers[0].Shape = GeneratedShape.Star;

        Assert.Equal(OverlayRenderer.Key(plain, new Dictionary<string, string>()), OverlayRenderer.Key(plain.Copy(), new Dictionary<string, string>()));
        Assert.NotEqual(OverlayRenderer.Key(plain, new Dictionary<string, string>()), OverlayRenderer.Key(shaped, new Dictionary<string, string>()));
    }

    [Fact]
    public async Task An_animated_design_airs_standing_still_until_its_video_is_made()
    {
        var overlay = new Overlay
        {
            Name = "Sliding lower third", When = OverlayWhen.Start, OffsetSeconds = 10, DurationSeconds = 8, FadeSeconds = 0.5,
            Layers =
            [
                new OverlayLayer
                {
                    Kind = OverlayLayerKinds.Rect, X = 100, Y = 900, Width = 600, Height = 100,
                    Keyframes =
                    [
                        new OverlayKeyframe { At = TimeSpan.Zero, X = -600, Y = 900, Width = 600, Height = 100 },
                        new OverlayKeyframe { At = TimeSpan.FromSeconds(1), X = 100, Y = 900, Width = 600, Height = 100 }
                    ]
                }
            ]
        };
        Assert.True(OverlayAnimation.IsAnimated(overlay));

        var channel = new ChannelConfig
        {
            Id = "cartoons", Name = "Cartoons", Number = 1,
            Plugins = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["overlays"] = new OverlaysSection { Overlays = [overlay] } }
        };
        var config = new SchedulerConfig { Channels = [channel] };
        var schedule = new ChannelSchedule
        {
            ChannelId = "cartoons", ChannelName = "Cartoons", ChannelNumber = 1, Start = Eight, End = Eight.AddMinutes(22), Entries = [Episode()]
        };

        var processor = new OverlayProcessor(new OverlayRenderer(_folder), NullLogger<OverlayProcessor>.Instance);
        ChannelSchedule processed = await processor.ProcessAsync(new PlayoutContext(config, channel, schedule), CancellationToken.None);
        JsonArray layers = (JsonArray)JsonNode.Parse(processed.Entries[0].Annotations![PlayoutPublisher.GraphicsAnnotation])!;

        JsonNode layer = Assert.Single(layers)!;
        Assert.EndsWith(".png", (string)layer["source"]!["path"]!);
        Assert.Equal(10_000, (long)layer["timing"]!["phase_offset_ms"]!);
    }
}
