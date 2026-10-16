using SchedularrTV.Core.Configuration;
using SchedularrTV.GeneratedPromos;
using SchedularrTV.Overlays;
using SkiaSharp;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class GroupMaskTests
{
    private static OverlayLayer Rect(string color, string? parent = null) => new()
    {
        Kind = OverlayLayerKinds.Rect, Color = color, Parent = parent, Width = 100, Height = 100
    };

    private static Overlay Scene() => new()
    {
        Width = 100, Height = 100, FadeSeconds = 0,
        Layers =
        [
            Rect("#0000ff"),
            new() { Id = "outer", Kind = OverlayLayerKinds.Group, Width = 100, Height = 100 },
            Rect("#ff0000", "outer"),
            new() { Id = "inner", Parent = "outer", Kind = OverlayLayerKinds.Group, Width = 100, Height = 100 },
            Rect("#00ff00", "inner")
        ]
    };

    private static OverlayLayer Mask(string target, GeneratedMaskMode mode, float x = 0, float width = 100) => new()
    {
        Kind = OverlayLayerKinds.Rect, X = x, Width = width, Height = 100, Color = "#ffffff",
        Mask = new() { Target = target, Mode = mode }
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Partial_group_mask_is_applied_once_to_composited_overlapping_descendants(bool promo)
    {
        Overlay scene = Scene();
        OverlayLayer mask = Mask("outer", GeneratedMaskMode.Subtract);
        mask.Opacity = 50;
        scene.Layers.Add(mask);
        using SKBitmap result = Draw(scene, promo);
        SKColor pixel = result.GetPixel(50, 50);
        Assert.Equal(0, pixel.Red); // Masking each leaf separately would leak the red layer through the green.
        Assert.InRange(pixel.Green, (byte)126, (byte)129);
        Assert.InRange(pixel.Blue, (byte)126, (byte)129);
        Assert.Equal(255, pixel.Alpha);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inner_and_outer_group_masks_compose_and_leave_unrelated_layers_alone(bool promo)
    {
        Overlay scene = Scene();
        OverlayLayer sibling = Rect("#ffff00");
        sibling.X = 45; sibling.Width = 10; sibling.Height = 10;
        scene.Layers.Add(sibling);
        scene.Layers.Add(Mask("inner", GeneratedMaskMode.Subtract, 40, 20));
        scene.Layers.Add(Mask("outer", GeneratedMaskMode.Add, 10, 80));
        using SKBitmap result = Draw(scene, promo);
        Assert.Equal(SKColors.Blue, result.GetPixel(5, 50));
        Assert.Equal(SKColors.Lime, result.GetPixel(20, 50));
        Assert.Equal(SKColors.Red, result.GetPixel(50, 50));
        Assert.Equal(SKColors.Lime, result.GetPixel(80, 50));
        Assert.Equal(SKColors.Blue, result.GetPixel(95, 50));
        Assert.Equal(SKColors.Yellow, result.GetPixel(50, 5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_mask_inside_its_target_group_uses_the_group_space_without_recursive_rendering(bool promo)
    {
        Overlay scene = Scene();
        scene.Layers[1].X = 20;
        scene.Layers[3].X = 10;
        OverlayLayer mask = Mask("outer", GeneratedMaskMode.Add, 0, 30);
        mask.Parent = "inner";
        scene.Layers.Add(mask);
        using SKBitmap result = Draw(scene, promo);
        Assert.Equal(SKColors.Blue, result.GetPixel(25, 50));
        Assert.Equal(SKColors.Lime, result.GetPixel(40, 50));
        Assert.Equal(SKColors.Blue, result.GetPixel(65, 50));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Image_masks_can_target_nested_groups_using_their_alpha(bool promo)
    {
        string path = Path.Combine(Path.GetTempPath(), $"group-mask-{Guid.NewGuid():N}.png");
        try
        {
            using (var source = new SKBitmap(100, 100))
            {
                source.Erase(SKColors.Transparent);
                using var canvas = new SKCanvas(source);
                using var black = new SKPaint { Color = SKColors.Black };
                canvas.DrawRect(0, 0, 50, 100, black);
                using var data = source.Encode(SKEncodedImageFormat.Png, 100);
                using var file = File.Create(path);
                data.SaveTo(file);
            }
            Overlay scene = Scene();
            OverlayLayer mask = Mask("inner", GeneratedMaskMode.Add);
            mask.Kind = OverlayLayerKinds.Image;
            mask.Path = path;
            scene.Layers.Add(mask);
            using SKBitmap result = Draw(scene, promo);
            Assert.Equal(SKColors.Lime, result.GetPixel(25, 50));
            Assert.Equal(SKColors.Red, result.GetPixel(75, 50));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Overlay_render_parts_keep_masks_for_all_ancestor_groups()
    {
        Overlay scene = Scene();
        OverlayLayer outer = Mask("outer", GeneratedMaskMode.Add, 10, 80);
        OverlayLayer inner = Mask("inner", GeneratedMaskMode.Subtract, 40, 20);
        scene.Layers.AddRange([outer, inner]);
        Overlay part = OverlayRenderer.Part(scene, [scene.Layers[4]]);
        Assert.Equal(2, part.Layers.Count(layer => layer.Mask is not null));
        using SKBitmap result = Draw(part, false);
        Assert.Equal(0, result.GetPixel(5, 50).Alpha);
        Assert.Equal(SKColors.Lime, result.GetPixel(25, 50));
        Assert.Equal(0, result.GetPixel(50, 50).Alpha);
    }

    [Fact]
    public void Timed_group_contents_are_composited_as_an_animation_and_freeze_without_restarting_it()
    {
        Overlay scene = Scene();
        scene.Layers[4].InSeconds = 2;
        scene.Layers.Add(Mask("outer", GeneratedMaskMode.Add, 10, 80));
        Assert.True(OverlayAnimation.HasTimedGroupMask(scene));
        Assert.True(OverlayAnimation.IsAnimated(scene));
        using SKBitmap before = Draw(scene, false, new(1, 10, 0));
        using SKBitmap after = Draw(scene, false, new(3, 10, 0));
        Assert.Equal(SKColors.Red, before.GetPixel(50, 50));
        Assert.Equal(SKColors.Lime, after.GetPixel(50, 50));
        Overlay frozen = OverlayAnimation.Freeze(scene, 1);
        Assert.False(OverlayAnimation.IsAnimated(frozen));
        using SKBitmap still = Draw(frozen, false);
        Assert.Equal(SKColors.Red, still.GetPixel(50, 50));
    }

    [Fact]
    public void Overlay_mask_picker_includes_nested_groups_but_not_native_video_groups()
    {
        Overlay scene = Scene();
        OverlayLayer mask = Mask("outer", GeneratedMaskMode.Add);
        scene.Layers.Add(mask);
        Assert.Contains(scene.Layers[1], OverlayHierarchy.MaskTargets(scene, mask));
        Assert.Contains(scene.Layers[3], OverlayHierarchy.MaskTargets(scene, mask));
        scene.Layers.Insert(5, new() { Kind = OverlayLayerKinds.Video, Parent = "inner" });
        Assert.DoesNotContain(scene.Layers[1], OverlayHierarchy.MaskTargets(scene, mask));
        Assert.DoesNotContain(scene.Layers[3], OverlayHierarchy.MaskTargets(scene, mask));
    }

    private static SKBitmap Draw(Overlay scene, bool promo, OverlayClock? clock = null)
    {
        var bitmap = new SKBitmap(scene.Width, scene.Height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        if (!promo)
        {
            OverlayRenderer.DrawDesign(canvas, scene, new Dictionary<string, string>(), null, clock);
            return bitmap;
        }

        var groups = new Dictionary<string, PromoGroupLayer>();
        List<PromoLayer> layers = [];
        foreach (OverlayLayer layer in scene.Layers)
        {
            SKRect bounds = SKRect.Create(layer.X, layer.Y, layer.Width, layer.Height);
            PromoLayer drawn = layer.Kind switch
            {
                OverlayLayerKinds.Group => new PromoGroupLayer { Bounds = bounds },
                OverlayLayerKinds.Image => new PromoImageLayer { Bounds = bounds, Path = layer.Path! },
                _ => new PromoRectLayer { Bounds = bounds, Color = SKColor.Parse(layer.Color) }
            };
            drawn = drawn with
            {
                Id = layer.Id, Parent = layer.Parent is { } parent ? groups[parent] : null,
                Mask = layer.Mask is { } mask ? new(mask.Mode, mask.Target) : null,
                Opacity = (float)(layer.Opacity / 100), In = PromoTransition.None, Out = PromoTransition.None
            };
            if (drawn is PromoGroupLayer group) groups[layer.Id!] = group;
            layers.Add(drawn);
        }
        using var images = new PromoImageCache();
        PromoRenderer.DrawFrame(canvas, new() { Width = scene.Width, Height = scene.Height, Duration = TimeSpan.FromSeconds(10), Layers = layers },
            TimeSpan.FromSeconds(1), images, drawBackground: false);
        return bitmap;
    }
}
