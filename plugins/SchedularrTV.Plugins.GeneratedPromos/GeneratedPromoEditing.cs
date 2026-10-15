using System.Globalization;
using System.Text.Json;
using SkiaSharp;
using SchedularrTV.Core.Configuration;

namespace SchedularrTV.GeneratedPromos;

/// <summary>Template edits shared by the generated promo editor.</summary>
public static class GeneratedPromoEditing
{
    public static (string Rgb, int AlphaPercent) SplitColor(string? hex)
    {
        string value = hex?.Trim() ?? string.Empty;
        if (value.Length == 9 && value[0] == '#'
            && byte.TryParse(value.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte alpha))
        {
            return ($"#{value[3..].ToLowerInvariant()}", (int)Math.Round(alpha / 2.55));
        }

        return value.Length == 7 && value[0] == '#' ? (value.ToLowerInvariant(), 100) : ("#ffffff", 100);
    }

    public static string JoinColor(string rgb, int alphaPercent)
    {
        string color = rgb.Length == 7 && rgb[0] == '#' ? rgb.ToUpperInvariant() : "#FFFFFF";
        int alpha = (int)Math.Round(Math.Clamp(alphaPercent, 0, 100) * 2.55);
        return alpha >= 255 ? color : $"#{alpha:X2}{color[1..]}";
    }

    public static void Move<T>(List<T> items, T item, int delta)
    {
        int from = items.IndexOf(item);
        int to = from + delta;
        if (from < 0 || to < 0 || to >= items.Count)
        {
            return;
        }

        items.RemoveAt(from);
        items.Insert(to, item);
    }

    public static T Copy<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    public static string UniqueName(IEnumerable<string> existing, string name)
    {
        HashSet<string> taken = new(existing, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name))
        {
            return name;
        }

