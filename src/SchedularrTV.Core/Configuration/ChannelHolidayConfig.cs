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
    WeekOf
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
