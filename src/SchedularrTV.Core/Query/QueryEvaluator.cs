using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using SchedularrTV.Core.Media;

namespace SchedularrTV.Core.Query;

/// <summary>
/// Evaluates a <see cref="QueryExpression"/> against an in-memory <see cref="MediaItem"/>.
/// Kept separate from the provider so remote providers can push down what they can and
/// post-filter the remainder with the exact same semantics.
/// </summary>
public static class QueryEvaluator
{
    private const int MaxRegexCacheSize = 512;

    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new(StringComparer.Ordinal);

    private static readonly HashSet<string> SetFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "tag", "tags", "collection", "collections", "genre", "genres"
    };

    private static readonly HashSet<string> TextFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "name", "plot", "overview", "description"
    };

    public static IReadOnlyList<MediaItem> Filter(IEnumerable<MediaItem> items, QueryExpression expression) =>
        items.Where(Prepare(expression)).ToList();

    private static Func<MediaItem, bool> Prepare(QueryExpression expression)
    {
        switch (expression)
        {
            case AllExpression:
                return _ => true;
            case NoneExpression:
                return _ => false;
            case AndExpression and:
                var required = and.Operands.Select(Prepare).ToArray();
                return item =>
                {
                    foreach (var predicate in required) if (!predicate(item)) return false;
                    return true;
                };
            case OrExpression or:
                var alternatives = or.Operands.Select(Prepare).ToArray();
                return item =>
                {
                    foreach (var predicate in alternatives) if (predicate(item)) return true;
                    return false;
                };
            case NotExpression not:
                var excluded = Prepare(not.Operand);
                return item => !excluded(item);
            case FieldExpression field:
                string name = Normalize(field.Field);
                if (name is "season" or "episode" or "year" or "duration")
                    return item => MatchesField(item, field);
                bool equals = field.Operator is ComparisonOperator.Equals or ComparisonOperator.NotEquals or ComparisonOperator.In
                    || field.Operator == ComparisonOperator.Match && !TextFields.Contains(name);
                if (!equals) return item => MatchesField(item, field);
                var expected = new HashSet<string>(field.Values, StringComparer.OrdinalIgnoreCase);
                bool negate = field.Operator == ComparisonOperator.NotEquals;
                Func<MediaItem, string?>? scalar = name switch
                {
                    "id" => item => item.Id,
                    "title" or "name" => item => item.Title,
                    "plot" or "overview" or "description" => item => item.Overview,
                    "show" or "series" or "series_name" => item => item.SeriesName,
                    "kind" or "type" => item => item.Kind.ToString(),
                    "path" => item => item.Path,
                    "version" => item => item.VersionName,
                    _ => null
                };
                if (scalar is not null)
                    return item => (scalar(item) is { } value && expected.Contains(value)) != negate;
                return item =>
                {
                    foreach (string value in GetValues(item, name))
                        if (expected.Contains(value)) return !negate;
                    return negate;
                };
            default:
                return _ => false;
        }
    }

    public static bool Matches(MediaItem item, QueryExpression expression) => expression switch
    {
        AllExpression => true,
        NoneExpression => false,
        AndExpression and => and.Operands.All(o => Matches(item, o)),
        OrExpression or => or.Operands.Any(o => Matches(item, o)),
        NotExpression not => !Matches(item, not.Operand),
        FieldExpression field => MatchesField(item, field),
        _ => false
    };

    private static bool MatchesField(MediaItem item, FieldExpression expression)
    {
        string field = Normalize(expression.Field);

        if (TryGetNumeric(item, field, out double? numeric))
        {
            return MatchesNumeric(numeric, expression);
        }

        IReadOnlyList<string> candidates = GetValues(item, field);

        return expression.Operator switch
        {
            ComparisonOperator.Match when SetFields.Contains(field) => AnyEquals(candidates, expression.Values),
            ComparisonOperator.Match when TextFields.Contains(field) => AnyContains(candidates, expression.Value),
            ComparisonOperator.Match => AnyEquals(candidates, expression.Values),
            ComparisonOperator.Equals => AnyEquals(candidates, expression.Values),
            ComparisonOperator.NotEquals => !AnyEquals(candidates, expression.Values),
            ComparisonOperator.In => AnyEquals(candidates, expression.Values),
            ComparisonOperator.Contains => AnyContains(candidates, expression.Value),
            ComparisonOperator.Matches => AnyRegex(candidates, expression.Value),
            _ => false
        };
    }

    private static bool MatchesNumeric(double? actual, FieldExpression expression)
    {
        if (actual is null)
        {
            return expression.Operator == ComparisonOperator.NotEquals;
        }

        if (expression.Operator == ComparisonOperator.In)
        {
            return expression.Values.Any(v => TryParseNumber(v, out double n) && Math.Abs(n - actual.Value) < 0.0001);
        }

        if (!TryParseNumber(expression.Value, out double expected))
        {
            return false;
        }

        return expression.Operator switch
        {
            ComparisonOperator.Match or ComparisonOperator.Equals => Math.Abs(actual.Value - expected) < 0.0001,
            ComparisonOperator.NotEquals => Math.Abs(actual.Value - expected) >= 0.0001,
            ComparisonOperator.GreaterThan => actual.Value > expected,
            ComparisonOperator.GreaterThanOrEqual => actual.Value >= expected,
            ComparisonOperator.LessThan => actual.Value < expected,
            ComparisonOperator.LessThanOrEqual => actual.Value <= expected,
            _ => false
        };
    }

    private static bool TryGetNumeric(MediaItem item, string field, out double? value)
    {
        switch (field)
        {
            case "season":
                value = item.SeasonNumber;
                return true;
            case "episode":
                value = item.EpisodeNumber;
                return true;
            case "year":
                value = item.PremiereDate?.Year;
                return true;
            case "duration":
                value = item.Duration.TotalSeconds;
                return true;
            default:
                value = null;
                return false;
        }
    }

    private static IReadOnlyList<string> GetValues(MediaItem item, string field) => field switch
    {
        "id" => [item.Id],
        "title" or "name" => [item.Title],
        "plot" or "overview" or "description" => item.Overview is null ? [] : [item.Overview],
        "tag" or "tags" => item.Tags,
        "collection" or "collections" => item.Collections,
        "genre" or "genres" => item.Genres,
        "network" or "networks" or "studio" => item.Networks,
        "show" or "series" or "series_name" => item.SeriesName is null ? [] : [item.SeriesName],
        "show_id" or "series_id" => ResolveShowId(item),
        "kind" or "type" => [item.Kind.ToString()],
        "path" => item.Path is null ? [] : [item.Path],
        "version" => item.VersionName is null ? [] : [item.VersionName],

        // A rating:… tag (as an attribute) still answers for an item Jellyfin has no rating for.
        "rating" or "content_rating" or "official_rating" when item.Rating is { Length: > 0 } rating => [rating],
        _ => item.Attributes.TryGetValue(field, out string? attribute) ? [attribute] : []
    };

    // Promo targeting matches on show_id, but Jellyfin series ids are opaque GUIDs; allow either
    // the real id or an author-friendly alias supplied via attributes/series name.
    private static IReadOnlyList<string> ResolveShowId(MediaItem item)
    {
        List<string> values = [];
        if (item.SeriesId is not null)
        {
            values.Add(item.SeriesId);
        }

        if (item.Attributes.TryGetValue("show_id", out string? alias))
        {
            values.Add(alias);
        }

        if (item.SeriesName is not null)
        {
            values.Add(item.SeriesName);
        }

        return values;
    }

    private static string Normalize(string field)
    {
        const string attributePrefix = "attr.";
        return field.StartsWith(attributePrefix, StringComparison.OrdinalIgnoreCase)
            ? field[attributePrefix.Length..]
            : field;
    }

    private static bool AnyEquals(IReadOnlyList<string> candidates, IReadOnlyList<string> values)
    {
        for (int i = 0; i < candidates.Count; i++)
        {
            for (int j = 0; j < values.Count; j++)
            {
                if (string.Equals(candidates[i], values[j], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool AnyContains(IReadOnlyList<string> candidates, string value)
    {
        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].Contains(value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AnyRegex(IReadOnlyList<string> candidates, string pattern)
    {
        Regex regex = GetRegex(pattern);
        for (int i = 0; i < candidates.Count; i++)
        {
            try
            {
                if (regex.IsMatch(candidates[i]))
                {
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        return false;
    }

    private static Regex GetRegex(string pattern)
    {
        if (RegexCache.TryGetValue(pattern, out Regex? cached))
        {
            return cached;
        }

        Regex regex;
        try
        {
            // Config-supplied patterns are untrusted input; NonBacktracking removes catastrophic
            // backtracking entirely, and the timeout covers patterns it cannot compile.
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
        }
        catch (NotSupportedException)
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException)
        {
            regex = new Regex(Regex.Escape(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (RegexCache.Count < MaxRegexCacheSize)
        {
            RegexCache.TryAdd(pattern, regex);
        }

        return regex;
    }

    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
