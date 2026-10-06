using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SkiaSharp;

namespace SchedularrTV.Overlays;

/// <summary>
/// Draws design overlays into transparent, frame-sized PNGs for ErsatzTV to lay over the picture. Each distinct look
/// (the design with its text filled in) is drawn once and kept, named by a hash of what's in it.
/// </summary>
public sealed class OverlayRenderer(Func<string> folder)
{
    public OverlayRenderer(string folder)
        : this(() => folder)
    {
    }

    private static readonly JsonSerializerOptions HashJson = new() { WriteIndented = false };

    // Kept for the plugin's lifetime: there are only ever a few, and they're shared by every drawing.
    private static readonly ConcurrentDictionary<(string Family, bool Bold), SKTypeface> Typefaces = new();
    private readonly ConcurrentDictionary<string, string> _known = new(StringComparer.Ordinal);

    /// <summary>Read on each use, so a changed setting applies straight away.</summary>
    public string Folder => folder();

    /// <summary>The PNG for a design with these tokens filled in, drawn if it isn't already.</summary>
    public string Render(Overlay overlay, IReadOnlyDictionary<string, string> tokens)
    {
        string key = Key(overlay, tokens);
        if (_known.TryGetValue(key, out string? known) && File.Exists(known))
        {
            Touch(known);
            return known;
        }

        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, key + ".png");
        if (File.Exists(path))
        {
            Touch(path);
        }
        else
        {
            byte[] png = Draw(overlay, tokens, scale: 1, backdrop: false, posters: null);
            string temp = Path.Combine(Folder, $"{key}.{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(temp, png);
            File.Move(temp, path, overwrite: true);
        }

        _known[key] = path;
        return path;
    }

    /// <summary>
    /// The editor's preview: the overlay over a stand-in picture (a design drawn, a file placed as ErsatzTV will), at
    /// <paramref name="scale"/> of the design's size.
    /// </summary>
    /// <param name="posters">A still from each video layer's file (by its path), drawn in its box; one without shows as a marked box.</param>
    public static byte[] Preview(Overlay overlay, IReadOnlyDictionary<string, string> tokens, float scale, IReadOnlyDictionary<string, string>? posters = null)
    {
        if (overlay.Kind == OverlayKinds.File)
        {
            return DrawFile(overlay, scale);
        }

        return Draw(overlay, tokens, scale, backdrop: true, posters ?? new Dictionary<string, string>());
    }

    /// <summary>
    /// A design split into what goes to ErsatzTV: each video layer on its own, and runs of the drawn layers between them
    /// that come and go together, in drawing order (bottom first).
    /// </summary>
    public static IReadOnlyList<(LayerTiming Timing, IReadOnlyList<OverlayLayer> Layers)> Groups(Overlay overlay)
    {
        List<(LayerTiming, IReadOnlyList<OverlayLayer>)> groups = [];
        List<OverlayLayer>? run = null;
        LayerTiming runTiming = default;
        foreach (OverlayLayer layer in overlay.Layers)
        {
            LayerTiming timing = LayerTiming.Of(layer);
            if (layer.Kind == OverlayLayerKinds.Video)
            {
                run = null;
                groups.Add((timing, [layer]));
                continue;
            }

            if (run is null || runTiming != timing)
            {
                run = [];
                runTiming = timing;
                groups.Add((timing, run));
            }

            run.Add(layer);
        }

        return groups;
    }

    /// <summary>Fills {tokens} in a text; unknown ones are left as written.</summary>
    public static string Fill(string? text, IReadOnlyDictionary<string, string> tokens)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = new StringBuilder(text);
        foreach ((string name, string value) in tokens)
        {
            result.Replace("{" + name + "}", value);
        }

