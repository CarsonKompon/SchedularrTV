using SchedularrTV.Core.Configuration;

namespace SchedularrTV.Overlays;

/// <summary>A layer's position, size, opacity (0 to 100) and turn at one moment.</summary>
/// <param name="Rotation">Degrees clockwise; when setting a pose, null leaves the turn as it is.</param>
public readonly record struct OverlayPose(float X, float Y, float Width, float Height, double Opacity, float? Rotation = null);

/// <summary>
/// A moment in one appearance of an overlay, as a drawing (the editor's preview, or a frame of an animated overlay's
/// video) sees it: how far in, how long the appearance is, and which fades are drawn into the picture.
/// </summary>
/// <param name="Seconds">From the start of the appearance.</param>
/// <param name="Length">How long the appearance (or a whole-programme overlay's animation) is.</param>
/// <param name="Fade">The overlay's fade, which layers coming in late or going early also fade by.</param>
/// <param name="FadeIn">Fades the whole design in at the start (when ErsatzTV doesn't).</param>
/// <param name="FadeOut">Fades the whole design out at the end (when ErsatzTV doesn't).</param>
/// <param name="LayerOuts">Whether layers go <see cref="OverlayLayer.OutSeconds"/> before the end (not for whole-programme ones, whose end isn't here).</param>
public sealed record OverlayClock(double Seconds, double Length, double Fade, bool FadeIn = false, bool FadeOut = false, bool LayerOuts = true);

/// <summary>Keyframes: where a layer is at a moment of an appearance, and how it comes and goes within it.</summary>
public static class OverlayAnimation
{
    /// <summary>Frames a second, in the editor's timeline and an animated overlay's video, unless the overlay sets its own.</summary>
    public const double DefaultFps = 30;

    /// <summary>The overlay's frame rate: its own (to match its videos), or <see cref="DefaultFps"/>.</summary>
    public static double FpsOf(Overlay overlay) =>
        overlay.Fps is { } fps && double.IsFinite(fps) ? Math.Clamp(fps, 1, 120) : DefaultFps;

    /// <summary>A frame rate as ffmpeg and file names take it: 30, 29.97, 23.976.</summary>
    public static string FpsText(double fps) => fps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Two keyframes closer than this (seconds) are the same one: well under a frame.</summary>
    public const double KeyframeTolerance = 0.002;

    public static bool HasKeyframes(OverlayLayer layer) => layer.Keyframes is { Count: > 0 };

    /// <summary>
    /// Whether a design moves: some layer (other than a video, which ErsatzTV places itself) has keyframes, or a picture
    /// layer shows an animated picture (a GIF).
    /// </summary>
    public static bool IsAnimated(Overlay overlay) =>
        overlay.Kind != OverlayKinds.File && overlay.Layers.Any(layer => layer.Kind != OverlayLayerKinds.Video
            && (HasKeyframes(layer) || (layer.Kind == OverlayLayerKinds.Image && SchedularrTV.Service.Images.AnimatedImages.IsAnimated(layer.Path))));

    /// <summary>How long a whole-programme overlay's animation runs.</summary>
    public static double AnimationSeconds(Overlay overlay) => Math.Clamp(overlay.AnimationSeconds ?? Overlay.DefaultAnimationSeconds, 1, 120);

    /// <summary>The length of one rule's timeline: its appearance, or a whole-programme overlay's animation.</summary>
    public static double Length(Overlay rule) =>
        rule.When == OverlayWhen.Always ? AnimationSeconds(rule) : Math.Max(1, rule.DurationSeconds);

    /// <summary>The editor's timeline: the longest of the overlay's appearances (or its animation, all the way through).</summary>
    public static double TimelineSeconds(Overlay overlay)
    {
        if (overlay.Schedule is not { } schedule)
        {
            return Length(overlay);
        }

        if (schedule.Always)
        {
            return AnimationSeconds(overlay);
        }

        // As the rules have them (see OverlayTiming.Rules), without copying the design for each.
        List<double> durations = schedule.Slots
            .Where(slot => slot.When is OverlayWhen.Start or OverlayWhen.End or OverlayWhen.Percent)
            .Select(slot => slot.DurationSeconds)
            .ToList();
        if (schedule.Automatic is { Enabled: true } automatic)
        {
            durations.Add(automatic.DurationSeconds);
        }

        return durations.Count == 0 ? AnimationSeconds(overlay) : Math.Max(1, durations.Max());
    }

