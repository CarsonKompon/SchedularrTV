using System.Collections.Concurrent;
using System.Globalization;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Core.Temporal;

namespace SchedularrTV.Overlays;

/// <summary>When an entry airs, as overlay variants judge it: the local time, and the holidays and season then.</summary>
public sealed record OverlayMoment(DateTimeOffset Local, TemporalTags Tags)
{
    /// <summary><paramref name="start"/> in the channel's time zone (else the schedule's), with the schedule's holidays.</summary>
    public static OverlayMoment At(DateTimeOffset start, SchedulerConfig config, ChannelConfig channel)
    {
        TimeZoneInfo zone = SchedulingEngine.ResolveTimeZone(string.IsNullOrWhiteSpace(channel.TimeZone) ? config.Settings.TimeZone : channel.TimeZone);
        DateTimeOffset local = TimeZoneInfo.ConvertTime(start, zone);

        // Holidays are always worked out here, even where they're turned off for commercials.
        var options = new TemporalTagOptions { Holidays = config.Settings.TemporalTags.Holidays, IncludeHolidays = true };
        return new OverlayMoment(local, TemporalTagResolver.Resolve(local, options));
    }
}

/// <summary>Picks the picture a file overlay shows on an entry: the first variant whose conditions hold, else its own file.</summary>
public static class OverlayVariants
{
    private static readonly ConcurrentDictionary<string, QueryExpression?> Queries = new(StringComparer.Ordinal);

    /// <summary>The file to show, or null for none (no variant matches and the overlay has no file of its own).</summary>
    public static string? FileFor(Overlay overlay, MediaItem? item, OverlayMoment moment)
    {
        foreach (OverlayVariant variant in overlay.Variants)
        {
            if (variant.Enabled && variant.File is { Length: > 0 } file && Matches(variant, item, moment))
            {
                return file;
            }
        }

        return overlay.File is { Length: > 0 } own ? own : null;
    }

    /// <summary>The first variant that holds (with a file to show), or null when the overlay's own file shows.</summary>
    public static OverlayVariant? Winner(Overlay overlay, MediaItem? item, OverlayMoment moment) =>
        overlay.Variants.FirstOrDefault(variant => variant.Enabled && variant.File is { Length: > 0 } && Matches(variant, item, moment));

    /// <summary>Whether every condition the variant sets holds for this item at this moment.</summary>
    public static bool Matches(OverlayVariant variant, MediaItem? item, OverlayMoment moment)
    {
        DateTimeOffset local = moment.Local;

        if (variant.Ratings.Count > 0
            && (item?.Rating is not { Length: > 0 } rating || !variant.Ratings.Any(wanted => SameRating(wanted, rating))))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(variant.Query) && (item is null || Query(variant.Query) is not { } query || !QueryEvaluator.Matches(item, query)))
        {
            return false;
        }

        if (variant.Holidays.Count > 0
            && !variant.Holidays.Any(holiday => moment.Tags.Holidays.Contains(holiday.Trim(), StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (variant.Seasons.Count > 0 && !variant.Seasons.Any(season => string.Equals(season.Trim(), moment.Tags.Season, StringComparison.OrdinalIgnoreCase)
                                                                        || (season.Trim().Equals("Autumn", StringComparison.OrdinalIgnoreCase) && moment.Tags.Season == "Fall")))
        {
            return false;
        }

        if (variant.Months.Count > 0 && !variant.Months.Contains(local.Month))
        {
            return false;
        }

        if ((!string.IsNullOrWhiteSpace(variant.DateFrom) || !string.IsNullOrWhiteSpace(variant.DateTo)) && !InDates(variant.DateFrom, variant.DateTo, local))
        {
            return false;
        }

        if (variant.Days.Count > 0 && !variant.Days.Contains(local.DayOfWeek))
        {
            return false;
        }

        if ((variant.TimeFrom is not null || variant.TimeTo is not null) && !InTimes(variant.TimeFrom, variant.TimeTo, local.TimeOfDay))
        {
            return false;
        }

        return true;
    }

    /// <summary>A query as written, parsed once; null when it doesn't parse (so the variant never matches).</summary>
    public static QueryExpression? Query(string text) =>
        Queries.GetOrAdd(text.Trim(), query => QueryParser.TryParse(query, out QueryExpression expression, out _) ? expression : null);

    /// <summary>Ratings compared ignoring case, spaces and dashes: "TV-Y7" is "tv y7", "PG13" is "PG-13".</summary>
    public static bool SameRating(string a, string b) => string.Equals(Plain(a), Plain(b), StringComparison.OrdinalIgnoreCase);

    private static string Plain(string rating) => new([.. rating.Where(char.IsLetterOrDigit)]);

    /// <summary>Whether the day falls from <paramref name="from"/> to <paramref name="to"/> (<c>MM-DD</c>, wrapping the new year when from is later).</summary>
    public static bool InDates(string? from, string? to, DateTimeOffset local)
    {
        int day = local.Month * 100 + local.Day;
        int? start = DayOf(from);
        int? end = DayOf(to);
        return (start, end) switch
        {
            ({ } a, { } b) when a <= b => day >= a && day <= b,
            ({ } a, { } b) => day >= a || day <= b,
            ({ } a, null) => day >= a,
            (null, { } b) => day <= b,
            _ => true
        };
    }

    /// <summary>Whether the time of day is from <paramref name="from"/> (inclusive) to <paramref name="to"/> (exclusive), across midnight when from is later.</summary>
    public static bool InTimes(TimeSpan? from, TimeSpan? to, TimeSpan time)
    {
        TimeSpan start = from ?? TimeSpan.Zero;
        TimeSpan end = to ?? TimeSpan.FromDays(1);
        return start <= end ? time >= start && time < end : time >= start || time < end;
    }

    private static int? DayOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string[] parts = text.Trim().Split('-', '/');
        return parts.Length == 2
               && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int month) && month is >= 1 and <= 12
               && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int day) && day is >= 1 and <= 31
            ? month * 100 + day
            : null;
    }
}
