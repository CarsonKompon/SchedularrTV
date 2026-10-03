namespace SchedularrTV.Core.Temporal;

/// <summary>
/// Declarative holiday window. Either a fixed <see cref="Start"/>/<see cref="End"/> MM-DD range,
/// or an <see cref="NthWeekday"/> rule for floating holidays such as US Thanksgiving.
/// </summary>
public sealed class HolidayDefinition
{
    public required string Name { get; set; }

    /// <summary>Inclusive window start as <c>MM-DD</c>.</summary>
    public string? Start { get; set; }

    /// <summary>Inclusive window end as <c>MM-DD</c>. May wrap the year boundary (e.g. 12-26 to 01-01).</summary>
    public string? End { get; set; }

    public NthWeekdayRule? NthWeekday { get; set; }

    /// <summary>Days before the resolved anchor date that still count as the holiday.</summary>
    public int LeadDays { get; set; }

    /// <summary>Days after the resolved anchor date that still count as the holiday.</summary>
    public int TrailDays { get; set; }
}

/// <summary>Floating-date rule, e.g. "4th Thursday of November". Use <see cref="Ordinal"/> = -1 for "last".</summary>
public sealed class NthWeekdayRule
{
    public int Month { get; set; }

    public DayOfWeek DayOfWeek { get; set; }

    public int Ordinal { get; set; } = 1;
}
