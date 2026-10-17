using SkiaSharp;

namespace SchedularrTV.GeneratedPromos;

public enum PromoTransition
{
    None,
    Fade,
    SlideLeft,
    SlideRight,
    SlideUp,
    SlideDown,
    Zoom
}

/// <summary>One drawable layer. Layers draw in list order, so later layers sit on top.</summary>
public abstract record PromoLayer
{
    public string? Id { get; init; }

    /// <summary>A mask is drawn only into its target's alpha, never onto the background.</summary>
    public PromoLayerMask? Mask { get; init; }

    public required SKRect Bounds { get; init; }

    public TimeSpan Start { get; init; }

    /// <summary>Null keeps the layer until the promo ends.</summary>
    public TimeSpan? End { get; init; }

    public PromoTransition In { get; init; } = PromoTransition.Fade;

    public PromoTransition Out { get; init; } = PromoTransition.Fade;

    /// <summary>How long the enter transition takes (and the exit, unless <see cref="TransitionOutDuration"/> is set).</summary>
    public TimeSpan TransitionDuration { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>How long the exit transition takes; null takes as long as the enter.</summary>
    public TimeSpan? TransitionOutDuration { get; init; }

    /// <summary>The exit transition's length: its own, else the enter's.</summary>
    public TimeSpan OutDuration => TransitionOutDuration ?? TransitionDuration;

    public float Opacity { get; init; } = 1;

    /// <summary>In time order; while there are any they set the bounds and opacity instead of <see cref="Bounds"/> and <see cref="Opacity"/>.</summary>
    public IReadOnlyList<PromoKeyframe>? Keyframes { get; init; }

    /// <summary>Degrees clockwise about the middle of <see cref="Bounds"/>.</summary>
    public float Rotation { get; init; }

    /// <summary>Corner pin: the box's contents stretched with perspective to its four pulled corners (all zero: none).</summary>
    public PromoCorners Corners { get; init; }

    /// <summary>
    /// The group this layer is inside, or null at the top. Its bounds are then in the group's space (from the group's
    /// top-left corner), and the group's movement, rotation, opacity and timing apply on top of its own.
    /// </summary>
    public PromoGroupLayer? Parent { get; init; }
}

/// <summary>Draws nothing: places the layers inside it (see <see cref="PromoLayer.Parent"/>).</summary>
public sealed record PromoGroupLayer : PromoLayer;

public sealed record PromoLayerMask(SchedularrTV.Core.Configuration.GeneratedMaskMode Mode, string? Target);

public enum PromoVerticalAlign
{
    Top,
    Middle,
    Bottom
}

public enum PromoEasing
{
    Linear,
    EaseIn,
    EaseOut,
    EaseInOut,
    Hold
}

/// <summary>A layer's bounds and opacity at <see cref="At"/>; <see cref="Easing"/> is how it moves on to the next one.</summary>
public readonly record struct PromoKeyframe(TimeSpan At, SKRect Bounds, float Opacity, PromoEasing Easing = PromoEasing.Linear, float Rotation = 0,
    PromoCorners Corners = default);

public sealed record PromoRectLayer : PromoLayer
{
    public SKColor Color { get; init; } = SKColors.Black;

    public float CornerRadius { get; init; }

    public SchedularrTV.Core.Configuration.GeneratedShape Shape { get; init; }

    // Rotation is on every layer now (PromoLayer.Rotation), so shapes, text, pictures and groups all turn the same way.
    public float SoftEdges { get; init; }
}

public sealed record PromoTextLayer : PromoLayer
{
    public required IReadOnlyList<string> Lines { get; init; }

    public SKColor Color { get; init; } = SKColors.White;

    /// <summary>Text shrinks from this size until every line fits the bounds.</summary>
    public float MaxFontSize { get; init; } = 96;

    public string FontFamily { get; init; } = "Segoe UI";

    public bool Bold { get; init; } = true;

    public SKTextAlign Align { get; init; } = SKTextAlign.Left;

    public PromoVerticalAlign VerticalAlign { get; init; } = PromoVerticalAlign.Top;

