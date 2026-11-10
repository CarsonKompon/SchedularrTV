using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Core.Temporal;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>An ad break's bumpers standing in as "Now Playing" and "This Has Been" promos for any show.</summary>
public sealed class BumperPromoTests
{
    private const string Root = @"D:\Media\Commercials\Any Channel";

    private static readonly DateTimeOffset December = new(2026, 12, 20, 20, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset July = new(2026, 7, 10, 20, 0, 0, TimeSpan.Zero);

    private static MediaItem Clip(string path) => new()
    {
        Id = path, Title = Path.GetFileNameWithoutExtension(path), Kind = MediaKind.Filler, Duration = TimeSpan.FromSeconds(8), Path = path
    };

    private static AdBreakConfig AdBreak(BumperPromoUse? nowPlaying, BumperPromoUse? thisHasBeen = null) => new()
    {
        BumpersAsNowPlaying = nowPlaying,
        BumpersAsThisHasBeen = thisHasBeen,
        Folders =
        [
            new AdBreakFolder { Path = $@"{Root}\Ads", Role = AdFolderRole.Ads },
            new AdBreakFolder { Path = $@"{Root}\Bumpers", Role = AdFolderRole.Bumpers },
            new AdBreakFolder { Path = $@"{Root}\Bumpers\Retired", Role = AdFolderRole.Skip }
        ],
        Promos = [new AdBreakPromo { Type = AdPromoType.NowPlaying, Path = $@"{Root}\Now Playing" }]
    };

    [Fact]
    public void Off_by_default_bumpers_are_only_bumpers()
    {
        PromoConfig promos = AdBreakCompiler.Promos([AdBreak(null)], new TemporalTagOptions());

        Assert.Single(promos.NowPlaying.Folders!);
        Assert.Null(promos.NowPlaying.FallbackFolders);
        Assert.False(promos.NowPlaying.Varied);
        Assert.Empty(promos.ThisHasBeen.Folders!);
    }

    [Fact]
    public void Mixed_in_bumpers_join_the_spot_and_take_turns()
    {
        PromoConfig promos = AdBreakCompiler.Promos([AdBreak(BumperPromoUse.Mixed)], new TemporalTagOptions());

        Assert.Equal(2, promos.NowPlaying.Folders!.Count);
        Assert.True(promos.NowPlaying.Varied);
        Assert.Null(promos.NowPlaying.FallbackFolders);
        Assert.Empty(promos.ThisHasBeen.Folders!); // set per spot
    }

    [Fact]
    public void Bumpers_for_when_there_is_no_promo_wait_their_turn()
    {
        PromoConfig promos = AdBreakCompiler.Promos([AdBreak(null, BumperPromoUse.WhenNoPromo)], new TemporalTagOptions());

        Assert.Empty(promos.ThisHasBeen.Folders!);
        PromoFolderSource bumpers = Assert.Single(promos.ThisHasBeen.FallbackFolders!);
        Assert.False(promos.ThisHasBeen.Varied);
        Assert.Null(promos.NowPlaying.FallbackFolders);
        Assert.False(bumpers.PerShow);
    }

    [Fact]
    public void Only_bumpers_for_the_time_of_year_play_as_promos_for_any_show()
    {
        PromoConfig promos = AdBreakCompiler.Promos([AdBreak(BumperPromoUse.Mixed)], new TemporalTagOptions());
        IReadOnlyList<PromoFolderSource> folders = promos.NowPlaying.Folders!;

        MediaItem christmas = Clip($@"{Root}\Bumpers\Christmas\snow ident.mp4");
        MediaItem anyTime = Clip($@"{Root}\Bumpers\Station\ident.mp4");
        MediaItem retired = Clip($@"{Root}\Bumpers\Retired\old.mp4");

        // Seasonal although the ad break itself isn't set to be, and any subfolder (Station) isn't read as a show.
        Assert.Equal("Any Show", AdBreakCompiler.Describe(christmas, folders, December)!.Attributes["show_id"]);
        Assert.Null(AdBreakCompiler.Describe(christmas, folders, July));
        Assert.Equal("Any Show", AdBreakCompiler.Describe(anyTime, folders, July)!.Attributes["show_id"]);
        Assert.Null(AdBreakCompiler.Describe(retired, folders, July));
    }
}
