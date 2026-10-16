using SchedularrTV.Core.Configuration;
using SchedularrTV.GeneratedPromos;
using SkiaSharp;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class GeneratedPromoMaskTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static PromoRectLayer Rect(float x, float y, float width, float height, SKColor color) => new()
    {
        Bounds = SKRect.Create(x, y, width, height), Color = color, In = PromoTransition.None, Out = PromoTransition.None
    };

    private static PromoComposition Composition(params PromoLayer[] layers) => new()
    {
        Width = 200, Height = 100, Duration = TimeSpan.FromSeconds(5), BackgroundColor = SKColors.Blue, Layers = layers
    };

    private static SKBitmap Draw(PromoComposition composition, double second = 1, bool background = true)
    {
        var bitmap = new SKBitmap(composition.Width, composition.Height);
        using var canvas = new SKCanvas(bitmap);
        using var images = new PromoImageCache();
        PromoRenderer.DrawFrame(canvas, composition, TimeSpan.FromSeconds(second), images, background);
        return bitmap;
    }

    [Fact]
    public void Subtract_cuts_only_its_target_and_never_paints_the_mask_or_erases_the_background()
    {
        var target = Rect(0, 0, 200, 100, SKColors.Red) with { Id = "text" };
        var other = Rect(80, 10, 40, 20, SKColors.Yellow);
        var mask = Rect(50, 0, 100, 100, SKColors.Lime) with { Mask = new(GeneratedMaskMode.Subtract, "text") };
        using SKBitmap image = Draw(Composition(target, other, mask));
        Assert.Equal(SKColors.Red, image.GetPixel(20, 50));
        Assert.Equal(SKColors.Blue, image.GetPixel(100, 50));
        Assert.Equal(SKColors.Yellow, image.GetPixel(100, 20));

        using SKBitmap transparent = Draw(Composition(target, mask), background: false);
        Assert.Equal(0, transparent.GetPixel(100, 50).Alpha);
        Assert.Equal(255, transparent.GetPixel(20, 50).Alpha);
    }

    [Fact]
    public void Add_masks_reveal_their_union_then_subtract_masks_cut_holes()
    {
        var target = Rect(0, 0, 200, 100, SKColors.Red) with { Id = "target" };
        var left = Rect(20, 10, 70, 80, SKColors.Black) with { Mask = new(GeneratedMaskMode.Add, "target") };
        var right = Rect(110, 10, 70, 80, SKColors.White) with { Mask = new(GeneratedMaskMode.Add, "target") };
        var hole = Rect(40, 20, 20, 60, SKColors.White) with { Mask = new(GeneratedMaskMode.Subtract, "target") };
        using SKBitmap image = Draw(Composition(target, hole, left, right));
        Assert.Equal(SKColors.Red, image.GetPixel(30, 50));
        Assert.Equal(SKColors.Red, image.GetPixel(140, 50));
        Assert.Equal(SKColors.Blue, image.GetPixel(50, 50));
        Assert.Equal(SKColors.Blue, image.GetPixel(100, 50));
        Assert.Equal(SKColors.Blue, image.GetPixel(190, 50));
    }

    [Fact]
    public void Mask_opacity_feathering_and_timing_control_the_cutout()
    {
        var target = Rect(0, 0, 200, 100, SKColors.Red) with { Id = "target" };
        var mask = Rect(50, 10, 100, 80, SKColors.White) with
        {
            Mask = new(GeneratedMaskMode.Subtract, "target"), Opacity = 0.5f, SoftEdges = 4,
            Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(3)
        };
        using SKBitmap before = Draw(Composition(target, mask), 0.5);
        using SKBitmap during = Draw(Composition(target, mask), 2);
        using SKBitmap after = Draw(Composition(target, mask), 4);
        Assert.Equal(SKColors.Red, before.GetPixel(100, 50));
        Assert.Equal(SKColors.Red, after.GetPixel(100, 50));
        Assert.InRange(during.GetPixel(100, 50).Red, (byte)125, (byte)130);
        Assert.InRange(during.GetPixel(100, 50).Blue, (byte)125, (byte)130);
        Assert.InRange(during.GetPixel(50, 50).Blue, (byte)40, (byte)90);
        // An additive mask with no coverage (including outside its active time) reveals nothing.
        using SKBitmap hidden = Draw(Composition(target, mask with { Mask = new(GeneratedMaskMode.Add, "target") }), 0.5);
        Assert.Equal(SKColors.Blue, hidden.GetPixel(100, 50));
    }

    [Fact]
    public void Perspective_keyframed_masks_follow_the_object_independently_of_the_text_transform()
    {
        var group = new PromoGroupLayer
        {
            Bounds = SKRect.Create(10, 5, 180, 90), Rotation = 5,
            Corners = new(0, 0, -10, 8, -5, -4, 0, 0), In = PromoTransition.None, Out = PromoTransition.None
        };
        var text = new PromoTextLayer
        {
            Id = "title", Bounds = SKRect.Create(0, 10, 200, 80), Lines = ["HELLO"], MaxFontSize = 60,
            Color = SKColors.Red, Rotation = -5, In = PromoTransition.None, Out = PromoTransition.None
        };
        var mask = Rect(10, 0, 70, 90, SKColors.White) with
        {
            Parent = group, Mask = new(GeneratedMaskMode.Subtract, "title"),
            Keyframes =
            [
                new(TimeSpan.Zero, SKRect.Create(10, 0, 70, 90), 1),
                new(TimeSpan.FromSeconds(2), SKRect.Create(100, 0, 70, 90), 1,
                    Corners: new(0, 0, -25, 20, -10, -15, 0, 0))
            ]
        };
        int previousHidden = -1;
        foreach (double second in new[] { 0.0, 1.0, 2.0 })
        {
            using SKBitmap original = Draw(Composition(text), second, background: false);
            using SKBitmap matte = Draw(Composition(group, mask with { Mask = null }), second, background: false);
            using SKBitmap result = Draw(Composition(group, text, mask), second, background: false);
            int hidden = 0, visible = 0;
            for (int y = 0; y < 100; y++)
            for (int x = 0; x < 200; x++)
            {
                int expected = (original.GetPixel(x, y).Alpha * (255 - matte.GetPixel(x, y).Alpha) + 127) / 255;
                Assert.InRange((int)result.GetPixel(x, y).Alpha, Math.Max(0, expected - 1), Math.Min(255, expected + 1));
                if (original.GetPixel(x, y).Alpha > 200 && expected == 0) hidden++;
                if (expected > 200) visible++;
            }
            Assert.True(hidden > 20 && visible > 20, "Only the text behind the moving object should be hidden.");
            Assert.NotEqual(previousHidden, hidden);
            previousHidden = hidden;
        }
    }

    [Fact]
    public void Image_masks_use_alpha_not_brightness()
    {
        string path = Path.Combine(Path.GetTempPath(), $"promo-mask-{Guid.NewGuid():N}.png");
        try
        {
            using (var source = new SKBitmap(200, 100))
            {
                source.Erase(SKColors.Transparent);
                using (var canvas = new SKCanvas(source))
                using (var black = new SKPaint { Color = SKColors.Black })
                using (var white = new SKPaint { Color = SKColors.White.WithAlpha(128) })
                {
                    canvas.DrawRect(0, 0, 60, 100, black);
                    canvas.DrawRect(60, 0, 60, 100, white);
                }
                using SKData png = source.Encode(SKEncodedImageFormat.Png, 100);
                using Stream file = File.Create(path);
                png.SaveTo(file);
            }
            var target = Rect(0, 0, 200, 100, SKColors.Red) with { Id = "target" };
            var mask = new PromoImageLayer
            {
                Path = path, Bounds = target.Bounds, Mask = new(GeneratedMaskMode.Add, "target"),
                In = PromoTransition.None, Out = PromoTransition.None
            };
            using SKBitmap result = Draw(Composition(target, mask));
            Assert.Equal(SKColors.Red, result.GetPixel(30, 50));
            Assert.InRange(result.GetPixel(90, 50).Red, (byte)126, (byte)129);
            Assert.InRange(result.GetPixel(90, 50).Blue, (byte)126, (byte)129);
            Assert.Equal(SKColors.Blue, result.GetPixel(150, 50));
        }
        finally { File.Delete(path); }
    }

    private static SchedulerConfig Config(GeneratedPromoTemplate template) => new()
    {
        Channels = [new() { Id = "channel", Name = "Channel", Number = 1, BaseRotation = new() { Query = "*" },
            GeneratedPromos = new() { Templates = [template] } }]
    };

    private static GeneratedPromoTemplate Template() => new()
    {
        Name = "Roto", Width = 200, Height = 100, BackgroundColor = "#0000FF", Layers =
        [
            new() { Id = "title", Name = "Title", Kind = GeneratedLayerKind.Text, Text = "HELLO", Width = 200, Height = 100 },
            new() { Name = "Object", Kind = GeneratedLayerKind.Rect, X = 50, Width = 100, Height = 100,
                Mask = new() { Target = "title", Mode = GeneratedMaskMode.Subtract }, Corners = new() { TopRightY = 20 } }
        ]
    };

    [Fact]
    public void Masks_round_trip_resolve_and_change_render_keys()
    {
        var template = Template();
        Assert.Empty(SchedulerConfigLoader.Validate(Config(template)).Errors);
        var loaded = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(Config(template))).Channels[0].GeneratedPromos!.Templates[0];
        Assert.Equal("title", loaded.Layers[1].Mask!.Target);
        Assert.Equal(GeneratedMaskMode.Subtract, loaded.Layers[1].Mask!.Mode);
        ResolvedGeneratedPromo promo = Resolve(loaded);
        PromoComposition composition = GeneratedPromoResolver.Compose(promo);
        Assert.Equal("title", composition.Layers[0].Id);
        Assert.Equal(new PromoLayerMask(GeneratedMaskMode.Subtract, "title"), composition.Layers[1].Mask);
        Assert.Equal(20, composition.Layers[1].Corners.TopRightY);
        loaded.Layers[1].Mask!.Mode = GeneratedMaskMode.Add;
        Assert.NotEqual(promo.Key, Resolve(loaded).Key);
    }

    private static ResolvedGeneratedPromo Resolve(GeneratedPromoTemplate template) =>
        GeneratedPromoResolver.Resolve(template, "Channel", [new("Show", Noon)], TimeZoneInfo.Utc, Noon)!;

    [Fact]
    public void A_missing_image_add_mask_does_not_reveal_the_target()
    {
        var template = Template();
        template.Layers[1].Kind = GeneratedLayerKind.Image;
        template.Layers[1].Mask!.Mode = GeneratedMaskMode.Add;
        PromoComposition composition = GeneratedPromoResolver.Compose(Resolve(template));
        Assert.Equal(2, composition.Layers.Count);
        using SKBitmap image = Draw(composition);
        Assert.All(image.Pixels, pixel => Assert.Equal(SKColors.Blue, pixel));
    }

    [Fact]
    public void Duplicate_groups_remap_mask_targets_and_deleted_targets_leave_masks_invisible()
    {
        var template = Template();
        GeneratedLayer group = GeneratedPromoHierarchy.Group(template, [.. template.Layers], TimeSpan.Zero);
        GeneratedLayer copy = GeneratedPromoHierarchy.Duplicate(template, group);
        List<GeneratedLayer> children = GeneratedPromoHierarchy.Children(template, copy);
        Assert.NotEqual("title", children[0].Id);
        Assert.Equal(children[0].Id, children[1].Mask!.Target);
        Assert.Equal("title", template.Layers.First(layer => layer.Name == "Object").Mask!.Target);
        Assert.Empty(SchedulerConfigLoader.Validate(Config(template)).Errors);
        GeneratedPromoHierarchy.Remove(template, children[0]);
        Assert.NotNull(children[1].Mask);
        Assert.Null(children[1].Mask!.Target);
        Assert.Contains(SchedulerConfigLoader.Validate(Config(template)).Errors, error => error.Contains("mask.target"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("self")]
    [InlineData("above")]
    [InlineData("group")]
    [InlineData("mask")]
    [InlineData("wrong-kind")]
    [InlineData("wrong-mode")]
    [InlineData("duplicate-id")]
    public void Invalid_mask_links_are_reported(string problem)
    {
        var template = Template();
        var target = template.Layers[0];
        var mask = template.Layers[1];
        switch (problem)
        {
            case "missing": mask.Mask!.Target = "gone"; break;
            case "self": mask.Id = "self"; mask.Mask!.Target = "self"; break;
            case "above": template.Layers.Reverse(); break;
            case "group": target.Kind = GeneratedLayerKind.Group; break;
            case "mask": target.Mask = new() { Target = "title" }; break;
            case "wrong-kind": mask.Kind = GeneratedLayerKind.Text; break;
            case "wrong-mode": mask.Mask!.Mode = (GeneratedMaskMode)99; break;
            case "duplicate-id": mask.Id = "title"; break;
        }
        Assert.NotEmpty(SchedulerConfigLoader.Validate(Config(template)).Errors);
    }
}