    /// <summary>Long lines break between words to fill the box's width before the text shrinks.</summary>
    public bool WordWrap { get; init; }

    /// <summary>Extra space between letters, as a fraction of the font size (0 is the font's own).</summary>
    public float LetterSpacing { get; init; }

    /// <summary>The distance between lines, as a multiple of the font's own.</summary>
    public float LineSpacing { get; init; } = 1;

    public SKColor? Shadow { get; init; } = new SKColor(0, 0, 0, 160);
}

public sealed record PromoImageLayer : PromoLayer
{
    public required string Path { get; init; }
}

public sealed record PromoComposition
{
    public int Width { get; init; } = 1920;

    public int Height { get; init; } = 1080;

    public int Fps { get; init; } = 30;

    public required TimeSpan Duration { get; init; }

    public SKColor BackgroundColor { get; init; } = new(16, 24, 40);

    /// <summary>Image or video filling the frame behind every layer. Videos loop.</summary>
    public string? BackgroundPath { get; init; }

    /// <summary>Offset into a background video where the promo starts.</summary>
    public TimeSpan BackgroundStart { get; init; }

    /// <summary>The background video's own sound, mixed in like music.</summary>
    public PromoMusic? BackgroundAudio { get; init; }

    /// <summary>A finished video laid over the background, scaled to fit and held on its last frame; may be transparent.</summary>
    public string? OverlayPath { get; init; }

    public PromoMusic? OverlayAudio { get; init; }

    public IReadOnlyList<PromoLayer> Layers { get; init; } = [];

    public string? AudioPath { get; init; }

    public TimeSpan AudioStart { get; init; }

    /// <summary>Playback speed for the voice, above 1 to fit a long read into the promo.</summary>
    public double AudioTempo { get; init; } = 1;

    public PromoMusic? Music { get; init; }

    /// <summary>The sound of video layers that include it, each starting and fading with its layer.</summary>
    public IReadOnlyList<PromoMusic> LayerAudio { get; init; } = [];

    public int FrameCount => (int)Math.Ceiling(Duration.TotalSeconds * Fps);
}

/// <param name="Bounds">Where the layer is at this moment: its keyframed bounds, or its own when it has none.</param>
/// <param name="Rotation">Degrees clockwise about the middle of <paramref name="Bounds"/>.</param>
public readonly record struct PromoLayerFrame(float Opacity, float OffsetX, float OffsetY, float Scale, SKRect Bounds = default, float Rotation = 0,
    PromoCorners Corners = default);

public sealed record PromoMusic(string Path, double Volume, TimeSpan StartAt, TimeSpan FadeIn, TimeSpan FadeOut, bool DuckUnderVoice, bool Loop = true)
{
    /// <summary>
    /// How loud it is over the promo, 0 to 1, straight lines between the points (a step where two share a time);
    /// null leaves it at full volume. Layers that mute the episode clip make these.
    /// </summary>
    public IReadOnlyList<PromoLevel>? Levels { get; init; }

    /// <summary>For the music: dips while the episode clip's sound (<see cref="PromoComposition.BackgroundAudio"/>) is playing.</summary>
    public bool DuckUnderBackground { get; init; }

    /// <summary>How far into the promo it starts (silent until then); <see cref="Levels"/> are still in promo time.</summary>
    public TimeSpan Delay { get; init; }
}

/// <summary>A sound's level (0 to 1) at a point in the promo.</summary>
public readonly record struct PromoLevel(TimeSpan At, double Level);

/// <summary>
/// Turns layers that block the episode clip's sound into a volume curve: the more opaque the layer (as it fades or
/// slides in and out), the quieter the clip, and a layer that cuts in cuts the sound. Several multiply.
/// </summary>
public static class PromoAudioEnvelope
{
    // A hard cut becomes a step this long, and each transition is followed with this many points.
    private const double Step = 0.001;
    private const int Samples = 8;

