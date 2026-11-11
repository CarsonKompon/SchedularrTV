using SchedularrTV.Core.Configuration;
using SchedularrTV.GeneratedPromos;
using SkiaSharp;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>Videos that don't loop hold their last frame: video layers, and background videos set to.</summary>
public sealed class VideoHoldTests
{
    private static PromoVideoLayer Video(bool loop, double startAt = 0) => new()
    {
        Path = "clip.mp4", Bounds = SKRect.Create(0, 0, 100, 100), Loop = loop, SourceStart = TimeSpan.FromSeconds(startAt)
    };

    [Fact]
    public void A_video_that_doesnt_loop_holds_from_just_before_its_end()
    {
        PromoVideoLayer once = Video(loop: false);

        Assert.False(once.HoldsAt(TimeSpan.FromSeconds(4), 5, 30));
        Assert.True(once.HoldsAt(TimeSpan.FromSeconds(4.96), 5, 30));
        Assert.True(once.HoldsAt(TimeSpan.FromSeconds(12), 5, 30));
        // Started 3s into the file, it runs out 2s in.
        Assert.True(Video(loop: false, startAt: 3).HoldsAt(TimeSpan.FromSeconds(2.5), 5, 30));
    }

    [Fact]
    public void A_looping_video_or_one_of_unknown_length_never_holds()
    {
        Assert.False(Video(loop: true).HoldsAt(TimeSpan.FromSeconds(12), 5, 30));
        Assert.False(Video(loop: false).HoldsAt(TimeSpan.FromSeconds(12), 0, 30));
        Assert.False(Video(loop: false).HoldsAt(TimeSpan.FromSeconds(12), double.NaN, 30));
    }

    [Fact]
    public void Backgrounds_loop_unless_set_to_hold_and_the_setting_is_left_out_until_used()
    {
        var template = new GeneratedPromoTemplate();
        Assert.Null(template.BackgroundHold);
        Assert.DoesNotContain("BackgroundHold", System.Text.Json.JsonSerializer.Serialize(template));
        Assert.True(new PromoComposition { Duration = TimeSpan.FromSeconds(15) }.BackgroundLoop);
    }
}