        return result.ToString();
    }

    private static string Key(Overlay overlay, IReadOnlyDictionary<string, string> tokens)
    {
        var look = new
        {
            overlay.Width,
            overlay.Height,
            Layers = overlay.Layers.Select(layer => new
            {
                layer.Kind, layer.X, layer.Y, layer.Width, layer.Height, Text = Fill(layer.Text, tokens), layer.Path,
                PathStamp = layer.Path is { Length: > 0 } path && File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0,
                layer.Color, layer.FontFamily, layer.FontSize, layer.Bold, layer.Align, layer.Shadow, layer.CornerRadius, layer.Opacity
            })
        };

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(look, HashJson)));
        return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }

    /// <param name="posters">For the preview, stills of its videos; null when drawing for ErsatzTV, which shows videos itself.</param>
    private static byte[] Draw(Overlay overlay, IReadOnlyDictionary<string, string> tokens, float scale, bool backdrop,
        IReadOnlyDictionary<string, string>? posters)
    {
        int width = Math.Max(16, (int)Math.Round(Math.Clamp(overlay.Width, 64, 7680) * scale));
        int height = Math.Max(16, (int)Math.Round(Math.Clamp(overlay.Height, 64, 4320) * scale));
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using SKSurface surface = SKSurface.Create(info);
        SKCanvas canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        if (backdrop)
        {
            DrawBackdrop(canvas, width, height);
        }

        canvas.Scale(scale);
        foreach (OverlayLayer layer in overlay.Layers)
        {
            if (layer.Kind == OverlayLayerKinds.Video && posters is null)
            {
                continue;
            }

            DrawLayer(canvas, layer, tokens, posters);
        }

        using SKImage image = surface.Snapshot();
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A stand-in for the programme underneath, so the preview shows how the overlay reads over a picture.</summary>
    private static void DrawBackdrop(SKCanvas canvas, int width, int height)
    {
        using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height),
            [new SKColor(0x2b, 0x3a, 0x67), new SKColor(0x6b, 0x4a, 0x7a), new SKColor(0x1f, 0x5f, 0x5b)], null, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(0, 0, width, height, paint);
        using var haze = new SKPaint { Color = new SKColor(255, 255, 255, 18), IsAntialias = true };
        canvas.DrawCircle(width * 0.3f, height * 0.4f, height * 0.35f, haze);
        canvas.DrawCircle(width * 0.72f, height * 0.62f, height * 0.25f, haze);
    }

    private static void DrawLayer(SKCanvas canvas, OverlayLayer layer, IReadOnlyDictionary<string, string> tokens,
        IReadOnlyDictionary<string, string>? posters)
    {
        SKRect bounds = SKRect.Create(layer.X, layer.Y, Math.Max(1, layer.Width), Math.Max(1, layer.Height));
        byte alpha = (byte)Math.Round(Math.Clamp(layer.Opacity, 0, 100) / 100 * 255);
        if (alpha == 0)
        {
            return;
        }

        bool translucent = alpha < 255;
        if (translucent)
        {
            SKRect area = bounds;
            area.Inflate(bounds.Height * 0.25f + 8, bounds.Height * 0.25f + 8);
            using var fade = new SKPaint { Color = SKColors.Black.WithAlpha(alpha) };
            canvas.SaveLayer(area, fade);
        }

        switch (layer.Kind)
        {
            case OverlayLayerKinds.Rect:
                using (var paint = new SKPaint { Color = Colour(layer.Color, SKColors.Black), IsAntialias = true })
                {
                    canvas.DrawRoundRect(bounds, layer.CornerRadius, layer.CornerRadius, paint);
                }

                break;
            case OverlayLayerKinds.Image when layer.Path is { Length: > 0 } path && File.Exists(path):
                using (SKImage? picture = SKImage.FromEncodedData(path))
                {
                    if (picture is not null)
                    {
                        DrawContained(canvas, picture, bounds);
                    }
                }

                break;
            case OverlayLayerKinds.Text:
                DrawText(canvas, layer, Fill(layer.Text, tokens), bounds);
                break;
            case OverlayLayerKinds.Video:
                DrawVideoStill(canvas, layer, bounds, posters);
                break;
        }

        if (translucent)
        {
            canvas.Restore();
        }
    }

    private static void DrawText(SKCanvas canvas, OverlayLayer layer, string text, SKRect bounds)
    {
        if (text.Length == 0)
        {
            return;
        }

        SKTypeface typeface = Typefaces.GetOrAdd((layer.FontFamily ?? "Arial", layer.Bold),
            key => SKTypeface.FromFamilyName(key.Family, key.Bold ? SKFontStyle.Bold : SKFontStyle.Normal) ?? SKTypeface.Default);
        using var font = new SKFont(typeface, Math.Clamp(layer.FontSize, 6, 400)) { Edging = SKFontEdging.Antialias, Subpixel = true };
        using var paint = new SKPaint { Color = Colour(layer.Color, SKColors.White), IsAntialias = true };
        using SKImageFilter? shadow = layer.Shadow
            ? SKImageFilter.CreateDropShadow(0, font.Size * 0.05f, font.Size * 0.06f, font.Size * 0.06f, new SKColor(0, 0, 0, 170))
            : null;
        paint.ImageFilter = shadow;

        // Long text shrinks to fit the box rather than run out of it.
        while (font.Size > 8 && (font.MeasureText(text, paint) > bounds.Width || font.Spacing > bounds.Height * 1.15f))
        {
            font.Size *= 0.94f;
        }

        SKTextAlign align = layer.Align switch
        {
            "center" => SKTextAlign.Center,
            "right" => SKTextAlign.Right,
            _ => SKTextAlign.Left
        };
        float x = align switch
        {
            SKTextAlign.Center => bounds.MidX,
            SKTextAlign.Right => bounds.Right,
            _ => bounds.Left
        };

        // Centred in the box's height.
        SKFontMetrics metrics = font.Metrics;
        float y = bounds.MidY - (metrics.Ascent + metrics.Descent) / 2;
        canvas.DrawText(text, x, y, align, font, paint);
    }

    /// <summary>
    /// In the preview, a video as ErsatzTV places it: its still at the box's width from the box's top-left (ErsatzTV sizes
    /// it by width), or a marked box when there's no still.
    /// </summary>
    private static void DrawVideoStill(SKCanvas canvas, OverlayLayer layer, SKRect bounds, IReadOnlyDictionary<string, string>? posters)
    {
        if (layer.Path is { Length: > 0 } path && posters?.GetValueOrDefault(path) is { } poster && File.Exists(poster))
        {
            using SKImage? still = SKImage.FromEncodedData(poster);
            if (still is not null)
            {
                var placed = SKRect.Create(bounds.Left, bounds.Top, bounds.Width, bounds.Width * still.Height / still.Width);
                using var paint = new SKPaint { IsAntialias = true };
                canvas.DrawImage(still, placed, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
                return;
            }
        }

        using var fill = new SKPaint { Color = new SKColor(255, 255, 255, 40), IsAntialias = true };
        using var edge = new SKPaint { Color = new SKColor(255, 255, 255, 140), IsAntialias = true, IsStroke = true, StrokeWidth = 2,
            PathEffect = SKPathEffect.CreateDash([10, 6], 0) };
        canvas.DrawRect(bounds, fill);
        canvas.DrawRect(bounds, edge);

        // A play mark in the middle.
        float size = Math.Min(bounds.Width, bounds.Height) * 0.25f;
        using var mark = new SKPaint { Color = new SKColor(255, 255, 255, 170), IsAntialias = true };
        string P(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);
        using SKPath? triangle = SKPath.ParseSvgPathData(
            $"M{P(bounds.MidX - size * 0.4f)} {P(bounds.MidY - size / 2)} L{P(bounds.MidX + size * 0.5f)} {P(bounds.MidY)} L{P(bounds.MidX - size * 0.4f)} {P(bounds.MidY + size / 2)} Z");
        if (triangle is not null)
        {
            canvas.DrawPath(triangle, mark);
        }
    }

    public static bool IsVideo(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.GetExtension(path).ToLowerInvariant() is ".mov" or ".webm" or ".mp4" or ".mkv" or ".avi" or ".m4v";

    private static void DrawContained(SKCanvas canvas, SKImage image, SKRect bounds)
    {
        float scale = Math.Min(bounds.Width / image.Width, bounds.Height / image.Height);
        var destination = SKRect.Create(bounds.MidX - image.Width * scale / 2, bounds.MidY - image.Height * scale / 2, image.Width * scale, image.Height * scale);
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawImage(image, destination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
    }

    /// <summary>A file overlay previewed where ErsatzTV will put it: anchored, sized and inset as set.</summary>
    private static byte[] DrawFile(Overlay overlay, float scale)
    {
        int width = (int)Math.Round(1920 * scale);
        int height = (int)Math.Round(1080 * scale);
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using SKSurface surface = SKSurface.Create(info);
        SKCanvas canvas = surface.Canvas;
        DrawBackdrop(canvas, width, height);

        SKRect box = FileBox(overlay, width, height, aspect: null);
        SKImage? picture = overlay.File is { Length: > 0 } file && File.Exists(file) && IsImage(file) ? SKImage.FromEncodedData(file) : null;
        using (picture)
        {
            if (picture is not null)
            {
                box = FileBox(overlay, width, height, picture.Height / (float)picture.Width);
            }

            byte alpha = (byte)Math.Round(Math.Clamp(overlay.Opacity, 0, 100) / 100 * 255);
            using var fade = new SKPaint { Color = SKColors.Black.WithAlpha(alpha) };
            canvas.SaveLayer(box, fade);
            if (picture is not null)
            {
                canvas.DrawImage(picture, box, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            }
            else
            {
                using var stand = new SKPaint { Color = new SKColor(255, 255, 255, 60), IsAntialias = true };
                canvas.DrawRoundRect(box, 6 * scale, 6 * scale, stand);
            }

            canvas.Restore();
        }

        using SKImage image = surface.Snapshot();
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Where a file overlay lands on a frame: ErsatzTV's anchors, width as a percent, margins as percents.</summary>
    public static SKRect FileBox(Overlay overlay, float frameWidth, float frameHeight, float? aspect)
    {
        float width = (float)(frameWidth * Math.Clamp(overlay.SizePercent, 1, 100) / 100);
        float height = width * (aspect ?? 9f / 16f);
        float marginX = (float)(frameWidth * Math.Clamp(overlay.MarginXPercent, 0, 100) / 100);
        float marginY = (float)(frameHeight * Math.Clamp(overlay.MarginYPercent, 0, 100) / 100);
        string anchor = overlay.Anchor ?? "bottom_right";
        float x = anchor.EndsWith("left", StringComparison.Ordinal) ? marginX
            : anchor.EndsWith("right", StringComparison.Ordinal) ? frameWidth - width - marginX
            : (frameWidth - width) / 2;
        float y = anchor.StartsWith("top", StringComparison.Ordinal) ? marginY
            : anchor.StartsWith("bottom", StringComparison.Ordinal) ? frameHeight - height - marginY
            : (frameHeight - height) / 2;
        return SKRect.Create(x, y, width, height);
    }

    public static bool IsImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif";

    private static SKColor Colour(string? value, SKColor fallback) =>
        value is { Length: 7 or 9 } && value[0] == '#' && SKColor.TryParse(value, out SKColor colour) ? colour : fallback;

    /// <summary>
    /// Marks a drawing as still in use (at most once a day), so <see cref="Prune"/> never removes one a playout still
    /// points at: every publish touches what it uses.
    /// </summary>
    private static void Touch(string path)
    {
        try
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(1))
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Removes drawn PNGs no publish has used for <paramref name="age"/>, so old looks don't pile up.</summary>
    public int Prune(TimeSpan age)
    {
        if (!Directory.Exists(Folder))
        {
            return 0;
        }

        int removed = 0;
        DateTime cutoff = DateTime.UtcNow - age;
        foreach (string file in Directory.EnumerateFiles(Folder, "*.png"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        _known.Clear();
        return removed;
    }

    /// <summary>For messages: a time as a guide would say it.</summary>
    public static string ClockTime(DateTimeOffset time) => time.ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture);
}
