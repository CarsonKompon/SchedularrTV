using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SchedularrTV.Core.Configuration;
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
    /// <param name="clock">The moment of an appearance to draw (keyframes, layers coming and going); null draws it as it stands.</param>
    public static byte[] Preview(Overlay overlay, IReadOnlyDictionary<string, string> tokens, float scale, IReadOnlyDictionary<string, string>? posters = null,
        OverlayClock? clock = null)
    {
        if (overlay.Kind == OverlayKinds.File)
        {
            return DrawFile(overlay, scale);
        }

        return Draw(overlay, tokens, scale, backdrop: true, posters ?? new Dictionary<string, string>(), clock);
    }

    /// <summary>Whether a layer is drawn into the design's picture itself (not a group, which draws nothing, nor a mask, drawn only into its target).</summary>
    public static bool IsDrawn(OverlayLayer layer) => !OverlayHierarchy.IsGroup(layer) && layer.Mask is null;

    /// <summary>
    /// A design split into what goes to ErsatzTV: each video layer on its own, and runs of the drawn layers between them
    /// that come and go together (with their groups' timing), in drawing order (bottom first).
    /// </summary>
    /// <param name="byTiming">False keeps every drawn run between videos together, for an animated design whose timing is drawn into it.</param>
    public static IReadOnlyList<(LayerTiming Timing, IReadOnlyList<OverlayLayer> Layers)> Groups(Overlay overlay, bool byTiming = true)
    {
        List<(LayerTiming, IReadOnlyList<OverlayLayer>)> groups = [];
        List<OverlayLayer>? run = null;
        LayerTiming runTiming = default;
        foreach (OverlayLayer layer in overlay.Layers.Where(IsDrawn))
        {
            LayerTiming timing = byTiming ? OverlayHierarchy.EffectiveTiming(overlay, layer) : LayerTiming.Whole;
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

    /// <summary>
    /// The design with only these of its drawn layers, as a picture of its own: their groups (which draw nothing) and the
    /// masks cutting them come along.
    /// </summary>
    public static Overlay Part(Overlay overlay, IReadOnlyCollection<OverlayLayer> layers)
    {
        HashSet<string> targets = [.. layers.Select(layer => layer.Id).OfType<string>()];
        Overlay part = overlay.Copy();
        part.Layers = [.. overlay.Layers
            .Where(layer => layers.Contains(layer) || OverlayHierarchy.IsGroup(layer)
                || (layer.Mask?.Target is { } target && targets.Contains(target)))
            .Select(layer => layer.Copy())];
        return part;
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

    /// <summary>What a design looks like with these tokens, as a short hash: the name its drawing is kept under.</summary>
    public static string Key(Overlay overlay, IReadOnlyDictionary<string, string> tokens)
    {
        var look = new
        {
            overlay.Width,
            overlay.Height,
            Layers = overlay.Layers.Select(layer => Look(layer, tokens))
        };

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(look, HashJson)));
        return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }

    // A layer as its drawing depends on it. Layers using none of the newer settings hash as they always have, so their
    // drawings are kept.
    private static object Look(OverlayLayer layer, IReadOnlyDictionary<string, string> tokens)
    {
        var basic = new
        {
            layer.Kind, layer.X, layer.Y, layer.Width, layer.Height, Text = Fill(layer.Text, tokens), layer.Path,
            PathStamp = layer.Path is { Length: > 0 } path && File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0,
            layer.Color, layer.FontFamily, layer.FontSize, layer.Bold, layer.Align, layer.Shadow, layer.CornerRadius, layer.Opacity
        };

        bool newer = layer.Id is not null || layer.Parent is not null || layer.Mask is not null || layer.Shape is not null
                     || layer.SoftEdges is not null || layer.Rotation is not null || layer.Keyframes is { Count: > 0 };
        if (!newer)
        {
            return basic;
        }

        return new
        {
            Basic = basic,
            layer.Id,
            layer.Parent,
            Mask = layer.Mask is { } mask ? $"{mask.Mode}:{mask.Target}" : null,
            Shape = layer.Shape?.ToString(),
            layer.SoftEdges,
            layer.Rotation,
            layer.InSeconds,
            layer.OutSeconds,
            Keys = layer.Keyframes?.Select(key => new
            {
                At = key.At.TotalSeconds, key.X, key.Y, key.Width, key.Height, key.Opacity, key.Rotation, Easing = key.Easing.ToString()
            }).ToList()
        };
    }

    /// <param name="posters">For the preview, stills of its videos; null when drawing for ErsatzTV, which shows videos itself.</param>
    private static byte[] Draw(Overlay overlay, IReadOnlyDictionary<string, string> tokens, float scale, bool backdrop,
        IReadOnlyDictionary<string, string>? posters, OverlayClock? clock = null)
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
        DrawDesign(canvas, overlay, tokens, posters, clock);

        using SKImage image = surface.Snapshot();
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Draws the design's layers onto a canvas in its own pixels, bottom first: each inside its groups, cut by its masks,
    /// and, with a <paramref name="clock"/>, posed by its keyframes and faded as it comes and goes at that moment.
    /// </summary>
    /// <param name="posters">Stills of its videos, for the preview; null leaves videos out (ErsatzTV shows them itself).</param>
    public static void DrawDesign(SKCanvas canvas, Overlay overlay, IReadOnlyDictionary<string, string> tokens,
        IReadOnlyDictionary<string, string>? posters, OverlayClock? clock)
    {
        var frame = SKRect.Create(Math.Max(1, overlay.Width), Math.Max(1, overlay.Height));
        double envelope = clock is null ? 1 : OverlayAnimation.Envelope(clock);
        ILookup<string, OverlayLayer> masks = overlay.Layers
            .Where(layer => OverlayHierarchy.CanMask(layer) && layer.Mask?.Target is { Length: > 0 })
            .ToLookup(layer => layer.Mask!.Target!, StringComparer.Ordinal);

        foreach (OverlayLayer layer in overlay.Layers)
        {
            if (!IsDrawn(layer) || (layer.Kind == OverlayLayerKinds.Video && posters is null))
            {
                continue;
            }

            OverlayLayer[] applied = layer.Id is { } id && layer.Kind != OverlayLayerKinds.Video ? [.. masks[id]] : [];
            if (applied.Length == 0)
            {
                DrawPlaced(canvas, overlay, layer, tokens, posters, clock, envelope);
                continue;
            }

            // The target on its own first, so a cutout shows what's under it rather than erasing it.
            canvas.SaveLayer(frame, null);
            DrawPlaced(canvas, overlay, layer, tokens, posters, clock, envelope);
            ApplyMasks(GeneratedMaskMode.Add, SKBlendMode.DstIn);
            ApplyMasks(GeneratedMaskMode.Subtract, SKBlendMode.DstOut);
            canvas.Restore();

            void ApplyMasks(GeneratedMaskMode mode, SKBlendMode blend)
            {
                if (!applied.Any(mask => mask.Mask!.Mode == mode))
                {
                    return;
                }

                using var paint = new SKPaint { BlendMode = blend };
                // A whole-frame layer: outside the masks drawn into it is clear, which DstIn needs.
                canvas.SaveLayer(frame, paint);
                foreach (OverlayLayer mask in applied.Where(mask => mask.Mask!.Mode == mode))
                {
                    DrawPlaced(canvas, overlay, mask, tokens, posters, clock, 1);
                }

                canvas.Restore();
            }
        }
    }

    /// <summary>One layer where it is: inside its groups (outermost first, each moving its contents to its corner and turning them about its middle), turned, faded.</summary>
    private static void DrawPlaced(SKCanvas canvas, Overlay overlay, OverlayLayer layer, IReadOnlyDictionary<string, string> tokens,
        IReadOnlyDictionary<string, string>? posters, OverlayClock? clock, double envelope)
    {
        double seconds = clock?.Seconds ?? 0;
        OverlayPose pose = OverlayAnimation.PoseAt(layer, seconds);
        double opacity = Math.Clamp(pose.Opacity, 0, 100) / 100 * envelope;
        if (clock is not null)
        {
            opacity *= OverlayAnimation.Visibility(layer, clock);
        }

        List<OverlayLayer> groups = [.. OverlayHierarchy.Ancestors(overlay, layer)];
        groups.Reverse();
        List<OverlayPose> groupPoses = [];
        foreach (OverlayLayer group in groups)
        {
            OverlayPose around = OverlayAnimation.PoseAt(group, seconds);
            groupPoses.Add(around);
            opacity *= Math.Clamp(around.Opacity, 0, 100) / 100;
            if (clock is not null)
            {
                opacity *= OverlayAnimation.Visibility(group, clock);
            }
        }

        if (opacity <= 0.001)
        {
            return;
        }

        canvas.Save();
        foreach (OverlayPose around in groupPoses)
        {
            float turn = around.Rotation ?? 0;
            if (Math.Abs(turn) > 0.001f)
            {
                canvas.RotateDegrees(turn, around.X + around.Width / 2, around.Y + around.Height / 2);
            }

            canvas.Translate(around.X, around.Y);
        }

        SKRect bounds = SKRect.Create(pose.X, pose.Y, Math.Max(1, pose.Width), Math.Max(1, pose.Height));
        // ErsatzTV can't turn a video, so the preview doesn't either.
        float rotation = layer.Kind == OverlayLayerKinds.Video ? 0 : pose.Rotation ?? 0;
        if (Math.Abs(rotation) > 0.001f)
        {
            canvas.RotateDegrees(rotation, bounds.MidX, bounds.MidY);
        }

        byte alpha = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        bool translucent = alpha < 255;
        if (translucent)
        {
            SKRect area = bounds;
            area.Inflate(bounds.Height * 0.25f + 8, bounds.Height * 0.25f + 8);
            float soft = layer.SoftEdges ?? 0;
            area.Inflate(soft * 4, soft * 4);
            using var fade = new SKPaint { Color = SKColors.Black.WithAlpha(alpha) };
            canvas.SaveLayer(area, fade);
        }

        switch (layer.Kind)
        {
            case OverlayLayerKinds.Rect:
                DrawShape(canvas, layer, bounds);
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
                DrawVideoStill(canvas, layer, bounds, posters, FrameKey(overlay, layer));
                break;
        }

        if (translucent)
        {
            canvas.Restore();
        }

        canvas.Restore();
    }

    /// <summary>A shape: a (rounded) rectangle, ellipse, triangle, diamond or star filling its box, its edges feathered if set.</summary>
    private static void DrawShape(SKCanvas canvas, OverlayLayer layer, SKRect bounds)
    {
        float soft = Math.Clamp(layer.SoftEdges ?? 0, 0, 200);
        using SKMaskFilter? blur = soft > 0 ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, soft) : null;
        using var paint = new SKPaint { Color = Colour(layer.Color, SKColors.Black), IsAntialias = true, MaskFilter = blur };
        using var path = new SKPathBuilder();
        switch (layer.Shape)
        {
            case GeneratedShape.Ellipse:
                canvas.DrawOval(bounds, paint);
                return;
            case GeneratedShape.Triangle:
                path.MoveTo(bounds.MidX, bounds.Top);
                path.LineTo(bounds.Right, bounds.Bottom);
                path.LineTo(bounds.Left, bounds.Bottom);
                break;
            case GeneratedShape.Diamond:
                path.MoveTo(bounds.MidX, bounds.Top);
                path.LineTo(bounds.Right, bounds.MidY);
                path.LineTo(bounds.MidX, bounds.Bottom);
                path.LineTo(bounds.Left, bounds.MidY);
                break;
            case GeneratedShape.Star:
                for (int i = 0; i < 10; i++)
                {
                    double angle = -Math.PI / 2 + i * Math.PI / 5;
                    float radius = i % 2 == 0 ? 1 : 0.4f;
                    float x = bounds.MidX + (float)Math.Cos(angle) * bounds.Width / 2 * radius;
                    float y = bounds.MidY + (float)Math.Sin(angle) * bounds.Height / 2 * radius;
                    if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
                }

                break;
            default:
                canvas.DrawRoundRect(bounds, layer.CornerRadius, layer.CornerRadius, paint);
                return;
        }

        path.Close();
        using SKPath outline = path.Detach();
        canvas.DrawPath(outline, paint);
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
    /// <summary>
    /// What a video layer's frame at the preview's moment is filed under in the stills given to the preview: its file
    /// and when it starts playing (as it comes in), since layers playing one file from different moments show different frames.
    /// </summary>
    public static string FrameKey(Overlay overlay, OverlayLayer video) =>
        $"{video.Path}|{OverlayHierarchy.EffectiveTiming(overlay, video).InSeconds.ToString("0.###", CultureInfo.InvariantCulture)}";

    /// <param name="frameKey">The layer's own frame among <paramref name="posters"/> (see <see cref="FrameKey"/>), else its file's still.</param>
    private static void DrawVideoStill(SKCanvas canvas, OverlayLayer layer, SKRect bounds, IReadOnlyDictionary<string, string>? posters, string frameKey)
    {
        if (layer.Path is { Length: > 0 } path && (posters?.GetValueOrDefault(frameKey) ?? posters?.GetValueOrDefault(path)) is { } poster && File.Exists(poster))
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
        // Drawings, and animated designs' videos.
        foreach (string file in Directory.EnumerateFiles(Folder, "*.png")
                     .Concat(Directory.EnumerateFiles(Folder, "anim-*.webm"))
                     .Concat(Directory.EnumerateFiles(Folder, "anim-*.mov")))
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
