using System.Text.Json;
using SchedularrTV.Core.Configuration;
using SkiaSharp;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class GeneratedPromoShapeTests
{
    [Theory]
    [InlineData(GeneratedShape.Rectangle)]
    [InlineData(GeneratedShape.Ellipse)]
    [InlineData(GeneratedShape.Triangle)]
    [InlineData(GeneratedShape.Diamond)]
    [InlineData(GeneratedShape.Star)]
    public void Shapes_have_a_filled_centre_and_distinct_outlines(GeneratedShape shape)
    {
        using var bitmap = Render(new PromoRectLayer { Bounds = SKRect.Create(40, 40, 120, 120), Shape = shape });
        Assert.Equal(255, bitmap.GetPixel(100, 100).Alpha);
        Assert.Equal(0, bitmap.GetPixel(20, 20).Alpha);
        Assert.Equal(shape == GeneratedShape.Rectangle ? 255 : 0, bitmap.GetPixel(45, 45).Alpha);
    }

    [Fact]
    public void Rotation_turns_about_the_centre()
    {
        var layer = new PromoRectLayer { Bounds = SKRect.Create(40, 80, 120, 40) };
        using var original = Render(layer);
        using var rotated = Render(layer with { Rotation = 90 });
        Assert.Equal(255, original.GetPixel(50, 100).Alpha);
        Assert.Equal(0, rotated.GetPixel(50, 100).Alpha);
        Assert.Equal(0, original.GetPixel(100, 50).Alpha);
        Assert.Equal(255, rotated.GetPixel(100, 50).Alpha);
    }

    [Fact]
    public void Soft_edges_feather_outside_the_box_even_with_layer_opacity()
    {
        var layer = new PromoRectLayer { Bounds = SKRect.Create(70, 70, 60, 60), Opacity = 0.5f };
        using var sharp = Render(layer);
        using var soft = Render(layer with { SoftEdges = 10 });
        Assert.Equal(0, sharp.GetPixel(60, 100).Alpha);
        Assert.InRange(soft.GetPixel(60, 100).Alpha, 1, 127);
        Assert.InRange(soft.GetPixel(100, 100).Alpha, 120, 128);
    }

    [Fact]
    public void Shape_settings_reach_the_composition()
    {
        var template = GeneratedPromoTemplates.UpNext();
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        var panel = template.Layers[0];
        panel.Shape = GeneratedShape.Diamond;
        panel.Rotation = 25;
        panel.SoftEdges = 12;
        var promo = GeneratedPromoResolver.Resolve(template, "Channel", [new UpNextListing("Show", now)], TimeZoneInfo.Utc, now)!;
        var shape = Assert.IsType<PromoRectLayer>(GeneratedPromoResolver.Compose(promo).Layers[0]);
        Assert.Equal(GeneratedShape.Diamond, shape.Shape);
        Assert.Equal(25, shape.Rotation);
        Assert.Equal(12, shape.SoftEdges);
    }

    [Fact]
    public void Legacy_rectangles_omit_new_properties_and_shapes_round_trip()
    {
        var legacy = new GeneratedLayer { Kind = GeneratedLayerKind.Rect };
        string json = JsonSerializer.Serialize(legacy);
        Assert.DoesNotContain("\"Shape\"", json);
        Assert.DoesNotContain("\"Rotation\"", json);
        Assert.DoesNotContain("\"SoftEdges\"", json);
        legacy.Shape = GeneratedShape.Star;
        legacy.Rotation = 35;
        legacy.SoftEdges = 8;
        var copy = JsonSerializer.Deserialize<GeneratedLayer>(JsonSerializer.Serialize(legacy))!;
        Assert.Equal(legacy.Shape, copy.Shape);
        Assert.Equal(legacy.Rotation, copy.Rotation);
        Assert.Equal(legacy.SoftEdges, copy.SoftEdges);
    }

    private static SKBitmap Render(PromoRectLayer layer)
    {
        var bitmap = new SKBitmap(200, 200);
        using var canvas = new SKCanvas(bitmap);
        using var images = new PromoImageCache();
        PromoRenderer.DrawFrame(canvas, new PromoComposition
        {
            Width = 200, Height = 200, Duration = TimeSpan.FromSeconds(5),
            Layers = [layer with { In = PromoTransition.None, Out = PromoTransition.None }]
        }, TimeSpan.FromSeconds(1), images, drawBackground: false);
        return bitmap;
    }
}
