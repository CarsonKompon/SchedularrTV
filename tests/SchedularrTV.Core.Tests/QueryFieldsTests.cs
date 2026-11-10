using SchedularrTV.Core.Query;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>Library search boxes take words or a query; these are what tells them apart.</summary>
public sealed class QueryFieldsTests
{
    [Theory]
    [InlineData("genre:'Comedy' AND year < 1995")]
    [InlineData("kind == 'Movie' AND NOT collection:'Disney Channel Movies'")]
    [InlineData("network:'Teletoon'")]
    [InlineData("title CONTAINS 'christmas'")]
    [InlineData("season >= 2")]
    [InlineData("attr.brand:'Teletoon'")]
    public void Queries_are_read_as_queries(string text)
    {
        Assert.True(QueryFields.IsQuery(text, out QueryExpression expression, out string? error), error);
        Assert.NotEqual(QueryExpression.None, expression);
    }

    [Theory]
    [InlineData("Rugrats")]
    [InlineData("the simpsons")]
    [InlineData("")]
    [InlineData("Love in the Time of Cholera")]
    public void Words_stay_a_title_search(string text)
    {
        Assert.False(QueryFields.IsQuery(text, out _, out string? error));
        Assert.Null(error);
    }

    [Theory]
    // A title with a colon reads like a field, but "Trek" isn't one.
    [InlineData("Star Trek: The Next Generation")]
    [InlineData("Batman: The Animated Series")]
    // Half-typed.
    [InlineData("genre:")]
    public void Text_that_only_looks_like_a_query_is_searched_as_words_and_says_why(string text)
    {
        Assert.False(QueryFields.IsQuery(text, out _, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Every_field_in_the_cheatsheet_is_known()
    {
        Assert.All(QueryFields.All.SelectMany(field => field.Aliases.Prepend(field.Name)), name => Assert.True(QueryFields.IsKnown(name), name));
        Assert.All(QueryFields.All, field => Assert.True(QueryParser.TryParse(field.Example, out _, out string? error), $"{field.Example}: {error}"));
    }
}