    /// <summary>Where the layer is, how big, how opaque and how turned at <paramref name="seconds"/>, following its keyframes.</summary>
    public static OverlayPose PoseAt(OverlayLayer layer, double seconds)
    {
        if (layer.Keyframes is not { Count: > 0 } unsorted)
        {
            return new OverlayPose(layer.X, layer.Y, layer.Width, layer.Height, layer.Opacity, layer.Rotation ?? 0);
        }

        List<OverlayKeyframe> keys = unsorted.Count == 1 ? unsorted : unsorted.OrderBy(key => key.At).ToList();
        if (seconds <= keys[0].At.TotalSeconds)
        {
            return Of(keys[0]);
        }

        for (int index = 0; index < keys.Count - 1; index++)
        {
            OverlayKeyframe from = keys[index];
            OverlayKeyframe to = keys[index + 1];
            if (seconds >= to.At.TotalSeconds)
            {
                continue;
            }

            double span = (to.At - from.At).TotalSeconds;
            double progress = span <= 0 ? 1 : Curve(from.Easing, (seconds - from.At.TotalSeconds) / span);
            return new OverlayPose(
                Lerp(from.X, to.X, progress), Lerp(from.Y, to.Y, progress),
                Lerp(from.Width, to.Width, progress), Lerp(from.Height, to.Height, progress),
                from.Opacity + (to.Opacity - from.Opacity) * progress,
                Lerp(from.Rotation, to.Rotation, progress));
        }

        return Of(keys[^1]);
    }

    private static OverlayPose Of(OverlayKeyframe key) => new(key.X, key.Y, key.Width, key.Height, key.Opacity, key.Rotation);

    private static float Lerp(float from, float to, double progress) => (float)(from + (to - from) * progress);

    /// <summary>How far along (0 to 1) a move with this easing is at <paramref name="progress"/> of its time.</summary>
    public static double Curve(GeneratedEasing easing, double progress)
    {
        double t = Math.Clamp(progress, 0, 1);
        return easing switch
        {
            GeneratedEasing.Hold => t >= 1 ? 1 : 0,
            GeneratedEasing.EaseIn => t * t * t,
            GeneratedEasing.EaseOut => 1 - Math.Pow(1 - t, 3),
            GeneratedEasing.EaseInOut => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2,
            _ => t
        };
    }

    /// <summary>
    /// How much of the layer shows (0 to 1) at the clock's moment, from its own coming in late and going early: it
    /// fades in and out by the overlay's fade, as ErsatzTV fades a part of a design timed on its own.
    /// </summary>
    public static double Visibility(OverlayLayer layer, OverlayClock clock)
    {
        double t = clock.Seconds;
        double from = Math.Max(0, layer.InSeconds);
        double to = clock.LayerOuts ? clock.Length - Math.Max(0, layer.OutSeconds) : double.PositiveInfinity;
        if (t < from || t >= to)
        {
            return 0;
        }

        if (layer.Kind == OverlayLayerKinds.Video)
        {
            return 1;
        }

        double fade = Math.Max(0, clock.Fade);
        if (!double.IsInfinity(to))
        {
            fade = Math.Min(fade, (to - from) / 3);
        }

        double shown = 1;
        if (fade > 0 && from > 0)
        {
            shown *= Math.Clamp((t - from) / fade, 0, 1);
        }

        if (fade > 0 && clock.LayerOuts && layer.OutSeconds > 0)
        {
            shown *= Math.Clamp((to - t) / fade, 0, 1);
        }

        return shown;
    }

    /// <summary>The whole design's own fade in and out at the clock's moment (0 to 1), where it's drawn in.</summary>
    public static double Envelope(OverlayClock clock)
    {
        double fade = Math.Min(Math.Max(0, clock.Fade), clock.Length / 3);
        if (fade <= 0)
        {
            return 1;
        }

        double shown = 1;
        if (clock.FadeIn)
        {
            shown *= Math.Clamp(clock.Seconds / fade, 0, 1);
        }

        if (clock.FadeOut)
        {
            shown *= Math.Clamp((clock.Length - clock.Seconds) / fade, 0, 1);
        }

        return shown;
    }

    /// <summary>A copy of the design standing still as it is at <paramref name="seconds"/>: every keyframed layer put there, its keyframes gone.</summary>
    public static Overlay Freeze(Overlay overlay, double seconds)
    {
        Overlay frozen = overlay.Copy();
        foreach (OverlayLayer layer in frozen.Layers.Where(HasKeyframes))
        {
            OverlayPose pose = PoseAt(layer, seconds);
            (layer.X, layer.Y, layer.Width, layer.Height, layer.Opacity) = (pose.X, pose.Y, pose.Width, pose.Height, pose.Opacity);
            layer.Rotation = pose.Rotation is { } turn && Math.Abs(turn) >= 0.0005f ? turn : null;
            layer.Keyframes = null;
        }

        return frozen;
    }

    // ---- Editing keyframes ----

    /// <summary>The keyframe at <paramref name="seconds"/>, if there is one.</summary>
    public static OverlayKeyframe? KeyframeAt(OverlayLayer layer, double seconds) =>
        layer.Keyframes?.Where(key => Math.Abs(key.At.TotalSeconds - seconds) <= KeyframeTolerance)
            .OrderBy(key => Math.Abs(key.At.TotalSeconds - seconds))
            .FirstOrDefault();

