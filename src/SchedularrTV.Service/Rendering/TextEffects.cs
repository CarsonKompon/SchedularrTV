using System.Globalization;
using SchedularrTV.Core.Configuration;
using SkiaSharp;

namespace SchedularrTV.Service.Rendering;

/// <summary>
/// A text layer's outline and drop shadow as they're drawn, shared by generated promos, overlays and credits squeeze.
/// Sizes are fractions of the font size. The outline goes round the letters first, and the shadow is cast by the letters
/// and their outline together.
/// </summary>
public sealed record TextEffects(SKColor? OutlineColor, float OutlineWidth, SKColor? ShadowColor, float ShadowDx, float ShadowDy, float ShadowBlur)
{
    /// <summary>
    /// The effects a layer asks for, or null when it asks for nothing the renderers didn't already draw (a shadow left as it
    /// always was, no outline), so those layers keep drawing exactly as before.
    /// </summary>
    /// <param name="usual">The shadow this kind of layer has always had, used for a shadow left unchanged beside an outline.</param>
    public static TextEffects? From(bool shadow, TextShadowSettings? shadowStyle, bool? outline, TextOutlineSettings? outlineStyle, TextShadowSettings usual)
    {
        bool outlined = outline == true;
        if (!outlined && (!shadow || shadowStyle is null))
        {
            return null;
        }

        SKColor? outlineColor = null;
        float outlineWidth = 0;
        if (outlined)
        {
            TextOutlineSettings style = outlineStyle ?? new TextOutlineSettings();
            outlineWidth = (float)Math.Clamp(style.Width, 0, 50) / 100;
            outlineColor = outlineWidth > 0 ? Colour(style.Color, style.Opacity) : null;
        }

        SKColor? shadowColor = null;
        float dx = 0, dy = 0, blur = 0;
        if (shadow)
        {
            TextShadowSettings style = shadowStyle ?? usual;
            double distance = Math.Clamp(style.Distance, 0, 100) / 100;
            double radians = style.Angle * Math.PI / 180;
            dx = (float)(distance * Math.Cos(radians));
            dy = (float)(distance * Math.Sin(radians));
            blur = (float)Math.Clamp(style.Blur, 0, 100) / 100;
            shadowColor = Colour(style.Color, style.Opacity);
        }

        return new TextEffects(outlineColor, outlineWidth, shadowColor, dx, dy, blur);
    }

    /// <summary>How far the outline reaches past the letters at <paramref name="fontSize"/>, kept clear inside the box.</summary>
    public float Margin(float fontSize) => OutlineColor is null ? 0 : OutlineWidth * fontSize;

    /// <summary>Draws the lines (each at its own position) with the outline and shadow, in <paramref name="fill"/>'s colour.</summary>
    public void Draw(SKCanvas canvas, IReadOnlyList<(string Line, float X, float Y)> lines, SKTextAlign align, SKFont font, SKColor fill, float letterSpacing)
    {
        float size = font.Size;
        using SKImageFilter? shadow = ShadowColor is { } shade
            ? SKImageFilter.CreateDropShadow(ShadowDx * size, ShadowDy * size, ShadowBlur * size, ShadowBlur * size, shade)
            : null;

        // Drawn into a layer of their own, so the shadow is cast by the letters and their outline as one.
        using var layer = new SKPaint { ImageFilter = shadow };
        if (shadow is not null)
        {
            canvas.SaveLayer(layer);
        }

        if (OutlineColor is { } edge)
        {
            // A stroke is centred on the letters' edge: twice the width puts that much outside them, under the fill.
            using var stroke = new SKPaint
            {
                Color = edge, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 * OutlineWidth * size,
                StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round
            };
            foreach ((string line, float x, float y) in lines)
            {
                TextFit.Draw(canvas, line, x, y, align, font, stroke, letterSpacing);
            }
        }

        using var paint = new SKPaint { Color = fill, IsAntialias = true };
        foreach ((string line, float x, float y) in lines)
        {
            TextFit.Draw(canvas, line, x, y, align, font, paint, letterSpacing);
        }

        if (shadow is not null)
        {
            canvas.Restore();
        }
    }

    /// <summary>A #rrggbb colour at an opacity (0 to 100); black when it doesn't read.</summary>
    public static SKColor Colour(string? hex, double opacity)
    {
        SKColor colour = SKColor.TryParse(hex ?? string.Empty, out SKColor parsed) ? parsed : SKColors.Black;
        return colour.WithAlpha((byte)Math.Round(Math.Clamp(opacity, 0, 100) * 2.55));
    }

    /// <summary>A short, stable description, for the hashes drawings are kept under.</summary>
    public string Key() => string.Join(',', new object?[] { OutlineColor, OutlineWidth, ShadowColor, ShadowDx, ShadowDy, ShadowBlur }
        .Select(value => Convert.ToString(value, CultureInfo.InvariantCulture)));
}
