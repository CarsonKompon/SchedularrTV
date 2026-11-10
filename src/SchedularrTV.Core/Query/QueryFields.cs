using System.Text.RegularExpressions;

namespace SchedularrTV.Core.Query;

/// <summary>One field a query can test, for the cheatsheet: its name, other names it answers to, and an example.</summary>
public sealed record QueryField(string Name, IReadOnlyList<string> Aliases, string Description, string Example);

/// <summary>
/// The fields the query language knows (as <see cref="QueryEvaluator"/> reads them), and telling a typed search apart from a
/// query, for search boxes that take either.
/// </summary>
public static partial class QueryFields
{
    public static readonly IReadOnlyList<QueryField> All =
    [
        new("show", ["series", "series_name"], "The show's name (episodes only)", "show:'Rugrats'"),
        new("title", ["name"], "The episode's or movie's own title; with : it matches any part of it", "title:'christmas'"),
        new("kind", ["type"], "Episode, Movie or Filler (commercials, bumpers, promos)", "kind == 'Movie'"),
        new("season", [], "Season number", "season >= 2"),
        new("episode", [], "Episode number within its season", "episode <= 3"),
        new("year", [], "Year it first aired or came out", "year < 1990"),
        new("duration", [], "Length in seconds", "duration >= 1200"),
        new("genre", ["genres"], "Any of its genres (episodes use their show's)", "genre:'Comedy'"),
        new("network", ["networks", "studio"], "Any of its show's networks", "network:'Teletoon'"),
        new("collection", ["collections"], "Any library collection it's in (a show's count for its episodes)", "collection:'Halloween'"),
        new("tag", ["tags"], "Any of its tags", "tag:'Commercials'"),
        new("rating", ["content_rating", "official_rating"], "Content rating (episodes use their show's)", "rating:'TV-Y7'"),
        new("plot", ["overview", "description"], "Its description; with : it matches any part of it", "plot:'haunted'"),
        new("path", [], "Its file path", "path CONTAINS 'Specials'"),
        new("version", [], "Which version of a film or episode (when there are several)", "version:'Extended'"),
        new("show_id", ["series_id"], "The show's id, or its name or show_id alias", "show_id == 'Rugrats'"),
        new("id", [], "The item's own id", "id == 'a1b2c3'"),
        new("promo_type", [], "A promo's type (from a promo_type: tag)", "promo_type IN ['up_next', 'later_today']")
    ];

    // Tags a media source turns into fields of their own (Jellyfin's AttributeTagPrefixes, by default).
    private static readonly string[] AttributeFields = ["promo_type", "show_id", "channel", "brand", "trimmable", "stop_before_end"];

    private static readonly HashSet<string> Known = new(
        All.SelectMany(field => field.Aliases.Prepend(field.Name)).Concat(AttributeFields), StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="field"/> is one the language knows (any <c>attr.</c> field counts).</summary>
    public static bool IsKnown(string field) =>
        Known.Contains(field) || field.StartsWith("attr.", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every field <paramref name="expression"/> tests.</summary>
    public static IEnumerable<string> Used(QueryExpression expression) => expression switch
    {
        AndExpression and => and.Operands.SelectMany(Used),
        OrExpression or => or.Operands.SelectMany(Used),
        NotExpression not => Used(not.Operand),
        FieldExpression field => [field.Field],
        _ => []
    };

    /// <summary>
    /// Whether a search box's text is meant as a query rather than words to find: it has a comparison in it, reads as a
    /// query, and every field it names is a real one (so "Star Trek: The Next Generation" is still a title search).
    /// <paramref name="error"/> says why text that looks like a query doesn't read as one.
    /// </summary>
    public static bool IsQuery(string? text, out QueryExpression expression, out string? error)
    {
        expression = QueryExpression.None;
        error = null;
        if (string.IsNullOrWhiteSpace(text) || !LooksLikeQuery().IsMatch(text))
        {
            return false;
        }

        if (!QueryParser.TryParse(text, out QueryExpression parsed, out string? problem))
        {
            error = problem;
            return false;
        }

        if (Used(parsed).FirstOrDefault(field => !IsKnown(field)) is { } unknown)
        {
            error = $"'{unknown}' isn't a field the query language knows.";
            return false;
        }

        expression = parsed;
        return true;
    }

    // A field followed by a comparison, or a keyword operator: what a query has and a title search doesn't.
    [GeneratedRegex(@"[A-Za-z_][\w.]*\s*(:|==|!=|>=|<=|>|<)|\s(CONTAINS|MATCHES|IN)\s")]
    private static partial Regex LooksLikeQuery();
}
