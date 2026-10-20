using SchedularrTV.Core.Configuration;
using SchedularrTV.CreditSqueeze;
using SchedularrTV.GeneratedPromos;
using SchedularrTV.Overlays;
using SkiaSharp;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class CanvasTypographyTests
{
    [Fact]
    public void Legacy_bold_remains_compatible_and_explicit_weight_and_italic_survive_yaml()
    {
        var legacy = SchedulerConfigLoader.ConvertSection<GeneratedLayer>(new Dictionary<string, object?> { ["bold"] = false })!;
        Assert.Equal(400, GeneratedTextStyle.Weight(legacy.FontWeight, legacy.Bold));
        Assert.Equal(700, GeneratedTextStyle.Weight(null, true));
        var restored = SchedulerConfigLoader.ConvertSection<GeneratedLayer>(new GeneratedLayer { Bold = true, FontWeight = 300, Italic = true })!;
        Assert.Equal(300, GeneratedTextStyle.Weight(restored.FontWeight, restored.Bold));
        Assert.True(restored.Italic);
        using var cache = new PromoImageCache();
        string family = SKTypeface.Default.FamilyName;
        Assert.Same(cache.Typeface(family, false), cache.Typeface(family, true, 400));
        Assert.Same(cache.Typeface(family, true), cache.Typeface(family, false, 700));
    }

    [Fact]
    public void Credits_normal_text_ocr_pages_and_fallback_apply_the_same_style_and_tokens()
    {
        var layer = new GeneratedLayer { Kind = GeneratedLayerKind.Text, Text = "{up_next} / {next}", Width = 400, Height = 200,
            FontWeight = 600, Italic = true, Shadow = false, TextCase = GeneratedTextCase.Upper, LetterSpacing = 10, LineSpacing = 1.5f };
        var canvas = new SqueezeCanvas { Layers = [layer] };
        var job = new SqueezeJob("credits.mp4", TimeSpan.Zero, TimeSpan.FromSeconds(10), new("Next Show", null, "8 PM", null), [], "Channel", "Up next", "#FFFFFF", "#000000", 1, false);
        var text = Assert.IsType<PromoTextLayer>(Assert.Single(canvas.Compose(job).Layers));
        Assert.Equal(["NEXT SHOW / NEXT SHOW"], text.Lines);
        Assert.Equal(600, text.FontWeight);
        Assert.True(text.Italic);
        Assert.Null(text.Shadow);
        Assert.Equal(.1f, text.LetterSpacing);
        Assert.Equal(1.5f, text.LineSpacing);
        layer.CreditsText = new() { FallbackText = "Next: {up_next}" };
        var raw = new CreditsOcrResult([new("Alex Smith", 95)], 1);
        var ocr = Assert.IsType<PromoTextLayer>(Assert.Single(canvas.Compose(job with { OcrTranscript = raw }).Layers));
        Assert.Equal(["ALEX SMITH"], ocr.LinesAt(TimeSpan.Zero));
        Assert.Equal("Alex Smith", raw.Lines[0].Text);
        var fallback = Assert.IsType<PromoTextLayer>(Assert.Single(canvas.Compose(job with { OcrTranscript = new([], 0, Failure: "test") }).Layers));
        Assert.Equal(["NEXT: NEXT SHOW"], fallback.LinesAt(TimeSpan.Zero));
    }

    [Fact]
    public void Promo_composition_and_overlay_cache_include_font_style()
    {
        var layer = new GeneratedLayer { Kind = GeneratedLayerKind.Text, FontWeight = 500, Italic = true, Width = 300, Height = 100 };
        var template = new GeneratedPromoTemplate { Layers = [layer] };
        var resolved = new ResolvedGeneratedPromo(template, [new[] { "Example" }], null, "test");
        var text = Assert.Single(GeneratedPromoResolver.Compose(resolved).Layers.OfType<PromoTextLayer>());
        Assert.Equal(500, text.FontWeight);
        Assert.True(text.Italic);
        var overlay = new Overlay { Layers = [new OverlayLayer { Kind = OverlayLayerKinds.Text, Text = "Example" }] };
        var tokens = new Dictionary<string, string>();
        string original = OverlayRenderer.Key(overlay, tokens);
        overlay.Layers[0].FontWeight = 500;
        string weighted = OverlayRenderer.Key(overlay, tokens);
        Assert.NotEqual(original, weighted);
        overlay.Layers[0].Italic = true;
        Assert.NotEqual(weighted, OverlayRenderer.Key(overlay, tokens));
    }

    [Fact]
    public void Italic_changes_the_rendered_pixels()
    {
        var text = new PromoTextLayer { Lines = ["Italic typography"], Bounds = SKRect.Create(10, 10, 500, 120),
            FontFamily = SKTypeface.Default.FamilyName, MaxFontSize = 50, FontWeight = 400, Shadow = null, In = PromoTransition.None, Out = PromoTransition.None };
        byte[] Draw(PromoTextLayer layer)
        {
            using var surface = SKSurface.Create(new SKImageInfo(520, 140));
            using var cache = new PromoImageCache();
            PromoRenderer.DrawFrame(surface.Canvas, new PromoComposition { Width = 520, Height = 140, Duration = TimeSpan.FromSeconds(2), Layers = [layer] }, TimeSpan.FromSeconds(1), cache);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        Assert.False(Draw(text).SequenceEqual(Draw(text with { Italic = true })));
    }
}
