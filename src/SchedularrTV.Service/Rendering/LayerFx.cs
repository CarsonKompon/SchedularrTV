using System.Globalization;
using SchedularrTV.Core.Configuration;
using SkiaSharp;

namespace SchedularrTV.Service.Rendering;

/// <summary>
/// Draws a layer's effects (outlines, drop shadows, glows), shared by generated promos, overlays and credits squeeze. The
/// layer is drawn into a layer of its own, and the effects are applied to it in order as one chain of image filters: each
/// works on what the ones before it produced, so an outline of an outline, or a shadow cast by both, comes out as expected.
/// </summary>
public static class LayerFx
{
    // Outlines narrower than this (in screen pixels) are grown as a square; wider ones as a rounder shape.
    private const float RoundFrom = 3;

    /// <summary>
    /// Starts drawing a layer with its effects: everything drawn until the matching <see cref="SKCanvas.Restore"/> has them
    /// applied. Returns false (and saves nothing) when the layer has none switched on.
    /// </summary>
    /// <param name="bounds">The layer's box in the canvas's current space; null for a whole-canvas layer (a group's contents).</param>
    /// <param name="scale">Multiplies every size, for a design drawn bigger or smaller than it was laid out.</param>
    /// <param name="turned">How far the canvas is turned here (the layer's turn and its groups'), so shadows still fall the way they're set on screen.</param>
    public static bool Begin(SKCanvas canvas, IReadOnlyList<LayerEffect>? effects, SKRect? bounds, float scale = 1, float turned = 0)
    {
        // How many of the canvas's real pixels one of its units covers here, so outlines are sized on whole screen pixels.
        SKMatrix matrix = canvas.TotalMatrix;
        float pixels = (float)Math.Sqrt(Math.Abs(matrix.ScaleX * matrix.ScaleY - matrix.SkewX * matrix.SkewY));
        using SKImageFilter? filter = Filter(effects, scale, turned, float.IsFinite(pixels) && pixels > 0.0001f ? pixels : 1);
        if (filter is null)
        {
            return false;
        }

        // The layer keeps the paint (and with it the filter), so these can go once it's saved.
        using var paint = new SKPaint { ImageFilter = filter };
        if (bounds is { } box)
        {
            float reach = Reach(effects, scale);
            canvas.SaveLayer(SKRect.Inflate(box, reach + box.Height * 0.25f + 8, reach + box.Height * 0.25f + 8), paint);
        }
        else
        {
            canvas.SaveLayer(paint);
        }

        return true;
    }

    /// <summary>The effects as one image filter, or null when none is switched on.</summary>
    /// <param name="pixels">Screen pixels per canvas unit where it's drawn (see <see cref="Begin"/>).</param>
    public static SKImageFilter? Filter(IReadOnlyList<LayerEffect>? effects, float scale = 1, float turned = 0, float pixels = 1)
    {
        if (!LayerEffects.Any(effects))
        {
            return null;
        }

        SKImageFilter? current = null;
        List<SKImageFilter> made = [];
        try
        {
            foreach (LayerEffect effect in effects!.Where(effect => effect.IsOn))
            {
                SKImageFilter? next = effect.Kind switch
                {
                    LayerEffectKind.Outline => Outline(effect, current, scale, pixels, made),
                    LayerEffectKind.Shadow => Shadow(effect, current, scale, turned),
                    LayerEffectKind.Glow => Glow(effect, current, scale),
                    LayerEffectKind.Emboss => Emboss(effect, current, scale, turned, made),
                    _ => null
                };
                if (next is not null)
                {
                    if (current is not null) made.Add(current);
                    current = next;
                }
            }

            return current;
        }
        finally
        {
            // Each filter holds on to the ones it's built from, so only the finished chain needs keeping.
            foreach (SKImageFilter piece in made) piece.Dispose();
        }
    }

    /// <summary>How far past what's drawn the effects can reach, in the canvas's pixels.</summary>
    public static float Reach(IReadOnlyList<LayerEffect>? effects, float scale = 1)
    {
        double reach = 0;
        foreach (LayerEffect effect in effects?.Where(effect => effect.IsOn) ?? [])
        {
            reach += effect.Kind switch
            {
                LayerEffectKind.Outline => Size(effect.Width, 200) + 2,
                LayerEffectKind.Shadow => Size(effect.Distance, 500) + 3 * Size(effect.Blur, 200) + 2,
                // Inside what's there: it reaches no further out.
                LayerEffectKind.Emboss => 0,
                _ => 3 * Size(effect.Blur, 200) + 2
            };
        }

        return (float)reach * scale;
    }

