using SchedularrTV.Core.Configuration;
using SchedularrTV.Service.Rendering;
using SkiaSharp;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>Layer FX: outlines, shadows and glows stacked in order on any canvas layer.</summary>
public sealed class LayerFxTests
{
    private static readonly SKRect Square = SKRect.Create(50, 50, 20, 20);

    // A white square drawn with the effects, on a clear 120 × 140 canvas.
    private static SKBitmap Draw(params LayerEffect[] effects)
    {
        var bitmap = new SKBitmap(120, 140);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        bool fx = LayerFx.Begin(canvas, effects, Square);
        using (var paint = new SKPaint { Color = SKColors.White })
        {
            canvas.DrawRect(Square, paint);
        }

        if (fx) canvas.Restore();
        canvas.Flush();
        return bitmap;
    }

    private static LayerEffect Outline(string colour, double width) => new() { Kind = LayerEffectKind.Outline, Color = colour, Width = width };

    private static bool Is(SKColor pixel, byte red, byte green, byte blue) =>
        pixel.Alpha > 200 && Math.Abs(pixel.Red - red) < 60 && Math.Abs(pixel.Green - green) < 60 && Math.Abs(pixel.Blue - blue) < 60;

    [Fact]
    public void An_outline_goes_round_the_layer_under_it()
    {
        using SKBitmap drawn = Draw(Outline("#ff0000", 6));

        Assert.True(Is(drawn.GetPixel(60, 60), 255, 255, 255), "the layer is still on top");
        Assert.True(Is(drawn.GetPixel(60, 47), 255, 0, 0), "no outline 3px out");
        Assert.Equal(0, drawn.GetPixel(60, 40).Alpha);
    }

    [Fact]
    public void A_second_outline_goes_round_the_first()
    {
        using SKBitmap drawn = Draw(Outline("#ff0000", 4), Outline("#00ff00", 4));

        Assert.True(Is(drawn.GetPixel(60, 48), 255, 0, 0), "the first outline is inside");
        Assert.True(Is(drawn.GetPixel(60, 44), 0, 255, 0), "the second goes round it");
        Assert.Equal(0, drawn.GetPixel(60, 39).Alpha);
    }

    [Fact]
    public void A_shadow_after_an_outline_is_cast_by_both_and_switched_off_effects_do_nothing()
    {
        var shadow = new LayerEffect { Kind = LayerEffectKind.Shadow, Color = "#0000ff", Opacity = 100, Distance = 40, Angle = 90, Blur = 0 };
        using SKBitmap drawn = Draw(Outline("#ff0000", 4), shadow);

        Assert.True(Is(drawn.GetPixel(60, 100), 0, 0, 255), "no shadow of the layer");
        Assert.True(Is(drawn.GetPixel(60, 112), 0, 0, 255), "the outline casts no shadow");

        shadow.Hidden = true;
        using SKBitmap hidden = Draw(Outline("#ff0000", 4), shadow);
        Assert.Equal(0, hidden.GetPixel(60, 100).Alpha);
    }

    [Fact]
    public void A_turned_layer_s_shadow_still_falls_the_way_it_is_set_on_screen()
    {
        var down = new LayerEffect { Kind = LayerEffectKind.Shadow, Color = "#0000ff", Opacity = 100, Distance = 30, Angle = 90, Blur = 0 };
        using var bitmap = new SKBitmap(120, 140);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            // A quarter turn about the square's middle: it stays where it is, but the canvas's own "down" is now the screen's left.
            canvas.RotateDegrees(90, Square.MidX, Square.MidY);
            bool fx = LayerFx.Begin(canvas, [down], Square, turned: 90);
            using (var paint = new SKPaint { Color = SKColors.White })
            {
                canvas.DrawRect(Square, paint);
            }

            if (fx) canvas.Restore();
        }

        Assert.True(Is(bitmap.GetPixel(60, 90), 0, 0, 255), "the shadow doesn't fall down the screen");
        Assert.Equal(0, bitmap.GetPixel(30, 60).Alpha);
    }

    [Fact]
    public void Nothing_switched_on_is_no_effect_at_all()
    {
        Assert.Null(LayerFx.Filter(null));
        Assert.Null(LayerFx.Filter([new LayerEffect { Kind = LayerEffectKind.Glow, Hidden = true }]));
        Assert.Null(LayerFx.Key([]));
        Assert.Equal(0, LayerFx.Reach(null));
        Assert.False(LayerEffects.Any([new LayerEffect { Hidden = true }]));
    }

    [Fact]
    public void Text_s_older_outline_and_shadow_read_as_the_same_effects_in_pixels()
    {
        List<LayerEffect> effects = LayerEffects.FromText(true, null, true, new TextOutlineSettings { Width = 4, Color = "#ff0000" },
            TextShadowSettings.Promo, fontSize: 50);

        Assert.Equal(new[] { LayerEffectKind.Outline, LayerEffectKind.Shadow }, effects.Select(effect => effect.Kind));
        Assert.Equal(2, effects[0].Width);
        Assert.Equal("#ff0000", effects[0].Color);
        Assert.Equal((63.0, 2.0, 2.5, 90.0), (effects[1].Opacity, effects[1].Distance, effects[1].Blur, effects[1].Angle));
        Assert.Empty(LayerEffects.FromText(false, null, null, null, TextShadowSettings.Promo, 50));
    }

    [Fact]
    public void Scaling_effects_scales_their_sizes_and_leaves_out_switched_off_ones()
    {
        List<LayerEffect> scaled = LayerEffects.Scaled([Outline("#000000", 4), new LayerEffect { Kind = LayerEffectKind.Glow, Hidden = true }], 0.5)!;

        Assert.Single(scaled);
        Assert.Equal(2, scaled[0].Width);
    }
}