    public static IReadOnlyList<PromoLevel> ForBlockers(IReadOnlyList<PromoLayer> blockers, TimeSpan duration)
    {
        double total = duration.TotalSeconds;
        SortedSet<double> times = [0, total];
        void Add(double time)
        {
            if (time >= 0 && time <= total)
            {
                times.Add(Math.Round(time, 4));
            }
        }

        foreach (PromoLayer layer in blockers)
        {
            double start = layer.Start.TotalSeconds;
            double end = (layer.End ?? duration).TotalSeconds;
            double entering = Math.Min(layer.TransitionDuration.TotalSeconds, Math.Max(0, end - start));
            double leaving = Math.Min(layer.OutDuration.TotalSeconds, Math.Max(0, end - start));
            foreach (double edge in (double[])[start, end])
            {
                Add(edge - Step);
                Add(edge);
            }

            for (int sample = 1; sample <= Samples; sample++)
            {
                if (entering > 0) Add(start + entering * sample / Samples);
                if (leaving > 0) Add(end - leaving * sample / Samples);
            }
        }

        List<PromoLevel> points = [.. times.Select(time => new PromoLevel(TimeSpan.FromSeconds(time), Level(blockers, time, duration)))];

        // Points in the middle of a flat stretch say nothing.
        List<PromoLevel> kept = [];
        for (int index = 0; index < points.Count; index++)
        {
            bool flat = index > 0 && index < points.Count - 1
                && Math.Abs(points[index - 1].Level - points[index].Level) < 0.0005
                && Math.Abs(points[index + 1].Level - points[index].Level) < 0.0005;
            if (!flat)
            {
                kept.Add(points[index]);
            }
        }

        return kept;
    }

    /// <summary>
    /// A sound that follows a layer: as loud as the layer is opaque (with the groups it's in), silent while it's off
    /// screen. Sampled every 50 ms, which follows fades, transitions and keyframed opacity closely enough to hear.
    /// </summary>
    public static IReadOnlyList<PromoLevel> ForLayer(PromoLayer layer, TimeSpan duration)
    {
        double total = duration.TotalSeconds;
        SortedSet<double> times = [0, total];
        for (double time = 0; time < total; time += 0.05)
        {
            times.Add(Math.Round(time, 4));
        }

        // Hard edges where it appears and leaves.
        foreach (double edge in (double[])[layer.Start.TotalSeconds, (layer.End ?? duration).TotalSeconds])
        {
            if (edge - Step >= 0 && edge <= total) times.Add(Math.Round(edge - Step, 4));
            if (edge >= 0 && edge <= total) times.Add(Math.Round(edge, 4));
        }

        List<PromoLevel> points = [.. times.Select(time => new PromoLevel(TimeSpan.FromSeconds(time),
            Math.Clamp(PromoAnimation.EvaluateInGroups(layer, TimeSpan.FromSeconds(time), duration, 1)?.Opacity ?? 0, 0, 1)))];

        List<PromoLevel> kept = [];
        for (int index = 0; index < points.Count; index++)
        {
            bool flat = index > 0 && index < points.Count - 1
                && Math.Abs(points[index - 1].Level - points[index].Level) < 0.0005
                && Math.Abs(points[index + 1].Level - points[index].Level) < 0.0005;
            if (!flat)
            {
                kept.Add(points[index]);
            }
        }

        return kept;
    }

    /// <summary>The level at <paramref name="seconds"/>: what every blocker lets through.</summary>
    public static double Level(IReadOnlyList<PromoLayer> blockers, double seconds, TimeSpan duration) =>
        blockers.Aggregate(1.0, (level, layer) =>
            level * (1 - Math.Clamp(PromoAnimation.Evaluate(layer, TimeSpan.FromSeconds(seconds), duration, 1)?.Opacity ?? 0, 0, 1)));

