using SchedularrTV.Providers.Jellyfin;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class JellyfinProjectionTests
{
    [Theory]
    [InlineData(@"M:\TV\The Simpsons\Specials\clip.mp4")]
    [InlineData("/media/TV/The Simpsons/specials/clip.mp4")]
    [InlineData("Specials/clip.mp4")]
    [InlineData("/media/TV/Show//Specials///clip.mp4//")]
    [InlineData(@"M:\TV/Show\Specials/clip.mp4")]
    public void Explicit_specials_folder_overrides_incorrect_regular_season_metadata(string path)
    {
        var item = new JellyfinItem { Type = "Episode", Path = path, SeasonId = "season20", ParentIndexNumber = 20 };
        int? season = JellyfinMediaLibrary.ResolveSeasonNumber(item, new Dictionary<string, int> { ["season20"] = 20 });
        Assert.Equal(0, season);
        var episode = new SchedularrTV.Core.Media.MediaItem
        {
            Id = "clip", Title = "Wrong episode title", Kind = SchedularrTV.Core.Media.MediaKind.Episode,
            SeriesName = "The Simpsons", SeasonNumber = season
        };
        Assert.False(SchedularrTV.Core.Query.QueryEvaluator.Matches(episode,
            SchedularrTV.Core.Query.ContentIncludeCompiler.ExcludingSeasons([0])));
    }

    [Theory]
    [InlineData(@"M:\TV\Specials\Season 2\episode.mp4", "Episode", 2)]
    [InlineData(@"M:\TV\Show\Season 2\Specials.mp4", "Episode", 2)]
    [InlineData(@"M:\TV\Show\Specials\movie.mp4", "Movie", 2)]
    [InlineData(null, "Episode", 2)]
    [InlineData("Specials", "Episode", 2)]
    [InlineData("/Specials/", "Episode", 2)]
    [InlineData("", "Episode", 2)]
    public void Other_paths_and_media_keep_parent_season_precedence(string? path, string type, int expected)
    {
        var item = new JellyfinItem { Type = type, Path = path, SeasonId = "season2", ParentIndexNumber = 20 };
        Assert.Equal(expected, JellyfinMediaLibrary.ResolveSeasonNumber(item, new Dictionary<string, int> { ["season2"] = 2 }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10000)]
    [InlineData(int.MaxValue)]
    public void A_nonsense_production_year_yields_no_premiere_date(int? year) =>
        Assert.Null(JellyfinMediaLibrary.YearToDate(year));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1999, 1999)]
    [InlineData(9999, 9999)]
    public void A_valid_production_year_becomes_january_first(int year, int expected)
    {
        DateTimeOffset? date = JellyfinMediaLibrary.YearToDate(year);

        Assert.NotNull(date);
        Assert.Equal(expected, date.Value.Year);
        Assert.Equal(1, date.Value.Month);
        Assert.Equal(1, date.Value.Day);
        Assert.Equal(TimeSpan.Zero, date.Value.Offset);
    }
}
