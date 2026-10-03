using System.Globalization;

namespace SchedularrTV.Core.Temporal;

public sealed record TemporalTags(
    string Month,
    string Season,
    string ExactDate,
    string FullDate,
    IReadOnlyList<string> Holidays,
    IReadOnlyList<string> All);

/// <summary>
/// Derives the date-driven tag set used to widen commercial/filler queries
/// (see spec 1.2 Dynamic Tag Resolution).
/// </summary>
public static class TemporalTagResolver
{
    public static TemporalTags Resolve(DateTimeOffset moment, TemporalTagOptions? options = null)
    {
        options ??= new TemporalTagOptions();
        DateTime date = moment.Date;

        string month = CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(date.Month);
        string season = GetSeason(date.Month);
        string exactDate = date.ToString("MM-dd", CultureInfo.InvariantCulture);
        string fullDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        IReadOnlyList<HolidayDefinition> definitions = options.Holidays.Count > 0
            ? options.Holidays
            : TemporalTagOptions.DefaultHolidays;

        List<string> holidays = options.IncludeHolidays
            ? definitions.Where(d => IsWithin(d, date)).Select(d => d.Name).ToList()
            : [];

        List<string> all = [];
        if (options.IncludeMonth)
        {
            all.Add(month);
        }

        if (options.IncludeSeason)
        {
            all.Add(season);
        }

        if (options.IncludeExactDate)
        {
            all.Add(exactDate);
        }

        if (options.IncludeFullDate)
        {
            all.Add(fullDate);
        }

        all.AddRange(holidays);

        return new TemporalTags(month, season, exactDate, fullDate, holidays, all);
    }

    /// <summary>Meteorological seasons: Winter Dec-Feb, Spring Mar-May, Summer Jun-Aug, Fall Sep-Nov.</summary>
    public static string GetSeason(int month) => month switch
    {
        12 or 1 or 2 => "Winter",
        3 or 4 or 5 => "Spring",
        6 or 7 or 8 => "Summer",
        _ => "Fall"
    };

    public static bool IsWithin(HolidayDefinition definition, DateTime date)
    {
        if (definition.NthWeekday is { } rule)
        {
            DateTime anchor = ResolveNthWeekday(date.Year, rule);
            DateTime start = anchor.AddDays(-definition.LeadDays);
            DateTime end = anchor.AddDays(definition.TrailDays);
            return date >= start && date <= end;
        }

        if (definition.Start is null || definition.End is null)
        {
            return false;
        }

        if (!TryParseMonthDay(definition.Start, out int startMonth, out int startDay)
            || !TryParseMonthDay(definition.End, out int endMonth, out int endDay))
        {
            return false;
        }

        int current = (date.Month * 100) + date.Day;
        int from = (startMonth * 100) + startDay;
        int to = (endMonth * 100) + endDay;

        // A window such as 12-26..01-02 wraps the year boundary.
        return from <= to
            ? current >= from && current <= to
            : current >= from || current <= to;
    }

    public static DateTime ResolveNthWeekday(int year, NthWeekdayRule rule)
    {
        if (rule.Ordinal < 0)
        {
            var last = new DateTime(year, rule.Month, DateTime.DaysInMonth(year, rule.Month));
            int back = ((int)last.DayOfWeek - (int)rule.DayOfWeek + 7) % 7;
            return last.AddDays(-back - ((-rule.Ordinal - 1) * 7));
        }

        var first = new DateTime(year, rule.Month, 1);
        int forward = ((int)rule.DayOfWeek - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(forward + ((Math.Max(rule.Ordinal, 1) - 1) * 7));
    }

    private static bool TryParseMonthDay(string value, out int month, out int day)
    {
        month = 0;
        day = 0;
        string[] parts = value.Split('-', StringSplitOptions.TrimEntries);
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out month)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out day);
    }
}
