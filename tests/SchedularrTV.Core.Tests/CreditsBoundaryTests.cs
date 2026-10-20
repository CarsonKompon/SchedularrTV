using SchedularrTV.Core.Configuration;
using SchedularrTV.CreditSqueeze;
using SchedularrTV.GeneratedPromos;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class CreditsBoundaryTests
{
    private static CreditsOcrLine Line(string text, double top = .5) => new(text, 96, new(.2, top, .5, .04));

    [Fact]
    public void Detection_finds_a_35_second_interval_and_excludes_pre_and_post_credit_footage()
    {
        var samples = Enumerable.Range(0, 90).Select(second => new CreditsOcrSample(TimeSpan.FromSeconds(second),
            second is >= 20 and < 55 ? [Line("Directed by"), Line("Alex Smith", .56)]
                : second >= 55 ? [Line("That was interesting", .8), Line("We should go home now", .86)] : [])).ToArray();
        var interval = CreditsBoundaryAnalysis.Find(samples, TimeSpan.FromSeconds(90), 1);
        Assert.Equal(new CreditsInterval(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(55)), interval);
    }

    [Fact]
    public void Isolated_noise_and_normal_subtitles_are_not_credits()
    {
        var scraps = Enumerable.Range(0, 60).Select(second => new CreditsOcrSample(TimeSpan.FromSeconds(second), [Line("reg")])).ToArray();
        Assert.Null(CreditsBoundaryAnalysis.Find(scraps, TimeSpan.FromSeconds(60), 1));
        Assert.False(CreditsOcrFiltering.Usable([Line("reg")]));
        var subtitles = Enumerable.Range(0, 60).Select(second => new CreditsOcrSample(TimeSpan.FromSeconds(second),
            [Line("Music is my life"), Line("We should go home now", .56)])).ToArray();
        Assert.Null(CreditsBoundaryAnalysis.Find(subtitles, TimeSpan.FromSeconds(60), 1));
    }

    [Fact]
    public void Scrolling_text_movement_is_evidence_even_without_english_role_headings()
    {
        var samples = Enumerable.Range(0, 12).Select(second => new CreditsOcrSample(TimeSpan.FromSeconds(second),
            [Line("Alex Smith", .8 - second * .03), Line("Jamie Roe", .86 - second * .03)])).ToArray();
        Assert.NotNull(CreditsBoundaryAnalysis.Find(samples, TimeSpan.FromSeconds(12), 1));
    }

    [Fact]
    public void Scrolling_windows_can_miss_a_line_without_repeating_the_previous_names()
    {
        var transcript = new CreditsTranscript();
        transcript.Add([Line("Alex Smith"), Line("Jamie Roe"), Line("Pat Jones"), Line("Sam Doe")]);
        transcript.Add([Line("Jamie Roe"), Line("Sam Doe"), Line("Robin Lee")]);
        transcript.Add([]);
        transcript.Add([Line("Sam Doe"), Line("Robin Lee"), Line("Chris Gray")]);
        Assert.Equal(new[] { "Alex Smith", "Jamie Roe", "Pat Jones", "Sam Doe", "Robin Lee", "Chris Gray" }, transcript.Lines.Select(line => line.Text));
    }

    [Fact]
    public void Clipped_scroll_fragments_are_not_promoted_to_names()
    {
        IReadOnlyList<CreditsOcrLine>[] frames =
        [
            [new("mith", 96, new(.2, 0, .2, .02)), Line("Directed by", .04), Line("Jamie Roe", .1)],
            [Line("Alex Smith", .2), Line("Jamie Roe", .26)]
        ];
        var clean = CreditsOcrAnalysis.Clean(frames);
        Assert.DoesNotContain(clean[0], line => line.Text == "mith");
        Assert.Contains(clean[1], line => line.Text == "Alex Smith");
    }

    [Fact]
    public void Animation_timings_scale_to_credits_but_source_offsets_and_ocr_pages_do_not()
    {
        var video = new GeneratedLayer { Kind = GeneratedLayerKind.Video, Width = 1280, Height = 720, Start = TimeSpan.FromSeconds(6), End = TimeSpan.FromSeconds(54),
            TransitionDuration = TimeSpan.FromSeconds(6), TransitionOutDuration = TimeSpan.FromSeconds(3), Video = new() { Episode = SqueezeCanvas.Credits },
            Keyframes = [new() { Width = 1280, Height = 720 }, new() { At = TimeSpan.FromSeconds(30), Width = 640, Height = 360 }, new() { At = TimeSpan.FromSeconds(60), Width = 1280, Height = 720 }] };
        var text = new GeneratedLayer { Kind = GeneratedLayerKind.Text, Width = 400, Height = 400, CreditsText = new() { MatchSourcePages = true } };
        var canvas = new SqueezeCanvas { TimelineSeconds = 60, Layers = [video, text] };
        var job = new SqueezeJob("source.mp4", TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(35), new("Next", null, "8 PM", null), [], "Channel", "Next", "#fff", "#000", 1, false)
        { OcrTranscript = new([Line("Alex Smith"), Line("Jamie Roe")], 35, Pages: [new(TimeSpan.Zero, [Line("Alex Smith")]), new(TimeSpan.FromSeconds(10), [Line("Jamie Roe")])]) };
        var composition = canvas.Compose(job);
        var drawn = Assert.IsType<PromoVideoLayer>(composition.Layers[0]);
        Assert.Equal(TimeSpan.FromSeconds(35), composition.Duration);
        Assert.Equal(new[] { 0d, 17.5, 35d }, drawn.Keyframes!.Select(key => key.At.TotalSeconds));
        Assert.Equal(3.5, drawn.Start.TotalSeconds);
        Assert.Equal(31.5, drawn.End!.Value.TotalSeconds);
        Assert.Equal(3.5, drawn.TransitionDuration.TotalSeconds);
        Assert.Equal(1.75, drawn.TransitionOutDuration!.Value.TotalSeconds);
        Assert.Equal(100, drawn.SourceStart.TotalSeconds);
        var credits = Assert.IsType<PromoTextLayer>(composition.Layers[1]);
        Assert.Equal(new[] { 0d, 10d }, credits.Pages!.Select(page => page.At.TotalSeconds));
        canvas.FitTimingToCredits = false;
        Assert.Equal(30, Assert.IsType<PromoVideoLayer>(canvas.Compose(job).Layers[0]).Keyframes![1].At.TotalSeconds);
    }
}
