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
    public required SKRect Bounds { get; init; }

    public TimeSpan Start { get; init; }

    /// <summary>Null keeps the layer until the promo ends.</summary>
    public TimeSpan? End { get; init; }

    public PromoTransition In { get; init; } = PromoTransition.Fade;

    public PromoTransition Out { get; init; } = PromoTransition.Fade;

    public TimeSpan TransitionDuration { get; init; } = TimeSpan.FromMilliseconds(400);

    public float Opacity { get; init; } = 1;
}

public sealed record PromoRectLayer : PromoLayer
{
    public SKColor Color { get; init; } = SKColors.Black;

    public float CornerRadius { get; init; }
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

    public int FrameCount => (int)Math.Ceiling(Duration.TotalSeconds * Fps);
}

public readonly record struct PromoLayerFrame(float Opacity, float OffsetX, float OffsetY, float Scale);

public sealed record PromoMusic(string Path, double Volume, TimeSpan StartAt, TimeSpan FadeIn, TimeSpan FadeOut, bool DuckUnderVoice, bool Loop = true)
{
    /// <summary>
    /// How loud it is over the promo, 0 to 1, straight lines between the points (a step where two share a time);
    /// null leaves it at full volume. Layers that mute the episode clip make these.
    /// </summary>
    public IReadOnlyList<PromoLevel>? Levels { get; init; }

    /// <summary>For the music: dips while the episode clip's sound (<see cref="PromoComposition.BackgroundAudio"/>) is playing.</summary>
    public bool DuckUnderBackground { get; init; }
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
            double length = Math.Min(layer.TransitionDuration.TotalSeconds, Math.Max(0, end - start));
            foreach (double edge in (double[])[start, end])
            {
                Add(edge - Step);
                Add(edge);
            }

            for (int sample = 1; sample <= Samples && length > 0; sample++)
            {
                Add(start + length * sample / Samples);
                Add(end - length * sample / Samples);
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

        double length = layer.TransitionDuration.TotalSeconds;
        float entering = Ease(length <= 0 ? 1 : (time - layer.Start).TotalSeconds / length);
        float leaving = Ease(length <= 0 ? 1 : (end - time).TotalSeconds / length);
        float distance = canvasHeight * 0.12f;

        var frame = new PromoLayerFrame(layer.Opacity, 0, 0, 1);
        frame = Apply(frame, layer.In, entering, distance, entering: true);
        return Apply(frame, layer.Out, leaving, distance, entering: false);
    }

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
