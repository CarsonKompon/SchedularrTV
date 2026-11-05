using System.Globalization;

namespace SchedularrTV.Core.Configuration;

/// <summary>
/// When a title in a collection may be scheduled (<see cref="ContentInclude.Timeslots"/>): on these days, between these
/// times, in the channel's time zone. It doesn't promise the title airs then, only that it isn't considered at other times.
/// </summary>
public sealed class TitleTimeslot
{
    /// <summary>The days it applies on; empty is every day.</summary>
    public List<DayOfWeek> Days { get; set; } = [];

    /// <summary>From this time of day; null is from midnight.</summary>
    public TimeSpan? Start { get; set; }

    /// <summary>
    /// Until this time of day (not including it); null is until midnight. Earlier than <see cref="Start"/>, it runs past
    /// midnight into the next day (a slot from 10pm to 2am on Friday covers Saturday's early hours).
    /// </summary>
    public TimeSpan? End { get; set; }

    /// <summary>Whether a programme starting at <paramref name="local"/> (the channel's time) is inside this slot.</summary>
    public bool Allows(DateTime local)
    {
        TimeSpan time = local.TimeOfDay;
        TimeSpan start = Start ?? TimeSpan.Zero;
        TimeSpan? end = End is { } until && until > TimeSpan.Zero ? until : null;

        if (end is not { } stop || stop > start)
        {
            // Within one day.
            return OnDay(local.DayOfWeek) && time >= start && (end is null || time < end);
        }

        // Past midnight: the evening of a slot's day, or the early hours of the day after it.
        return (OnDay(local.DayOfWeek) && time >= start) || (OnDay(Previous(local.DayOfWeek)) && time < stop);
    }

    private bool OnDay(DayOfWeek day) => Days.Count == 0 || Days.Contains(day);

    private static DayOfWeek Previous(DayOfWeek day) => (DayOfWeek)(((int)day + 6) % 7);

    /// <summary>"Sat, Sun 7am–12pm", "Every day from 6pm", "Mon–Fri".</summary>
    public string Describe()
    {
        string days = Days.Count is 0 or 7 ? "Every day" : DescribeDays(Days);
        string? times = (Start, End) switch
        {
            (null, null) => null,
            ({ } from, null) => $"from {Clock(from)}",
            (null, { } until) => $"until {Clock(until)}",
            ({ } from, { } until) => $"{Clock(from)}–{Clock(until)}"
        };
        return times is null ? days : $"{days} {times}";
    }

    /// <summary>Day names in week order, with runs joined: "Mon–Fri", "Sat, Sun".</summary>
    public static string DescribeDays(IEnumerable<DayOfWeek> days)
    {
        int[] sorted = [.. days.Distinct().Select(day => (int)day).Order()];
        List<string> parts = [];
        for (int i = 0; i < sorted.Length;)
        {
            int run = i;
            while (run + 1 < sorted.Length && sorted[run + 1] == sorted[run] + 1)
            {
                run++;
            }

            string first = Short((DayOfWeek)sorted[i]);
            parts.Add(run - i >= 2 ? $"{first}–{Short((DayOfWeek)sorted[run])}" : run > i ? $"{first}, {Short((DayOfWeek)sorted[run])}" : first);
            i = run + 1;
        }

        return string.Join(", ", parts);
    }

    private static string Short(DayOfWeek day) => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(day);

    private static string Clock(TimeSpan time)
    {
        DateTime at = DateTime.Today.Add(time);
        return at.Minute == 0
            ? at.ToString("htt", CultureInfo.InvariantCulture).ToLowerInvariant()
            : at.ToString("h:mmtt", CultureInfo.InvariantCulture).ToLowerInvariant();
    }

    public TitleTimeslot Copy() => new() { Days = [.. Days], Start = Start, End = End };

    /// <summary>Whether any of <paramref name="slots"/> allows <paramref name="local"/>; none at all allows any time.</summary>
    public static bool AnyAllows(IReadOnlyList<TitleTimeslot> slots, DateTime local) =>
        slots.Count == 0 || slots.Any(slot => slot.Allows(local));
}
