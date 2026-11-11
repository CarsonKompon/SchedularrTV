using SchedularrTV.Core.Configuration;
using SchedularrTV.Service.Rendering;
using SkiaSharp;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>Text outlines and drop shadows set up their own way, shared by the canvas editors' renderers.</summary>
public sealed class TextEffectsTests
{
    [Fact]
    public void The_usual_shadow_or_none_leaves_text_drawn_as_it_always_was()
    {
        Assert.Null(TextEffects.From(shadow: true, null, outline: null, null, TextShadowSettings.Promo));
        Assert.Null(TextEffects.From(shadow: false, new TextShadowSettings(), outline: false, null, TextShadowSettings.Promo));
    }

    [Fact]
    public void An_outline_keeps_the_usual_shadow_beside_it()
    {
        TextEffects effects = TextEffects.From(shadow: true, null, outline: true, null, TextShadowSettings.Overlay)!;

        Assert.NotNull(effects.OutlineColor);
        Assert.Equal(0.04f, effects.OutlineWidth, 3);
        Assert.Equal(0.05f, effects.ShadowDy, 3);  // the overlays' usual shadow, straight down
        Assert.Equal(0f, effects.ShadowDx, 3);
        Assert.Equal((byte)171, effects.ShadowColor!.Value.Alpha);
    }

    [Fact]
    public void Shadow_settings_place_it_by_angle_and_distance()
    {
        TextEffects effects = TextEffects.From(true, new TextShadowSettings { Angle = 0, Distance = 10, Blur = 0, Color = "#ff0000", Opacity = 100 },
            null, null, TextShadowSettings.Promo)!;

        Assert.Equal(0.1f, effects.ShadowDx, 3);
        Assert.Equal(0f, effects.ShadowDy, 3);
        Assert.Equal(new SKColor(255, 0, 0, 255), effects.ShadowColor);
        Assert.Null(effects.OutlineColor);
    }

    private static int Painted(SKBitmap bitmap, Func<SKColor, bool> wanted)
    {
        int count = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (wanted(bitmap.GetPixel(x, y))) count++;
            }
        }

        return count;
    }

    private static SKBitmap Draw(TextEffects? effects)
    {
        var bitmap = new SKBitmap(400, 300);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var font = new SKFont(SKTypeface.Default, 80);
        if (effects is null)
        {
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            TextFit.Draw(canvas, "Hello", 40, 100, SKTextAlign.Left, font, paint);
        }
        else
        {
            effects.Draw(canvas, [("Hello", 40f, 100f)], SKTextAlign.Left, font, SKColors.White, 0);
        }

        return bitmap;
    }

    [Fact]
    public void The_outline_goes_round_the_letters_and_the_shadow_falls_from_it_too()
    {
        using SKBitmap plain = Draw(null);
        using SKBitmap outlined = Draw(TextEffects.From(false, null, true, new TextOutlineSettings { Width = 6, Color = "#ff0000" }, TextShadowSettings.Promo));
        // Cast a whole text height down, clear of the letters, so all of it shows.
        using SKBitmap shadowed = Draw(TextEffects.From(true, new TextShadowSettings { Angle = 90, Distance = 100, Blur = 0, Opacity = 100, Color = "#0000ff" },
            true, new TextOutlineSettings { Width = 6, Color = "#ff0000" }, TextShadowSettings.Promo));

        int letters = Painted(plain, colour => colour.Alpha > 128);
        int outline = Painted(outlined, colour => colour.Alpha > 128 && colour.Red > 200 && colour.Green < 80);
        Assert.True(outline > letters / 4, "no outline round the letters");
        // The fill is still on top of it.
        Assert.True(Painted(outlined, colour => colour.Alpha > 200 && colour.Red > 200 && colour.Green > 200) > letters / 2);

        // The shadow is the shape of the letters and their outline together: bigger than the letters alone.
        int shadow = Painted(shadowed, colour => colour.Alpha > 128 && colour.Blue > 200 && colour.Red < 80);
        Assert.True(shadow > letters, $"the shadow ({shadow}) is no bigger than the letters ({letters}), so it left the outline out");
    }
}
