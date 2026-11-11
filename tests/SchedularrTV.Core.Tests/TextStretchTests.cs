using SchedularrTV.Service.Rendering;
using SkiaSharp;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>Squashing and stretching text's letters (thinner, wider, shorter, taller) in the canvas editors.</summary>
public sealed class TextStretchTests
{
    [Fact]
    public void Settings_read_as_multiples_and_nonsense_is_as_the_font_has_it()
    {
        Assert.Equal(1f, TextFit.Stretch(null));
        Assert.Equal(1f, TextFit.Stretch(float.NaN));
        Assert.Equal(1f, TextFit.Stretch(0));
        Assert.Equal(1.5f, TextFit.Stretch(150), 3);
        Assert.Equal(0.1f, TextFit.Stretch(1), 3);
        Assert.Equal(4f, TextFit.Stretch(1000), 3);
    }

    [Fact]
    public void Wider_letters_measure_wider_and_taller_ones_keep_their_width()
    {
        static float Width(float across, float up)
        {
            using var font = new SKFont(SKTypeface.Default, 40);
            TextFit.Stretch(font, across, up);
            return font.MeasureText("HHHH");
        }

        float plain = Width(1, 1);
        Assert.Equal(plain * 2, Width(2, 1), plain * 0.05f);
        Assert.Equal(plain * 0.5f, Width(0.5f, 1), plain * 0.05f);
        // Twice as tall but as wide as before.
        Assert.Equal(plain, Width(1, 2), plain * 0.05f);
    }

    [Fact]
    public void Taller_letters_grow_the_font_so_lines_are_spaced_to_match()
    {
        using var font = new SKFont(SKTypeface.Default, 40);
        TextFit.Stretch(font, 1, 1.5f);

        Assert.Equal(60, font.Size, 3);
        Assert.Equal(1 / 1.5f, font.ScaleX, 3);
    }
}
