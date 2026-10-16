using System.Collections.Concurrent;
using System.Globalization;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;

namespace SchedularrTV.Core.Scheduling;

/// <summary>Availability of the whole promo at its scheduled airtime, never at the time a render happens.</summary>
public static class GeneratedPromoEligibility
{
    public static bool IsAvailable(GeneratedPromoTemplate template, DateTimeOffset airsAt, TimeZoneInfo zone)
    {
        if (template.Availability is not { } availability) return true;
        DateTimeOffset local = TimeZoneInfo.ConvertTime(airsAt, zone);
        if (availability.TimeRange is { } time && !Contains(time, local.TimeOfDay)) return false;
        if (availability.DateRange is not { } dates) return true;
        if (!TryMonthDay(dates.Start, out int start) || !TryMonthDay(dates.End, out int end)) return false;
        int day = local.Month * 100 + local.Day;
        return start <= end ? day >= start && day <= end : day >= start || day <= end;
    }

    /// <summary>Whether the template limits which programmes it's about (<see cref="GeneratedPromoAvailability.Query"/>).</summary>
    public static bool HasProgrammeQuery(GeneratedPromoTemplate template) =>
        !string.IsNullOrWhiteSpace(template.Availability?.Query);

    /// <summary>
    /// Whether the programme the promo is about suits the template's library query. Templates without one suit any
    /// programme; with one, an unknown programme (null) doesn't, and neither does a query that doesn't read.
    /// </summary>
    public static bool SuitsProgramme(GeneratedPromoTemplate template, MediaItem? programme)
    {
        if (template.Availability?.Query is not { } text || string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        return programme is not null && Parsed(text) is { } query && QueryEvaluator.Matches(programme, query);
    }

    private static readonly ConcurrentDictionary<string, QueryExpression?> Queries = new(StringComparer.Ordinal);

    private static QueryExpression? Parsed(string text) =>
        Queries.GetOrAdd(text.Trim(), key => QueryParser.TryParse(key, out QueryExpression query, out _) ? query : null);

    public static bool Valid(GeneratedBackgroundTimeRange range) =>
        range.Start >= TimeSpan.Zero && range.Start < TimeSpan.FromDays(1)
        && range.End >= TimeSpan.Zero && range.End < TimeSpan.FromDays(1) && range.Start != range.End;

    /// <summary>Start inclusive, end exclusive. Earlier end times wrap across midnight.</summary>
    public static bool Contains(GeneratedBackgroundTimeRange range, TimeSpan time) => Valid(range)
        && (range.Start < range.End ? time >= range.Start && time < range.End : time >= range.Start || time < range.End);

    /// <summary>Use a leap year for validation so February 29 is supported without storing a year.</summary>
    public static bool TryMonthDay(string? text, out int key)
    {
        bool valid = DateOnly.TryParseExact($"2000-{text}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day);
        key = valid ? day.Month * 100 + day.Day : 0;
        return valid;
    }
}
