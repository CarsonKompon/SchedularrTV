using SchedularrTV.Core.Configuration;
using SchedularrTV.CreditSqueeze;
using SchedularrTV.GeneratedPromos;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class CreditsOcrPageTests
{
    private static CreditsOcrLine[] Lines(params string[] lines) => lines.Select(text => new CreditsOcrLine(text, 95)).ToArray();
    private static SqueezeJob Job() => new("source.mp4", TimeSpan.FromMinutes(20), TimeSpan.FromSeconds(12),
        new("Next Show", null, "8 PM", null), [], "Channel", "Up next", "#ffffff", "#000000", 1, false);

    [Fact]
    public void Scan_groups_keep_full_lines_blank_scans_and_nonconsecutive_repeats()
    {
        IReadOnlyList<CreditsOcrLine>[] frames = [Lines("Directed by", "Alex"), Lines("Directed by", "Alex"),
            Lines("Alex", "Producer", "Pat", "Jamie"), [], Lines("Directed by", "Alex")];
        var pages = CreditsOcrResult.ScanPages(frames, 2);
        Assert.Equal(new[] { 0d, 4d, 6d, 8d }, pages.Select(page => page.At.TotalSeconds));
        Assert.Equal(new[] { 2, 4, 0, 2 }, pages.Select(page => page.Lines.Count));
        Assert.Equal("Alex", pages[1].Lines[0].Text);
    }

    [Fact]
    public void Matching_pages_follow_source_times_and_clip_to_the_layers_active_window()
    {
        var source = new CreditsOcrResult(Lines("First", "Second", "Third"), 12, Pages:
            [new(TimeSpan.Zero, Lines("First", "Second")), new(TimeSpan.FromSeconds(3), Lines("Third", "Fourth", "Fifth", "Sixth")),
             new(TimeSpan.FromSeconds(7), []), new(TimeSpan.FromSeconds(9), Lines("Last")), new(TimeSpan.FromSeconds(10), Lines("Outside"))]);
        var layer = new GeneratedLayer { Kind = GeneratedLayerKind.Text, CreditsText = new() { MatchSourcePages = true, LinesPerPage = 1 },
            Start = TimeSpan.FromSeconds(2), End = TimeSpan.FromSeconds(10), TextCase = GeneratedTextCase.Upper, Width = 500, Height = 500 };
        var canvas = new SqueezeCanvas { Layers = [layer], TimelineSeconds = 12 };
        var rendered = Assert.IsType<PromoTextLayer>(Assert.Single(canvas.Compose(Job() with { OcrTranscript = source }).Layers));
        Assert.Equal(new[] { 2d, 3d, 7d, 9d }, rendered.Pages!.Select(page => page.At.TotalSeconds));
        Assert.Equal(["FIRST", "SECOND"], rendered.LinesAt(TimeSpan.FromSeconds(2.5)));
        Assert.Equal(4, rendered.LinesAt(TimeSpan.FromSeconds(3.1)).Count);
        Assert.Empty(rendered.LinesAt(TimeSpan.FromSeconds(7.5)));
        Assert.Equal(["LAST"], rendered.LinesAt(TimeSpan.FromSeconds(9)));
        Assert.Equal("First", source.Pages![0].Lines[0].Text);
    }

    [Fact]
    public void Matching_mode_shows_authored_fallback_together_and_fixed_mode_still_splits_it()
    {
        var layer = new GeneratedLayer { Kind = GeneratedLayerKind.Text, CreditsText = new()
            { MatchSourcePages = true, LinesPerPage = 1, FallbackText = "Thank you\nUp next: {up_next}\nStay tuned" }, Width = 500, Height = 500 };
        var canvas = new SqueezeCanvas { Layers = [layer] };
        var job = Job() with { OcrTranscript = new([], 0, Failure: "OCR unavailable") };
        var matched = Assert.IsType<PromoTextLayer>(Assert.Single(canvas.Compose(job).Layers));
        Assert.Equal(3, Assert.Single(matched.Pages!).Lines.Count);
        Assert.Contains("Up next: Next Show", matched.Pages![0].Lines);
        layer.CreditsText.MatchSourcePages = false;
        var fixedPages = Assert.IsType<PromoTextLayer>(Assert.Single(canvas.Compose(job).Layers));
        Assert.Equal(3, fixedPages.Pages!.Count);
        Assert.Equal(new[] { 0d, 4d, 8d }, fixedPages.Pages.Select(page => page.At.TotalSeconds));
    }

    [Fact]
    public void Source_page_setting_survives_copy_and_yaml_without_changing_legacy_defaults()
    {
        Assert.False(new GeneratedCreditsTextOptions().MatchSourcePages);
        var canvas = new SqueezeCanvas { Layers = [new() { Kind = GeneratedLayerKind.Text, CreditsText = new() { MatchSourcePages = true, LinesPerPage = 3 } }] };
        var copied = canvas.Copy();
        Assert.True(copied.Layers[0].CreditsText!.MatchSourcePages);
        var restored = SchedulerConfigLoader.ConvertSection<GeneratedLayer>(copied.Layers[0])!;
        Assert.True(restored.CreditsText!.MatchSourcePages);
        Assert.Equal(3, restored.CreditsText.LinesPerPage);
    }
}
