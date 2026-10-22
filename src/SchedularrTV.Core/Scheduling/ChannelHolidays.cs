using System.Globalization;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Query;

namespace SchedularrTV.Core.Scheduling;

/// <summary>When a channel's holidays fall (<see cref="ChannelHoliday"/>), and which one a moment is in.</summary>
public static class ChannelHolidays
{
    /// <summary>
    /// The holiday's days in <paramref name="year"/> (the occurrence that starts that year, weeks before and after
    /// included), or null when its dates aren't set or can't be read.
    /// </summary>
    public static (DateOnly Start, DateOnly End)? In(ChannelHoliday holiday, int year)
    {
        DateOnly start, end;
        if (holiday.Dates == HolidayDates.WeekOf)
        {
            if (!TryDay(holiday.Date, year, out DateOnly day)) return null;
            // Sunday to Saturday.
            start = day.AddDays(-(int)day.DayOfWeek);
            end = start.AddDays(6);
        }
        else
        {
            if (!TryDay(holiday.Start, year, out start) || !TryDay(holiday.End, year, out end)) return null;
            // An end before the start runs into the new year (December 26th to January 1st).
            if (end < start && !TryDay(holiday.End, year + 1, out end)) return null;
        }

        return (start.AddDays(-7 * Math.Max(0, holiday.WeeksBefore)), end.AddDays(7 * Math.Max(0, holiday.WeeksAfter)));
    }

    /// <summary>Whether <paramref name="day"/> is one of the holiday's days (this year's, or a neighbouring year's that reaches it).</summary>
    public static bool Contains(ChannelHoliday holiday, DateOnly day)
    {
        for (int year = day.Year - 1; year <= day.Year + 1; year++)
        {
            if (In(holiday, year) is { } days && day >= days.Start && day <= days.End)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The first of the channel's holidays (on, with a usable query) whose days hold <paramref name="at"/> in the channel's time.</summary>
    public static ChannelHoliday? At(ChannelConfig channel, DateTimeOffset at, TimeZoneInfo timeZone)
    {
        if (channel.Holidays is not { Count: > 0 } holidays)
        {
            return null;
        }

        DateOnly day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, timeZone).DateTime);
        return holidays.FirstOrDefault(holiday => holiday.Enabled && !string.IsNullOrWhiteSpace(holiday.Query) && Contains(holiday, day));
    }

    /// <summary>The holiday's days this year, in words, for the editor: "Sun 21 Dec – Sat 27 Dec 2026".</summary>
    public static string? Describe(ChannelHoliday holiday, int year, CultureInfo? culture = null) =>
        In(holiday, year) is { } days
            ? $"{days.Start.ToString("ddd d MMM", culture ?? CultureInfo.InvariantCulture)} – {days.End.ToString("ddd d MMM yyyy", culture ?? CultureInfo.InvariantCulture)}"
            : null;

    /// <summary>Why the holiday can't be used as written, or null.</summary>
    public static string? Problem(ChannelHoliday holiday, SchedulerConfig config)
    {
        if (holiday.Dates == HolidayDates.WeekOf ? !TryDay(holiday.Date, 2000, out _) : !TryDay(holiday.Start, 2000, out _) || !TryDay(holiday.End, 2000, out _))
        {
            return holiday.Dates == HolidayDates.WeekOf
                ? "needs the day whose week it is, as MM-DD (12-25)."
                : "needs a first and last day, as MM-DD (05-01 to 05-05).";
        }

        if (string.IsNullOrWhiteSpace(holiday.Query))
        {
            return "needs a query picking out its episodes, such as plot:'christmas'.";
        }

        if (!QueryParser.TryParse(holiday.Query, out _, out string? error))
        {
            return $"query can't be read: {error}";
        }

        if (!string.IsNullOrWhiteSpace(holiday.Fallback) && !config.GlobalCollections.ContainsKey(holiday.Fallback.Trim()))
        {
            return $"fallback collection '{holiday.Fallback}' doesn't exist.";
        }

        if (holiday.WeeksBefore is < 0 or > 52 || holiday.WeeksAfter is < 0 or > 52)
        {
            return "weeks before and after must be 0 to 52.";
        }

        return null;
    }

    /// <summary>
    /// <c>MM-DD</c> as that day in <paramref name="year"/>; February 29th is the 28th in a year without one.
    /// </summary>
    public static bool TryDay(string? monthDay, int year, out DateOnly day)
    {
        day = default;
        string[] parts = (monthDay ?? string.Empty).Trim().Split('-', '/');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int month)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int date)
            || month is < 1 or > 12 || date < 1 || date > DateTime.DaysInMonth(2000, month))
        {
            return false;
        }

        day = new DateOnly(year, month, Math.Min(date, DateTime.DaysInMonth(year, month)));
        return true;
    }
}
