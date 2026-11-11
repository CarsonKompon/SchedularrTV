using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Core.Temporal;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>Rating folders: promos for any programme with certain ratings ("viewer discretion is advised" before 18+ shows).</summary>
public sealed class RatingPromoFolderTests
{
    private const string Root = @"D:\Promos\Now Playing";

    private static readonly DateTimeOffset Noon = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly AdPromoRatingFolder Adults = new() { Folder = "Rated 18+", Ratings = ["TV-MA", "R"] };

    private static MediaItem Clip(string folder, string name) =>
        new() { Id = name, Title = name, Kind = MediaKind.Filler, Path = $@"{Root}\{folder}\{name}.mp4", Duration = TimeSpan.FromSeconds(10) };

    private static async Task<MediaItem?> PickAsync(string? rating, params MediaItem[] clips)
    {
        var session = new ScheduleBuildSession(new SchedulerConfig(), new FakeMediaLibrary([.. clips]), new InMemoryPlaybackStateStore(),
            new ChannelConfig { Id = "test", Name = "Test" }, TimeZoneInfo.Utc, Noon);
        var spot = new PromoSpotConfig
        {
            Enabled = true,
            Folders = [new(Root, [], PerShow: true, AnyShow: true, Times: false, Days: false, AnyShowFolder: "Any Show", RatingFolders: [Adults])]
        };
        return await new PromoSelector(session, new PromoConfig { FromAdBreaks = true }, null)
            .SelectSpotAsync(spot, ["Rick and Morty"], Noon, TimeSpan.FromMinutes(1), default, rating: rating);
    }

    [Fact]
    public async Task A_programme_with_a_matching_rating_gets_the_rating_folder_s_promo_ahead_of_any_show_s()
    {
        MediaItem? picked = await PickAsync("TV-MA", Clip("Any Show", "generic"), Clip("Rated 18+", "discretion"));
        Assert.Equal("discretion", picked?.Id);

        // Ratings are compared ignoring case, spaces and dashes.
        Assert.Equal("discretion", (await PickAsync("tv ma", Clip("Any Show", "generic"), Clip("Rated 18+", "discretion")))?.Id);
    }

    [Fact]
    public async Task Other_ratings_and_unrated_programmes_never_get_it()
    {
        Assert.Equal("generic", (await PickAsync("TV-PG", Clip("Any Show", "generic"), Clip("Rated 18+", "discretion")))?.Id);
        Assert.Equal("generic", (await PickAsync(null, Clip("Any Show", "generic"), Clip("Rated 18+", "discretion")))?.Id);
        Assert.Null(await PickAsync("TV-PG", Clip("Rated 18+", "discretion")));
    }

    [Fact]
    public async Task A_promo_for_the_programme_itself_still_comes_first()
    {
        MediaItem? picked = await PickAsync("TV-MA", Clip("Rick and Morty", "own"), Clip("Rated 18+", "discretion"), Clip("Any Show", "generic"));
        Assert.Equal("own", picked?.Id);
    }

    [Fact]
    public void Ad_breaks_hand_their_complete_rating_folders_to_their_promo_folders()
    {
        var adBreak = new AdBreakConfig
        {
            Promos = [new AdBreakPromo { Type = AdPromoType.NowPlaying, Path = Root }],
            RatingFolders = [Adults, new AdPromoRatingFolder { Folder = "Unfinished" }]
        };

        PromoFolderSource folder = Assert.Single(AdBreakCompiler.Promos([adBreak], new TemporalTagOptions()).NowPlaying.Folders!);
        Assert.Same(Adults, folder.RatingFolder("rated 18+"));
        Assert.Null(folder.RatingFolder("Unfinished"));
        Assert.Null(folder.RatingFolder("Any Show"));
    }

    [Fact]
    public void Ratings_match_ignoring_case_spaces_and_dashes_and_the_suggestions_cover_the_usual_bands()
    {
        Assert.True(Adults.Matches("tv-ma"));
        Assert.True(Adults.Matches("TVMA"));
        Assert.False(Adults.Matches("TV-14"));
        Assert.False(Adults.Matches(null));
        Assert.False(Adults.Matches(""));

        IReadOnlyList<AdPromoRatingFolder> suggested = AdPromoRatingFolder.Suggestions;
        Assert.Contains(suggested, group => group.Matches("TV-14") && group.Folder == "Rated 14+");
        Assert.Contains(suggested, group => group.Matches("NC-17") && group.Folder == "Rated 18+");
        Assert.All(suggested, group => Assert.True(group.IsComplete));
    }

    [Fact]
    public void Rating_folders_round_trip_through_the_schedule_file()
    {
        var config = new SchedulerConfig();
        config.AdBreaks["Late night"] = new AdBreakConfig { RatingFolders = [new() { Folder = " Rated 18+ ", Ratings = ["TV-MA", " R ", ""] }] };

        SchedulerConfig loaded = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config));
        AdPromoRatingFolder rated = Assert.Single(loaded.AdBreaks["Late night"].RatingFolders);
        Assert.Equal("Rated 18+", rated.Folder);
        Assert.Equal(new[] { "TV-MA", "R" }, rated.Ratings);
    }
}
