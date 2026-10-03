namespace SchedularrTV.Core.Temporal;

public sealed class TemporalTagOptions
{
    /// <summary>Tag applied to filler that is valid regardless of date.</summary>
    public string AnyTimeTag { get; set; } = "Any Time";

    public bool IncludeMonth { get; set; } = true;

    public bool IncludeSeason { get; set; } = true;

    /// <summary>Emit an <c>MM-DD</c> tag.</summary>
    public bool IncludeExactDate { get; set; } = true;

    /// <summary>Emit a <c>yyyy-MM-DD</c> tag in addition to <c>MM-DD</c>.</summary>
    public bool IncludeFullDate { get; set; } = true;

    public bool IncludeHolidays { get; set; } = true;

    /// <summary>Overrides the defaults entirely when non-empty.</summary>
    public List<HolidayDefinition> Holidays { get; set; } = [];

    public static IReadOnlyList<HolidayDefinition> DefaultHolidays { get; } =
    [
        new() { Name = "Christmas", Start = "12-01", End = "12-25" },
        new() { Name = "New Year", Start = "12-26", End = "01-02" },
        new() { Name = "Halloween", Start = "10-01", End = "10-31" },
        new() { Name = "Thanksgiving", NthWeekday = new NthWeekdayRule { Month = 11, DayOfWeek = DayOfWeek.Thursday, Ordinal = 4 }, LeadDays = 13, TrailDays = 1 },
        new() { Name = "Valentines", Start = "02-07", End = "02-14" },
        new() { Name = "Independence Day", Start = "07-01", End = "07-04" }
    ];
}