    /// <summary>
    /// Sets the layer's pose at <paramref name="seconds"/>: its own position, size, opacity and turn when it has no
    /// keyframes, otherwise the keyframe there, which is added if there isn't one yet.
    /// </summary>
    public static void SetPose(OverlayLayer layer, double seconds, OverlayPose pose)
    {
        pose = pose with { Width = Math.Max(1, pose.Width), Height = Math.Max(1, pose.Height), Opacity = Math.Clamp(pose.Opacity, 0, 100) };
        if (!HasKeyframes(layer))
        {
            (layer.X, layer.Y, layer.Width, layer.Height, layer.Opacity) = (pose.X, pose.Y, pose.Width, pose.Height, pose.Opacity);
            if (pose.Rotation is { } turned)
            {
                layer.Rotation = Math.Abs(turned) < 0.0005f ? null : turned;
            }

            return;
        }

        OverlayKeyframe key = KeyframeAt(layer, seconds) ?? AddKeyframe(layer, seconds);
        (key.X, key.Y, key.Width, key.Height, key.Opacity) = (pose.X, pose.Y, pose.Width, pose.Height, pose.Opacity);
        if (pose.Rotation is { } rotation)
        {
            key.Rotation = rotation;
        }
    }

    /// <summary>A keyframe at <paramref name="seconds"/> holding the pose the layer has there; an existing one there is returned instead.</summary>
    public static OverlayKeyframe AddKeyframe(OverlayLayer layer, double seconds)
    {
        if (KeyframeAt(layer, seconds) is { } existing)
        {
            return existing;
        }

        OverlayPose pose = PoseAt(layer, seconds);
        List<OverlayKeyframe> keys = layer.Keyframes ??= [];
        var key = new OverlayKeyframe
        {
            At = RoundTime(seconds),
            X = MathF.Round(pose.X),
            Y = MathF.Round(pose.Y),
            Width = MathF.Round(pose.Width),
            Height = MathF.Round(pose.Height),
            Opacity = pose.Opacity,
            Rotation = pose.Rotation ?? 0,
            Easing = keys.Where(other => other.At.TotalSeconds <= seconds).OrderBy(other => other.At).LastOrDefault()?.Easing ?? GeneratedEasing.Linear
        };
        keys.Add(key);
        SortKeyframes(layer);
        return key;
    }

    /// <summary>Starts animating the layer with one keyframe at <paramref name="seconds"/>, where it is now.</summary>
    public static OverlayKeyframe StartKeyframes(OverlayLayer layer, double seconds)
    {
        layer.Keyframes = null;
        return AddKeyframe(layer, seconds);
    }

    /// <summary>Stops animating the layer, leaving it where it is at <paramref name="seconds"/>.</summary>
    public static void StopKeyframes(OverlayLayer layer, double seconds)
    {
        OverlayPose pose = PoseAt(layer, seconds);
        layer.Keyframes = null;
        SetPose(layer, seconds, pose);
    }

    /// <summary>Removes a keyframe; removing the last one stops the animation, leaving the layer where that keyframe had it.</summary>
    public static void RemoveKeyframe(OverlayLayer layer, OverlayKeyframe key)
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
        }
    }

    /// <summary>Moves a keyframe to <paramref name="seconds"/> (within 0 and <paramref name="last"/>), keeping the list in time order.</summary>
    public static void MoveKeyframe(OverlayLayer layer, OverlayKeyframe key, double seconds, double last)
    {
        key.At = RoundTime(Math.Clamp(seconds, 0, Math.Max(0, last)));
        SortKeyframes(layer);
    }

    public static void SortKeyframes(OverlayLayer layer)
    {
        if (layer.Keyframes is { Count: > 1 } keys)
        {
            layer.Keyframes = [.. keys.OrderBy(key => key.At)];
        }
    }

    /// <summary>Moves a layer, and every keyframe it has, by the same amount.</summary>
    public static void Offset(OverlayLayer layer, float dx, float dy)
    {
        layer.X += dx;
        layer.Y += dy;
        foreach (OverlayKeyframe key in layer.Keyframes ?? [])
        {
            key.X += dx;
            key.Y += dy;
        }
    }

    /// <summary>Scales a layer's keyframes with the canvas.</summary>
    public static void Scale(OverlayLayer layer, float across, float down)
    {
        foreach (OverlayKeyframe key in layer.Keyframes ?? [])
        {
            key.X *= across;
            key.Y *= down;
            key.Width *= across;
            key.Height *= down;
        }
    }

    private static TimeSpan RoundTime(double seconds) => TimeSpan.FromSeconds(Math.Round(Math.Max(0, seconds), 4));
}
