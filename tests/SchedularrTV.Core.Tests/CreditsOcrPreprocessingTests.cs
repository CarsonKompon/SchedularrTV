using SchedularrTV.CreditSqueeze;
using SkiaSharp;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class CreditsOcrPreprocessingTests
{
    [Fact]
    public void Colored_ink_on_a_bright_background_becomes_wider_dark_ink_on_white()
    {
        using var source = new SKBitmap(100, 40);
        source.Erase(new SKColor(235, 220, 90));
        using (var canvas = new SKCanvas(source))
        using (var paint = new SKPaint { Color = new SKColor(200, 35, 50) }) canvas.DrawRect(30, 10, 40, 20, paint);
        using var result = CreditsOcrPreprocessing.Contrast(source);
        Assert.Equal((150, 40), (result.Width, result.Height));
        Assert.Equal(SKColors.White, result.GetPixel(5, 5));
        Assert.Equal(SKColors.Black, result.GetPixel(75, 20));
    }

    [Fact]
    public void White_credits_on_black_are_inverted_and_blank_frames_stay_blank()
    {
        using var source = new SKBitmap(100, 40);
        source.Erase(SKColors.Black);
        using (var canvas = new SKCanvas(source))
        using (var paint = new SKPaint { Color = SKColors.White }) canvas.DrawRect(30, 10, 40, 20, paint);
        using var result = CreditsOcrPreprocessing.Contrast(source);
        Assert.Equal(SKColors.White, result.GetPixel(5, 5));
        Assert.Equal(SKColors.Black, result.GetPixel(75, 20));
        source.Erase(SKColors.Black);
        using var blank = CreditsOcrPreprocessing.Contrast(source);
        Assert.All(blank.Pixels, pixel => Assert.Equal(SKColors.White, pixel));
    }

    [Fact]
    public void Retries_choose_a_supported_result_without_merging_conflicting_text()
    {
        CreditsOcrLine[] weak = [new("garbled", 32)];
        CreditsOcrLine[] readable = [new("POST PRODUCTION ASSISTANTS", 95), new("Matt Brown", 96), new("Michelle Smith", 96)];
        Assert.True(CreditsOcrPreprocessing.NeedsRetry(weak, 60));
        Assert.Same(readable, CreditsOcrPreprocessing.Prefer(weak, readable, 60));
        Assert.Same(readable, CreditsOcrPreprocessing.Prefer(readable, weak, 60));
        Assert.Same(readable, CreditsOcrPreprocessing.Prefer(readable, readable, 60));
    }
}
