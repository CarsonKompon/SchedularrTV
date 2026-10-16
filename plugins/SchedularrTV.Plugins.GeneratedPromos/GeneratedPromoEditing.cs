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

    private static readonly string[] TimeOfDayFormats = [@"hh\:mm", @"h\:mm", @"hh\:mm\:ss", @"h\:mm\:ss", @"hh\:mm\:ss\.FFFFFFF"];

    /// <summary>
    /// A time of day from a time input: <c>HH:mm</c> as browsers write it, or <c>HH:mm:ss</c> as Blazor passes it on
    /// (it adds the seconds). Kept to whole minutes, as the inputs step by a minute; false outside 00:00–23:59.
    /// </summary>
    public static bool TryParseTimeOfDay(string? text, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        if (!TimeSpan.TryParseExact(text?.Trim(), TimeOfDayFormats, CultureInfo.InvariantCulture, out TimeSpan parsed)
            || parsed < TimeSpan.Zero || parsed >= TimeSpan.FromDays(1))
        {
            return false;
        }

        time = TimeSpan.FromMinutes(Math.Floor(parsed.TotalMinutes));
        return true;
    }

    public static T Copy<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    /// <summary>A deep copy of the template whose variables keep pointing at the same shared images.</summary>
    public static GeneratedPromoTemplate Copy(GeneratedPromoTemplate template)
    {
        GeneratedPromoTemplate copy = Copy<GeneratedPromoTemplate>(template);
        if (template.VariableImages is { } originals && copy.VariableImages is { } copies)
        {
            for (int index = 0; index < Math.Min(originals.Count, copies.Count); index++)
            {
                copies[index].Linked = originals[index].Linked;
            }
        }

        return copy;
    }

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
        if (kind == GeneratedLayerKind.Video)
        {
            // Widescreen, like most episodes.
            height = width * 9 / 16;
        }

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
                GeneratedLayerKind.Rect => "Shape",
                GeneratedLayerKind.Image => "Image",
                GeneratedLayerKind.Video => "Video",
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
            Show = GeneratedPromoKinds.IsShowLayer(kind) ? GeneratedPromoResolver.DefaultShowOptions(kind) : null,
            // A clip of the promoted episode to start with: it works without choosing a file.
            Video = kind == GeneratedLayerKind.Video ? new GeneratedVideoOptions { Episode = "promoted" } : null
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
            if (layer.SoftEdges is { } softness) layer.SoftEdges = Math.Clamp(softness * Math.Min(scaleX, scaleY), 0, 200);
            ScaleKeyframes(layer, scaleX, scaleY);
        }

        // Corner pins are pixels too.
        foreach (IGeneratedPose pose in template.Layers.Cast<IGeneratedPose>()
                     .Concat((template.BackgroundOverrides ?? []).SelectMany(tracked => tracked.Layers)))
        {
            pose.Corners = ScaleCorners(pose.Corners, scaleX, scaleY);
            foreach (GeneratedKeyframe key in pose.Keyframes ?? [])
            {
                key.Corners = ScaleCorners(key.Corners, scaleX, scaleY);
            }
        }

        // Each background's own placements move with the frame too.
        foreach (GeneratedLayerOverride placed in (template.BackgroundOverrides ?? []).SelectMany(tracked => tracked.Layers))
        {
            placed.X = (float)Math.Round(placed.X * scaleX);
            placed.Y = (float)Math.Round(placed.Y * scaleY);
            placed.Width = Math.Max(1, (float)Math.Round(placed.Width * scaleX));
            placed.Height = Math.Max(1, (float)Math.Round(placed.Height * scaleY));
            ScaleKeyframes(placed, scaleX, scaleY);
        }

        template.Width = width;
        template.Height = height;
    }

    private static GeneratedCorners? ScaleCorners(GeneratedCorners? corners, float scaleX, float scaleY)
    {
        if (corners is null)
        {
            return null;
        }

        PromoCorners pin = PromoCorners.From(corners);
        return new PromoCorners(pin.TopLeftX * scaleX, pin.TopLeftY * scaleY, pin.TopRightX * scaleX, pin.TopRightY * scaleY,
            pin.BottomRightX * scaleX, pin.BottomRightY * scaleY, pin.BottomLeftX * scaleX, pin.BottomLeftY * scaleY).ToConfig();
    }

    private static void ScaleKeyframes(IGeneratedPose pose, float scaleX, float scaleY)
    {
        foreach (GeneratedKeyframe key in pose.Keyframes ?? [])
        {
            key.X = (float)Math.Round(key.X * scaleX);
            key.Y = (float)Math.Round(key.Y * scaleY);
            key.Width = Math.Max(1, (float)Math.Round(key.Width * scaleX));
            key.Height = Math.Max(1, (float)Math.Round(key.Height * scaleY));
        }
    }

    // ---- Per-background placements ----

    /// <summary>
    /// <paramref name="background"/>'s own placement of the layer, made on first use as a copy of where the template puts
    /// it (keyframes and all), so editing it starts from what's on screen. The layer is given an id if it has none.
    /// </summary>
    public static GeneratedLayerOverride Place(GeneratedPromoTemplate template, string background, GeneratedLayer layer)
    {
        if (GeneratedPromoResolver.Placement(template, background, layer) is { } existing)
        {
            return existing;
        }

        GeneratedBackgroundOverride tracked = Tracked(template, background, layer);
        var placed = new GeneratedLayerOverride
        {
            Layer = layer.Id!,
            X = layer.X,
            Y = layer.Y,
            Width = layer.Width,
            Height = layer.Height,
            Opacity = layer.Opacity,
            Rotation = layer.Rotation,
            Corners = PromoCorners.From(layer.Corners).ToConfig(),
            Keyframes = layer.Keyframes is { Count: > 0 } keys ? keys.Select(key => CopyKeyframe(key)).ToList() : null
        };
        tracked.Layers.Add(placed);
        return placed;
    }

    /// <summary>
    /// The background's entry, made if it has none yet; the layer is given an id if it has none, for the entry to name it by.
    /// </summary>
    private static GeneratedBackgroundOverride Tracked(GeneratedPromoTemplate template, string background, GeneratedLayer layer)
    {
        layer.Id ??= NewLayerId(template);
        List<GeneratedBackgroundOverride> all = template.BackgroundOverrides ??= [];
        return all.FirstOrDefault(candidate => string.Equals(candidate.Background, background, StringComparison.OrdinalIgnoreCase))
            ?? Add(all, new GeneratedBackgroundOverride { Background = background });
    }

    /// <summary>
    /// Colours the layer differently over <paramref name="background"/>; the layer's own colour (or null) goes back to it.
    /// </summary>
    public static void Recolour(GeneratedPromoTemplate template, string background, GeneratedLayer layer, string? color)
    {
        if (string.IsNullOrWhiteSpace(color) || string.Equals(color, layer.Color, StringComparison.OrdinalIgnoreCase))
        {
            if (GeneratedPromoResolver.Recolour(template, background, layer) is { } existing)
            {
                Entry(template, background)?.Colors?.Remove(existing);
            }

            Tidy(template);
            return;
        }

        if (GeneratedPromoResolver.Recolour(template, background, layer) is { } current)
        {
            current.Color = color;
            return;
        }

        GeneratedBackgroundOverride tracked = Tracked(template, background, layer);
        (tracked.Colors ??= []).Add(new GeneratedLayerColor { Layer = layer.Id!, Color = color });
    }

    /// <summary>
    /// <paramref name="background"/>'s own timing of the layer, made on first use as a copy of the layer's (start, end,
    /// enter and exit transitions and their length), so editing it starts from what the template does.
    /// </summary>
    public static GeneratedLayerTiming Retime(GeneratedPromoTemplate template, string background, GeneratedLayer layer)
    {
        if (GeneratedPromoResolver.Retiming(template, background, layer) is { } existing)
        {
            return existing;
        }

        GeneratedBackgroundOverride tracked = Tracked(template, background, layer);
        var timed = new GeneratedLayerTiming
        {
            Layer = layer.Id!,
            Start = layer.Start,
            End = layer.End,
            TransitionIn = layer.TransitionIn,
            TransitionOut = layer.TransitionOut,
            TransitionDuration = layer.TransitionDuration
        };
        (tracked.Timings ??= []).Add(timed);
        return timed;
    }

    /// <summary>Puts the layer back the way the template has it (place, keyframes, timing and colour), over <paramref name="background"/>.</summary>
    public static void Unplace(GeneratedPromoTemplate template, string background, GeneratedLayer layer)
    {
        if (Entry(template, background) is { } tracked)
        {
            if (GeneratedPromoResolver.Placement(template, background, layer) is { } placed) tracked.Layers.Remove(placed);
            if (GeneratedPromoResolver.Recolour(template, background, layer) is { } colour) tracked.Colors?.Remove(colour);
            if (GeneratedPromoResolver.Retiming(template, background, layer) is { } timing) tracked.Timings?.Remove(timing);
        }

        Tidy(template);
    }

    /// <summary>Puts only the layer's timing back to the template's, over <paramref name="background"/>.</summary>
    public static void Untime(GeneratedPromoTemplate template, string background, GeneratedLayer layer)
    {
        if (GeneratedPromoResolver.Retiming(template, background, layer) is { } timing)
        {
            Entry(template, background)?.Timings?.Remove(timing);
        }

        Tidy(template);
    }

    /// <summary>Whether <paramref name="background"/> places, times or colours the layer its own way.</summary>
    public static bool Customised(GeneratedPromoTemplate template, string? background, GeneratedLayer layer) =>
        GeneratedPromoResolver.Placement(template, background, layer) is not null
        || GeneratedPromoResolver.Recolour(template, background, layer) is not null
        || GeneratedPromoResolver.Retiming(template, background, layer) is not null;

    /// <summary>How many layers <paramref name="background"/> places or colours its own way.</summary>
    public static int PlacedCount(GeneratedPromoTemplate template, string background) =>
        template.Layers.Count(layer => Customised(template, background, layer));

    private static GeneratedBackgroundOverride? Entry(GeneratedPromoTemplate template, string background) =>
        template.BackgroundOverrides?.FirstOrDefault(candidate => string.Equals(candidate.Background, background, StringComparison.OrdinalIgnoreCase));

    /// <summary>Follows a background file being swapped for another (null: removed), keeping or dropping its placements.</summary>
    public static void RenameBackground(GeneratedPromoTemplate template, string from, string? to)
    {
        foreach (GeneratedBackgroundRule rule in (template.BackgroundRules ?? [])
                     .Where(rule => string.Equals(rule.Background, from, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (string.IsNullOrWhiteSpace(to)) template.BackgroundRules!.Remove(rule);
            else rule.Background = to;
        }
        if (template.BackgroundRules is { Count: 0 }) template.BackgroundRules = null;
        foreach (GeneratedBackgroundOverride tracked in (template.BackgroundOverrides ?? [])
                     .Where(candidate => string.Equals(candidate.Background, from, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (string.IsNullOrWhiteSpace(to))
            {
                template.BackgroundOverrides!.Remove(tracked);
            }
            else
            {
                tracked.Background = to;
            }
        }

        Tidy(template);
    }

    /// <summary>Drops a deleted layer's placements.</summary>
    public static void ForgetLayer(GeneratedPromoTemplate template, GeneratedLayer layer)
    {
        if (layer.Id is { Length: > 0 } id)
        {
            // Keep the mask editable and invisible until the user chooses a new target.
            foreach (GeneratedLayer mask in template.Layers.Where(candidate => candidate.Mask?.Target == id))
                mask.Mask!.Target = null;
            foreach (GeneratedBackgroundOverride tracked in template.BackgroundOverrides ?? [])
            {
                tracked.Layers.RemoveAll(placed => string.Equals(placed.Layer, id, StringComparison.Ordinal));
                tracked.Colors?.RemoveAll(colour => string.Equals(colour.Layer, id, StringComparison.Ordinal));
                tracked.Timings?.RemoveAll(timing => string.Equals(timing.Layer, id, StringComparison.Ordinal));
            }
        }

        Tidy(template);
    }

    // Empty lists go, so the YAML and render keys stay as if there had never been any.
    private static void Tidy(GeneratedPromoTemplate template)
    {
        foreach (GeneratedBackgroundOverride tracked in template.BackgroundOverrides ?? [])
        {
            if (tracked.Colors is { Count: 0 }) tracked.Colors = null;
            if (tracked.Timings is { Count: 0 }) tracked.Timings = null;
        }

        template.BackgroundOverrides?.RemoveAll(tracked => tracked.Layers.Count == 0 && tracked.Colors is null && tracked.Timings is null);
        if (template.BackgroundOverrides is { Count: 0 })
        {
            template.BackgroundOverrides = null;
        }
    }

    private static string NewLayerId(GeneratedPromoTemplate template)
    {
        while (true)
        {
            string id = Guid.NewGuid().ToString("N")[..8];
            if (!template.Layers.Any(layer => string.Equals(layer.Id, id, StringComparison.Ordinal)))
            {
                return id;
            }
        }
    }

    private static T Add<T>(List<T> list, T item)
    {
        list.Add(item);
        return item;
    }

    private static GeneratedKeyframe CopyKeyframe(GeneratedKeyframe key) => new()
    {
        At = key.At, X = key.X, Y = key.Y, Width = key.Width, Height = key.Height, Opacity = key.Opacity, Easing = key.Easing,
        Rotation = key.Rotation, Corners = PromoCorners.From(key.Corners).ToConfig()
    };

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

    // These work on a layer, or on one background's placement of it (both are poses).

    public static bool HasKeyframes(IGeneratedPose layer) => layer.Keyframes is { Count: > 0 };

    /// <summary>Where the layer is, how big and how opaque, at <paramref name="time"/>, following its keyframes.</summary>
    public static LayerPose PoseAt(IGeneratedPose layer, TimeSpan time)
    {
        IReadOnlyList<PromoKeyframe>? keyframes = GeneratedPromoResolver.Keyframes(layer);
        (SKRect bounds, float opacity) = PromoAnimation.Pose(SKRect.Create(layer.X, layer.Y, layer.Width, layer.Height), layer.Opacity, keyframes, time);
        return new LayerPose(bounds.Left, bounds.Top, bounds.Width, bounds.Height, opacity,
            PromoAnimation.RotationAt(GeneratedPromoResolver.Degrees(layer.Rotation), keyframes, time),
            PromoAnimation.CornersAt(PromoCorners.From(layer.Corners), keyframes, time));
    }

    /// <summary>The keyframe at <paramref name="time"/>, if there is one.</summary>
    public static GeneratedKeyframe? KeyframeAt(IGeneratedPose layer, TimeSpan time) =>
        layer.Keyframes?.Where(key => Math.Abs((key.At - time).TotalSeconds) <= KeyframeTolerance)
            .OrderBy(key => Math.Abs((key.At - time).TotalSeconds))
            .FirstOrDefault();

    /// <summary>
    /// Sets the layer's pose at <paramref name="time"/>: its own position, size and opacity when it has no keyframes,
    /// otherwise the keyframe there, which is added if there isn't one yet.
    /// </summary>
    public static void SetPose(IGeneratedPose layer, TimeSpan time, LayerPose pose)
    {
        pose = pose with { Width = Math.Max(1, pose.Width), Height = Math.Max(1, pose.Height), Opacity = Math.Clamp(pose.Opacity, 0, 1) };
        if (!HasKeyframes(layer))
        {
            (layer.X, layer.Y, layer.Width, layer.Height, layer.Opacity) = (pose.X, pose.Y, pose.Width, pose.Height, pose.Opacity);
            if (pose.Rotation is { } turned)
            {
                // Nothing rather than 0, so unrotated layers keep their render keys.
                layer.Rotation = Math.Abs(turned) < 0.0005f ? null : turned;
            }

            if (pose.Corners is { } pinned)
            {
                layer.Corners = pinned.ToConfig();
            }

            return;
        }

        GeneratedKeyframe key = KeyframeAt(layer, time) ?? AddKeyframe(layer, time);
        (key.X, key.Y, key.Width, key.Height, key.Opacity) = (pose.X, pose.Y, pose.Width, pose.Height, pose.Opacity);
        if (pose.Rotation is { } rotation)
        {
            key.Rotation = rotation;
        }

        if (pose.Corners is { } corners)
        {
            key.Corners = corners.ToConfig();
        }
    }

    /// <summary>
    /// A keyframe at <paramref name="time"/> holding the pose the layer has there, so adding it changes nothing until it's
    /// moved. It eases on the way the keyframe before it does. An existing keyframe there is returned instead.
    /// </summary>
    public static GeneratedKeyframe AddKeyframe(IGeneratedPose layer, TimeSpan time)
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
            Rotation = pose.Rotation ?? 0,
            Corners = pose.Corners?.ToConfig(),
            Easing = keys.Where(other => other.At <= time).OrderBy(other => other.At).LastOrDefault()?.Easing ?? GeneratedEasing.Linear
        };
        keys.Add(key);
        SortKeyframes(layer);
        return key;
    }

    /// <summary>Starts animating the layer with one keyframe, at <paramref name="time"/>, where it is now.</summary>
    public static GeneratedKeyframe StartKeyframes(IGeneratedPose layer, TimeSpan time)
    {
        layer.Keyframes = null;
        return AddKeyframe(layer, time);
    }

    /// <summary>Stops animating the layer, leaving it where it is at <paramref name="time"/>.</summary>
    public static void StopKeyframes(IGeneratedPose layer, TimeSpan time)
    {
        LayerPose pose = PoseAt(layer, time);
        layer.Keyframes = null;
        SetPose(layer, time, pose);
    }

    /// <summary>Removes a keyframe; removing the last one stops the animation, leaving the layer where that keyframe had it.</summary>
    public static void RemoveKeyframe(IGeneratedPose layer, GeneratedKeyframe key)
    {
        if (layer.Keyframes is not { } keys || !keys.Remove(key))
        {
            return;
        }

        if (keys.Count == 0)
        {
            layer.Keyframes = null;
            (layer.X, layer.Y, layer.Width, layer.Height, layer.Opacity) = (key.X, key.Y, key.Width, key.Height, key.Opacity);
            layer.Rotation = Math.Abs(key.Rotation) < 0.0005f ? null : key.Rotation;
            layer.Corners = PromoCorners.From(key.Corners).ToConfig();
        }
    }

    /// <summary>Moves a keyframe to <paramref name="time"/> (within the promo), keeping the list in time order.</summary>
    public static void MoveKeyframe(IGeneratedPose layer, GeneratedKeyframe key, TimeSpan time, TimeSpan duration)
    {
        key.At = RoundTime(time < TimeSpan.Zero ? TimeSpan.Zero : time > duration ? duration : time);
        SortKeyframes(layer);
    }

    public static void SortKeyframes(IGeneratedPose layer)
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
/// <param name="Rotation">Degrees clockwise; when setting a pose, null leaves the rotation as it is.</param>
/// <param name="Corners">The corner pin; when setting a pose, null leaves it as it is.</param>
public readonly record struct LayerPose(float X, float Y, float Width, float Height, float Opacity, float? Rotation = null, PromoCorners? Corners = null);
