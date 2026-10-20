using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SchedularrTV.Core.Configuration;
using SchedularrTV.CreditSqueeze;
using SchedularrTV.GeneratedPromos;
using SchedularrTV.Plugins;
using SkiaSharp;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class CreditsOcrTests : IDisposable
{
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "credits-ocr-" + Guid.NewGuid().ToString("N"))).FullName;
    public void Dispose() => Directory.Delete(_folder, recursive: true);
    private CreditSqueezeSettings Settings => new(new Context(_folder));
    private string FilePath(string name) => Path.Combine(_folder, name);
    private static SqueezeJob Job(string source = "source.mp4") => new(source, TimeSpan.Zero, TimeSpan.FromSeconds(12),
        new("Next", null, "8 PM", null), [], "Channel", "Next", "#FFFFFF", "#000000", 1, false);
    private static CreditsOcrLine[] Lines(params string[] lines) => lines.Select(line => new CreditsOcrLine(line, 95)).ToArray();

    [Fact]
    public async Task Anamorphic_video_is_normalized_before_ocr_so_fonts_keep_their_display_shape()
    {
        string source = FilePath("anamorphic.mp4");
        await Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=white:s=160x120:r=10:d=1",
            "-vf", "setsar=2", "-pix_fmt", "yuv420p", source);
        var engine = new RecordingEngine();
        using var ocr = new CreditsOcr(Settings, engine);
        await ocr.ReadAsync(Job(source) with { Length = TimeSpan.FromSeconds(1) }, CancellationToken.None);
        Assert.Equal((640, 240), Assert.Single(engine.Dimensions));
    }

    [Fact]
    public async Task Detection_refines_real_sample_boundaries_and_primes_the_transcript_cache()
    {
        string source = FilePath("detect.mp4");
        await Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=black:s=160x100:r=10:d=20",
            "-vf", "drawbox=color=white:t=fill:enable='between(t,5,14.9)'", "-pix_fmt", "yuv420p", source);
        var engine = new SegmentEngine();
        using var ocr = new CreditsOcr(Settings, engine);
        using var detector = new CreditsDetection(Settings, ocr, TimeProvider.System, NullLogger<CreditsDetection>.Instance);
        int detectedEvents = 0;
        detector.Found += () => detectedEvents++;
        var result = await detector.DetectAsync(source, TimeSpan.FromSeconds(20), new(), CancellationToken.None);
        Assert.NotNull(result.Interval);
        Assert.InRange(result.Interval.Start.TotalSeconds, 4, 6);
        Assert.InRange(result.Interval.End.TotalSeconds, 14, 16);
        int calls = engine.Calls;
        Assert.Equal(result, await detector.DetectAsync(source, TimeSpan.FromSeconds(20), new(), CancellationToken.None));
        Assert.Equal(calls, engine.Calls);
        Assert.Equal(1, detectedEvents);
        var job = Job(source) with { SourceStart = result.Interval.Start, Length = result.Interval.Length,
            CreditsOcr = Settings.Ocr with { Recognition = new(), SampleSeconds = 1 } };
        var transcript = await ocr.ReadAsync(job, CancellationToken.None);
        Assert.Contains(transcript.Lines, line => line.Text == "Alex Smith");
        Assert.Equal(calls, engine.Calls);
        File.SetLastWriteTimeUtc(source, File.GetLastWriteTimeUtc(source).AddSeconds(1));
        Assert.Null(detector.Known(source, TimeSpan.FromSeconds(20), new()));
    }

    private sealed class SegmentEngine : ICreditsOcrEngine
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<CreditsOcrLine>> ReadAsync(string image, CreditsOcrOptions options, CancellationToken cancellationToken)
        {
            Calls++;
            using var bitmap = SKBitmap.Decode(image);
            IReadOnlyList<CreditsOcrLine> lines = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2).Red > 200
                ? [new("Directed by", 96, new(.2, .5, .4, .04)), new("Alex Smith", 96, new(.2, .56, .4, .04))] : [];
            return Task.FromResult(lines);
        }
    }

    [Fact]
    public async Task A_single_recurring_fragment_is_not_cached_or_reported_as_success()
    {
        string source = await MakeSource();
        using var ocr = new CreditsOcr(Settings, new FragmentEngine());
        var job = Job(source) with { Length = TimeSpan.FromSeconds(1) };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ocr.ReadAsync(job, CancellationToken.None));
        Assert.Contains("too little reliable text", error.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(Settings.SaveFolder, "_credits-text"), "*.json"));
        Assert.True((await ocr.ReadForCanvasAsync(job, FallbackCanvas(), CancellationToken.None)).UsedFallback);
    }

    private sealed class FragmentEngine : ICreditsOcrEngine
    {
        public Task<IReadOnlyList<CreditsOcrLine>> ReadAsync(string image, CreditsOcrOptions options, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CreditsOcrLine>>([new("reg", 98)]);
    }

    [Fact]
    public async Task Canvas_file_video_preview_seeks_real_frames_caches_them_and_obeys_loop_and_hold()
    {
        string source = await MakeSource();
        using var services = new ServiceCollection().BuildServiceProvider();
        using var renderer = new SqueezeRenderer(Settings, services, TimeProvider.System, NullLogger<SqueezeRenderer>.Instance);
        var decoder = renderer.CreateCanvasVideoPreview();
        var layer = new PromoVideoLayer { Path = source, Bounds = SKRect.Create(0, 0, 320, 180), Stretch = true };
        byte[] first = await decoder.FrameAsync(layer, TimeSpan.Zero, 30, CancellationToken.None);
        byte[] second = await decoder.FrameAsync(layer, TimeSpan.FromSeconds(1.5), 30, CancellationToken.None);
        using var dark = SKBitmap.Decode(first);
        using var light = SKBitmap.Decode(second);
        Assert.True(dark.GetPixel(160, 90).Red < 20);
        Assert.True(light.GetPixel(160, 90).Red > 240);
        Assert.Same(second, await decoder.FrameAsync(layer, TimeSpan.FromSeconds(1.5), 30, CancellationToken.None));
        Assert.Same(second, await decoder.FrameAsync(layer with { Loop = true }, TimeSpan.FromSeconds(4.5), 30, CancellationToken.None));
        using var held = SKBitmap.Decode(await decoder.FrameAsync(layer, TimeSpan.FromSeconds(99), 30, CancellationToken.None));
        Assert.True(held.GetPixel(160, 90).Red > 240);
    }

    [Fact]
    public async Task Original_ending_is_playable_after_ocr_fails_and_uses_the_exact_source_seek()
    {
        string source = await MakeSource();
        var engine = new RecordingEngine { Empty = true };
        using var ocr = new CreditsOcr(Settings, engine);
        using var services = new ServiceCollection().AddSingleton(ocr).BuildServiceProvider();
        using var renderer = new SqueezeRenderer(Settings, services, TimeProvider.System, NullLogger<SqueezeRenderer>.Instance);
        var job = Job(source) with { SourceStart = TimeSpan.FromSeconds(1.2), Length = TimeSpan.FromSeconds(1), Canvases = [FallbackCanvas(null)] };
        string original = await renderer.SourcePreviewAsync(job, "source", CancellationToken.None);
        Assert.Equal(0, engine.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => renderer.PreviewAsync(job, "source", CancellationToken.None));
        string path = Assert.IsType<string>(renderer.PreviewFile(original));
        byte[] pixels = await FramePixels(path, "0", "original-ending.png");
        Assert.True(pixels.Where((_, index) => index % 4 != 3).Average(value => (double)value) > 240,
            "The source player must start in the white ending, not the black programme before it.");
        Assert.Null(renderer.PreviewFile("../source.mp4"));
        Assert.Equal(1, engine.Calls);
    }

    private static SqueezeCanvas FallbackCanvas(string? fallback = "Thanks for watching {show}.") => new()
    {
        Layers = [new GeneratedLayer
        {
            Name = "Credits", Kind = GeneratedLayerKind.Text, CreditsText = new() { FallbackText = fallback, LinesPerPage = 1 },
            Width = 1280, Height = 720, FontSize = 60, TransitionIn = GeneratedTransition.None, TransitionOut = GeneratedTransition.None
        }]
    };

    [Fact]
    public void Default_text_is_per_layer_paged_and_used_only_without_recognized_credits()
    {
        var canvas = FallbackCanvas("Thanks for watching {show}.\nUp next: {next} on {channel}.");
        var job = Job() with { EndingShow = "Example", OcrTranscript = new([], 0, Failure: "No readable text") };
        var text = Assert.IsType<PromoTextLayer>(Assert.Single(canvas.Compose(job).Layers));
        Assert.Equal(["Thanks for watching Example."], text.LinesAt(TimeSpan.Zero));
        Assert.Equal(["Up next: Next on Channel."], text.LinesAt(TimeSpan.FromSeconds(6)));
        var actual = Assert.IsType<PromoTextLayer>(Assert.Single(canvas.Compose(job with { OcrTranscript = new(Lines("Original {channel}"), 1) }).Layers));
        Assert.Equal(["Original {channel}"], actual.LinesAt(TimeSpan.Zero));
        Assert.Equal(canvas.Layers[0].CreditsText!.FallbackText, canvas.Copy().Layers[0].CreditsText!.FallbackText);
        Assert.NotEqual(SqueezeRenderer.Key(job with { Canvases = [canvas] }), SqueezeRenderer.Key(job with { Canvases = [FallbackCanvas("Other default")] }));
    }

    [Fact]
    public async Task Empty_ocr_uses_default_without_caching_failure_and_success_is_retried()
    {
        string source = await MakeSource();
        var engine = new RecordingEngine { Empty = true };
        using var ocr = new CreditsOcr(Settings, engine);
        var job = Job(source) with { Length = TimeSpan.FromSeconds(1) };
        var result = await ocr.ReadForCanvasAsync(job, FallbackCanvas(), CancellationToken.None);
        Assert.True(result.UsedFallback);
        Assert.Contains("no readable credits", result.Failure);
        Assert.Empty(result.Lines);
        Assert.Empty(Directory.GetFiles(Path.Combine(Settings.SaveFolder, "_credits-text"), "*.json"));
        engine.Empty = false;
        var recovered = await ocr.ReadForCanvasAsync(job, FallbackCanvas(), CancellationToken.None);
        Assert.False(recovered.UsedFallback);
        Assert.NotEmpty(recovered.Lines);
        Assert.Equal(2, engine.Calls);
    }

    [Fact]
    public async Task Missing_ocr_tool_can_use_configured_default()
    {
        string source = await MakeSource();
        using var ocr = new CreditsOcr(Settings, new TesseractCreditsOcr());
        var job = Job(source) with { Length = TimeSpan.FromSeconds(1), CreditsOcr = new(FilePath("missing-tesseract.exe"), "eng", 1) };
        Assert.True((await ocr.ReadForCanvasAsync(job, FallbackCanvas(), CancellationToken.None)).UsedFallback);
    }

    [Fact]
    public async Task Missing_defaults_and_cancellation_still_fail_instead_of_silently_rendering_blank_text()
    {
        string source = await MakeSource();
        var engine = new RecordingEngine { Empty = true };
        using var ocr = new CreditsOcr(Settings, engine);
        var job = Job(source) with { Length = TimeSpan.FromSeconds(1) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ocr.ReadForCanvasAsync(job, FallbackCanvas("  "), CancellationToken.None));
        var partial = FallbackCanvas();
        partial.Layers.Add(new() { Kind = GeneratedLayerKind.Text, CreditsText = new() });
        await Assert.ThrowsAsync<InvalidOperationException>(() => ocr.ReadForCanvasAsync(job, partial, CancellationToken.None));
        using var stop = new CancellationTokenSource();
        engine.OnRead = stop.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ocr.ReadForCanvasAsync(job, FallbackCanvas(), stop.Token));
    }

    [Fact]
    public async Task Preview_renders_fallback_and_reports_the_same_ocr_attempt_once()
    {
        string source = await MakeSource();
        var engine = new RecordingEngine { Empty = true };
        using var ocr = new CreditsOcr(Settings, engine);
        using var services = new ServiceCollection().AddSingleton(ocr).BuildServiceProvider();
        using var renderer = new SqueezeRenderer(Settings, services, TimeProvider.System, NullLogger<SqueezeRenderer>.Instance);
        var job = Job(source) with { Length = TimeSpan.FromSeconds(1), EndingShow = "Example", Canvases = [FallbackCanvas()] };
        CreditsOcrResult? report = null;
        string name = await renderer.PreviewAsync(job, "test", CancellationToken.None, result => report = result);
        Assert.NotNull(renderer.PreviewFile(name));
        Assert.True(report?.UsedFallback);
        Assert.Equal(1, engine.Calls);
    }

    [Theory]
    [InlineData("a", 99, false)]
    [InlineData("3", 99, false)]
    [InlineData("_", 99, false)]
    [InlineData("Ba", 40, false)]
    [InlineData("WINUTES:", 32, false)]
    [InlineData("ary", 0, false)]
    [InlineData("--|a|--", 95, false)]
    [InlineData("J. K. Simmons", 92, true)]
    [InlineData("Zoë O’Neil-Smith", 92, true)]
    [InlineData("Li", 96, true)]
    [InlineData("AND", 90, true)]
    [InlineData("山", 95, true)]
    [InlineData("김", 95, true)]
    [InlineData("© 2025", 95, true)]
    public void Noise_filter_keeps_names_and_legitimate_short_words(string text, double confidence, bool expected) =>
        Assert.Equal(expected, CreditsOcrFiltering.Keep(new(text, confidence)));

    [Fact]
    public void Recognition_options_invalidate_transcripts_and_are_copied_with_looks()
    {
        var options = new CreditsOcrOptions("tesseract", "eng", 1);
        Assert.NotEqual(CreditsOcr.Key(Job(), options), CreditsOcr.Key(Job(), options with { Recognition = new(65, 100) }));
        Assert.NotEqual(CreditsOcr.Key(Job(), options), CreditsOcr.Key(Job(), options with { Recognition = new(MinimumConfidence: 85) }));
        Assert.NotEqual(CreditsOcr.Key(Job(), options), CreditsOcr.Key(Job(), options with { Version = 1 }));
        var section = new CreditSqueezeSection { Recognition = new(65, 100, 70) };
        Assert.Equal(section.Recognition, section.WithOnly(section).Recognition);
        Assert.Equal(new CreditsRecognition(95, 100, 95), new CreditsRecognition(999, -10, 300).Normalized());
        Assert.True(CreditsOcrFiltering.Keep(new("Alex Doe", 50), 45));
        Assert.False(CreditsOcrFiltering.Keep(new("Alex Doe", 50)));
    }

    [Fact]
    public async Task Recognition_area_crops_the_source_before_ocr()
    {
        string source = FilePath("lower-third.mp4");
        await Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=black:s=160x100:r=10:d=1",
            "-vf", "drawbox=x=0:y=65:w=160:h=35:color=white:t=fill", "-pix_fmt", "yuv420p", source);
        var engine = new RecordingEngine();
        using var ocr = new CreditsOcr(Settings, engine);
        var job = Job(source) with { Length = TimeSpan.FromSeconds(1), CreditsOcr = Settings.Ocr with { Recognition = new(65, 100) } };
        await ocr.ReadAsync(job, CancellationToken.None);
        Assert.True(Assert.Single(engine.Brightness) > 240, "The cropped OCR image must contain only the white bottom credits area.");
        await ocr.ReadAsync(job with { CreditsOcr = Settings.Ocr }, CancellationToken.None);
        Assert.True(engine.Brightness[1] < 20, "The whole-frame OCR sample still includes the black scene above the credits.");
        Assert.Equal(2, engine.Calls);
    }

    [Fact]
    public async Task Rejected_detections_never_enter_the_transcript_and_are_reported()
    {
        string source = await MakeSource();
        using var ocr = new CreditsOcr(Settings, new NoisyEngine());
        var result = await ocr.ReadAsync(Job(source) with { Length = TimeSpan.FromSeconds(1) }, CancellationToken.None);
        Assert.Equal(["Written by", "Zoë O’Neil-Smith"], result.Lines.Select(line => line.Text));
        Assert.Equal(4, result.RejectedDetections);
    }

    private sealed class NoisyEngine : ICreditsOcrEngine
    {
        public Task<IReadOnlyList<CreditsOcrLine>> ReadAsync(string image, CreditsOcrOptions options, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CreditsOcrLine>>([new("a", 95), new("3", 95), new("_", 90), new("WINUTES:", 32), new("Written by", 97), new("Zoë O’Neil-Smith", 94)]);
    }

    [Fact]
    public void Tsv_preserves_names_accents_punctuation_reading_order_and_uncertain_words()
    {
        string tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n"
            + "4\t1\t1\t1\t1\t0\t0\t0\t200\t20\t-1\t\n"
            + "5\t1\t1\t1\t1\t1\t0\t0\t90\t20\t97.5\tWritten\n"
            + "5\t1\t1\t1\t1\t2\t100\t0\t30\t20\t95\tby\n"
            + "5\t1\t2\t1\t1\t1\t0\t40\t80\t20\t94\tZoë\n"
            + "5\t1\t2\t1\t1\t2\t90\t40\t90\t20\t92\tO’Neil-Smith\n"
            + "5\t1\t3\t1\t1\t1\t0\t80\t90\t20\t32\tJ0HN\n"
            + "bad row\n";
        var result = TesseractCreditsOcr.ParseTsv(tsv);
        Assert.Equal(["Written by", "Zoë O’Neil-Smith", "J0HN"], result.Select(line => line.Text));
        Assert.Equal(1, new CreditsOcrResult(result, 1).UncertainLines);
    }

    [Fact]
    public void Cards_and_overlapping_scrolls_do_not_repeat_but_later_recurring_names_are_kept()
    {
        var transcript = new CreditsTranscript();
        transcript.Add(Lines("Written by", "Alex Doe", "Directed by"));
        transcript.Add([]); // A fade or a missed frame does not duplicate the preceding card.
        transcript.Add(Lines("Written by", "Alex Doe", "Directed by"));
        transcript.Add(Lines("Alex Doe")); // An intermediate, contained scroll window.
        transcript.Add(Lines("Alex Doe", "Directed by", "Jamie Roe"));
        transcript.Add(Lines("Jamie Roe", "Produced by", "Alex Doe"));
        Assert.Equal(["Written by", "Alex Doe", "Directed by", "Jamie Roe", "Produced by", "Alex Doe"], transcript.Lines.Select(line => line.Text));
    }

    [Fact]
    public void Pages_cover_the_text_layers_whole_active_time_including_the_last_partial_page()
    {
        var pages = SqueezeCanvas.CreditsPages(Lines("A", "B", "C", "D", "E"), 2, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(12));
        Assert.Equal([3d, 6d, 9d], pages.Select(page => page.At.TotalSeconds));
        Assert.Equal(["A", "B", "C", "D", "E"], pages.SelectMany(page => page.Lines));
        var layer = new PromoTextLayer { Bounds = SKRect.Create(0, 0, 100, 100), Lines = [], Pages = pages };
        Assert.Empty(layer.LinesAt(TimeSpan.FromSeconds(2.9)));
        Assert.Equal(["A", "B"], layer.LinesAt(TimeSpan.FromSeconds(3)));
        Assert.Equal(["C", "D"], layer.LinesAt(TimeSpan.FromSeconds(6)));
        Assert.Equal(["E"], layer.LinesAt(TimeSpan.FromSeconds(11.99)));
        Assert.Empty(SqueezeCanvas.CreditsPages(Lines("A"), 2, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public void Credits_text_is_independent_of_video_and_does_not_interpret_recognized_tokens()
    {
        var canvas = SqueezeCanvas.Create("4:3");
        canvas.TimelineSeconds = Job().Length.TotalSeconds;
        canvas.Layers.Clear();
        canvas.Layers.Add(new GeneratedLayer
        {
            Kind = GeneratedLayerKind.Text, CreditsText = new() { LinesPerPage = 1 }, Start = TimeSpan.FromSeconds(2), End = TimeSpan.FromSeconds(10),
            Text = "not used", TransitionIn = GeneratedTransition.None, TransitionOut = GeneratedTransition.None,
            Keyframes = [new() { At = TimeSpan.FromSeconds(2), X = 20, Width = 200, Height = 200 }]
        });
        var composition = canvas.Compose(Job() with { OcrTranscript = new(Lines("{channel}", "Real Name"), 10) });
        var text = Assert.IsType<PromoTextLayer>(Assert.Single(composition.Layers));
        Assert.Equal(["{channel}"], text.LinesAt(TimeSpan.FromSeconds(2)));
        Assert.Equal(["Real Name"], text.LinesAt(TimeSpan.FromSeconds(6)));
        Assert.Equal(TimeSpan.FromSeconds(2), text.Start);
        Assert.Equal(TimeSpan.FromSeconds(10), text.End);
        Assert.Single(text.Keyframes!);
        Assert.Empty(composition.Layers.OfType<PromoVideoLayer>());
    }

    [Fact]
    public async Task Frame_extraction_seeks_original_credits_and_concurrent_reads_reuse_the_transcript_cache()
    {
        string source = await MakeSource();
        var engine = new RecordingEngine();
        using var ocr = new CreditsOcr(Settings, engine);
        var job = Job(source) with { SourceStart = TimeSpan.FromSeconds(1.2), Length = TimeSpan.FromSeconds(1.6) };
        var results = await Task.WhenAll(ocr.ReadAsync(job, CancellationToken.None), ocr.ReadAsync(job, CancellationToken.None));
        Assert.Equal(1, engine.Calls); // repeated static frames are recognized once
        var statistics = Assert.IsType<CreditsOcrStatistics>(results[0].Statistics);
        Assert.Equal(2, statistics.SampledFrames);
        Assert.Equal(1, statistics.UniqueFrames);
        Assert.All(engine.Brightness, brightness => Assert.True(brightness > 240, "OCR must receive the white credits segment, not the earlier black programme."));
        Assert.Equal(["Cast", "Alex Doe"], results[0].Lines.Select(line => line.Text));
        Assert.Equal(results[0].Lines, results[1].Lines);
        Assert.Single(results[0].Pages!);
        Assert.Single(results[1].Pages!);
        Assert.Equal(TimeSpan.Zero, results[1].Pages![0].At);
        Assert.Equal(["Cast", "Alex Doe"], results[1].Pages![0].Lines.Select(line => line.Text));
        Assert.Empty(Directory.GetDirectories(Path.Combine(Settings.SaveFolder, "_credits-text"), ".work-*"));

        // A different layout must reuse OCR, while a different language must read it again.
        await ocr.ReadAsync(job with { Canvases = [SqueezeCanvas.Create("1:1")] }, CancellationToken.None);
        Assert.Equal(1, engine.Calls);
        await ocr.ReadAsync(job with { CreditsOcr = Settings.Ocr with { Language = "fra" } }, CancellationToken.None);
        Assert.Equal(2, engine.Calls);
    }

    [Fact]
    public async Task Empty_or_cancelled_ocr_is_not_cached_as_success_and_can_be_retried()
    {
        string source = await MakeSource();
        var engine = new RecordingEngine { Empty = true };
        using var ocr = new CreditsOcr(Settings, engine);
        var job = Job(source) with { SourceStart = TimeSpan.FromSeconds(1), Length = TimeSpan.FromSeconds(1) };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ocr.ReadAsync(job, CancellationToken.None));
        Assert.Contains("no readable credits", error.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(Settings.SaveFolder, "_credits-text"), "*.json"));
        engine.Empty = false;
        using var stop = new CancellationTokenSource();
        engine.OnRead = () => stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ocr.ReadAsync(job, stop.Token));
        Assert.Empty(Directory.GetFiles(Path.Combine(Settings.SaveFolder, "_credits-text"), "*.json"));
        Assert.Empty(Directory.GetDirectories(Path.Combine(Settings.SaveFolder, "_credits-text"), ".work-*"));
        engine.OnRead = null;
        Assert.NotEmpty((await ocr.ReadAsync(job, CancellationToken.None)).Lines);
    }

    [Fact]
    public async Task Missing_tesseract_has_an_actionable_error_instead_of_invented_or_blank_credits()
    {
        var options = new CreditsOcrOptions(FilePath("no-such-tesseract"), "eng", 1);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new TesseractCreditsOcr().ReadAsync("frame.png", options, CancellationToken.None));
        Assert.Contains("couldn't start", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("plugin settings", error.Message);
    }

    [Fact]
    public async Task Source_changes_invalidate_ocr_and_render_keys()
    {
        string source = FilePath("source.mp4");
        await File.WriteAllTextAsync(source, "source1");
        var canvas = SqueezeCanvas.Create("4:3");
        canvas.Layers.Add(new() { Kind = GeneratedLayerKind.Text, CreditsText = new() });
        using var services = new ServiceCollection().BuildServiceProvider();
        using var renderer = new SqueezeRenderer(Settings, services, TimeProvider.System, NullLogger<SqueezeRenderer>.Instance);
        var channel = new ChannelConfig { Id = "channel", Name = "Channel" };
        var first = renderer.ForChannel(Job(source) with { Canvases = [canvas] }, channel);
        string ocrKey = CreditsOcr.Key(first, Settings.Ocr);
        await File.WriteAllTextAsync(source, "replacement source2");
        var second = renderer.ForChannel(first, channel);
        Assert.NotEqual(ocrKey, CreditsOcr.Key(second, Settings.Ocr));
        Assert.NotEqual(SqueezeRenderer.Key(first), SqueezeRenderer.Key(second));
        Assert.NotEqual(SqueezeRenderer.Key(second), SqueezeRenderer.Key(second with { CreditsOcr = Settings.Ocr with { Language = "fra" } }));
        // Resolved words don't change the job's identity after it is queued.
        Assert.Equal(SqueezeRenderer.Key(second), SqueezeRenderer.Key(second with { OcrTranscript = new(Lines("A"), 1) }));
    }

    [Fact]
    public async Task Encoded_pages_change_even_after_the_original_video_is_completely_removed()
    {
        var canvas = SqueezeCanvas.Create("4:3");
        canvas.Layers.Clear();
        canvas.Layers.Add(new()
        {
            Kind = GeneratedLayerKind.Text, CreditsText = new() { LinesPerPage = 1 }, X = 60, Y = 100, Width = 840, Height = 500,
            FontSize = 100, TransitionIn = GeneratedTransition.None, TransitionOut = GeneratedTransition.None
        });
        var job = Job() with { Length = TimeSpan.FromSeconds(1), OcrTranscript = new(Lines("AAAAAAAA", "iiiiiiii"), 2) };
        string output = FilePath("pages.mp4");
        await new PromoRenderer().RenderAsync(canvas.Compose(job, 144), output);
        byte[] first = await FramePixels(output, "0.2", "first.png");
        byte[] second = await FramePixels(output, "0.7", "second.png");
        Assert.False(first.SequenceEqual(second), "A page change must invalidate the held-frame optimization, even without any video layer.");
        int firstInk = first.Where((value, index) => index % 4 != 3 && value > 100).Count();
        int secondInk = second.Where((value, index) => index % 4 != 3 && value > 100).Count();
        Assert.True(firstInk > secondInk * 1.5, "The second page has distinctly different glyphs, not merely encoding noise.");
        Assert.True(secondInk > 50);
    }

    private async Task<byte[]> FramePixels(string video, string at, string name)
    {
        string file = FilePath(name);
        await Run("ffmpeg", "-v", "error", "-y", "-ss", at, "-i", video, "-frames:v", "1", file);
        using var bitmap = SKBitmap.Decode(await File.ReadAllBytesAsync(file));
        return bitmap.GetPixelSpan().ToArray();
    }

    private async Task<string> MakeSource()
    {
        string file = FilePath("source.mp4");
        await Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=white:s=160x90:r=10:d=3",
            "-vf", "drawbox=color=black:t=fill:enable='lt(t,1)'", "-pix_fmt", "yuv420p", file);
        return file;
    }

    private static async Task Run(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        Assert.True(process.ExitCode == 0, await errors);
    }

    private sealed class RecordingEngine : ICreditsOcrEngine
    {
        public int Calls { get; private set; }
        public bool Empty { get; set; }
        public Action? OnRead { get; set; }
        public List<byte> Brightness { get; } = [];
        public List<(int Width, int Height)> Dimensions { get; } = [];
        public async Task<IReadOnlyList<CreditsOcrLine>> ReadAsync(string image, CreditsOcrOptions options, CancellationToken cancellationToken)
        {
            Calls++;
            OnRead?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            using var bitmap = SKBitmap.Decode(await File.ReadAllBytesAsync(image, cancellationToken));
            Dimensions.Add((bitmap.Width, bitmap.Height));
            Brightness.Add(bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2).Red);
            return Empty ? [] : Lines("Cast", "Alex Doe");
        }
    }

    private sealed class Context(string folder) : IPluginContext
    {
        public PluginManifest Manifest { get; } = new() { Id = "credit-squeeze", Name = "Credit squeeze" };
        public string? PluginDirectory => null;
        public string DataDirectory => folder;
        public string AppDataDirectory => folder;
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
        public IConfiguration AppConfiguration { get; } = new ConfigurationBuilder().Build();
        public bool IsLoaded(string pluginId) => false;
        public string StaticFilesPath => "/plugins/credit-squeeze/";
    }
}
