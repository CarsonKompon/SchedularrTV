using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class ExcludeSeasonsTests
{
    private static List<MediaItem> Library() =>
    [
        TestMedia.Episode("Adventure Time", 0, 1, TimeSpan.FromMinutes(4), title: "Behind the scenes"),
        TestMedia.Episode("Adventure Time", 1, 1, TimeSpan.FromMinutes(11)),
        TestMedia.Episode("Adventure Time", 9, 1, TimeSpan.FromMinutes(11)),
        TestMedia.Episode("Steven Universe", 0, 1, TimeSpan.FromMinutes(3), title: "Extras"),
        TestMedia.Episode("Steven Universe", 1, 1, TimeSpan.FromMinutes(11)),
        Unnumbered("Adventure Time", "Tracey Ullman style short")
    ];

    /// <summary>An episode in a named folder the provider could not number.</summary>
    private static MediaItem Unnumbered(string series, string title) => new()
    {
        Id = $"{series}-unnumbered-{title}",
        Title = title,
        Kind = MediaKind.Episode,
        Duration = TimeSpan.FromMinutes(3),
        SeriesId = series,
        SeriesName = series
    };

    private static IReadOnlyList<MediaItem> Resolve(ContentSelector selector)
    {
        QueryExpression expression = selector.Include.Count > 0
            ? ContentIncludeCompiler.Compile(selector.Include, selector.ExcludeSeasons)
            : QueryExpression.And(
            [
                QueryParser.Parse(selector.Query!),
                ContentIncludeCompiler.ExcludingSeasons(selector.ExcludeSeasons)
            ]);

        return QueryEvaluator.Filter(Library(), expression);
    }

    [Fact]
    public void Excluding_season_zero_drops_specials_from_every_title()
    {
        ContentSelector selector = new()
        {
            ExcludeSeasons = [0],
            Include =
            [
                new ContentInclude { Show = "Adventure Time" },
                new ContentInclude { Show = "Steven Universe" }
            ]
        };

        Assert.All(Resolve(selector), i => Assert.NotEqual(0, i.SeasonNumber));
    }

    [Fact]
    public void A_season_added_later_is_still_included()
    {
        ContentSelector selector = new()
        {
            ExcludeSeasons = [0],
            Include = [new ContentInclude { Show = "Adventure Time" }]
        };

        // Season 9 was never listed anywhere; excluding 0 must not turn this into a whitelist.
        Assert.Contains(Resolve(selector), i => i.SeasonNumber == 9);
    }

    [Fact]
    public void A_title_that_names_season_zero_keeps_it()
    {
        ContentSelector selector = new()
        {
            ExcludeSeasons = [0],
            Include =
            [
                new ContentInclude { Show = "Adventure Time", Seasons = [0, 1] },
                new ContentInclude { Show = "Steven Universe" }
            ]
        };

        IReadOnlyList<MediaItem> resolved = Resolve(selector);

        Assert.Contains(resolved, i => i.SeriesName == "Adventure Time" && i.SeasonNumber == 0);
        Assert.DoesNotContain(resolved, i => i.SeriesName == "Steven Universe" && i.SeasonNumber == 0);
    }

    [Fact]
    public void Without_the_setting_nothing_changes()
    {
        ContentSelector selector = new()
        {
            Include = [new ContentInclude { Show = "Adventure Time" }]
        };

        Assert.Contains(Resolve(selector), i => i.SeasonNumber == 0);
    }

    [Fact]
    public void A_query_based_collection_can_exclude_seasons_too()
    {
        ContentSelector selector = new()
        {
            Query = "show:'Adventure Time'",
            ExcludeSeasons = [0]
        };

        IReadOnlyList<MediaItem> resolved = Resolve(selector);

        Assert.NotEmpty(resolved);
        Assert.All(resolved, i => Assert.NotEqual(0, i.SeasonNumber));
    }

    [Fact]
    public void An_item_with_no_season_is_not_treated_as_season_zero()
    {
        MediaItem movie = new()
        {
            Id = "film-1",
            Title = "A Film",
            Kind = MediaKind.Movie,
            Duration = TimeSpan.FromMinutes(90)
        };

        IReadOnlyList<MediaItem> resolved = QueryEvaluator.Filter(
            [movie],
            ContentIncludeCompiler.ExcludingSeasons([0]));

        Assert.Single(resolved);
    }

    [Fact]
    public void Episodes_in_an_unnumbered_extras_folder_are_dropped_with_the_specials()
    {
        ContentSelector selector = new()
        {
            ExcludeSeasons = [0],
            Include = [new ContentInclude { Show = "Adventure Time" }]
        };

        // "Tracey Ullman Shorts" and friends have no season number at all, so excluding 0 alone
        // would leave every extra in the rotation.
        Assert.DoesNotContain(Resolve(selector), i => i.SeasonNumber is null);
    }

    [Fact]
    public void Unnumbered_episodes_survive_when_specials_are_not_excluded()
    {
        ContentSelector selector = new()
        {
            ExcludeSeasons = [9],
            Include = [new ContentInclude { Show = "Adventure Time" }]
        };

        Assert.Contains(Resolve(selector), i => i.SeasonNumber is null);
    }
}
