using System.Text;

namespace SchedularrTV.Core.Query;

/// <summary>
/// Abstract syntax tree for the media query language. The tree (not the raw string) is the canonical
/// representation so a future visual editor can build/inspect queries structurally and
/// round-trip them back to text via <see cref="ToQueryString"/>.
/// </summary>
public abstract record QueryExpression
{
    public abstract string ToQueryString();

    public override string ToString() => ToQueryString();

    public static QueryExpression All { get; } = new AllExpression();

    public static QueryExpression None { get; } = new NoneExpression();

    public static QueryExpression And(IEnumerable<QueryExpression> operands) => Combine(operands, isAnd: true);

    public static QueryExpression Or(IEnumerable<QueryExpression> operands) => Combine(operands, isAnd: false);

    private static QueryExpression Combine(IEnumerable<QueryExpression> operands, bool isAnd)
    {
        // Flatten and drop identity elements so generated queries (temporal tags, base tags) stay readable.
        List<QueryExpression> flattened = [];
        foreach (QueryExpression operand in operands)
        {
            switch (operand)
            {
                case AllExpression when isAnd:
                case NoneExpression when !isAnd:
                    continue;
                case NoneExpression when isAnd:
                    return None;
                case AllExpression when !isAnd:
                    return All;
                case AndExpression a when isAnd:
                    flattened.AddRange(a.Operands);
                    break;
                case OrExpression o when !isAnd:
                    flattened.AddRange(o.Operands);
                    break;
                default:
                    flattened.Add(operand);
                    break;
            }
        }

        return flattened.Count switch
        {
            0 => isAnd ? All : None,
            1 => flattened[0],
            _ => isAnd ? new AndExpression(flattened) : new OrExpression(flattened)
        };
    }
}

public sealed record AllExpression : QueryExpression
{
    public override string ToQueryString() => "*";
}

public sealed record NoneExpression : QueryExpression
{
    public override string ToQueryString() => "NONE";
}

public sealed record AndExpression(IReadOnlyList<QueryExpression> Operands) : QueryExpression
{
    public override string ToQueryString() =>
        string.Join(" AND ", Operands.Select(Wrap));

    private static string Wrap(QueryExpression e) => e is OrExpression ? $"({e.ToQueryString()})" : e.ToQueryString();
}

public sealed record OrExpression(IReadOnlyList<QueryExpression> Operands) : QueryExpression
{
    public override string ToQueryString() =>
        string.Join(" OR ", Operands.Select(Wrap));

    private static string Wrap(QueryExpression e) => e is AndExpression ? $"({e.ToQueryString()})" : e.ToQueryString();
}

public sealed record NotExpression(QueryExpression Operand) : QueryExpression
{
    public override string ToQueryString() => $"NOT ({Operand.ToQueryString()})";
}

public sealed record FieldExpression(string Field, ComparisonOperator Operator, IReadOnlyList<string> Values) : QueryExpression
{
    public string Value => Values.Count > 0 ? Values[0] : string.Empty;

    public override string ToQueryString()
    {
        var sb = new StringBuilder(Field);
        switch (Operator)
        {
            case ComparisonOperator.Match:
                sb.Append(':').Append(Quote(Value));
                break;
            case ComparisonOperator.In:
                sb.Append(" IN [").Append(string.Join(", ", Values.Select(Quote))).Append(']');
                break;
            default:
                sb.Append(' ').Append(Symbol(Operator)).Append(' ').Append(Quote(Value));
                break;
        }

        return sb.ToString();
    }

    private static string Symbol(ComparisonOperator op) => op switch
    {
        ComparisonOperator.Equals => "==",
        ComparisonOperator.NotEquals => "!=",
        ComparisonOperator.Contains => "CONTAINS",
        ComparisonOperator.Matches => "MATCHES",
        ComparisonOperator.GreaterThan => ">",
        ComparisonOperator.GreaterThanOrEqual => ">=",
        ComparisonOperator.LessThan => "<",
        ComparisonOperator.LessThanOrEqual => "<=",
        _ => ":"
    };

    private static string Quote(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}
