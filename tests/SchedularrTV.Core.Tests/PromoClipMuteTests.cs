using System.Diagnostics;
using SchedularrTV.Core.Configuration;
using SchedularrTV.GeneratedPromos;
using SkiaSharp;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class PromoClipMuteTests
{
    private static readonly TimeSpan Ten = TimeSpan.FromSeconds(10);

    private static PromoRectLayer Box(double start, double? end, PromoTransition transition, double fade = 0.4, float opacity = 1) => new()
    {
        Bounds = SKRect.Empty,
        Start = TimeSpan.FromSeconds(start),
        End = end is { } until ? TimeSpan.FromSeconds(until) : null,
        In = transition,
        Out = transition,
        TransitionDuration = TimeSpan.FromSeconds(fade),
        Opacity = opacity
    };

    private static double At(IReadOnlyList<PromoLevel> points, double seconds)
    {
        // Straight lines between points, as the ffmpeg expression does.
        int next = 0;
        while (next < points.Count && points[next].At.TotalSeconds <= seconds) next++;
        if (next == 0) return points[0].Level;
        PromoLevel from = points[next - 1];
        if (next == points.Count) return from.Level;
        PromoLevel to = points[next];
        double span = (to.At - from.At).TotalSeconds;
        return from.Level + (to.Level - from.Level) * (seconds - from.At.TotalSeconds) / span;
    }

    [Fact]
    public void A_box_fading_in_fades_the_clip_out_and_back_as_it_leaves()
    {
        IReadOnlyList<PromoLevel> points = PromoAudioEnvelope.ForBlockers([Box(2, 8, PromoTransition.Fade)], Ten);

        Assert.Equal(1, At(points, 1), 3);
        Assert.InRange(At(points, 2.1), 0.05, 0.95);
        Assert.Equal(0, At(points, 5), 3);
        Assert.InRange(At(points, 7.9), 0.05, 0.95);
        Assert.Equal(1, At(points, 9), 3);
    }

    [Fact]
    public void A_box_cutting_in_cuts_the_sound_and_a_see_through_one_turns_it_down()
    {
        IReadOnlyList<PromoLevel> cut = PromoAudioEnvelope.ForBlockers([Box(3, null, PromoTransition.None)], Ten);
        Assert.Equal(1, At(cut, 2.998), 3);
        Assert.Equal(0, At(cut, 3.0), 3);
        Assert.Equal(0, At(cut, 9.5), 3);

        IReadOnlyList<PromoLevel> half = PromoAudioEnvelope.ForBlockers([Box(0, null, PromoTransition.None, opacity: 0.5f)], Ten);
        Assert.Equal(0.5, At(half, 5), 3);
    }

    [Fact]
    public void Only_marked_layers_mute_the_clip()
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext();
        Assert.Null(GeneratedPromoResolver.ClipLevels(template));

        template.Layers[0].MutesClip = true;
        Assert.NotNull(GeneratedPromoResolver.ClipLevels(template));
    }

    [Fact]
    public async Task Ffmpeg_reads_the_volume_curve()
    {
        IReadOnlyList<PromoLevel> points = PromoAudioEnvelope.ForBlockers([Box(0.2, 0.8, PromoTransition.Fade, fade: 0.2)], TimeSpan.FromSeconds(1));
        var info = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in (string[])["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=d=1",
                     "-filter_complex", $"[0:a]volume='{PromoAudioEnvelope.Expression(points)}':eval=frame[a]", "-map", "[a]", "-f", "null", "-"])
        {
            info.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(info)!;
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }
}
