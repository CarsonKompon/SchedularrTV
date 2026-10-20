using SchedularrTV.CreditSqueeze;
using SkiaSharp;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class CreditsOcrMixedLayoutTests
{
    private static CreditsOcrLine Line(string text, double top) => new(text, 96, new(.2, top, .4, .035));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Mixed_cards_and_scrolls_are_one_interval_in_either_order(bool scrollFirst, bool movingBackground)
    {
        var cards = Enumerable.Range(0, 8).Select(index => new CreditsOcrSample(TimeSpan.Zero,
            [Line("Alex Smith", .3), Line("Jamie Roe", .36)], StaticFrame: !movingBackground, VisualGroup: movingBackground ? index + 100 : 0)).ToArray();
        var scroll = Enumerable.Range(0, 8).Select(index => new CreditsOcrSample(TimeSpan.Zero,
            [Line("Pat Jones", .7 - index * .03), Line("Sam Doe", .76 - index * .03), Line("Chris Gray", .82 - index * .03)], VisualGroup: index + 1)).ToArray();
        var mixed = (scrollFirst ? scroll.Concat(cards) : cards.Concat(scroll)).Select((sample, index) => sample with { At = TimeSpan.FromSeconds(index) }).ToArray();
        var classified = CreditsLayouts.Classify(mixed);
        Assert.Equal(8, classified.Count(sample => sample.Layout == CreditsLayoutKind.Card));
        Assert.Equal(8, classified.Count(sample => sample.Layout == CreditsLayoutKind.Scroll));
        Assert.Equal(new CreditsInterval(TimeSpan.Zero, TimeSpan.FromSeconds(16)), CreditsBoundaryAnalysis.Find(classified, TimeSpan.FromSeconds(16), 1));
        var transcript = new CreditsTranscript();
        foreach (var sample in classified) transcript.Add(sample);
        Assert.Equal(scrollFirst ? new[] { "Pat Jones", "Sam Doe", "Chris Gray", "Alex Smith", "Jamie Roe" }
            : ["Alex Smith", "Jamie Roe", "Pat Jones", "Sam Doe", "Chris Gray"], transcript.Lines.Select(line => line.Text));
    }

    [Fact]
    public void Hard_cut_between_static_cards_is_not_misidentified_as_scrolling()
    {
        var samples = new[]
        {
            new CreditsOcrSample(TimeSpan.Zero, [Line("Alex Smith", .2), Line("Jamie Roe", .26)], true, VisualGroup: 0),
            new CreditsOcrSample(TimeSpan.FromSeconds(1), [Line("Alex Smith", .5), Line("Jamie Roe", .56)], true, VisualGroup: 1)
        };
        Assert.All(CreditsLayouts.Classify(samples), sample => Assert.Equal(CreditsLayoutKind.Card, sample.Layout));
    }

    [Fact]
    public void Alternating_ocr_box_jitter_is_not_a_scroll_but_slow_consistent_motion_is()
    {
        var jitter = Enumerable.Range(0, 12).Select(index => new CreditsOcrSample(TimeSpan.FromSeconds(index),
            [Line("This is dialogue", .7 + index % 2 * .002), Line("It should not become credits", .76 + index % 2 * .002)], VisualGroup: index)).ToArray();
        var classified = CreditsLayouts.Classify(jitter);
        Assert.DoesNotContain(classified, sample => sample.Layout == CreditsLayoutKind.Scroll);
        Assert.Null(CreditsBoundaryAnalysis.Find(classified, TimeSpan.FromSeconds(12), 1));
        var slow = Enumerable.Range(0, 12).Select(index => new CreditsOcrSample(TimeSpan.FromSeconds(index),
            [Line("Alex Smith", .7 - index * .002), Line("Jamie Roe", .76 - index * .002)], VisualGroup: index)).ToArray();
        Assert.All(CreditsLayouts.Classify(slow), sample => Assert.Equal(CreditsLayoutKind.Scroll, sample.Layout));
    }

    [Fact]
    public void A_blank_transition_between_styles_is_bridged_but_a_scene_is_not()
    {
        var cards = Enumerable.Range(0, 8).Select(index => new CreditsOcrSample(TimeSpan.FromSeconds(index),
            [Line("Alex Smith", .3), Line("Jamie Roe", .36)], true, CreditsLayoutKind.Card, 0));
        var gap = Enumerable.Range(8, 4).Select(index => new CreditsOcrSample(TimeSpan.FromSeconds(index), [], BlankFrame: true));
        var scroll = Enumerable.Range(0, 8).Select(index => new CreditsOcrSample(TimeSpan.FromSeconds(index + 12),
            [Line("Pat Jones", .7 - index * .03), Line("Sam Doe", .76 - index * .03)], Layout: CreditsLayoutKind.Scroll, VisualGroup: index + 1));
        var mixed = cards.Concat(gap).Concat(scroll).ToArray();
        Assert.Equal(new CreditsInterval(TimeSpan.Zero, TimeSpan.FromSeconds(20)), CreditsBoundaryAnalysis.Find(mixed, TimeSpan.FromSeconds(20), 1));
        var scene = mixed.Select(sample => sample with { BlankFrame = false }).ToArray();
        var interval = Assert.IsType<CreditsInterval>(CreditsBoundaryAnalysis.Find(scene, TimeSpan.FromSeconds(20), 1));
        Assert.Equal(TimeSpan.FromSeconds(8), interval.Length);
    }

    [Fact]
    public void A_card_after_a_scroll_does_not_lose_names_that_were_already_in_the_scroll()
    {
        var transcript = new CreditsTranscript();
        transcript.Add(new CreditsOcrSample(TimeSpan.Zero, [Line("Alex Smith", .3), Line("Jamie Roe", .4), Line("Pat Jones", .5)], Layout: CreditsLayoutKind.Scroll, VisualGroup: 0));
        transcript.Add(new CreditsOcrSample(TimeSpan.FromSeconds(1), [Line("Alex Smith", .3), Line("Jamie Roe", .4)], true, CreditsLayoutKind.Card, 1));
        Assert.Equal(new[] { "Alex Smith", "Jamie Roe", "Pat Jones", "Alex Smith", "Jamie Roe" }, transcript.Lines.Select(line => line.Text));
    }

    [Fact]
    public void Scroll_consensus_keeps_the_clearer_observed_spelling_without_merging_confident_names()
    {
        var transcript = new CreditsTranscript();
        var samples = new[]
        {
            new CreditsOcrSample(TimeSpan.Zero, [Line("Matt Brown", .4), Line("Michelle Smith", .5), new("Wendi McNee5e", 70, new(.2, .6, .4, .035))], Layout: CreditsLayoutKind.Scroll),
            new CreditsOcrSample(TimeSpan.FromSeconds(1), [Line("Matt Brown", .3), Line("Michelle Smith", .4), Line("Wendi McNeese", .5)], Layout: CreditsLayoutKind.Scroll)
        };
        var refined = CreditsTextAgreement.Refine(samples);
        foreach (var sample in refined.Samples) transcript.Add(sample);
        Assert.Equal(1, refined.Corrections);
        Assert.Equal("Wendi McNeese", refined.Samples[0].Lines[2].Text);
        Assert.Equal("Wendi McNee5e", samples[0].Lines[2].Text);
        Assert.Equal(new[] { "Matt Brown", "Michelle Smith", "Wendi McNeese" }, transcript.Lines.Select(line => line.Text));

        var distinct = new CreditsTranscript();
        distinct.Add(new CreditsOcrSample(TimeSpan.Zero, [Line("Matt Brown", .4), Line("Pat Jones", .5), Line("John Smith", .6)], Layout: CreditsLayoutKind.Scroll));
        distinct.Add(new CreditsOcrSample(TimeSpan.FromSeconds(1), [Line("Matt Brown", .3), Line("Pat Jones", .4), Line("Joan Smith", .5)], Layout: CreditsLayoutKind.Scroll));
        Assert.Contains(distinct.Lines, line => line.Text == "John Smith");
        Assert.Contains(distinct.Lines, line => line.Text == "Joan Smith");
    }

    [Fact]
    public void Conflicting_strong_readings_do_not_force_a_guessed_name_into_scan_pages()
    {
        var samples = new[]
        {
            new CreditsOcrSample(TimeSpan.Zero, [Line("Matt Brown", .4), Line("Pat Jones", .5), new("John Smith", 70, new(.2, .6, .4, .035))], Layout: CreditsLayoutKind.Scroll),
            new CreditsOcrSample(TimeSpan.FromSeconds(1), [Line("Matt Brown", .3), Line("Pat Jones", .4), Line("Joan Smith", .5)], Layout: CreditsLayoutKind.Scroll),
            new CreditsOcrSample(TimeSpan.FromSeconds(2), [Line("Matt Brown", .2), Line("Pat Jones", .3), Line("John Smith", .4)], Layout: CreditsLayoutKind.Scroll)
        };
        var result = CreditsTextAgreement.Refine(samples);
        Assert.Equal(0, result.Corrections);
        Assert.Equal(70, result.Samples[0].Lines[2].Confidence);
    }

    [Fact]
    public async Task Frame_reuse_does_not_chain_fades_or_ignore_a_small_changed_glyph()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ocr-reuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string Image(string name, byte color, bool glyph = false)
            {
                using var bitmap = new SKBitmap(32, 32);
                bitmap.Erase(new SKColor(color, color, color));
                if (glyph) bitmap.SetPixel(16, 16, SKColors.White);
                using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                string path = Path.Combine(folder, name + ".png");
                File.WriteAllBytes(path, data.ToArray());
                return path;
            }
            var reuse = new CreditsFrameReuse();
            Assert.Equal(0, await reuse.AddAsync(Image("a", 0), CancellationToken.None));
            Assert.Equal(0, await reuse.AddAsync(Image("b", 4), CancellationToken.None));
            Assert.Equal(1, await reuse.AddAsync(Image("c", 200), CancellationToken.None));
            Assert.Equal(0, await reuse.AddAsync(Image("d", 4), CancellationToken.None));
            Assert.Equal(2, await reuse.AddAsync(Image("e", 7), CancellationToken.None));
            Assert.Equal(3, await reuse.AddAsync(Image("f", 7, glyph: true), CancellationToken.None));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Batched_tsv_preserves_blank_pages_and_page_identity()
    {
        string tsv = "1\t1\t0\t0\t0\t0\t0\t0\t1000\t500\t-1\t\n"
            + "1\t2\t0\t0\t0\t0\t0\t0\t1000\t500\t-1\t\n"
            + "5\t2\t1\t1\t1\t1\t100\t200\t200\t30\t95\tAlex Smith\n"
            + "1\t3\t0\t0\t0\t0\t0\t0\t1000\t500\t-1\t\n"
            + "5\t3\t1\t1\t1\t1\t100\t300\t200\t30\t95\tJamie Roe\n";
        var pages = TesseractCreditsOcr.ParseTsvPages(tsv, 3);
        Assert.Empty(pages[0]);
        Assert.Equal("Alex Smith", Assert.Single(pages[1]).Text);
        Assert.Equal("Jamie Roe", Assert.Single(pages[2]).Text);
    }

    [Fact]
    public void Clear_short_cards_do_not_trigger_expensive_retries()
    {
        Assert.False(CreditsOcrPreprocessing.NeedsRetry([Line("Alex Smith", .2), Line("Jamie Roe", .3)], 60));
        Assert.True(CreditsOcrPreprocessing.NeedsRetry([new("reg", 98)], 60));
    }
}
