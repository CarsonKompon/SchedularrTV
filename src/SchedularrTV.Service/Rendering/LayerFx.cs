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
    // Where a softened edge is cut to make an outline: Φ(-2), so with a blur of half the width the edge lands that far out.
    private const float EdgeLevel = 0.0228f;

    // How steeply alpha rises across the softened edge at that point, per pixel of blur: 1 / φ(2).
    private const float EdgeSteepness = 18.5f;

    /// <summary>
    /// Starts drawing a layer with its effects: everything drawn until the matching <see cref="SKCanvas.Restore"/> has them
    /// applied. Returns false (and saves nothing) when the layer has none switched on.
    /// </summary>
    /// <param name="bounds">The layer's box in the canvas's current space; null for a whole-canvas layer (a group's contents).</param>
    /// <param name="scale">Multiplies every size, for a design drawn bigger or smaller than it was laid out.</param>
    /// <param name="turned">How far the canvas is turned here (the layer's turn and its groups'), so shadows still fall the way they're set on screen.</param>
    public static bool Begin(SKCanvas canvas, IReadOnlyList<LayerEffect>? effects, SKRect? bounds, float scale = 1, float turned = 0)
    {
        using SKImageFilter? filter = Filter(effects, scale, turned);
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
    public static SKImageFilter? Filter(IReadOnlyList<LayerEffect>? effects, float scale = 1, float turned = 0)
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
                    LayerEffectKind.Outline => Outline(effect, current, scale, made),
                    LayerEffectKind.Shadow => Shadow(effect, current, scale, turned),
                    LayerEffectKind.Glow => Glow(effect, current, scale),
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
                _ => Join("g", effect.Color, effect.Opacity, effect.Blur)
            }))
            : null;

    private static string Join(string kind, string? colour, params double[] values) =>
        kind + ":" + (colour ?? string.Empty).ToLowerInvariant() + "," + string.Join(',', values.Select(value => value.ToString("0.###", CultureInfo.InvariantCulture)));

    // An edge all the way round: what's there softened by half the width, cut sharply where that edge reaches the full
    // width out (which rounds corners, as a pen would), coloured, and put underneath.
    private static SKImageFilter? Outline(LayerEffect effect, SKImageFilter? input, float scale, List<SKImageFilter> made)
    {
        float width = (float)Size(effect.Width, 200) * scale;
        if (width < 0.05f)
        {
            return null;
        }

        float sigma = Math.Max(0.25f, width / 2);
        float steep = Math.Clamp(EdgeSteepness * sigma, 2, 400);
        SKColor colour = TextEffects.Colour(effect.Color, 100);
        float[] cut =
        [
            0, 0, 0, 0, colour.Red / 255f,
            0, 0, 0, 0, colour.Green / 255f,
            0, 0, 0, 0, colour.Blue / 255f,
            0, 0, 0, steep, 0.5f - steep * EdgeLevel
        ];
        float[] fade =
        [
            1, 0, 0, 0, 0,
            0, 1, 0, 0, 0,
            0, 0, 1, 0, 0,
            0, 0, 0, (float)(Math.Clamp(effect.Opacity, 0, 100) / 100), 0
        ];

        SKImageFilter soft = SKImageFilter.CreateBlur(sigma, sigma, input);
        made.Add(soft);
        using SKColorFilter cutting = SKColorFilter.CreateColorMatrix(cut);
        using SKColorFilter fading = SKColorFilter.CreateColorMatrix(fade);
        // Two steps, so the cut is clamped before it's faded.
        using SKColorFilter both = SKColorFilter.CreateCompose(fading, cutting);
        SKImageFilter edge = SKImageFilter.CreateColorFilter(both, soft);
        made.Add(edge);
        return SKImageFilter.CreateBlendMode(SKBlendMode.SrcOver, edge, input);
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

    private static SKImageFilter Glow(LayerEffect effect, SKImageFilter? input, float scale)
    {
        float sigma = Math.Max(0.5f, (float)Size(effect.Blur, 200) * scale);
        return SKImageFilter.CreateDropShadow(0, 0, sigma, sigma, TextEffects.Colour(effect.Color, effect.Opacity), input);
    }

    private static double Size(double value, double most) => double.IsFinite(value) ? Math.Clamp(value, 0, most) : 0;
}
