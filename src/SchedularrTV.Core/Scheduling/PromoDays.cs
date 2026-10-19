namespace SchedularrTV.Core.Scheduling;

/// <summary>
/// The day folders promos can be filed under: a weekday (<c>Saturday</c>), or a group of them, <c>Weekdays</c>
/// (Monday to Friday) or <c>Weekends</c> (Saturday and Sunday), as in <c>Eventually/Weekdays/4pm</c>. Read into the
/// <c>promo_day</c> attribute as <c>saturday</c>, <c>weekdays</c> or <c>weekends</c>.
/// </summary>
public static class PromoDays
{
    public const string Weekdays = "weekdays";
    public const string Weekends = "weekends";

    /// <summary>
    /// Recognises a day folder: a weekday, or "Weekdays"/"Weekends" (singular, and with a space, dash or underscore,
    /// too). Letters only, so a number is never read as a day. <paramref name="key"/> is what's written to
    /// <c>promo_day</c>.
    /// </summary>
    public static bool TryRead(string folderName, out string key)
    {
        key = string.Empty;
        string trimmed = folderName.Trim();
        string squeezed = new([.. trimmed.Where(c => c is not (' ' or '-' or '_'))]);
        if (squeezed.Length == 0 || !squeezed.All(char.IsLetter))
        {
            return false;
        }

        if (squeezed.Equals("weekday", StringComparison.OrdinalIgnoreCase) || squeezed.Equals("weekdays", StringComparison.OrdinalIgnoreCase))
        {
            key = Weekdays;
            return true;
        }

        if (squeezed.Equals("weekend", StringComparison.OrdinalIgnoreCase) || squeezed.Equals("weekends", StringComparison.OrdinalIgnoreCase))
        {
            key = Weekends;
            return true;
        }

        if (trimmed.All(char.IsLetter) && Enum.TryParse(trimmed, ignoreCase: true, out DayOfWeek day))
        {
            key = day.ToString().ToLowerInvariant();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a promo filed for <paramref name="filed"/> (a <c>promo_day</c>: a weekday, <c>weekdays</c> or
    /// <c>weekends</c>) suits an airing on <paramref name="day"/> (a weekday's name, lower case).
    /// </summary>
    public static bool Matches(string filed, string day)
    {
        if (string.Equals(filed, day, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!Enum.TryParse(day, ignoreCase: true, out DayOfWeek weekday))
        {
            return false;
        }

        bool weekend = weekday is DayOfWeek.Saturday or DayOfWeek.Sunday;
        return (string.Equals(filed, Weekdays, StringComparison.OrdinalIgnoreCase) && !weekend)
               || (string.Equals(filed, Weekends, StringComparison.OrdinalIgnoreCase) && weekend);
    }
}
