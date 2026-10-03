namespace SchedularrTV.Core.Query;

public enum ComparisonOperator
{
    /// <summary>Field-defaulted match: exact for set fields (tag/collection/show), substring for text fields.</summary>
    Match,
    Equals,
    NotEquals,
    Contains,
    Matches,
    In,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual
}
