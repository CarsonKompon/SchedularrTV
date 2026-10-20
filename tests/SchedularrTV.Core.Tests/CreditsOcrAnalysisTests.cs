using SchedularrTV.CreditSqueeze;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class CreditsOcrAnalysisTests
{
    private static CreditsOcrLine Line(string text, double x, double y, double confidence = 95, double width = .28, double height = .035) =>
        new(text, confidence, new(x, y, width, height));

    [Theory]
    [InlineData(.1, .1)]
    [InlineData(.35, .45)]
    [InlineData(.65, .7)]
    [InlineData(.1, .82)]
    public void Credits_are_found_anywhere_without_a_fixed_source_crop(double x, double y)
    {
        IReadOnlyList<CreditsOcrLine>[] frames = [[Line("Written by", x, y), Line("Zoë O’Neil-Smith", x, y + .05, 74),
            Line("ary", .8, .1, 84, .06, .02), Line("WINUTES", .5, .25, 76)]];
        var clean = Assert.Single(CreditsOcrAnalysis.Clean(frames));
        Assert.Equal(["Written by", "Zoë O’Neil-Smith"], clean.Select(line => line.Text));
    }

    [Fact]
    public void Multiple_columns_and_changing_card_positions_are_retained()
    {
        IReadOnlyList<CreditsOcrLine>[] frames =
        [
            [Line("CAST", .1, .1), Line("Alex Smith", .1, .15, 78), Line("CREW", .6, .1), Line("Jamie Doe", .6, .15, 77)],
            [Line("Produced by", .35, .8), Line("Pat Jones", .35, .85, 76)],
            [Line("Special thanks", .65, .3), Line("Renée Lee", .65, .35, 76)]
        ];
        var clean = CreditsOcrAnalysis.Clean(frames);
        Assert.Equal(4, clean[0].Count);
        Assert.Equal(2, clean[1].Count);
        Assert.Equal(2, clean[2].Count);
    }

    [Fact]
    public void Moving_text_is_corroborated_without_requiring_a_fixed_vertical_position()
    {
        IReadOnlyList<CreditsOcrLine>[] frames =
        [
            [Line("Rare surname", .35, .7, 72), Line("sae", .8, .2, 79, .06)],
            [Line("Rare surname", .35, .5, 76), Line("WINUTES", .1, .7, 78)],
            [Line("Rare surname", .35, .3, 74)]
        ];
        var clean = CreditsOcrAnalysis.Clean(frames);
        Assert.All(clean, frame => Assert.Equal("Rare surname", Assert.Single(frame).Text));
    }

    [Fact]
    public void Clear_brief_single_line_cards_are_not_lost_to_a_repeat_requirement()
    {
        IReadOnlyList<CreditsOcrLine>[] frames = [[Line("Alex Smith", .3, .5)], [], [Line("© 2026", .4, .9)]];
        var clean = CreditsOcrAnalysis.Clean(frames);
        Assert.Equal("Alex Smith", Assert.Single(clean[0]).Text);
        Assert.Empty(clean[1]);
        Assert.Equal("© 2026", Assert.Single(clean[2]).Text);
    }

    [Fact]
    public void Static_isolated_branding_is_removed_while_credit_groups_change()
    {
        var frames = Enumerable.Range(0, 6).Select(index => (IReadOnlyList<CreditsOcrLine>)new[]
        {
            Line("MINUTES", .02, .1, 97, .1, .03),
            Line("CAST", .4, .6), Line($"Person {index}", .4, .65)
        }).ToArray();
        var clean = CreditsOcrAnalysis.Clean(frames);
        Assert.All(clean, frame => Assert.Equal(2, frame.Count));
        Assert.All(clean, frame => Assert.DoesNotContain(frame, line => line.Text == "MINUTES"));
    }

    [Fact]
    public void A_persistent_role_heading_beside_changing_names_is_not_treated_as_a_watermark()
    {
        var frames = Enumerable.Range(0, 6).Select(index => (IReadOnlyList<CreditsOcrLine>)new[]
        {
            Line("CAST", .4, .1), Line($"Person {index}", .4, .15)
        }).ToArray();
        Assert.All(CreditsOcrAnalysis.Clean(frames), frame => Assert.Equal(2, frame.Count));
    }

    [Fact]
    public void A_static_card_with_a_separated_title_is_not_mistaken_for_persistent_branding()
    {
        var frames = Enumerable.Range(0, 6).Select(_ => (IReadOnlyList<CreditsOcrLine>)new[]
        {
            Line("Special thanks", .3, .1), Line("Alex Smith", .3, .6), Line("Jamie Doe", .3, .65)
        }).ToArray();
        Assert.All(CreditsOcrAnalysis.Clean(frames), frame => Assert.Equal(3, frame.Count));
    }

    [Fact]
    public void Tsv_word_bounds_are_combined_and_normalized_to_the_source_frame()
    {
        string tsv = "1\t1\t0\t0\t0\t0\t0\t0\t1000\t500\t-1\t\n"
            + "5\t1\t1\t1\t1\t1\t100\t400\t80\t20\t95\tWritten\n"
            + "5\t1\t1\t1\t1\t2\t190\t400\t30\t20\t95\tby\n";
        var line = Assert.Single(TesseractCreditsOcr.ParseTsv(tsv));
        Assert.Equal("Written by", line.Text);
        Assert.Equal(new CreditsTextBox(.1, .8, .12, .04), line.Box);
    }
}
