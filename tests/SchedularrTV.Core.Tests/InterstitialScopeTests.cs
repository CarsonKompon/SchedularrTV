using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Query;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Core.Temporal;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class InterstitialScopeTests
{
    [Theory]
    [InlineData(null, null, null, null)]
    [InlineData(3, null, null, 3)]
    [InlineData(3, 2, null, 2)]
    [InlineData(3, 2, 0, 0)]
    [InlineData(3, 2, 4, 4)]
    public void Shorts_limits_inherit_and_survive_yaml_roundtrip(int? global, int? channelLimit, int? blockLimit, int? expected)
    {
        var config = Config();
        config.Settings.Interstitials.MaxShowsPerSlot = global;
        var channel = Channel();
        channel.Interstitials = new() { MaxShowsPerSlot = channelLimit };
        channel.Blocks.Add(new BlockConfig { Name = "Block", Interstitials = new() { MaxShowsPerSlot = blockLimit } });
        config.Channels.Add(channel);
        var restored = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config));
        Assert.Equal(expected, SchedulingEngine.ResolveInterstitials(restored, restored.Channels[0],
            restored.Channels[0].Blocks[0]).MaxShowsPerSlot);
    }

    [Theory]
    [InlineData(null, null, false, 4)]
    [InlineData(1, null, false, 1)]
    [InlineData(0, null, false, 0)]
    [InlineData(1, 2, false, 2)]
    [InlineData(2, 0, false, 0)]
    [InlineData(2, null, true, 2)]
    public async Task Shorts_limit_is_shared_by_stacked_programmes_and_resets_each_slot(
        int? channelLimit, int? blockLimit, bool stacked, int expectedPerSlot)
    {
        var config = new SchedulerConfig();
        config.Settings.TimeZone = "UTC";
        var channel = new ChannelConfig
        {
            Id = "test", Name = "Test", PadToNearest = TimeSpan.FromMinutes(30), StackToFill = stacked,
            BaseRotation = new() { Include = [new ContentInclude { Show = "Main" }], Order = PlaybackOrder.Shuffle },
            Interstitials = new()
            {
                Shows = new() { Include = [new ContentInclude { Show = "Shorts" }], Order = PlaybackOrder.Random },
                MaxShowsPerSlot = channelLimit,
                ShowChance = 100,
                Commercials = new() { Query = "tag:'Ads'", MaxRepeatsPerBreak = 100 },
                Filler = new() { Fallback = false }
            }
        };
        if (blockLimit is not null)
        {
            channel.Blocks.Add(new BlockConfig
            {
                Name = "Block", Time = TimeSpan.Zero, EndTime = TimeSpan.FromHours(1),
                Interstitials = new() { MaxShowsPerSlot = blockLimit }
            });
        }
        List<MediaItem> items = Enumerable.Range(1, 8)
            .Select(number => TestMedia.Episode("Main", 1, number, TimeSpan.FromMinutes(stacked ? 7 : 22))).ToList();
        items.Add(TestMedia.Episode("Shorts", 1, 1, TimeSpan.FromSeconds(stacked ? 30 : 120)));
        items.Add(TestMedia.Filler("ad", TimeSpan.FromSeconds(30), "Ads"));
        var start = new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero);
        ChannelSchedule schedule = await new SchedulingEngine(new FakeMediaLibrary(items), new InMemoryPlaybackStateStore())
            .BuildAsync(config, channel, start, start.AddHours(1));
        for (int slot = 0; slot < 2; slot++)
        {
            Assert.Equal(expectedPerSlot, schedule.Entries.Count(entry => entry.Kind == ScheduleEntryKind.Interstitial
                && entry.Start >= start.AddMinutes(slot * 30) && entry.Start < start.AddMinutes((slot + 1) * 30)));
        }
        Assert.Equal(TimeSpan.Zero, schedule.TotalPadding);
        Assert.Equal(start.AddHours(1), schedule.Entries[^1].End);
    }

    [Fact]
    public void Negative_shorts_limits_are_invalid_in_every_scope()
    {
        var config = Config();
        config.Settings.Interstitials.MaxShowsPerSlot = -1;
        var channel = Channel();
        channel.Interstitials = new() { MaxShowsPerSlot = -1 };
        channel.Blocks.Add(new BlockConfig { Name = "Block", Interstitials = new() { MaxShowsPerSlot = -1 } });
        config.Channels.Add(channel);
        Assert.Equal(3, SchedulerConfigLoader.Validate(config).Errors.Count(error => error.Contains("max_shows_per_slot")));
    }

    [Fact]
    public async Task Empty_main_pool_resets_the_shorts_allowance_at_each_grid_boundary()
    {
        var config = new SchedulerConfig();
        config.Settings.TimeZone = "UTC";
        var channel = new ChannelConfig
        {
            Id = "empty", Name = "Empty", PadToNearest = TimeSpan.FromMinutes(30),
            Interstitials = new()
            {
                MaxShowsPerSlot = 1,
                ShowChance = 100,
                Shows = new() { Include = [new ContentInclude { Show = "Shorts" }], Order = PlaybackOrder.Random },
                Filler = new() { Fallback = false }
            }
        };
        var start = new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero);
        ChannelSchedule schedule = await new SchedulingEngine(new FakeMediaLibrary([
            TestMedia.Episode("Shorts", 1, 1, TimeSpan.FromMinutes(2))]), new InMemoryPlaybackStateStore())
            .BuildAsync(config, channel, start, start.AddHours(1));
        Assert.Equal([start, start.AddMinutes(30)], schedule.Entries
            .Where(entry => entry.Kind == ScheduleEntryKind.Interstitial).Select(entry => entry.Start));
        Assert.Equal(start.AddHours(1), schedule.Entries[^1].End);
    }

    private static SchedulerConfig Config() => new()
    {
        Settings = new SchedulerSettings
        {
            TimeZone = "UTC",
            Interstitials = new InterstitialsConfig
            {
                Commercials = new CommercialsConfig
                {
                    BaseQuery = "tag:\"Commercials\"",
                    BaseTags = ["Any Channel"],
                    Types = ["Ads", "Bumpers"]
                },
                Filler = new FillerTierConfig
                {
                    BaseTags = ["Any Channel"],
                    Types = ["Bumpers"]
                }
            }
        }
    };

    private static ChannelConfig Channel() => new()
    {
        Id = "ch27",
        Name = "Treehouse",
        Number = 27,
        AutoTemporalTags = false,
        CommercialBaseTags = ["Any Childrens"]
    };

    [Fact]
    public void A_channels_brands_replace_the_global_ones_without_losing_the_type_filter()
    {
        InterstitialsConfig resolved = SchedulingEngine.ResolveInterstitials(Config(), Channel(), null);

        Assert.Equal(["Any Childrens"], resolved.Commercials!.BaseTags!);
        Assert.Equal(["Ads", "Bumpers"], resolved.Commercials.Types!);
    }

    [Fact]
    public void Promos_follow_the_adverts_when_nothing_names_their_brands()
    {
        var commercials = new CommercialsConfig { BaseTags = ["Any Childrens"] };

        List<string>? resolved = SchedulingEngine.ResolvePromoBaseTags(
            null, Channel(), new PromoConfig(), commercials);

        Assert.Equal(["Any Childrens"], resolved!);
    }

    [Fact]
    public void A_channel_can_point_promos_at_its_own_brands_without_overriding_the_rest()
    {
        ChannelConfig channel = Channel();
        channel.PromoBaseTags = ["Any Channel", "Treehouse"];

        List<string>? resolved = SchedulingEngine.ResolvePromoBaseTags(
            null, channel, new PromoConfig(), new CommercialsConfig { BaseTags = ["Any Childrens"] });

        Assert.Equal(["Any Channel", "Treehouse"], resolved!);
    }

    [Fact]
    public void A_blocks_promo_brands_win_over_the_channels()
    {
        ChannelConfig channel = Channel();
        channel.PromoBaseTags = ["Any Channel"];

        var block = new BlockConfig { Name = "Night", PromoBaseTags = ["Adult Swim"] };

        List<string>? resolved = SchedulingEngine.ResolvePromoBaseTags(
            block, channel, new PromoConfig(), new CommercialsConfig { BaseTags = ["Any Childrens"] });

        Assert.Equal(["Adult Swim"], resolved!);
    }

    [Fact]
    public void The_promo_blocks_own_brands_are_used_when_no_shorthand_is_set()
    {
        var promos = new PromoConfig { BaseTags = ["Promos Only"] };

        List<string>? resolved = SchedulingEngine.ResolvePromoBaseTags(
            null, Channel(), promos, new CommercialsConfig { BaseTags = ["Any Childrens"] });

        Assert.Equal(["Promos Only"], resolved!);
    }

    [Fact]
    public void The_commercial_pool_is_narrowed_to_the_channels_brand_and_the_configured_types()
    {
        InterstitialsConfig resolved = SchedulingEngine.ResolveInterstitials(Config(), Channel(), null);

        string query = InterstitialQueryBuilder
            .BuildCommercials(resolved.Commercials!, DateTimeOffset.UnixEpoch, new TemporalTagOptions())
            .ToQueryString();

        Assert.Contains("Any Childrens", query);
        Assert.Contains("Ads", query);
        Assert.DoesNotContain("Any Channel", query);
    }

    [Fact]
    public void Filler_is_scoped_to_its_own_brands_and_types()
    {
        InterstitialsConfig resolved = SchedulingEngine.ResolveInterstitials(Config(), Channel(), null);

        string query = InterstitialQueryBuilder
            .BuildFiller(resolved.Filler!, DateTimeOffset.UnixEpoch, new TemporalTagOptions())
            .ToQueryString();

        Assert.Contains("Bumpers", query);
    }

    [Fact]
    public void A_clip_from_another_brand_is_not_in_the_channels_commercial_pool()
    {
        InterstitialsConfig resolved = SchedulingEngine.ResolveInterstitials(Config(), Channel(), null);
        QueryExpression pool = InterstitialQueryBuilder
            .BuildCommercials(resolved.Commercials!, DateTimeOffset.UnixEpoch, new TemporalTagOptions());

        MediaItem adultSwim = TestMedia.Filler("as-1", TimeSpan.FromSeconds(15), "Commercials", "Adult Swim", "OldBumps", "Any Time");
        MediaItem childrens = TestMedia.Filler("kids-1", TimeSpan.FromSeconds(15), "Commercials", "Any Childrens", "Ads", "Any Time");

        IReadOnlyList<MediaItem> matched = QueryEvaluator.Filter([adultSwim, childrens], pool);

        Assert.Equal(["kids-1"], matched.Select(i => i.Id));
    }
}
