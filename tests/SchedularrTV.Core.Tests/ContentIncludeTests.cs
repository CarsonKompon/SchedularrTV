using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class ContentIncludeTests
{
    private static MediaItem Episode(string series, int season, int episode) =>
        TestMedia.Episode(series, season, episode, TimeSpan.FromMinutes(22));

    private static MediaItem Movie(string title) => new()
    {
        Id = title,
        Title = title,
        Kind = MediaKind.Movie,
        Duration = TimeSpan.FromMinutes(95)
    };

    private static MediaItem FromSeries(string seriesId, string seriesName, int season, int episode) => new()
    {
        Id = $"{seriesId}-{season}-{episode}",
        Title = $"{seriesName} {season}x{episode}",
        Kind = MediaKind.Episode,
        Duration = TimeSpan.FromMinutes(22),
        SeriesId = seriesId,
        SeriesName = seriesName,
        SeasonNumber = season,
        EpisodeNumber = episode
    };

    [Fact]
    public void A_show_id_separates_two_series_that_share_a_name()
    {
        MediaItem original = FromSeries("0cce011a", "The Tom Green Show", 1, 1);
        MediaItem rogers = FromSeries("6aa2741f", "The Tom Green Show", 1, 1);

        QueryExpression expression = ContentIncludeCompiler.Compile(
            new ContentInclude { Show = "The Tom Green Show", ShowId = "0cce011a" });

        Assert.True(QueryEvaluator.Matches(original, expression));
        Assert.False(QueryEvaluator.Matches(rogers, expression));
    }

    [Fact]
    public void Without_a_show_id_a_shared_name_still_matches_both()
    {
        MediaItem original = FromSeries("0cce011a", "The Tom Green Show", 1, 1);
        MediaItem rogers = FromSeries("6aa2741f", "The Tom Green Show", 1, 1);

        // Entries written before ids were stored keep their original meaning.
        QueryExpression expression = ContentIncludeCompiler.Compile(
            new ContentInclude { Show = "The Tom Green Show" });

        Assert.True(QueryEvaluator.Matches(original, expression));
        Assert.True(QueryEvaluator.Matches(rogers, expression));
    }

    [Fact]
    public void Seasons_still_narrow_an_id_pinned_show()
    {
        QueryExpression expression = ContentIncludeCompiler.Compile(
            new ContentInclude { Show = "The Tom Green Show", ShowId = "0cce011a", Seasons = [2] });

        Assert.True(QueryEvaluator.Matches(FromSeries("0cce011a", "The Tom Green Show", 2, 1), expression));
        Assert.False(QueryEvaluator.Matches(FromSeries("0cce011a", "The Tom Green Show", 1, 1), expression));
        Assert.False(QueryEvaluator.Matches(FromSeries("6aa2741f", "The Tom Green Show", 2, 1), expression));
    }

    [Fact]
    public void An_entry_with_only_a_show_id_is_not_empty()
    {
        Assert.False(new ContentInclude { ShowId = "0cce011a" }.IsEmpty);
        Assert.True(new ContentInclude().IsEmpty);
    }

    [Fact]
    public void A_show_include_matches_every_season()
    {
        QueryExpression expression = ContentIncludeCompiler.Compile(new ContentInclude { Show = "Rugrats" });

        Assert.True(QueryEvaluator.Matches(Episode("Rugrats", 1, 1), expression));
        Assert.True(QueryEvaluator.Matches(Episode("Rugrats", 5, 9), expression));
        Assert.False(QueryEvaluator.Matches(Episode("Doug", 1, 1), expression));
    }

    [Fact]
    public void Seasons_narrow_a_show_include()
    {
        QueryExpression expression = ContentIncludeCompiler.Compile(
            new ContentInclude { Show = "Rugrats", Seasons = [2, 3] });

        Assert.True(QueryEvaluator.Matches(Episode("Rugrats", 2, 4), expression));
        Assert.True(QueryEvaluator.Matches(Episode("Rugrats", 3, 1), expression));
        Assert.False(QueryEvaluator.Matches(Episode("Rugrats", 1, 1), expression));
    }

    [Fact]
    public void Individual_episodes_win_over_seasons()
    {
        QueryExpression expression = ContentIncludeCompiler.Compile(
            new ContentInclude { Show = "Rugrats", Seasons = [1], Episodes = ["S02E04", "S03E11"] });

        Assert.True(QueryEvaluator.Matches(Episode("Rugrats", 2, 4), expression));
        Assert.True(QueryEvaluator.Matches(Episode("Rugrats", 3, 11), expression));
        Assert.False(QueryEvaluator.Matches(Episode("Rugrats", 1, 1), expression));
        Assert.False(QueryEvaluator.Matches(Episode("Rugrats", 2, 5), expression));
    }

    [Fact]
    public void A_movie_include_does_not_match_an_episode_of_the_same_name()
    {
        QueryExpression expression = ContentIncludeCompiler.Compile(new ContentInclude { Movie = "Space Jam" });

        Assert.True(QueryEvaluator.Matches(Movie("Space Jam"), expression));
        Assert.False(QueryEvaluator.Matches(TestMedia.Episode("Space Jam", 1, 1, TimeSpan.FromMinutes(22)), expression));
    }

    [Fact]
    public void Several_includes_union()
    {
        QueryExpression expression = ContentIncludeCompiler.Compile(
        [
            new ContentInclude { Show = "Rugrats" },
            new ContentInclude { Movie = "Space Jam" }
        ]);

        Assert.True(QueryEvaluator.Matches(Episode("Rugrats", 1, 1), expression));
        Assert.True(QueryEvaluator.Matches(Movie("Space Jam"), expression));
        Assert.False(QueryEvaluator.Matches(Episode("Doug", 1, 1), expression));
    }

    [Fact]
    public void An_empty_include_list_matches_nothing()
    {
        Assert.IsType<NoneExpression>(ContentIncludeCompiler.Compile([]));
        Assert.IsType<NoneExpression>(ContentIncludeCompiler.Compile([new ContentInclude()]));
    }

    [Theory]
    [InlineData("Rugrats", null, "Rugrats")]
    [InlineData(null, "Space Jam", "Space Jam")]
    public void Describe_is_readable(string? show, string? movie, string expected) =>
        Assert.Equal(expected, new ContentInclude { Show = show, Movie = movie }.Describe());

    [Fact]
    public void Durations_round_trip_through_the_editor_format()
    {
        foreach (TimeSpan value in (TimeSpan[])
        [
            TimeSpan.FromMinutes(30), TimeSpan.FromHours(12), TimeSpan.FromSeconds(45),
            TimeSpan.FromMilliseconds(250), new TimeSpan(1, 7, 23), TimeSpan.FromDays(7)
        ])
        {
            string text = DurationText.Format(value);
            Assert.True(DurationText.TryParse(text, out TimeSpan parsed), $"could not re-parse '{text}'");
            Assert.Equal(value, parsed);
        }
    }
}