    /// <summary>The curve as an ffmpeg <c>volume</c> expression of <c>t</c>, the bed's own time from the promo's start.</summary>
    public static string Expression(IReadOnlyList<PromoLevel> points)
    {
        if (points.Count == 0)
        {
            return "1";
        }

        string expression = Number(points[^1].Level);
        for (int index = points.Count - 2; index >= 0; index--)
        {
            PromoLevel from = points[index];
            PromoLevel to = points[index + 1];
            double span = (to.At - from.At).TotalSeconds;
            string segment = span <= 0 || Math.Abs(to.Level - from.Level) < 0.0005
                ? Number(from.Level)
                : $"{Number(from.Level)}+({Number(to.Level - from.Level)})*(t-{Number(from.At.TotalSeconds)})/{Number(span)}";
            expression = $"if(lt(t,{Number(to.At.TotalSeconds)}),{segment},{expression})";
        }

        return expression;
    }

    private static string Number(double value) => value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
}

public static class PromoAnimation
{
    /// <summary>Null while the layer is off screen.</summary>
    public static PromoLayerFrame? Evaluate(PromoLayer layer, TimeSpan time, TimeSpan duration, float canvasHeight)
    {
        TimeSpan end = layer.End ?? duration;
        if (time < layer.Start || time >= end)
        {
            return null;
        }

        double inLength = layer.TransitionDuration.TotalSeconds;
        double outLength = layer.OutDuration.TotalSeconds;
        float entering = Ease(inLength <= 0 ? 1 : (time - layer.Start).TotalSeconds / inLength);
        float leaving = Ease(outLength <= 0 ? 1 : (end - time).TotalSeconds / outLength);
        float distance = canvasHeight * 0.12f;

        (SKRect bounds, float opacity) = Pose(layer.Bounds, layer.Opacity, layer.Keyframes, time);
        var frame = new PromoLayerFrame(opacity, 0, 0, 1, bounds, RotationAt(layer.Rotation, layer.Keyframes, time),
            CornersAt(layer.Corners, layer.Keyframes, time));
        frame = Apply(frame, layer.In, entering, distance, entering: true);
        return Apply(frame, layer.Out, leaving, distance, entering: false);
    }

    /// <summary>
    /// The bounds and opacity at <paramref name="time"/>: held at the first keyframe before it and the last after it,
    /// and eased from each keyframe to the next in between. Without keyframes, the layer's own.
    /// </summary>
    public static (SKRect Bounds, float Opacity) Pose(SKRect bounds, float opacity, IReadOnlyList<PromoKeyframe>? keyframes, TimeSpan time)
    {
        if (keyframes is not { Count: > 0 })
        {
            return (bounds, opacity);
        }

        if (time <= keyframes[0].At)
        {
            return (keyframes[0].Bounds, keyframes[0].Opacity);
        }

        for (int index = 0; index < keyframes.Count - 1; index++)
        {
            PromoKeyframe from = keyframes[index];
            PromoKeyframe to = keyframes[index + 1];
            if (time >= to.At)
            {
                continue;
            }

            double span = (to.At - from.At).TotalSeconds;
            float progress = span <= 0 ? 1 : Curve(from.Easing, (time - from.At).TotalSeconds / span);
            return (SKRect.Create(
                    Lerp(from.Bounds.Left, to.Bounds.Left, progress),
                    Lerp(from.Bounds.Top, to.Bounds.Top, progress),
                    Lerp(from.Bounds.Width, to.Bounds.Width, progress),
                    Lerp(from.Bounds.Height, to.Bounds.Height, progress)),
                Lerp(from.Opacity, to.Opacity, progress));
        }

        return (keyframes[^1].Bounds, keyframes[^1].Opacity);
    }

    /// <summary>The rotation at <paramref name="time"/>, following the keyframes the same way as <see cref="Pose"/>.</summary>
    public static float RotationAt(float rotation, IReadOnlyList<PromoKeyframe>? keyframes, TimeSpan time)
    {
        if (keyframes is not { Count: > 0 })
        {
            return rotation;
        }

        if (time <= keyframes[0].At)
        {
            return keyframes[0].Rotation;
        }

        for (int index = 0; index < keyframes.Count - 1; index++)
        {
            PromoKeyframe from = keyframes[index];
            PromoKeyframe to = keyframes[index + 1];
            if (time >= to.At)
            {
                continue;
            }

            double span = (to.At - from.At).TotalSeconds;
            float progress = span <= 0 ? 1 : Curve(from.Easing, (time - from.At).TotalSeconds / span);
            return Lerp(from.Rotation, to.Rotation, progress);
        }

        return keyframes[^1].Rotation;
    }

