using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Query;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Core.Temporal;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class AdBreakMigrationTests
{
    private static readonly DateTimeOffset Moment = new(2026, 12, 20, 20, 0, 0, TimeSpan.Zero);

    private static readonly TemporalTagOptions Temporal = new();

    private static int _number;

    // Each channel has its own number, so the schedule is valid (two channels can't share one).
    private static ChannelConfig Channel(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Number = Interlocked.Increment(ref _number),
        BaseRotation = new ContentSelector { Query = "kind == 'Episode'" }
    };

    private static string Ads(SchedulerConfig config, ChannelConfig channel, BlockConfig? block = null) =>
        InterstitialQueryBuilder.BuildCommercials(SchedulingEngine.ResolveInterstitials(config, channel, block).Commercials!, Moment, Temporal).ToQueryString();

    private static string Filler(SchedulerConfig config, ChannelConfig channel, BlockConfig? block = null) =>
        InterstitialQueryBuilder.BuildFiller(SchedulingEngine.ResolveInterstitials(config, channel, block).Filler!, Moment, Temporal).ToQueryString();

    private static string UpNext(SchedulerConfig config, ChannelConfig channel)
    {
        (PromoConfig promos, List<string>? brands) = SchedulingEngine.ResolvedPromos(config, channel);
        return InterstitialQueryBuilder.BuildSpot(promos, promos.UpNext, ["Top Cat"], brands).ToQueryString();
    }

    private static SchedulerConfig Legacy()
    {
        ChannelConfig toons = Channel("toons", "Toons");
        toons.CommercialBaseTags = ["Cartoon Network"];
        toons.AutoTemporalTags = true;
        toons.Interstitials = new InterstitialsConfig
        {
            ShowChance = 50,
            Commercials = new CommercialsConfig { Types = ["Ads"], MaxRepeatsPerBreak = 2 },
            Filler = new FillerTierConfig { Types = ["Bumpers"] }
        };
        toons.Promos = new PromoConfig { MaxPerBreak = 1 };
        toons.Blocks.Add(new BlockConfig { Name = "Late", Time = TimeSpan.FromHours(23), CommercialBaseTags = ["Adult Swim"], Shows = [new ContentSelector { Query = "kind == 'Episode'" }] });

        SchedulerConfig config = new();
        config.Channels.Add(toons);
        config.Channels.Add(Channel("one", "One"));
        config.Channels.Add(Channel("two", "Two"));
        return config;
    }

    [Fact]
    public void Breaks_are_filled_from_the_same_clips_after_the_move()
    {
        SchedulerConfig config = Legacy();
        ChannelConfig toons = config.Channels[0];
        BlockConfig late = toons.Blocks[0];

        string ads = Ads(config, toons), filler = Filler(config, toons), lateAds = Ads(config, toons, late), upNext = UpNext(config, toons);
        string plainAds = Ads(config, config.Channels[1]);
        int? repeats = SchedulingEngine.ResolveInterstitials(config, toons, null).Commercials!.MaxRepeatsPerBreak;

        Assert.True(AdBreakMigration.Migrate(config));

        Assert.Equal(ads, Ads(config, toons));
        Assert.Equal(filler, Filler(config, toons));
        Assert.Equal(lateAds, Ads(config, toons, late));
        Assert.Equal(upNext, UpNext(config, toons));
        Assert.Equal(plainAds, Ads(config, config.Channels[1]));
        Assert.Equal(repeats, SchedulingEngine.ResolveInterstitials(config, toons, null).Commercials!.MaxRepeatsPerBreak);
        Assert.Equal(1, SchedulingEngine.ResolvedPromos(config, toons).Promos.MaxPerBreak);
    }

    [Fact]
    public void The_old_settings_are_cleared_and_the_channels_point_at_ad_breaks()
    {
        SchedulerConfig config = Legacy();
        AdBreakMigration.Migrate(config);
        ChannelConfig toons = config.Channels[0];

        Assert.True(config.Settings.AdBreaksMigrated);
        Assert.Equal(["Toons"], toons.AdBreaks);
        Assert.Null(toons.CommercialBaseTags);
        Assert.Null(toons.AutoTemporalTags);
        Assert.Null(toons.Promos);
        Assert.Null(toons.Interstitials!.Commercials);
        Assert.Equal(50, toons.Interstitials.ShowChance);

        // Channels on the global defaults share one; a block that differed gets its own.
        Assert.Equal([AdBreakMigration.DefaultName], config.Channels[1].AdBreaks);
        Assert.Equal([AdBreakMigration.DefaultName], config.Channels[2].AdBreaks);
        Assert.Equal(["Toons – Late"], toons.Blocks[0].AdBreaks);
        Assert.Null(toons.Blocks[0].CommercialBaseTags);
        Assert.Equal(3, config.AdBreaks.Count);
    }

    [Fact]
    public void It_runs_once_and_survives_a_round_trip()
    {
        SchedulerConfig config = Legacy();
        AdBreakMigration.Migrate(config);

        SchedulerConfig reloaded = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config));
        Assert.False(AdBreakMigration.Migrate(reloaded));
        Assert.Empty(SchedulerConfigLoader.Validate(reloaded).Errors);
        Assert.Equal(Ads(config, config.Channels[0]), Ads(reloaded, reloaded.Channels[0]));
    }

    [Fact]
    public void After_the_move_a_channel_with_no_ad_breaks_has_empty_breaks()
    {
        SchedulerConfig config = Legacy();
        AdBreakMigration.Migrate(config);
        config.Channels[1].AdBreaks = null;

        Assert.IsType<NoneExpression>(InterstitialQueryBuilder.BuildCommercials(
            SchedulingEngine.ResolveInterstitials(config, config.Channels[1], null).Commercials!, Moment, Temporal));
        Assert.False(SchedulingEngine.ResolveInterstitials(config, config.Channels[1], null).Filler!.IsEnabled);
    }

    [Fact]
    public void Tag_based_settings_added_later_are_moved_too()
    {
        SchedulerConfig config = Legacy();
        AdBreakMigration.Migrate(config);

        ChannelConfig imported = Channel("imported", "Imported");
        imported.CommercialBaseTags = ["Teletoon"];
        config.Channels.Add(imported);

        Assert.True(AdBreakMigration.Migrate(config));
        Assert.Equal(["Imported"], imported.AdBreaks);
        Assert.Null(imported.CommercialBaseTags);
    }
}
