using System.Globalization;
using System.Text.RegularExpressions;
using SchedularrTV.Core.Configuration;

namespace SchedularrTV.Core.Query;

/// <summary>Translates explicit collection membership into the query language.</summary>
public static partial class ContentIncludeCompiler
{
    public static QueryExpression Compile(IEnumerable<ContentInclude> includes) => Compile(includes, null);

    public static QueryExpression Compile(IEnumerable<ContentInclude> includes, IReadOnlyList<int>? excludeSeasons) =>
        QueryExpression.Or(includes.Where(i => !i.IsEmpty).Select(i => Compile(i, excludeSeasons)).ToList());

    public static QueryExpression Compile(ContentInclude include) => Compile(include, null);

    /// <summary>
    /// Excluded seasons apply only to titles that did not name their own, so listing season 0 on a
    /// title is the way to keep its specials while the rest of the collection drops them.
    /// </summary>
    public static QueryExpression Compile(ContentInclude include, IReadOnlyList<int>? excludeSeasons)
    {
        QueryExpression clause = CompileCore(include);

        if (excludeSeasons is { Count: > 0 }
            && include.Seasons.Count == 0
            && include.Episodes.Count == 0
            && clause is not NoneExpression)
        {
            clause = QueryExpression.And([clause, ExcludingSeasons(excludeSeasons)]);
        }

        return clause;
    }

    /// <summary>
    /// Excludes the given seasons. Excluding season 0 also drops episodes with no season at all:
    /// providers number a "Specials" folder 0, but a named extras folder ("Commentaries",
    /// "Tracey Ullman Shorts", "Season Unknown") gets no number, and those are the same thing to a
    /// viewer. Anything that is not an episode is untouched, so films keep playing.
    /// </summary>
    public static QueryExpression ExcludingSeasons(IReadOnlyList<int> seasons)
    {
        List<QueryExpression> clauses =
        [
            new NotExpression(new FieldExpression(
                "season",
                ComparisonOperator.In,
                [.. seasons.Select(s => s.ToString(CultureInfo.InvariantCulture))]))
        ];

        if (seasons.Contains(0))
        {
            clauses.Add(new NotExpression(QueryExpression.And(
            [
                new FieldExpression("kind", ComparisonOperator.Equals, ["Episode"]),
                new NotExpression(new FieldExpression("season", ComparisonOperator.GreaterThanOrEqual, ["0"]))
            ])));
        }

        return QueryExpression.And(clauses);
    }

    private static QueryExpression CompileCore(ContentInclude include)
    {
        if (!string.IsNullOrWhiteSpace(include.Query))
        {
            return QueryParser.Parse(include.Query);
        }

        if (!string.IsNullOrWhiteSpace(include.Collection))
        {
            return new FieldExpression("collection", ComparisonOperator.Match, [include.Collection]);
        }

        if (!string.IsNullOrWhiteSpace(include.Movie))
        {
            return QueryExpression.And(
            [
                new FieldExpression("kind", ComparisonOperator.Equals, ["Movie"]),
                new FieldExpression("title", ComparisonOperator.Equals, [include.Movie])
            ]);
        }

        if (string.IsNullOrWhiteSpace(include.Show) && string.IsNullOrWhiteSpace(include.ShowId))
        {
            return QueryExpression.None;
        }

        // The id wins when present: matching the name as well would re-admit the same-named series
        // the id was chosen to exclude.
        List<QueryExpression> clauses = string.IsNullOrWhiteSpace(include.ShowId)
            ? [new FieldExpression("show", ComparisonOperator.Match, [include.Show!])]
            : [new FieldExpression("show_id", ComparisonOperator.Equals, [include.ShowId])];

        // Specific episodes are the narrower choice, so they win over a whole-season selection.
        if (include.Episodes.Count > 0)
        {
            List<QueryExpression> episodes = [];
            foreach (string code in include.Episodes)
            {
                Match match = EpisodeCode().Match(code.Trim());
                if (!match.Success)
                {
                    continue;
                }

                episodes.Add(QueryExpression.And(
                [
                    new FieldExpression("season", ComparisonOperator.Equals, [match.Groups[1].Value.TrimStart('0') is { Length: > 0 } s ? s : "0"]),
                    new FieldExpression("episode", ComparisonOperator.Equals, [match.Groups[2].Value.TrimStart('0') is { Length: > 0 } e ? e : "0"])
                ]));
            }

            if (episodes.Count > 0)
            {
                clauses.Add(QueryExpression.Or(episodes));
            }
        }
        else if (include.Seasons.Count > 0)
        {
            clauses.Add(new FieldExpression(
                "season",
                ComparisonOperator.In,
                include.Seasons.Select(s => s.ToString(CultureInfo.InvariantCulture)).ToList()));
        }

        return QueryExpression.And(clauses);
    }

    [GeneratedRegex(@"^[Ss](\d{1,4})[Ee](\d{1,4})$")]
    private static partial Regex EpisodeCode();
}