    /// <summary>The corner pin at <paramref name="time"/>, following the keyframes the same way as <see cref="Pose"/>.</summary>
    public static PromoCorners CornersAt(PromoCorners corners, IReadOnlyList<PromoKeyframe>? keyframes, TimeSpan time)
    {
        if (keyframes is not { Count: > 0 })
        {
            return corners;
        }

        if (time <= keyframes[0].At)
        {
            return keyframes[0].Corners;
        }

        for (int index = 0; index < keyframes.Count - 1; index++)
        {
            PromoKeyframe from = keyframes[index];
            PromoKeyframe to = keyframes[index + 1];
            if (time >= to.At)
            {
                continue;
            }

            double span = (to.At - from.At).TotalSeconds;
            float progress = span <= 0 ? 1 : Curve(from.Easing, (time - from.At).TotalSeconds / span);
            return PromoCorners.Lerp(from.Corners, to.Corners, progress);
        }

        return keyframes[^1].Corners;
    }

    /// <summary>
    /// The layer's frame with every group it's inside applied too: null when it or any of them is off screen, otherwise
    /// its own frame with the opacity of all of them. Where it is on screen comes from <see cref="PromoRenderer"/>, which
    /// stacks the groups' movement and rotation.
    /// </summary>
    public static PromoLayerFrame? EvaluateInGroups(PromoLayer layer, TimeSpan time, TimeSpan duration, float canvasHeight)
    {
        if (Evaluate(layer, time, duration, canvasHeight) is not { } frame)
        {
            return null;
        }

        for (PromoGroupLayer? group = layer.Parent; group is not null; group = group.Parent)
        {
            if (Evaluate(group, time, duration, canvasHeight) is not { } around)
            {
                return null;
            }

            frame = frame with { Opacity = frame.Opacity * around.Opacity };
        }

        return frame;
    }

    /// <summary>How far along (0 to 1) a move with this easing is at <paramref name="progress"/> of its time.</summary>
    public static float Curve(PromoEasing easing, double progress)
    {
        double t = Math.Clamp(progress, 0, 1);
        return (float)(easing switch
        {
            PromoEasing.Hold => t >= 1 ? 1 : 0,
            PromoEasing.EaseIn => t * t * t,
            PromoEasing.EaseOut => 1 - Math.Pow(1 - t, 3),
            PromoEasing.EaseInOut => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2,
            _ => t
        });
    }

    private static float Lerp(float from, float to, float progress) => from + (to - from) * progress;

    private static PromoLayerFrame Apply(PromoLayerFrame frame, PromoTransition transition, float progress, float distance, bool entering)
    {
        // Entering moves towards rest along the named direction; leaving continues the same way.
        float travel = (1 - progress) * distance * (entering ? -1 : 1);
        return transition switch
        {
            PromoTransition.None => frame,
            PromoTransition.Fade => frame with { Opacity = frame.Opacity * progress },
            PromoTransition.SlideLeft => frame with { Opacity = frame.Opacity * progress, OffsetX = frame.OffsetX - travel },
            PromoTransition.SlideRight => frame with { Opacity = frame.Opacity * progress, OffsetX = frame.OffsetX + travel },
            PromoTransition.SlideUp => frame with { Opacity = frame.Opacity * progress, OffsetY = frame.OffsetY - travel },
            PromoTransition.SlideDown => frame with { Opacity = frame.Opacity * progress, OffsetY = frame.OffsetY + travel },
            PromoTransition.Zoom => frame with { Opacity = frame.Opacity * progress, Scale = frame.Scale * (0.8f + 0.2f * progress) },
            _ => frame
        };
    }

    private static float Ease(double progress)
    {
        double clamped = Math.Clamp(progress, 0, 1);
        return (float)(1 - Math.Pow(1 - clamped, 3));
    }
}
