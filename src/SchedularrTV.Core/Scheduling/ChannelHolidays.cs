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
        else if (holiday.Dates is HolidayDates.NthWeekday or HolidayDates.Easter)
        {
            if (Anchor(holiday, year) is not { } day) return null;
            switch (SpanOf(holiday))
            {
                case HolidaySpan.Week:
                    start = day.AddDays(-(int)day.DayOfWeek);
                    end = start.AddDays(6);
                    break;
                case HolidaySpan.LongWeekend:
                    (start, end) = LongWeekend(day);
                    break;
                default:
                    start = day;
                    end = day.AddDays(Math.Clamp(holiday.Length ?? 1, 1, 366) - 1);
                    break;
            }
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

    /// <summary>The first of the channel's own holidays (on, with a usable query) whose days hold <paramref name="at"/> in the channel's time.</summary>
    public static ChannelHoliday? At(ChannelConfig channel, DateTimeOffset at, TimeZoneInfo timeZone) =>
        First(channel.Holidays ?? [], at, timeZone);

    /// <summary>
    /// The holiday that applies at <paramref name="at"/>: the first of the channel's own, else the first of the global
    /// holidays (unless the channel leaves them off).
    /// </summary>
    public static ChannelHoliday? At(SchedulerConfig config, ChannelConfig channel, DateTimeOffset at, TimeZoneInfo timeZone) =>
        First(Effective(config, channel), at, timeZone);

    /// <summary>Every holiday that can apply to the channel, in the order they're tried: its own, then the global ones.</summary>
    public static IEnumerable<ChannelHoliday> Effective(SchedulerConfig config, ChannelConfig channel)
    {
        IEnumerable<ChannelHoliday> own = channel.Holidays ?? [];
        return channel.IgnoreGlobalHolidays == true ? own : own.Concat(config.GlobalHolidays ?? []);
    }

    private static ChannelHoliday? First(IEnumerable<ChannelHoliday> holidays, DateTimeOffset at, TimeZoneInfo timeZone)
    {
        DateOnly day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, timeZone).DateTime);
        return holidays.FirstOrDefault(holiday => holiday.Enabled && !string.IsNullOrWhiteSpace(holiday.Query) && Contains(holiday, day));
    }

    /// <summary>The holiday's days this year, in words, for the editor: "Sun 21 Dec – Sat 27 Dec 2026".</summary>
    public static string? Describe(ChannelHoliday holiday, int year, CultureInfo? culture = null) =>
        In(holiday, year) is { } days
            ? $"{days.Start.ToString("ddd d MMM", culture ?? CultureInfo.InvariantCulture)} – {days.End.ToString("ddd d MMM yyyy", culture ?? CultureInfo.InvariantCulture)}"
            : null;

    /// <summary>
    /// The day a moving holiday is counted from in <paramref name="year"/>: the nth weekday of its month, or Easter Sunday
    /// plus its offset. Null for other kinds, or when its rule isn't complete.
    /// </summary>
    public static DateOnly? Anchor(ChannelHoliday holiday, int year)
    {
        if (year is < 1 or > 9998) return null;

        if (holiday.Dates == HolidayDates.NthWeekday)
        {
            if (holiday.Month is not (>= 1 and <= 12) || holiday.Weekday is not { } weekday || !ValidOrdinal(holiday.Ordinal))
            {
                return null;
            }

            var rule = new Temporal.NthWeekdayRule { Month = holiday.Month.Value, DayOfWeek = weekday, Ordinal = holiday.Ordinal ?? 1 };
            return DateOnly.FromDateTime(Temporal.TemporalTagResolver.ResolveNthWeekday(year, rule));
        }

        if (holiday.Dates == HolidayDates.Easter)
        {
            return EasterSunday(year, holiday.Orthodox == true).AddDays(holiday.EasterOffset ?? 0);
        }

        return null;
    }

    /// <summary>
    /// Easter Sunday in <paramref name="year"/>, on the Gregorian calendar: the Western date (the anonymous Gregorian
    /// algorithm), or the Orthodox one (Meeus' Julian algorithm, moved onto the Gregorian calendar).
    /// </summary>
    public static DateOnly EasterSunday(int year, bool orthodox = false)
    {
        if (orthodox)
        {
            int a = year % 4, b = year % 7, c = year % 19;
            int d = (19 * c + 15) % 30;
            int e = (2 * a + 4 * b - d + 34) % 7;
            int month = (d + e + 114) / 31, day = (d + e + 114) % 31 + 1;
            // The Julian date is this many days behind the Gregorian one (13 from 1900 to 2099).
            int behind = year / 100 - year / 400 - 2;
            return new DateOnly(year, month, day).AddDays(behind);
        }

        int g = year % 19, century = year / 100, y = year % 100;
        int h = (19 * g + century - century / 4 - (century - (century + 8) / 25 + 1) / 3 + 15) % 30;
        int l = (32 + 2 * (century % 4) + 2 * (y / 4) - h - y % 4) % 7;
        int m = (g + 11 * h + 22 * l) / 451;
        int n = h + l - 7 * m + 114;
        return new DateOnly(year, n / 31, n % 31 + 1);
    }

    /// <summary>The span a moving holiday covers, taking the older <c>whole_week</c> into account.</summary>
    public static HolidaySpan SpanOf(ChannelHoliday holiday) =>
        holiday.Span ?? (holiday.WholeWeek == true ? HolidaySpan.Week : HolidaySpan.Days);

    /// <summary>
    /// The Friday-to-Monday long weekend holding <paramref name="day"/>: the weekend nearest it (a Wednesday takes the one
    /// before), stretched to take in the day when it falls mid-week.
    /// </summary>
    public static (DateOnly Start, DateOnly End) LongWeekend(DateOnly day)
    {
        int toSaturday = day.DayOfWeek switch
        {
            DayOfWeek.Sunday => -1,
            DayOfWeek.Monday => -2,
            DayOfWeek.Tuesday => -3,
            DayOfWeek.Wednesday => -4,
            DayOfWeek.Thursday => 2,
            DayOfWeek.Friday => 1,
            _ => 0
        };
        DateOnly saturday = day.AddDays(toSaturday);
        DateOnly friday = saturday.AddDays(-1), monday = saturday.AddDays(2);
        return (day < friday ? day : friday, day > monday ? day : monday);
    }

    private static bool ValidOrdinal(int? ordinal) => ordinal is null || ordinal == -1 || ordinal is >= 1 and <= 4;

    /// <summary>Why the holiday can't be used as written, or null.</summary>
    public static string? Problem(ChannelHoliday holiday, SchedulerConfig config)
    {
        string? dates = holiday.Dates switch
        {
            HolidayDates.WeekOf when !TryDay(holiday.Date, 2000, out _) => "needs the day whose week it is, as MM-DD (12-25).",
            HolidayDates.NthWeekday when holiday.Month is not (>= 1 and <= 12) || holiday.Weekday is null =>
                "needs a month and a day of the week (the 4th Thursday of November).",
            HolidayDates.NthWeekday when !ValidOrdinal(holiday.Ordinal) => "needs which one in the month: 1 to 4, or -1 for the last.",
            HolidayDates.Easter when holiday.EasterOffset is < -366 or > 366 => "days from Easter must be -366 to 366.",
            HolidayDates.NthWeekday or HolidayDates.Easter when holiday.Length is < 1 or > 366 => "must last 1 to 366 days.",
            HolidayDates.Range when !TryDay(holiday.Start, 2000, out _) || !TryDay(holiday.End, 2000, out _) =>
                "needs a first and last day, as MM-DD (05-01 to 05-05).",
            _ => null
        };
        if (dates is not null)
        {
            return dates;
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