        for (int index = 2; ; index++)
        {
            string candidate = $"{name} {index}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>A sensibly sized layer centred on the canvas.</summary>
    public static GeneratedLayer NewLayer(GeneratedPromoTemplate template, GeneratedLayerKind kind)
    {
        float width = template.Width * (kind == GeneratedLayerKind.Rect ? 0.5f : 0.4f);
        float height = template.Height * (kind is GeneratedLayerKind.Text or GeneratedLayerKind.UpNext || GeneratedPromoKinds.IsShowLayer(kind) ? 0.12f : 0.3f);
        var layer = new GeneratedLayer
        {
            Kind = kind,
            Name = UniqueName(template.Layers.Select(existing => existing.Name), kind switch
            {
                GeneratedLayerKind.UpNext => "Up Next",
                GeneratedLayerKind.NowPlaying => "Now Playing",
                GeneratedLayerKind.ThisHasBeen => "This Has Been",
                GeneratedLayerKind.ComingUp => "Coming Up",
                GeneratedLayerKind.Eventually => "Eventually",
                GeneratedLayerKind.YoureWatching => "You're Watching",
                GeneratedLayerKind.BackToTheShow => "Back to the Show",
                GeneratedLayerKind.Rect => "Rectangle",
                GeneratedLayerKind.Image => "Image",
                _ => "Text"
            }),
            X = (float)Math.Round((template.Width - width) / 2),
            Y = (float)Math.Round((template.Height - height) / 2),
            Width = (float)Math.Round(width),
            Height = (float)Math.Round(height),
            FontSize = (float)Math.Round(template.Height * 0.08),
            Color = kind == GeneratedLayerKind.Rect ? "#96000000" : "#FFFFFF",
            Text = kind == GeneratedLayerKind.Text ? "Only on {channel}" : null,
            UpNext = kind == GeneratedLayerKind.UpNext ? new GeneratedUpNextOptions() : null,
            Show = GeneratedPromoKinds.IsShowLayer(kind) ? GeneratedPromoResolver.DefaultShowOptions(kind) : null
        };
        return layer;
    }

    /// <summary>Pulls a layer whose centre is within <paramref name="threshold"/> of the canvas centre lines onto them.</summary>
    public static (float X, float Y, bool SnappedX, bool SnappedY) SnapToCenter(
        float x, float y, float width, float height, int canvasWidth, int canvasHeight, float threshold)
    {
        float centredX = (canvasWidth - width) / 2;
        float centredY = (canvasHeight - height) / 2;
        bool snapX = Math.Abs(x - centredX) <= threshold;
        bool snapY = Math.Abs(y - centredY) <= threshold;
        return (snapX ? centredX : x, snapY ? centredY : y, snapX, snapY);
    }

    /// <summary>Changes the output size, keeping every layer in the same place relative to the frame.</summary>
    public static void Resize(GeneratedPromoTemplate template, int width, int height)
    {
        float scaleX = width / (float)template.Width;
        float scaleY = height / (float)template.Height;
        foreach (GeneratedLayer layer in template.Layers)
        {
            layer.X = (float)Math.Round(layer.X * scaleX);
            layer.Y = (float)Math.Round(layer.Y * scaleY);
            layer.Width = Math.Max(1, (float)Math.Round(layer.Width * scaleX));
            layer.Height = Math.Max(1, (float)Math.Round(layer.Height * scaleY));
            layer.FontSize = Math.Max(4, (float)Math.Round(layer.FontSize * scaleY));
            layer.CornerRadius = (float)Math.Round(layer.CornerRadius * Math.Min(scaleX, scaleY));
            foreach (GeneratedKeyframe key in layer.Keyframes ?? [])
            {
                key.X = (float)Math.Round(key.X * scaleX);
                key.Y = (float)Math.Round(key.Y * scaleY);
                key.Width = Math.Max(1, (float)Math.Round(key.Width * scaleX));
                key.Height = Math.Max(1, (float)Math.Round(key.Height * scaleY));
            }
        }

        template.Width = width;
        template.Height = height;
    }

    /// <summary>Moves a layer, and every keyframe it has, by the same amount.</summary>
    public static void Offset(GeneratedLayer layer, float dx, float dy)
    {
        layer.X += dx;
        layer.Y += dy;
        foreach (GeneratedKeyframe key in layer.Keyframes ?? [])
        {
            key.X += dx;
            key.Y += dy;
        }
    }

    // ---- Keyframes ----

    /// <summary>
    /// Two keyframes closer than this (in seconds) are the same one: under half a frame even at 120 fps, so keyframes on
    /// neighbouring frames stay apart, but more than the rounding of a frame's time.
    /// </summary>
    public const double KeyframeTolerance = 0.002;

    public static bool HasKeyframes(GeneratedLayer layer) => layer.Keyframes is { Count: > 0 };

    /// <summary>Where the layer is, how big and how opaque, at <paramref name="time"/>, following its keyframes.</summary>
    public static LayerPose PoseAt(GeneratedLayer layer, TimeSpan time)
    {
        (SKRect bounds, float opacity) = PromoAnimation.Pose(SKRect.Create(layer.X, layer.Y, layer.Width, layer.Height), layer.Opacity,
            GeneratedPromoResolver.Keyframes(layer), time);
        return new LayerPose(bounds.Left, bounds.Top, bounds.Width, bounds.Height, opacity);
    }

    /// <summary>The keyframe at <paramref name="time"/>, if there is one.</summary>
    public static GeneratedKeyframe? KeyframeAt(GeneratedLayer layer, TimeSpan time) =>
        layer.Keyframes?.Where(key => Math.Abs((key.At - time).TotalSeconds) <= KeyframeTolerance)
            .OrderBy(key => Math.Abs((key.At - time).TotalSeconds))
            .FirstOrDefault();

    /// <summary>
    /// Sets the layer's pose at <paramref name="time"/>: its own position, size and opacity when it has no keyframes,
    /// otherwise the keyframe there, which is added if there isn't one yet.
    /// </summary>
    public static void SetPose(GeneratedLayer layer, TimeSpan time, LayerPose pose)
    {
        pose = pose with { Width = Math.Max(1, pose.Width), Height = Math.Max(1, pose.Height), Opacity = Math.Clamp(pose.Opacity, 0, 1) };
        if (!HasKeyframes(layer))
        {
            (layer.X, layer.Y, layer.Width, layer.Height, layer.Opacity) = (pose.X, pose.Y, pose.Width, pose.Height, pose.Opacity);
            return;
        }

        GeneratedKeyframe key = KeyframeAt(layer, time) ?? AddKeyframe(layer, time);
        (key.X, key.Y, key.Width, key.Height, key.Opacity) = (pose.X, pose.Y, pose.Width, pose.Height, pose.Opacity);
    }

    /// <summary>
    /// A keyframe at <paramref name="time"/> holding the pose the layer has there, so adding it changes nothing until it's
    /// moved. It eases on the way the keyframe before it does. An existing keyframe there is returned instead.
    /// </summary>
    public static GeneratedKeyframe AddKeyframe(GeneratedLayer layer, TimeSpan time)
    {
        if (KeyframeAt(layer, time) is { } existing)
        {
            return existing;
        }

        LayerPose pose = PoseAt(layer, time);
        List<GeneratedKeyframe> keys = layer.Keyframes ??= [];
        var key = new GeneratedKeyframe
        {
            At = RoundTime(time),
            X = MathF.Round(pose.X),
            Y = MathF.Round(pose.Y),
            Width = MathF.Round(pose.Width),
            Height = MathF.Round(pose.Height),
            Opacity = pose.Opacity,
            Easing = keys.Where(other => other.At <= time).OrderBy(other => other.At).LastOrDefault()?.Easing ?? GeneratedEasing.Linear
        };
        keys.Add(key);
        SortKeyframes(layer);
        return key;
    }

    /// <summary>Starts animating the layer with one keyframe, at <paramref name="time"/>, where it is now.</summary>
    public static GeneratedKeyframe StartKeyframes(GeneratedLayer layer, TimeSpan time)
    {
        layer.Keyframes = null;
        return AddKeyframe(layer, time);
    }

    /// <summary>Stops animating the layer, leaving it where it is at <paramref name="time"/>.</summary>
    public static void StopKeyframes(GeneratedLayer layer, TimeSpan time)
    {
        LayerPose pose = PoseAt(layer, time);
        layer.Keyframes = null;
        SetPose(layer, time, pose);
    }

    /// <summary>Removes a keyframe; removing the last one stops the animation, leaving the layer where that keyframe had it.</summary>
    public static void RemoveKeyframe(GeneratedLayer layer, GeneratedKeyframe key)
    {
        if (layer.Keyframes is not { } keys || !keys.Remove(key))
        {
            return;
        }

        if (keys.Count == 0)
        {
            layer.Keyframes = null;
            (layer.X, layer.Y, layer.Width, layer.Height, layer.Opacity) = (key.X, key.Y, key.Width, key.Height, key.Opacity);
        }
    }

    /// <summary>Moves a keyframe to <paramref name="time"/> (within the promo), keeping the list in time order.</summary>
    public static void MoveKeyframe(GeneratedLayer layer, GeneratedKeyframe key, TimeSpan time, TimeSpan duration)
    {
        key.At = RoundTime(time < TimeSpan.Zero ? TimeSpan.Zero : time > duration ? duration : time);
        SortKeyframes(layer);
    }

    public static void SortKeyframes(GeneratedLayer layer)
    {
        if (layer.Keyframes is { Count: > 1 } keys)
        {
            // Stable, so two keyframes dragged onto the same moment keep their order.
            layer.Keyframes = [.. keys.OrderBy(key => key.At)];
        }
    }

    // Fine enough to keep a frame's time (the editor puts keyframes on whole frames) without long decimals in the YAML.
    private static TimeSpan RoundTime(TimeSpan time) => TimeSpan.FromSeconds(Math.Round(Math.Max(0, time.TotalSeconds), 4));

    // ---- Backgrounds ----

    /// <summary>
    /// The editable background list, folding in the single background older templates carry, so the first entry is
    /// kept in <see cref="GeneratedPromoTemplate.Background"/> and the rest in <see cref="GeneratedPromoTemplate.Backgrounds"/>.
    /// </summary>
    public static List<string> Backgrounds(GeneratedPromoTemplate template) => [.. template.BackgroundChoices];

    /// <summary>
    /// Stores <paramref name="backgrounds"/>: the first as <see cref="GeneratedPromoTemplate.Background"/> (so a template
    /// with one background reads as it always has) and any others in <see cref="GeneratedPromoTemplate.Backgrounds"/>.
    /// </summary>
    public static void SetBackgrounds(GeneratedPromoTemplate template, IEnumerable<string?> backgrounds)
    {
        List<string> kept = [.. backgrounds.OfType<string>().Select(path => path.Trim()).Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        template.Background = kept.Count > 0 ? kept[0] : null;
        template.Backgrounds = kept.Count > 1 ? kept.Skip(1).ToList() : null;
    }
}

/// <summary>A layer's position, size and opacity at one moment.</summary>
public readonly record struct LayerPose(float X, float Y, float Width, float Height, float Opacity);