    /// <summary>A short, stable description of the switched-on effects, for the hashes drawings are kept under; null when there are none.</summary>
    public static string? Key(IReadOnlyList<LayerEffect>? effects) =>
        LayerEffects.Any(effects)
            ? string.Join(';', effects!.Where(effect => effect.IsOn).Select(effect => effect.Kind switch
            {
                LayerEffectKind.Outline => Join("o", effect.Color, effect.Opacity, effect.Width),
                LayerEffectKind.Shadow => Join("s", effect.Color, effect.Opacity, effect.Distance, effect.Angle, effect.Blur),
                LayerEffectKind.Emboss => Join("e", $"{effect.Color}/{effect.ShadeColor}", effect.Opacity, effect.Width, effect.Distance, effect.Angle),
                _ => Join("g", effect.Color, effect.Opacity, effect.Blur)
            }))
            : null;

    private static string Join(string kind, string? colour, params double[] values) =>
        kind + ":" + (colour ?? string.Empty).ToLowerInvariant() + "," + string.Join(',', values.Select(value => value.ToString("0.###", CultureInfo.InvariantCulture)));

    // An edge all the way round: what's there grown outwards by the width (each pixel takes the most covered pixel within
    // reach, so its own anti-aliased edge just moves out with it), coloured, and put underneath.
    private static SKImageFilter? Outline(LayerEffect effect, SKImageFilter? input, float scale, float pixels, List<SKImageFilter> made)
    {
        // Worked out in screen pixels, where growing happens a whole pixel at a time.
        float reach = (float)Size(effect.Width, 200) * scale * pixels;
        if (reach < 0.05f)
        {
            return null;
        }

        SKImageFilter grown = Grow(input, reach, pixels, made);
        SKColor colour = TextEffects.Colour(effect.Color, 100);
        float[] tint =
        [
            0, 0, 0, 0, colour.Red / 255f,
            0, 0, 0, 0, colour.Green / 255f,
            0, 0, 0, 0, colour.Blue / 255f,
            0, 0, 0, (float)(Math.Clamp(effect.Opacity, 0, 100) / 100), 0
        ];

        // Nothing stays nothing (alpha 0 times anything is 0), so it never spreads past what was grown.
        using SKColorFilter tinting = SKColorFilter.CreateColorMatrix(tint);
        SKImageFilter edge = SKImageFilter.CreateColorFilter(tinting, grown);
        made.Add(edge);
        return SKImageFilter.CreateBlendMode(SKBlendMode.SrcOver, edge, input);
    }

    /// <summary>
    /// What's there grown outwards by <paramref name="reach"/> screen pixels. Narrow reaches grow as a square, a part pixel
    /// shown by fading between the whole pixels either side (so a 1.5px outline has a soft half-covered last row, and a
    /// 0.5px one on a small preview still shows). Wider ones grow as overlapping rectangles whose corners sit on a circle,
    /// rounding the corners as a pen would.
    /// </summary>
    private static SKImageFilter Grow(SKImageFilter? input, float reach, float pixels, List<SKImageFilter> made)
    {
        // The filter's sizes are in canvas units; a hair over the whole pixels, so they never round down a pixel.
        SKImageFilter Dilate(float across, float down)
        {
            SKImageFilter grown = SKImageFilter.CreateDilate((across + 0.02f) / pixels, (down + 0.02f) / pixels, input);
            made.Add(grown);
            return grown;
        }

        if (reach < RoundFrom)
        {
            int whole = (int)MathF.Floor(reach);
            float part = reach - whole;
            if (part < 0.01f)
            {
                return Dilate(whole, whole);
            }

            SKImageFilter more = Dilate(whole + 1, whole + 1);
            if (part > 0.99f)
            {
                return more;
            }

            // Part way between the two: whole pixels (nothing grown at all, below one) and one more.
            // (Null is what's there, ungrown.)
            SKImageFilter? less = whole == 0 ? input : Dilate(whole, whole);
            SKImageFilter mixed = SKImageFilter.CreateArithmetic(0, part, 1 - part, 0, true, less, more);
            made.Add(mixed);
            return mixed;
        }

        double[] angles = reach < 10 ? [22.5, 67.5] : [11.25, 33.75, 56.25, 78.75];
        // Sized so the outline is exactly the width straight out from flat edges.
        double radius = reach / Math.Cos(angles[0] * Math.PI / 180);
        SKImageFilter? union = null;
        foreach (double angle in angles)
        {
            double radians = angle * Math.PI / 180;
            SKImageFilter piece = Dilate((float)Math.Round(radius * Math.Cos(radians)), (float)Math.Round(radius * Math.Sin(radians)));
            if (union is null)
            {
                union = piece;
                continue;
            }

            // Only their coverage matters (the colour is replaced next), and Lighten's coverage is the two together.
            union = SKImageFilter.CreateBlendMode(SKBlendMode.Lighten, union, piece);
            made.Add(union);
        }

        return union!;
    }

