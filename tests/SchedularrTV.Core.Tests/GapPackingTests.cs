using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Core.Temporal;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class GapPackingTests
{
    private static readonly TimeSpan Granularity = TimeSpan.FromMilliseconds(250);

    private static List<MediaItem> Ads(params int[] seconds) =>
        [.. seconds.Select((s, i) => TestMedia.Filler($"ad-{s}-{i}", TimeSpan.FromSeconds(s), "Commercials"))];

    // --- Tier 2: strict-fit commercials -------------------------------------------------------

    [Fact]
    public void A_commercial_longer_than_the_gap_is_never_selected()
    {
        List<MediaItem> ads = Ads(30, 45, 300);

        PackResult result = KnapsackPacker.Pack(ads, TimeSpan.FromSeconds(60), Granularity);

        Assert.All(result.Items, i => Assert.True(i.Duration <= TimeSpan.FromSeconds(60)));
        Assert.DoesNotContain(result.Items, i => i.Item.Duration > TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Commercials_never_overrun_the_gap()
    {
        List<MediaItem> ads = Ads(30, 15, 45, 10);

        PackResult result = KnapsackPacker.Pack(ads, TimeSpan.FromSeconds(65), Granularity);

        Assert.True(result.Filled <= TimeSpan.FromSeconds(65));
        Assert.Equal(TimeSpan.FromSeconds(65), result.Filled + result.Residual);
    }

    [Fact]
    public void Commercials_are_never_trimmed()
    {
        List<MediaItem> ads = Ads(30, 20, 10);

        PackResult result = KnapsackPacker.Pack(ads, TimeSpan.FromSeconds(47), Granularity);

        Assert.All(result.Items, i => Assert.False(i.Trimmed));
        Assert.All(result.Items, i => Assert.Equal(i.Item.Duration, i.Duration));
    }

    [Fact]
    public void Commercials_fill_a_gap_exactly_when_an_exact_subset_exists()
    {
        List<MediaItem> ads = Ads(30, 20, 10);

        PackResult result = KnapsackPacker.Pack(ads, TimeSpan.FromSeconds(60), Granularity);

        Assert.Equal(TimeSpan.Zero, result.Residual);
        Assert.Equal(3, result.Items.Count);
    }

    [Fact]
    public void A_commercial_can_repeat_when_repeats_are_allowed()
    {
        List<MediaItem> ads = Ads(30);

        PackResult single = KnapsackPacker.Pack(ads, TimeSpan.FromSeconds(60), Granularity, maxRepeatsPerItem: 1);
        PackResult repeated = KnapsackPacker.Pack(ads, TimeSpan.FromSeconds(60), Granularity, maxRepeatsPerItem: 2);

        Assert.Single(single.Items);
        Assert.Equal(2, repeated.Items.Count);
        Assert.Equal(TimeSpan.Zero, repeated.Residual);
    }

    // --- Tier 3: trimmable filler ---------------------------------------------------------------

    [Fact]
    public void Filler_trims_to_close_the_residual_gap_exactly()
    {
        List<MediaItem> ads = Ads(30);
        List<MediaItem> filler = [TestMedia.Bumper("bumper-60", TimeSpan.FromSeconds(60))];

        PackResult result = KnapsackPacker.Pack(ads, TimeSpan.FromSeconds(47), Granularity, 1, filler);

        Assert.Equal(TimeSpan.Zero, result.Residual);

        PackedItem trimmed = Assert.Single(result.Items, i => i.IsFiller);
        Assert.True(trimmed.Trimmed);
        Assert.Equal(TimeSpan.FromSeconds(17), trimmed.Duration);
        Assert.Equal(TimeSpan.FromSeconds(60), trimmed.Item.Duration);
    }

    [Fact]
    public void Filler_that_fits_exactly_is_not_marked_trimmed()
    {
        List<MediaItem> ads = Ads(30);
        List<MediaItem> filler = [TestMedia.Bumper("bumper-17", TimeSpan.FromSeconds(17))];

        PackResult result = KnapsackPacker.Pack(ads, TimeSpan.FromSeconds(47), Granularity, 1, filler);

        PackedItem chosen = Assert.Single(result.Items, i => i.IsFiller);
        Assert.False(chosen.Trimmed);
        Assert.Equal(TimeSpan.Zero, result.Residual);
    }

    [Fact]
    public void Filler_flagged_not_trimmable_is_never_cut()
    {
        List<MediaItem> ads = Ads(30);
        List<MediaItem> filler = [TestMedia.Bumper("bumper-60", TimeSpan.FromSeconds(60), trimmable: false)];

        PackResult result = KnapsackPacker.Pack(ads, TimeSpan.FromSeconds(47), Granularity, 1, filler);

        Assert.DoesNotContain(result.Items, i => i.Trimmed);
        Assert.Equal(TimeSpan.FromSeconds(17), result.Residual);
    }

    [Fact]
    public void Filler_honours_stop_before_end_from_the_item_and_the_tier()
    {
        List<MediaItem> ads = Ads(30);

        PackResult fromTier = KnapsackPacker.Pack(
            ads,
            TimeSpan.FromSeconds(47),
            Granularity,
            1,
            [TestMedia.Bumper("b", TimeSpan.FromSeconds(60))],
            new FillerPackOptions { StopBeforeEnd = true });

        PackResult fromItem = KnapsackPacker.Pack(
            ads,
            TimeSpan.FromSeconds(47),
            Granularity,
            1,
            [TestMedia.Bumper("b", TimeSpan.FromSeconds(60), stopBeforeEnd: true)]);

        Assert.True(Assert.Single(fromTier.Items, i => i.IsFiller).StopBeforeEnd);
        Assert.True(Assert.Single(fromItem.Items, i => i.IsFiller).StopBeforeEnd);
    }

    [Fact]
    public void Filler_chains_shorter_items_before_trimming()
    {
        List<MediaItem> filler =
        [
            TestMedia.Bumper("b-10", TimeSpan.FromSeconds(10), trimmable: false),
            TestMedia.Bumper("b-5", TimeSpan.FromSeconds(5), trimmable: false)
        ];

        PackResult result = KnapsackPacker.Pack([], TimeSpan.FromSeconds(15), Granularity, 1, filler);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(TimeSpan.Zero, result.Residual);
        Assert.All(result.Items, i => Assert.False(i.Trimmed));
    }

    [Fact]
    public void A_residual_below_the_minimum_is_left_as_padding()
    {
        List<MediaItem> ads = Ads(30);
        List<MediaItem> filler = [TestMedia.Bumper("bumper-60", TimeSpan.FromSeconds(60))];

        PackResult result = KnapsackPacker.Pack(
            ads,
            TimeSpan.FromSeconds(30) + TimeSpan.FromMilliseconds(400),
            Granularity,
            1,
            filler,
            new FillerPackOptions { MinTrimmedDuration = TimeSpan.FromSeconds(1) });

        Assert.DoesNotContain(result.Items, i => i.IsFiller);
        Assert.Equal(TimeSpan.FromMilliseconds(400), result.Residual);
    }

    [Fact]
    public void Filler_is_skipped_entirely_when_the_commercial_pass_lands_exactly()
    {
        List<MediaItem> ads = Ads(30, 20, 10);
        List<MediaItem> filler = [TestMedia.Bumper("bumper-60", TimeSpan.FromSeconds(60))];

        PackResult result = KnapsackPacker.Pack(ads, TimeSpan.FromSeconds(60), Granularity, 1, filler);

        Assert.DoesNotContain(result.Items, i => i.IsFiller);
    }

    // --- Query construction ---------------------------------------------------------------------

    [Fact]
    public void Commercial_query_expands_with_temporal_tags()
    {
        var commercials = new CommercialsConfig
        {
            BaseQuery = "tag:\"Commercials\"",
            BaseTags = ["Adult Swim"],
            AutoTemporalTags = true
        };

        var options = new TemporalTagOptions { IncludeFullDate = false };
        string query = InterstitialQueryBuilder
            .BuildCommercials(commercials, new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero), options)
            .ToQueryString();

        Assert.Contains("tag:\"Commercials\"", query, StringComparison.Ordinal);
        Assert.Contains("tag:\"Adult Swim\"", query, StringComparison.Ordinal);
        Assert.Contains("tag:\"Any Time\"", query, StringComparison.Ordinal);
        Assert.Contains("tag:\"January\"", query, StringComparison.Ordinal);
        Assert.Contains("tag:\"Winter\"", query, StringComparison.Ordinal);
        Assert.Contains("tag:\"01-10\"", query, StringComparison.Ordinal);
    }

    [Fact]
    public void Commercial_query_stays_narrow_when_temporal_tags_are_off()
    {
        var commercials = new CommercialsConfig { BaseQuery = "tag:\"Commercials\"", AutoTemporalTags = false };

        string query = InterstitialQueryBuilder
            .BuildCommercials(commercials, new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero), new TemporalTagOptions())
            .ToQueryString();

        Assert.Equal("tag:\"Commercials\"", query);
    }

    [Fact]
    public void Up_next_query_matches_either_promo_type_or_tag()
    {
        var promos = new PromoConfig { BaseQuery = "tag:'Promo'" };

        var byAttribute = TestMedia.Promo("a", TimeSpan.FromSeconds(15), "SpongeBob", "up_next");
        var byTag = TestMedia.Promo("b", TimeSpan.FromSeconds(15), "SpongeBob", promoType: null, "Up Next");
        var wrongShow = TestMedia.Promo("c", TimeSpan.FromSeconds(15), "Rugrats", "up_next");
        var wrongType = TestMedia.Promo("d", TimeSpan.FromSeconds(15), "SpongeBob", "later_today");

        Query.QueryExpression expression = InterstitialQueryBuilder.BuildUpNext(promos, ["SpongeBob"]);

        Assert.True(Query.QueryEvaluator.Matches(byAttribute, expression));
        Assert.True(Query.QueryEvaluator.Matches(byTag, expression));
        Assert.False(Query.QueryEvaluator.Matches(wrongShow, expression));
        Assert.False(Query.QueryEvaluator.Matches(wrongType, expression));
    }

    [Fact]
    public void Up_next_query_is_empty_when_no_target_is_known()
    {
        Query.QueryExpression expression = InterstitialQueryBuilder.BuildUpNext(new PromoConfig(), []);

        Assert.IsType<Query.NoneExpression>(expression);
    }

    [Fact]
    public void An_any_show_promo_matches_whatever_is_being_promoted()
    {
        var promos = new PromoConfig { BaseQuery = "tag:'Promo'" };

        var anyShow = TestMedia.Promo("a", TimeSpan.FromSeconds(15), "Any Show", "up_next");
        var otherShow = TestMedia.Promo("b", TimeSpan.FromSeconds(15), "Rugrats", "up_next");

        Query.QueryExpression expression = InterstitialQueryBuilder.BuildUpNext(promos, ["SpongeBob"]);

        Assert.True(Query.QueryEvaluator.Matches(anyShow, expression));
        Assert.False(Query.QueryEvaluator.Matches(otherShow, expression));
    }

    [Fact]
    public void A_promo_query_is_limited_to_the_channels_brand_folders()
    {
        var promos = new PromoConfig { BaseQuery = "tag:'Promo'" };

        var ownBrand = TestMedia.Promo("a", TimeSpan.FromSeconds(15), "SpongeBob", "up_next", "Cartoon Network");
        var shared = TestMedia.Promo("b", TimeSpan.FromSeconds(15), "SpongeBob", "up_next", "Any Channel");
        var otherBrand = TestMedia.Promo("c", TimeSpan.FromSeconds(15), "SpongeBob", "up_next", "Teletoon");

        Query.QueryExpression expression = InterstitialQueryBuilder.BuildSpot(
            promos,
            promos.UpNext,
            ["SpongeBob"],
            ["Any Channel", "Any Childrens", "Cartoon Network"]);

        Assert.True(Query.QueryEvaluator.Matches(ownBrand, expression));
        Assert.True(Query.QueryEvaluator.Matches(shared, expression));
        Assert.False(Query.QueryEvaluator.Matches(otherBrand, expression));
    }
}
