using SkiaSharp;
using SchedularrTV.Core.Configuration;

namespace SchedularrTV.GeneratedPromos;

/// <summary>
/// A corner pin: how far each corner of a box is pulled from where it would be, in the box's own (unturned) pixels.
/// All zeros is a plain box.
/// </summary>
public readonly record struct PromoCorners(
    float TopLeftX, float TopLeftY, float TopRightX, float TopRightY,
    float BottomRightX, float BottomRightY, float BottomLeftX, float BottomLeftY)
{
    public bool IsSquare =>
        Math.Abs(TopLeftX) < 0.01f && Math.Abs(TopLeftY) < 0.01f && Math.Abs(TopRightX) < 0.01f && Math.Abs(TopRightY) < 0.01f
        && Math.Abs(BottomRightX) < 0.01f && Math.Abs(BottomRightY) < 0.01f && Math.Abs(BottomLeftX) < 0.01f && Math.Abs(BottomLeftY) < 0.01f;

    public static PromoCorners From(GeneratedCorners? corners) => corners is null || !corners.IsFinite ? default : new(
        corners.TopLeftX, corners.TopLeftY, corners.TopRightX, corners.TopRightY,
        corners.BottomRightX, corners.BottomRightY, corners.BottomLeftX, corners.BottomLeftY);

    /// <summary>For keeping in a template: null when square, so templates without a pin keep their render keys.</summary>
    public GeneratedCorners? ToConfig() => IsSquare ? null : new GeneratedCorners
    {
        TopLeftX = Round(TopLeftX), TopLeftY = Round(TopLeftY), TopRightX = Round(TopRightX), TopRightY = Round(TopRightY),
        BottomRightX = Round(BottomRightX), BottomRightY = Round(BottomRightY), BottomLeftX = Round(BottomLeftX), BottomLeftY = Round(BottomLeftY)
    };

    private static float Round(float value) => MathF.Round(value, 1);

    public static PromoCorners Lerp(PromoCorners from, PromoCorners to, float progress) => new(
        from.TopLeftX + (to.TopLeftX - from.TopLeftX) * progress, from.TopLeftY + (to.TopLeftY - from.TopLeftY) * progress,
        from.TopRightX + (to.TopRightX - from.TopRightX) * progress, from.TopRightY + (to.TopRightY - from.TopRightY) * progress,
        from.BottomRightX + (to.BottomRightX - from.BottomRightX) * progress, from.BottomRightY + (to.BottomRightY - from.BottomRightY) * progress,
        from.BottomLeftX + (to.BottomLeftX - from.BottomLeftX) * progress, from.BottomLeftY + (to.BottomLeftY - from.BottomLeftY) * progress);

    /// <summary>One corner's pull: 0 top left, 1 top right, 2 bottom right, 3 bottom left.</summary>
    public (float X, float Y) this[int corner] => corner switch
    {
        0 => (TopLeftX, TopLeftY),
        1 => (TopRightX, TopRightY),
        2 => (BottomRightX, BottomRightY),
        _ => (BottomLeftX, BottomLeftY)
    };

    /// <summary>These corners with one of them pulled to <paramref name="x"/>, <paramref name="y"/>.</summary>
    public PromoCorners With(int corner, float x, float y) => corner switch
    {
        0 => this with { TopLeftX = x, TopLeftY = y },
        1 => this with { TopRightX = x, TopRightY = y },
        2 => this with { BottomRightX = x, BottomRightY = y },
        _ => this with { BottomLeftX = x, BottomLeftY = y }
    };
}

/// <summary>The perspective a corner pin gives: the box's square stretched to its four pulled corners.</summary>
public static class PromoPerspective
{
    /// <summary>Where the four corners of <paramref name="bounds"/> end up (top left, top right, bottom right, bottom left).</summary>
    public static (double X, double Y)[] Quad(SKRect bounds, PromoCorners corners) =>
    [
        (bounds.Left + corners.TopLeftX, bounds.Top + corners.TopLeftY),
        (bounds.Right + corners.TopRightX, bounds.Top + corners.TopRightY),
        (bounds.Right + corners.BottomRightX, bounds.Bottom + corners.BottomRightY),
        (bounds.Left + corners.BottomLeftX, bounds.Bottom + corners.BottomLeftY)
    ];

    /// <summary>
    /// The 3×3 perspective that takes <paramref name="bounds"/> onto its pinned corners, as nine numbers
    /// (row by row); null for a square box, or corners pulled so far they'd fold the picture over itself.
    /// </summary>
    public static double[]? Homography(SKRect bounds, PromoCorners corners)
    {
        if (corners.IsSquare || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return null;
        }

        // Heckbert's unit square to quadrilateral, then the box squeezed to the unit square first.
        (double X, double Y)[] quad = Quad(bounds, corners);
        (double x0, double y0) = quad[0];
        (double x1, double y1) = quad[1];
        (double x2, double y2) = quad[2];
        (double x3, double y3) = quad[3];
        double dx1 = x1 - x2, dx2 = x3 - x2, dx3 = x0 - x1 + x2 - x3;
        double dy1 = y1 - y2, dy2 = y3 - y2, dy3 = y0 - y1 + y2 - y3;
        double g = 0, h = 0;
        if (Math.Abs(dx3) > 1e-9 || Math.Abs(dy3) > 1e-9)
        {
            double den = dx1 * dy2 - dx2 * dy1;
            if (Math.Abs(den) < 1e-12)
            {
                return null;
            }

            g = (dx3 * dy2 - dx2 * dy3) / den;
            h = (dx1 * dy3 - dx3 * dy1) / den;
        }

        // The picture would fold (a corner pulled past the far side): left square instead.
        if (1 + g <= 1e-6 || 1 + h <= 1e-6 || 1 + g + h <= 1e-6)
        {
            return null;
        }

        double a = x1 - x0 + g * x1, b = x3 - x0 + h * x3, c = x0;
        double d = y1 - y0 + g * y1, e = y3 - y0 + h * y3, f = y0;

        // Then (u, v) = ((x - left) / width, (y - top) / height).
        double sx = 1 / (double)bounds.Width, sy = 1 / (double)bounds.Height;
        double ox = -bounds.Left * sx, oy = -bounds.Top * sy;
        return
        [
            a * sx, b * sy, a * ox + b * oy + c,
            d * sx, e * sy, d * ox + e * oy + f,
            g * sx, h * sy, g * ox + h * oy + 1
        ];
    }

    /// <summary>For drawing: the perspective as a Skia matrix, or null for none.</summary>
    public static SKMatrix? Matrix(SKRect bounds, PromoCorners corners) =>
        Homography(bounds, corners) is { } m
            ? new SKMatrix((float)m[0], (float)m[1], (float)m[2], (float)m[3], (float)m[4], (float)m[5], (float)m[6], (float)m[7], (float)m[8])
            : null;

    /// <summary>Where a point of the box ends up once pinned.</summary>
    public static (double X, double Y) Apply(double[]? homography, double x, double y)
    {
        if (homography is not { } m)
        {
            return (x, y);
        }

        double w = m[6] * x + m[7] * y + m[8];
        return Math.Abs(w) < 1e-12 ? (x, y) : ((m[0] * x + m[1] * y + m[2]) / w, (m[3] * x + m[4] * y + m[5]) / w);
    }
}
