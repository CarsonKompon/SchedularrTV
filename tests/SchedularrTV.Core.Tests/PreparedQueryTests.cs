using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class PreparedQueryTests
{
    [Theory]
    [InlineData("show IN ['Example', 'Other'] AND NOT (season == 0)")]
    [InlineData("title:pilot OR tag:comedy")]
    [InlineData("show_id == 'alias'")]
    [InlineData("show != 'Example' AND path != 'absent'")]
    [InlineData("kind IN ['Episode', 'Movie'] AND duration > 30")]
    [InlineData("attr.custom == 'value'")]
    [InlineData("title MATCHES 'p.*t' OR overview CONTAINS 'story'")]
    [InlineData("collection IN ['Classics'] OR version == 'HD'")]
    public void Prepared_filter_matches_interpreter_including_nulls_aliases_and_sets(string query)
    {
        MediaItem[] items = [new()
        {
            Id = "first", Title = "Pilot", SeriesName = "Example", SeriesId = "series", Kind = MediaKind.Episode,
            SeasonNumber = 1, EpisodeNumber = 7, Duration = TimeSpan.FromMinutes(22), Tags = ["Comedy"],
            Collections = ["Classics"], Attributes = new Dictionary<string, string> { ["show_id"] = "alias", ["custom"] = "value" }
        }, new() { Id = "second", Title = "Other", Kind = MediaKind.Movie, VersionName = "HD", Overview = "A story" },
        new() { Id = "third", Title = "", SeriesName = "Example", SeasonNumber = 0 }];
        var expression = QueryParser.Parse(query);
        Assert.Equal(items.Where(item => QueryEvaluator.Matches(item, expression)), QueryEvaluator.Filter(items, expression));
    }
}