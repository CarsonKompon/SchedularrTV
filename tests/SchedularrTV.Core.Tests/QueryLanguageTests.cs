using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class QueryLanguageTests
{
    [Fact]
    public void Parses_the_spec_commercial_query()
    {
        const string text = """
            tag:"Commercials" AND tag:"Adult Swim" AND (tag:"Any Time" OR tag:"January" OR tag:"Winter" OR tag:"01-10")
            """;

        QueryExpression expression = QueryParser.Parse(text);

        var and = Assert.IsType<AndExpression>(expression);
        Assert.Equal(3, and.Operands.Count);
        Assert.IsType<OrExpression>(and.Operands[2]);
    }

    [Fact]
    public void Round_trips_through_query_string()
    {
        QueryExpression original = QueryParser.Parse("show:'SpongeBob SquarePants' AND (season > 2 OR plot CONTAINS 'xmas')");
        QueryExpression reparsed = QueryParser.Parse(original.ToQueryString());

        Assert.Equal(original.ToQueryString(), reparsed.ToQueryString());
    }

    [Fact]
    public void Tag_match_is_exact_but_title_match_is_substring()
    {
        MediaItem item = TestMedia.Episode("SpongeBob SquarePants", 1, 1, TimeSpan.FromMinutes(11), title: "Christmas Who?");

        Assert.True(QueryEvaluator.Matches(item, QueryParser.Parse("show:'SpongeBob SquarePants'")));
        Assert.False(QueryEvaluator.Matches(item, QueryParser.Parse("show:'SpongeBob'")));
        Assert.True(QueryEvaluator.Matches(item, QueryParser.Parse("title:'christmas'")));
    }

    private static MediaItem WithNetworks(string series, params string[] networks) => new()
    {
        Id = series,
        Title = $"{series} pilot",
        Kind = MediaKind.Episode,
        Duration = TimeSpan.FromMinutes(22),
        SeriesName = series,
        Networks = networks
    };

    [Fact]
    public void Network_is_an_exact_set_match_across_every_network_a_show_aired_on()
    {
        MediaItem item = WithNetworks("6teen", "Teletoon", "Nickelodeon", "Cartoon Network");

        Assert.True(QueryEvaluator.Matches(item, QueryParser.Parse("network:'Teletoon'")));
        Assert.True(QueryEvaluator.Matches(item, QueryParser.Parse("network:'Cartoon Network'")));

        // Exact, like tag and collection: a partial name must not match.
        Assert.False(QueryEvaluator.Matches(item, QueryParser.Parse("network:'Cartoon'")));
        Assert.False(QueryEvaluator.Matches(item, QueryParser.Parse("network:'YTV'")));

        Assert.True(QueryEvaluator.Matches(item, QueryParser.Parse("networks:'Teletoon'")));
        Assert.True(QueryEvaluator.Matches(item, QueryParser.Parse("studio:'Teletoon'")));
    }

    [Fact]
    public void A_show_with_no_network_matches_nothing()
    {
        MediaItem item = WithNetworks("Akfamilyhome");

        Assert.False(QueryEvaluator.Matches(item, QueryParser.Parse("network:'Teletoon'")));
        Assert.True(QueryEvaluator.Matches(item, QueryParser.Parse("NOT network:'Teletoon'")));
    }

    [Fact]
    public void Supports_contains_matches_and_in_operators()
    {
        MediaItem promo = TestMedia.Promo("p1", TimeSpan.FromSeconds(15), "SpongeBob", "coming_up_at_4pm");

        Assert.True(QueryEvaluator.Matches(promo, QueryParser.Parse("show_id == 'SpongeBob' AND promo_type IN ['later_today', 'coming_up_at_4pm']")));
        Assert.True(QueryEvaluator.Matches(promo, QueryParser.Parse(@"promo_type MATCHES 'coming_up_at_\d+pm'")));
        Assert.False(QueryEvaluator.Matches(promo, QueryParser.Parse("promo_type IN ['up_next']")));
    }

    [Fact]
    public void Numeric_fields_compare_numerically()
    {
        MediaItem item = TestMedia.Episode("Show", 3, 12, TimeSpan.FromMinutes(22));

        Assert.True(QueryEvaluator.Matches(item, QueryParser.Parse("season >= 3 AND episode < 20")));
        Assert.False(QueryEvaluator.Matches(item, QueryParser.Parse("season > 3")));
        Assert.True(QueryEvaluator.Matches(item, QueryParser.Parse("duration <= 1320")));
    }

    [Fact]
    public void Reports_syntax_errors_instead_of_throwing()
    {
        Assert.False(QueryParser.TryParse("tag:", out _, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void A_catastrophic_regex_does_not_hang_evaluation()
    {
        MediaItem item = TestMedia.Episode("Show", 1, 1, TimeSpan.FromMinutes(22), title: new string('a', 40) + "!");

        // Classic exponential-backtracking pattern; NonBacktracking/timeouts must contain it.
        QueryExpression expression = QueryParser.Parse("title MATCHES '(a+)+$'");

        Assert.False(QueryEvaluator.Matches(item, expression));
    }
}