    private static SKImageFilter Shadow(LayerEffect effect, SKImageFilter? input, float scale, float turned)
    {
        double distance = Size(effect.Distance, 500) * scale;
        // Set on screen: undo the canvas's turn here, so a turning layer's shadow keeps falling the same way.
        double radians = (effect.Angle - turned) * Math.PI / 180;
        float sigma = (float)Size(effect.Blur, 200) * scale;
        return SKImageFilter.CreateDropShadow((float)(distance * Math.Cos(radians)), (float)(distance * Math.Sin(radians)), sigma, sigma,
            TextEffects.Colour(effect.Color, effect.Opacity), input);
    }

    // How strongly an emboss's slopes light up: the softened edge's change over the depth, made bright enough to read.
    private const float EmbossGain = 3f;

    /// <summary>
    /// A bevel inside what's there: its edges softened (by half the size) into a slope, and wherever the slope faces the
    /// light it's lit in <see cref="LayerEffect.Color"/>, and wherever it faces away it's shaded in
    /// <see cref="LayerEffect.ShadeColor"/>, both kept within the shape and laid over it.
    /// </summary>
    private static SKImageFilter? Emboss(LayerEffect effect, SKImageFilter? input, float scale, float turned, List<SKImageFilter> made)
    {
        float size = (float)Size(effect.Width, 200) * scale;
        float depth = Math.Max(0.5f * scale, (float)Size(effect.Distance, 200) * scale);
        float opacity = (float)(Math.Clamp(effect.Opacity, 0, 100) / 100);
        if (opacity <= 0)
        {
            return null;
        }

        SKImageFilter Keep(SKImageFilter filter)
        {
            made.Add(filter);
            return filter;
        }

        float sigma = Math.Max(0.3f, size / 2);
        // Towards the light, set on screen (the canvas's turn here undone, as for shadows).
        double radians = (effect.Angle - turned) * Math.PI / 180;
        float towardX = (float)(Math.Cos(radians) * depth);
        float towardY = (float)(Math.Sin(radians) * depth);

        SKImageFilter slope = Keep(SKImageFilter.CreateBlur(sigma, sigma, input));
        // The slope a step nearer the light, and a step further from it (an offset moves the picture, so the reverse way).
        SKImageFilter nearer = Keep(SKImageFilter.CreateOffset(-towardX, -towardY, slope));
        SKImageFilter further = Keep(SKImageFilter.CreateOffset(towardX, towardY, slope));
        // Lit where the shape falls away towards the light; shaded where it falls away from it.
        SKImageFilter lit = Keep(SKImageFilter.CreateArithmetic(0, EmbossGain, -EmbossGain, 0, true, nearer, slope));
        SKImageFilter shaded = Keep(SKImageFilter.CreateArithmetic(0, EmbossGain, -EmbossGain, 0, true, further, slope));

        SKImageFilter Tinted(SKImageFilter coverage, string? colour, string fallback)
        {
            SKColor tint = TextEffects.Colour(string.IsNullOrWhiteSpace(colour) ? fallback : colour, 100);
            float[] matrix =
            [
                0, 0, 0, 0, tint.Red / 255f,
                0, 0, 0, 0, tint.Green / 255f,
                0, 0, 0, 0, tint.Blue / 255f,
                0, 0, 0, opacity, 0
            ];
            using SKColorFilter tinting = SKColorFilter.CreateColorMatrix(matrix);
            SKImageFilter tinted = Keep(SKImageFilter.CreateColorFilter(tinting, coverage));
            // Only within the shape itself.
            return Keep(SKImageFilter.CreateBlendMode(SKBlendMode.SrcIn, input, tinted));
        }

        SKImageFilter litOver = Keep(SKImageFilter.CreateBlendMode(SKBlendMode.SrcOver, input, Tinted(lit, effect.Color, "#ffffff")));
        return SKImageFilter.CreateBlendMode(SKBlendMode.SrcOver, litOver, Tinted(shaded, effect.ShadeColor, "#000000"));
    }

    private static SKImageFilter Glow(LayerEffect effect, SKImageFilter? input, float scale)
    {
        float sigma = Math.Max(0.5f, (float)Size(effect.Blur, 200) * scale);
        return SKImageFilter.CreateDropShadow(0, 0, sigma, sigma, TextEffects.Colour(effect.Color, effect.Opacity), input);
    }

    private static double Size(double value, double most) => double.IsFinite(value) ? Math.Clamp(value, 0, most) : 0;
}
