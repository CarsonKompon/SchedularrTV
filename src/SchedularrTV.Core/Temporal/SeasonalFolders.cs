using System.Globalization;

namespace SchedularrTV.Core.Temporal;

/// <summary>
/// The folder names a seasonal ad break reads as "only play then": months, seasons, holidays and dates. Anything else
/// (an <c>Any Time</c> folder included) always plays.
/// </summary>
public static class SeasonalFolders
{
    private static readonly string[] Seasons = ["Winter", "Spring", "Summer", "Fall", "Autumn"];

    /// <summary>Every month, season and holiday name a folder can be called.</summary>
    public static IReadOnlyList<string> Names(TemporalTagOptions options)
    {
        List<string> names = [.. CultureInfo.InvariantCulture.DateTimeFormat.MonthNames.Where(name => name.Length > 0)];
        names.AddRange(Seasons);
        names.AddRange(Holidays(options).Select(holiday => holiday.Name).Where(name => !string.IsNullOrWhiteSpace(name)));
        return [.. names.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The names that apply on <paramref name="moment"/>'s date (dates are matched separately).</summary>
    public static IReadOnlyList<string> Active(DateTimeOffset moment, TemporalTagOptions options)
    {
        DateTime date = moment.Date;
        List<string> active =
        [
            CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(date.Month),
            TemporalTagResolver.GetSeason(date.Month)
        ];

        if (active[1] == "Fall")
        {
            active.Add("Autumn");
        }

        active.AddRange(Holidays(options).Where(holiday => TemporalTagResolver.IsWithin(holiday, date)).Select(holiday => holiday.Name));
        return active;
    }

    /// <summary>Whether a folder name is a date: <c>12-25</c> or <c>2026-12-25</c>.</summary>
    public static bool IsDate(string name, out int? year, out int month, out int day)
    {
        year = null;
        month = 0;
        day = 0;
        string[] parts = name.Trim().Split('-');
        if (parts.Length is < 2 or > 3 || parts.Any(part => part.Length == 0 || !part.All(char.IsDigit)))
        {
            return false;
        }

        int offset = parts.Length == 3 ? 1 : 0;
        if (offset == 1)
        {
            year = int.Parse(parts[0], CultureInfo.InvariantCulture);
        }

        month = int.Parse(parts[offset], CultureInfo.InvariantCulture);
        day = int.Parse(parts[offset + 1], CultureInfo.InvariantCulture);
        return month is >= 1 and <= 12 && day is >= 1 and <= 31;
    }

    /// <summary>Whether a folder name says the clips in it play only at certain times of year.</summary>
    public static bool IsSeasonal(string name, TemporalTagOptions options) =>
        IsDate(name, out _, out _, out _) || Names(options).Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a seasonal folder name applies on <paramref name="moment"/>'s date.</summary>
    public static bool IsActive(string name, DateTimeOffset moment, TemporalTagOptions options)
    {
        if (IsDate(name, out int? year, out int month, out int day))
        {
            return month == moment.Month && day == moment.Day && (year is null || year == moment.Year);
        }

        return Active(moment, options).Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<HolidayDefinition> Holidays(TemporalTagOptions options) =>
        options.IncludeHolidays ? options.Holidays.Count > 0 ? options.Holidays : TemporalTagOptions.DefaultHolidays : [];
}
