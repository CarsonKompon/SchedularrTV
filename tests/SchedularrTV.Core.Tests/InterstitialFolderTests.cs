using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Core.Temporal;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class InterstitialFolderTests
{
    private static readonly DateTimeOffset Moment = new(2026, 1, 12, 20, 0, 0, TimeSpan.Zero);

    private static readonly TemporalTagOptions NoTemporal = new();

    private static bool Matches(QueryExpression query, params string[] tags)
    {
        MediaItem item = new()
        {
            Id = "x",
            Title = "clip",
            Kind = MediaKind.Filler,
            Duration = TimeSpan.FromSeconds(30),
            Tags = [.. tags]
        };

        return QueryEvaluator.Matches(item, query);
    }

    [Fact]
    public void Commercial_types_are_or_ed_together()
    {
        CommercialsConfig commercials = new()
        {
            BaseQuery = "tag:\"Commercials\"",
            Types = ["Ads", "Bumpers"]
        };

        QueryExpression query = InterstitialQueryBuilder.BuildCommercials(commercials, Moment, NoTemporal);

        Assert.True(Matches(query, "Commercials", "Ads"));
        Assert.True(Matches(query, "Commercials", "Bumpers"));
        Assert.False(Matches(query, "Commercials", "Coming Up Next"));
    }

    [Fact]
    public void Types_and_brands_are_combined_with_and()
    {
        CommercialsConfig commercials = new()
        {
            BaseQuery = "tag:\"Commercials\"",
            BaseTags = ["Adult Swim", "Any Channel"],
            Types = ["Ads"]
        };

        QueryExpression query = InterstitialQueryBuilder.BuildCommercials(commercials, Moment, NoTemporal);

        Assert.True(Matches(query, "Commercials", "Adult Swim", "Ads"));
        Assert.True(Matches(query, "Commercials", "Any Channel", "Ads"));

        // Right brand, wrong folder.
        Assert.False(Matches(query, "Commercials", "Adult Swim", "Bumpers"));

        // Right folder, wrong brand.
        Assert.False(Matches(query, "Commercials", "Teletoon", "Ads"));
    }

    [Fact]
    public void Filler_types_narrow_the_pool_to_that_folder()
    {
        FillerTierConfig filler = new()
        {
            BaseQuery = "tag:\"Commercials\"",
            BaseTags = ["Adult Swim"],
            Types = ["Bumpers"]
        };

        QueryExpression query = InterstitialQueryBuilder.BuildFiller(filler, Moment, NoTemporal);

        Assert.True(Matches(query, "Commercials", "Adult Swim", "Bumpers"));
        Assert.False(Matches(query, "Commercials", "Adult Swim", "Ads"));
    }

    [Fact]
    public void Filler_inherits_the_commercial_pool_but_not_its_brands()
    {
        ChannelConfig channel = new()
        {
            Id = "cn",
            Name = "Cartoon Network",
            Number = 1,
            CommercialBaseTags = ["Any Channel"],
            Interstitials = new InterstitialsConfig
            {
                Commercials = new CommercialsConfig { BaseQuery = "tag:\"Commercials\"", Types = ["Ads"] },
                Filler = new FillerTierConfig { Types = ["Bumpers"] }
            }
        };

        InterstitialsConfig resolved = SchedulingEngine.ResolveInterstitials(new SchedulerConfig(), channel, block: null);
        QueryExpression query = InterstitialQueryBuilder.BuildFiller(resolved.Filler!, Moment, NoTemporal);

        // The pool is inherited, so filler stays inside the commercials library...
        Assert.False(Matches(query, "Bumpers"));
        Assert.True(Matches(query, "Commercials", "Any Channel", "Bumpers"));

        // ...but brands are not, because bumpers are usually filed under a specific brand and the
        // channel's advert brands may have none at all.
        Assert.True(Matches(query, "Commercials", "Teletoon", "Bumpers"));
    }

    [Fact]
    public void Filler_brands_can_still_be_set_explicitly()
    {
        ChannelConfig channel = new()
        {
            Id = "cn",
            Name = "Cartoon Network",
            Number = 1,
            CommercialBaseTags = ["Any Channel"],
            Interstitials = new InterstitialsConfig
            {
                Commercials = new CommercialsConfig { BaseQuery = "tag:\"Commercials\"" },
                Filler = new FillerTierConfig { Types = ["Bumpers"], BaseTags = ["Teletoon"] }
            }
        };

        InterstitialsConfig resolved = SchedulingEngine.ResolveInterstitials(new SchedulerConfig(), channel, block: null);
        QueryExpression query = InterstitialQueryBuilder.BuildFiller(resolved.Filler!, Moment, NoTemporal);

        Assert.True(Matches(query, "Commercials", "Teletoon", "Bumpers"));
        Assert.False(Matches(query, "Commercials", "Boomerang", "Bumpers"));
    }

    [Fact]
    public void A_hand_written_filler_query_is_not_narrowed_by_the_channel_brands()
    {
        ChannelConfig channel = new()
        {
            Id = "cn",
            Name = "Cartoon Network",
            Number = 1,
            CommercialBaseTags = ["Adult Swim"],
            Interstitials = new InterstitialsConfig
            {
                Commercials = new CommercialsConfig { BaseQuery = "tag:\"Commercials\"" },
                Filler = new FillerTierConfig { Query = "tag:\"House Bumpers\"" }
            }
        };

        InterstitialsConfig resolved = SchedulingEngine.ResolveInterstitials(new SchedulerConfig(), channel, block: null);
        QueryExpression query = InterstitialQueryBuilder.BuildFiller(resolved.Filler!, Moment, NoTemporal);

        Assert.True(Matches(query, "House Bumpers"));
    }

    [Fact]
    public void A_filler_tier_defined_only_by_folders_is_valid()
    {
        SchedulerConfig config = new()
        {
            Channels =
            [
                new ChannelConfig
                {
                    Id = "cn",
                    Name = "Cartoon Network",
                    Number = 1,
                    BaseRotation = new ContentSelector { Query = "kind == 'Episode'" },
                    Interstitials = new InterstitialsConfig
                    {
                        Filler = new FillerTierConfig { Types = ["Bumpers"] }
                    }
                }
            ]
        };

        ConfigValidationResult result = SchedulerConfigLoader.Validate(config);

        Assert.DoesNotContain(result.Errors, e => e.Contains("filler", StringComparison.OrdinalIgnoreCase));
    }
}
