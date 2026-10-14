using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Core.Temporal;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class AdBreakTests
{
    private const string Root = @"D:\Media\Commercials\Any Channel";

    private static readonly DateTimeOffset December = new(2026, 12, 20, 20, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset July = new(2026, 7, 10, 20, 0, 0, TimeSpan.Zero);

    private static MediaItem Clip(string path) => new()
    {
        Id = path,
        Title = Path.GetFileNameWithoutExtension(path),
        Kind = MediaKind.Filler,
        Duration = TimeSpan.FromSeconds(30),
        Path = path
    };

    private static AdBreakConfig AnyChannel(bool seasonal = false) => new()
    {
        Seasonal = seasonal,
        Folders =
        [
            new AdBreakFolder { Path = $@"{Root}\Ads", Role = AdFolderRole.Ads },
            new AdBreakFolder { Path = $@"{Root}\Ads\Old", Role = AdFolderRole.Skip },
            new AdBreakFolder { Path = $@"{Root}\Bumpers", Role = AdFolderRole.Bumpers },
            new AdBreakFolder { Path = $@"{Root}\Filler", Role = AdFolderRole.Filler }
        ],
        Promos =
        [
            new AdBreakPromo { Type = AdPromoType.UpNext, Path = $@"{Root}\Ads\Up Next", Times = true }
        ]
    };

    private static SchedulerConfig Config(params AdBreakConfig[] adBreaks)
    {
        SchedulerConfig config = new();
        for (int i = 0; i < adBreaks.Length; i++)
        {
            config.AdBreaks[$"Break {i}"] = adBreaks[i];
        }

        return config;
    }

    private static ChannelConfig Channel(params string[] adBreaks) => new()
    {
        Id = "ch",
        Name = "Channel",
        AdBreaks = [.. adBreaks]
    };

    [Fact]
    public void Ads_come_from_their_folder_but_not_a_skipped_or_promo_folder_inside_it()
    {
        SchedulerConfig config = Config(AnyChannel());
        InterstitialsConfig breaks = SchedulingEngine.ResolveInterstitials(config, Channel("Break 0"), null);
        QueryExpression ads = InterstitialQueryBuilder.BuildCommercials(breaks.Commercials!, July, new TemporalTagOptions());

        Assert.True(QueryEvaluator.Matches(Clip($@"{Root}\Ads\cola.mp4"), ads));
        Assert.True(QueryEvaluator.Matches(Clip($@"{Root}/Ads/Brand/cola.mp4"), ads));
        Assert.False(QueryEvaluator.Matches(Clip($@"{Root}\Ads\Old\cola.mp4"), ads));
        Assert.False(QueryEvaluator.Matches(Clip($@"{Root}\Ads\Up Next\top cat.mp4"), ads));
        Assert.False(QueryEvaluator.Matches(Clip($@"{Root}\Filler\loop.mp4"), ads));

        // Mixed bumpers go in with the ads.
        Assert.True(QueryEvaluator.Matches(Clip($@"{Root}\Bumpers\ident.mp4"), ads));
    }

    [Fact]
    public void Filler_comes_from_the_filler_folders_and_is_off_without_any()
    {
        InterstitialsConfig breaks = SchedulingEngine.ResolveInterstitials(Config(AnyChannel()), Channel("Break 0"), null);
        QueryExpression filler = InterstitialQueryBuilder.BuildFiller(breaks.Filler!, July, new TemporalTagOptions());
        Assert.True(breaks.Filler!.IsEnabled);
        Assert.True(QueryEvaluator.Matches(Clip($@"{Root}\Filler\loop.mp4"), filler));
        Assert.False(QueryEvaluator.Matches(Clip($@"{Root}\Ads\cola.mp4"), filler));

        AdBreakConfig noFiller = AnyChannel();
        noFiller.Folders.RemoveAll(folder => folder.Role == AdFolderRole.Filler);
        Assert.False(SchedulingEngine.ResolveInterstitials(Config(noFiller), Channel("Break 0"), null).Filler!.IsEnabled);
    }

    [Fact]
    public void Bumpers_at_the_ends_are_kept_out_of_the_ads()
    {
        AdBreakConfig adBreak = AnyChannel();
        adBreak.BumperPlacement = BumperPlacement.StartAndEnd;
        InterstitialsConfig breaks = SchedulingEngine.ResolveInterstitials(Config(adBreak), Channel("Break 0"), null);
        QueryExpression ads = InterstitialQueryBuilder.BuildCommercials(breaks.Commercials!, July, new TemporalTagOptions());

        Assert.False(QueryEvaluator.Matches(Clip($@"{Root}\Bumpers\ident.mp4"), ads));
        Assert.Single(breaks.OpeningBumpers!);
        Assert.Single(breaks.ClosingBumpers!);
    }

    [Fact]
    public void Several_ad_breaks_pool_their_ads()
    {
        AdBreakConfig kids = new() { Folders = [new AdBreakFolder { Path = @"D:\Media\Commercials\Kids\Ads", Role = AdFolderRole.Ads }] };
        InterstitialsConfig breaks = SchedulingEngine.ResolveInterstitials(Config(AnyChannel(), kids), Channel("Break 0", "Break 1"), null);
        QueryExpression ads = InterstitialQueryBuilder.BuildCommercials(breaks.Commercials!, July, new TemporalTagOptions());

        Assert.True(QueryEvaluator.Matches(Clip($@"{Root}\Ads\cola.mp4"), ads));
        Assert.True(QueryEvaluator.Matches(Clip(@"D:\Media\Commercials\Kids\Ads\toy.mp4"), ads));
        Assert.False(QueryEvaluator.Matches(Clip(@"D:\Media\Commercials\Adult\Ads\beer.mp4"), ads));
    }

    [Fact]
    public void A_block_can_use_its_own_ad_breaks()
    {
        AdBreakConfig kids = new() { Folders = [new AdBreakFolder { Path = @"D:\Media\Commercials\Kids\Ads", Role = AdFolderRole.Ads }] };
        SchedulerConfig config = Config(AnyChannel(), kids);
        ChannelConfig channel = Channel("Break 0");
        BlockConfig block = new() { Name = "Saturday", AdBreaks = ["Break 1"] };

        QueryExpression ads = InterstitialQueryBuilder.BuildCommercials(
            SchedulingEngine.ResolveInterstitials(config, channel, block).Commercials!, July, new TemporalTagOptions());

        Assert.True(QueryEvaluator.Matches(Clip(@"D:\Media\Commercials\Kids\Ads\toy.mp4"), ads));
        Assert.False(QueryEvaluator.Matches(Clip($@"{Root}\Ads\cola.mp4"), ads));
    }

    [Fact]
    public void Seasonal_folders_only_play_in_season()
    {
        InterstitialsConfig breaks = SchedulingEngine.ResolveInterstitials(Config(AnyChannel(seasonal: true)), Channel("Break 0"), null);
        TemporalTagOptions options = new();

        QueryExpression december = InterstitialQueryBuilder.BuildCommercials(breaks.Commercials!, December, options);
        QueryExpression july = InterstitialQueryBuilder.BuildCommercials(breaks.Commercials!, July, options);

        MediaItem christmas = Clip($@"{Root}\Ads\Christmas\toys.mp4");
        MediaItem winter = Clip($@"{Root}\Ads\Brand\Winter\coats.mp4");
        MediaItem anyTime = Clip($@"{Root}\Ads\Any Time\cola.mp4");
        MediaItem loose = Clip($@"{Root}\Ads\cola.mp4");
        MediaItem today = Clip($@"{Root}\Ads\12-20\sale.mp4");
        MediaItem otherDay = Clip($@"{Root}\Ads\12-21\sale.mp4");

        Assert.True(QueryEvaluator.Matches(christmas, december));
        Assert.False(QueryEvaluator.Matches(christmas, july));
        Assert.True(QueryEvaluator.Matches(winter, december));
        Assert.False(QueryEvaluator.Matches(winter, july));
        Assert.True(QueryEvaluator.Matches(anyTime, july));
        Assert.True(QueryEvaluator.Matches(loose, july));
        Assert.True(QueryEvaluator.Matches(today, december));
        Assert.False(QueryEvaluator.Matches(otherDay, december));
    }

    [Fact]
    public void Ad_breaks_take_over_from_the_tag_based_settings_only_when_chosen()
    {
        SchedulerConfig config = Config(AnyChannel());
        Assert.Null(SchedulingEngine.ResolveInterstitials(config, Channel(), null).Commercials!.Sources);
        Assert.NotNull(SchedulingEngine.ResolveInterstitials(config, Channel("Break 0"), null).Commercials!.Sources);
    }

    [Fact]
    public void Ad_break_tuning_sits_between_the_channel_and_the_global_settings()
    {
        AdBreakConfig adBreak = AnyChannel();
        adBreak.Commercials = new CommercialsConfig { MaxRepeatsPerBreak = 3, MinItemDuration = TimeSpan.FromSeconds(5), BaseQuery = "tag:\"Ignored\"" };
        ChannelConfig channel = Channel("Break 0");
        channel.Interstitials = new InterstitialsConfig { Commercials = new CommercialsConfig { MinItemDuration = TimeSpan.FromSeconds(10) } };

        CommercialsConfig commercials = SchedulingEngine.ResolveInterstitials(Config(adBreak), channel, null).Commercials!;

        Assert.Equal(3, commercials.MaxRepeatsPerBreak);
        Assert.Equal(TimeSpan.FromSeconds(10), commercials.MinItemDuration);
    }

    [Fact]
    public void Promo_folders_are_read_for_show_day_and_time()
    {
        PromoFolderSource folder = new($@"{Root}\Up Next", [], PerShow: true, AnyShow: true, Times: true, Days: true, AnyShowFolder: "Any Show");

        MediaItem? timed = AdBreakCompiler.Describe(Clip($@"{Root}\Up Next\Top Cat\Saturday\8:30pm\clip.mp4"), [folder]);
        Assert.NotNull(timed);
        Assert.Equal("Top Cat", timed!.Attributes["show_id"]);
        Assert.Equal("saturday", timed.Attributes["promo_day"]);
        Assert.Equal("830pm", timed.Attributes["promo_clock"]);

        MediaItem? generic = AdBreakCompiler.Describe(Clip($@"{Root}\Up Next\clip.mp4"), [folder]);
        Assert.Equal("Any Show", generic!.Attributes["show_id"]);

        PromoFolderSource showsOnly = folder with { AnyShow = false };
        Assert.Null(AdBreakCompiler.Describe(Clip($@"{Root}\Up Next\clip.mp4"), [showsOnly]));
        Assert.Null(AdBreakCompiler.Describe(Clip($@"{Root}\Ads\clip.mp4"), [folder]));
    }

    [Fact]
    public void A_promos_folder_sorted_by_show_and_type_reads_both_orders()
    {
        const string promos = @"D:\Media\Commercials\Any Channel\Promos";
        PromoFolderSource backToTheShow = new(promos, [], PerShow: true, AnyShow: true, Times: true, Days: true, AnyShowFolder: "Any Show",
            Type: AdPromoType.BackToTheShow);

        MediaItem? showFirst = AdBreakCompiler.Describe(Clip($@"{promos}\Looney Tunes\Back to the Show\clip.mp4"), [backToTheShow]);
        Assert.Equal("Looney Tunes", showFirst!.Attributes["show_id"]);

        MediaItem? typeFirst = AdBreakCompiler.Describe(Clip($@"{promos}\Back To The Show\Iron Man\clip.mp4"), [backToTheShow]);
        Assert.Equal("Iron Man", typeFirst!.Attributes["show_id"]);

        // Another type's folder, or none, isn't this spot's.
        Assert.Null(AdBreakCompiler.Describe(Clip($@"{promos}\Looney Tunes\You're Watching\clip.mp4"), [backToTheShow]));
        Assert.Null(AdBreakCompiler.Describe(Clip($@"{promos}\Looney Tunes\clip.mp4"), [backToTheShow]));
    }

    [Fact]
    public void The_any_show_folder_works_in_a_promos_folder_sorted_by_show_and_type()
    {
        const string promos = @"D:\Media\Commercials\Any Channel\Promos";
        PromoFolderSource nowPlaying = new(promos, [], PerShow: true, AnyShow: true, Times: true, Days: true, AnyShowFolder: "Any Show",
            Type: AdPromoType.NowPlaying);

        // Either order, and with a time folder inside.
        Assert.Equal("Any Show", AdBreakCompiler.Describe(Clip($@"{promos}\Any Show\Now Playing\clip.mp4"), [nowPlaying])!.Attributes["show_id"]);
        Assert.Equal("Any Show", AdBreakCompiler.Describe(Clip($@"{promos}\Now Playing\Any Show\clip.mp4"), [nowPlaying])!.Attributes["show_id"]);
        MediaItem? timed = AdBreakCompiler.Describe(Clip($@"{promos}\Any Show\Now Playing\8pm\clip.mp4"), [nowPlaying]);
        Assert.Equal("Any Show", timed!.Attributes["show_id"]);
        Assert.Equal("8pm", timed.Attributes["promo_clock"]);

        // Another type's clips in Any Show aren't this spot's.
        Assert.Null(AdBreakCompiler.Describe(Clip($@"{promos}\Any Show\Up Next\clip.mp4"), [nowPlaying]));

        // A renamed "any show" folder works the same way.
        PromoFolderSource renamed = nowPlaying with { AnyShowFolder = "All Shows" };
        Assert.Equal("All Shows", AdBreakCompiler.Describe(Clip($@"{promos}\All Shows\Now Playing\clip.mp4"), [renamed])!.Attributes["show_id"]);
    }

    [Fact]
    public void Seasonal_promo_folders_only_count_in_season_and_are_not_shows()
    {
        const string promos = @"D:\Media\Commercials\Any Channel\Promos";
        PromoFolderSource upNext = new(promos, [], PerShow: true, AnyShow: true, Times: true, Days: true, AnyShowFolder: "Any Show",
            Type: AdPromoType.UpNext, Seasonal: new TemporalTagOptions());
        MediaItem clip = Clip($@"{promos}\Christmas\Top Cat\Up Next\clip.mp4");

        Assert.Equal("Top Cat", AdBreakCompiler.Describe(clip, [upNext], December)!.Attributes["show_id"]);
        Assert.Null(AdBreakCompiler.Describe(clip, [upNext], July));
    }

    [Fact]
    public void A_promos_folder_feeds_every_spot_and_stays_out_of_the_ads()
    {
        AdBreakConfig adBreak = new()
        {
            Folders = [new AdBreakFolder { Path = Root, Role = AdFolderRole.Ads }],
            PromoRoots = [new AdBreakPromoRoot { Path = $@"{Root}\Promos" }]
        };
        SchedulerConfig config = Config(adBreak);

        PromoConfig promos = SchedulingEngine.ResolvePromoConfig(config, Channel("Break 0"), null);
        Assert.Equal(AdPromoType.UpNext, promos.UpNext.Folders!.Single().Type);
        Assert.Equal(AdPromoType.BackToTheShow, promos.BackToTheShow.Folders!.Single().Type);
        Assert.Equal(2, promos.Windows.Count);

        QueryExpression ads = InterstitialQueryBuilder.BuildCommercials(
            SchedulingEngine.ResolveInterstitials(config, Channel("Break 0"), null).Commercials!, July, new TemporalTagOptions());
        Assert.False(QueryEvaluator.Matches(Clip($@"{Root}\Promos\Top Cat\Up Next\clip.mp4"), ads));
        Assert.True(QueryEvaluator.Matches(Clip($@"{Root}\Ads\cola.mp4"), ads));
    }

    [Theory]
    [InlineData("4pm", "4pm")]
    [InlineData("4 PM", "4pm")]
    [InlineData("8:30pm", "830pm")]
    [InlineData("830pm", "830pm")]
    [InlineData("20:30", "830pm")]
    [InlineData("12am", "12am")]
    public void Start_time_folders_are_read_as_promo_matching_spells_them(string folder, string expected)
    {
        Assert.True(AdBreakCompiler.TryReadClock(folder, out string? clock));
        Assert.Equal(expected, clock);
    }

    [Theory]
    [InlineData("24")]
    [InlineData("Top Cat")]
    [InlineData("13pm")]
    public void Other_folders_are_not_start_times(string folder) =>
        Assert.False(AdBreakCompiler.TryReadClock(folder, out _));

    [Fact]
    public void Promos_from_ad_breaks_match_by_folder_and_keep_unused_spots_on()
    {
        PromoConfig promos = SchedulingEngine.ResolvePromoConfig(Config(AnyChannel()), Channel("Break 0"), null);

        Assert.True(promos.FromAdBreaks);
        Assert.Single(promos.UpNext.Folders!);
        Assert.True(promos.NowPlaying.Enabled);
        Assert.Empty(promos.NowPlaying.Folders!);
        Assert.Empty(promos.Windows);
    }

    [Fact]
    public void Ad_breaks_round_trip_through_yaml()
    {
        SchedulerConfig config = Config(AnyChannel(seasonal: true));
        ChannelConfig channel = Channel("Break 0");
        channel.BaseRotation = new ContentSelector { Query = "kind == 'Episode'" };
        config.Channels.Add(channel);

        SchedulerConfig parsed = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config));

        AdBreakConfig adBreak = parsed.AdBreaks["break 0"];
        Assert.True(adBreak.Seasonal);
        Assert.Equal(4, adBreak.Folders.Count);
        Assert.Equal(AdFolderRole.Skip, adBreak.Folders[1].Role);
        Assert.Equal(AdPromoType.UpNext, adBreak.Promos.Single().Type);
        Assert.Equal(["Break 0"], parsed.Channels[0].AdBreaks);
        Assert.True(SchedulerConfigLoader.Validate(parsed).IsValid);
    }

    [Fact]
    public void An_unknown_ad_break_is_a_validation_error()
    {
        SchedulerConfig config = Config();
        ChannelConfig channel = Channel("Nope");
        channel.BaseRotation = new ContentSelector { Query = "kind == 'Episode'" };
        config.Channels.Add(channel);

        Assert.Contains(SchedulerConfigLoader.Validate(config).Errors, error => error.Contains("ad_breaks", StringComparison.Ordinal));
    }

    [Fact]
    public void Looping_filler_goes_round_the_clips_to_fill_a_long_gap()
    {
        MediaItem a = Clip("a.mp4");
        MediaItem b = Clip("b.mp4");
        PackResult packed = KnapsackPacker.Pack([], TimeSpan.FromMinutes(3), TimeSpan.FromMilliseconds(250), 1, [a, b],
            new FillerPackOptions { Loop = true });

        Assert.Equal(TimeSpan.FromMinutes(3), packed.Filled);
        Assert.Equal(6, packed.Items.Count);
        Assert.NotEqual(packed.Items[0].Item.Id, packed.Items[1].Item.Id);
    }
}
