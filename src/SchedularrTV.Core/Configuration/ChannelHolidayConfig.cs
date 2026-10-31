using SchedularrTV.Core.Ordering;

namespace SchedularrTV.Core.Configuration;

/// <summary>How a channel holiday's days are given.</summary>
public enum HolidayDates
{
    /// <summary>The same days every year, <see cref="ChannelHoliday.Start"/> to <see cref="ChannelHoliday.End"/> (May 1–5).</summary>
    Range,

    /// <summary>
    /// The Sunday-to-Saturday week holding <see cref="ChannelHoliday.Date"/> that year ("the week of December 25th"),
    /// so it's always a whole week, whatever day the date falls on.
    /// </summary>
    WeekOf,

    /// <summary>
    /// A weekday of a month that moves each year, such as the 4th Thursday of November (Thanksgiving):
    /// <see cref="ChannelHoliday.Month"/>, <see cref="ChannelHoliday.Weekday"/> and <see cref="ChannelHoliday.Ordinal"/>.
    /// </summary>
    NthWeekday,

    /// <summary>
    /// A day counted from Easter Sunday (<see cref="ChannelHoliday.EasterOffset"/>: −2 is Good Friday), the Western or
    /// the Orthodox one (<see cref="ChannelHoliday.Orthodox"/>).
    /// </summary>
    Easter
}

/// <summary>Which days around a moving holiday's day (<see cref="HolidayDates.NthWeekday"/>, <see cref="HolidayDates.Easter"/>) it covers.</summary>
public enum HolidaySpan
{
    /// <summary><see cref="ChannelHoliday.Length"/> days, starting on the day.</summary>
    Days,

    /// <summary>The Sunday-to-Saturday week holding the day.</summary>
    Week,

    /// <summary>
    /// The Friday-to-Monday long weekend holding the day: the weekend nearest it, stretched to take in the day when it
    /// falls mid-week (a Thursday runs Thursday to Monday).
    /// </summary>
    LongWeekend
}

/// <summary>
/// A holiday on a channel (<c>holidays:</c>): days of the year when whatever would play (the block's shows, or the
/// channel's base rotation) is narrowed to the episodes a query picks out, such as <c>plot:'christmas'</c>. The shows
/// stay the ones the block plays; only the episodes change. With too few shows found, the rest of the time is filled
/// from a fallback collection, or with the usual episodes.
/// </summary>
public sealed class ChannelHoliday
{
    public string Name { get; set; } = "Holiday";

    public bool Enabled { get; set; } = true;

    public HolidayDates Dates { get; set; } = HolidayDates.Range;

    /// <summary>For <see cref="HolidayDates.Range"/>: the first day, <c>MM-DD</c>.</summary>
    public string? Start { get; set; }

    /// <summary>For <see cref="HolidayDates.Range"/>: the last day, <c>MM-DD</c>; before <see cref="Start"/>, it runs into the new year.</summary>
    public string? End { get; set; }

    /// <summary>For <see cref="HolidayDates.WeekOf"/>: the day whose week it is, <c>MM-DD</c>.</summary>
    public string? Date { get; set; }

    /// <summary>For <see cref="HolidayDates.NthWeekday"/>: the month, 1 to 12.</summary>
    public int? Month { get; set; }

    /// <summary>For <see cref="HolidayDates.NthWeekday"/>: the day of the week.</summary>
    public DayOfWeek? Weekday { get; set; }

    /// <summary>For <see cref="HolidayDates.NthWeekday"/>: which one in the month, 1 to 4, or −1 for the last. Null is the 1st.</summary>
    public int? Ordinal { get; set; }

    /// <summary>For <see cref="HolidayDates.Easter"/>: days from Easter Sunday (−2 Good Friday, −47 Mardi Gras, 49 Pentecost). Null is 0.</summary>
    public int? EasterOffset { get; set; }

    /// <summary>For <see cref="HolidayDates.Easter"/>: the Orthodox Easter rather than the Western one. Null is no.</summary>
    public bool? Orthodox { get; set; }

    /// <summary>
    /// For <see cref="HolidayDates.NthWeekday"/> and <see cref="HolidayDates.Easter"/>: which days around the day it
    /// covers. Null is <see cref="HolidaySpan.Days"/>.
    /// </summary>
    public HolidaySpan? Span { get; set; }

    /// <summary>
    /// For <see cref="HolidaySpan.Days"/>: how many days it lasts from the day (Thanksgiving to Sunday is 4). Null is 1.
    /// </summary>
    public int? Length { get; set; }

    /// <summary>Older spelling of <c>span: week</c>; read from the schedule file, then turned into <see cref="Span"/>.</summary>
    public bool? WholeWeek { get; set; }

    /// <summary>Whole weeks added before the days.</summary>
    public int WeeksBefore { get; set; }

    /// <summary>Whole weeks added after the days.</summary>
    public int WeeksAfter { get; set; }

    /// <summary>Which episodes are holiday ones (the library query language), e.g. <c>plot:'christmas' OR title:'xmas'</c>.</summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>Looks for matching episodes in the whole library rather than only in what the block (or base rotation) plays.</summary>
    public bool Global { get; set; }

    /// <summary>
    /// A collection (its key) that fills in when fewer than <see cref="MinimumShows"/> shows have holiday episodes.
    /// Null fills in with the usual episodes of what the block plays.
    /// </summary>
    public string? Fallback { get; set; }

    /// <summary>
    /// How many different shows the holiday needs. With fewer, each found show still gets its share of the time (one
    /// show of a minimum of three: a third), and the rest is filled in from <see cref="Fallback"/>.
    /// </summary>
    public int MinimumShows { get; set; } = 1;

    /// <summary>The order the holiday episodes play in; null shuffles them.</summary>
    public PlaybackOrder? Order { get; set; }

    /// <summary>A separate copy, for copying holidays between channels and the global list.</summary>
    public ChannelHoliday Clone() => (ChannelHoliday)MemberwiseClone();
}
