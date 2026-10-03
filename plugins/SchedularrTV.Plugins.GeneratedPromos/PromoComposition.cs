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

public sealed record PromoMusic(string Path, double Volume, TimeSpan StartAt, TimeSpan FadeIn, TimeSpan FadeOut, bool DuckUnderVoice, bool Loop = true);

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
