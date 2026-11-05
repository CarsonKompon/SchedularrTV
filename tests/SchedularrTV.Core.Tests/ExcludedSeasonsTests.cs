using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class ExcludedSeasonsTests
{
    private static MediaItem Episode(int season) => TestMedia.Episode("Rugrats", season, 1, TimeSpan.FromMinutes(22), "Kids");

    [Fact]
    public void Excluded_seasons_are_left_out_and_every_other_season_plays_including_new_ones()
    {
        var include = new ContentInclude { Show = "Rugrats", ExcludeSeasons = [2, 0] };
        QueryExpression query = ContentIncludeCompiler.Compile(include);

        Assert.True(QueryEvaluator.Matches(Episode(1), query));
        Assert.False(QueryEvaluator.Matches(Episode(2), query));
        Assert.False(QueryEvaluator.Matches(Episode(0), query));
        // A season the library gains later is in without ticking it.
        Assert.True(QueryEvaluator.Matches(Episode(9), query));
        Assert.False(include.IsWholeShow);
        Assert.Equal("Rugrats (all but season 2, 0)", include.Describe());
    }

    [Fact]
    public void Picked_seasons_win_over_excluded_ones()
    {
        var include = new ContentInclude { Show = "Rugrats", Seasons = [3], ExcludeSeasons = [2] };
        QueryExpression query = ContentIncludeCompiler.Compile(include);

        Assert.True(QueryEvaluator.Matches(Episode(3), query));
        Assert.False(QueryEvaluator.Matches(Episode(1), query));
    }

    [Fact]
    public void Excluded_seasons_survive_the_schedule_file()
    {
        SchedulerConfig config = new();
        config.GlobalCollections["Kids"] = new ContentSelector
        {
            Include = [new ContentInclude { Show = "Rugrats", ExcludeSeasons = [4, 2, 2] }, new ContentInclude { Show = "Doug", Seasons = [1], ExcludeSeasons = [2] }]
        };

        SchedulerConfig read = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config));
        List<ContentInclude> includes = read.GlobalCollections["Kids"].Include;

        Assert.Equal(new[] { 2, 4 }, includes[0].ExcludeSeasons);
        // Both can't hold at once; the picked seasons are kept.
        Assert.Equal(new[] { 1 }, includes[1].Seasons);
        Assert.Empty(includes[1].ExcludeSeasons);
    }
}
