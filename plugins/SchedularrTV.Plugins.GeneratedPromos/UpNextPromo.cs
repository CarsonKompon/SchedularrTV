using System.Globalization;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.GeneratedPromos;

/// <summary>
/// A programme a promo names; <paramref name="Path"/> and <paramref name="Runtime"/> locate its file for background
/// clips, and <paramref name="Overview"/> (what the episode is about) lets an AI-written voice-over talk about it.
/// </summary>
public sealed record UpNextListing(string Title, DateTimeOffset Start, string? Episode = null, string? Path = null, TimeSpan? Runtime = null,
    string? Overview = null);

/// <summary>Reads programmes from a schedule and phrases them for a voice-over.</summary>
public static class UpNextPromo
{
    public static IReadOnlyList<UpNextListing> Listings(ChannelSchedule schedule, DateTimeOffset after, int count)
    {
        List<UpNextListing> listings = [];
        foreach (ScheduleEntry entry in schedule.Entries.OrderBy(entry => entry.Start))
        {
            if (entry.Start < after || !IsListing(entry) || entry.Item is not { } item)
            {
                continue;
            }

            string title = TitleOf(item);
            if (listings.Count > 0 && string.Equals(listings[^1].Title, title, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            listings.Add(Listing(item, entry.Start));
            if (listings.Count == count)
            {
                break;
            }
        }

        return listings;
    }

    /// <summary>The programme that played last before a sign-off.</summary>
    public static UpNextListing? Previous(ChannelSchedule schedule, DateTimeOffset before)
    {
        ScheduleEntry? last = schedule.Entries
            .Where(entry => entry.Start < before && entry.Kind == ScheduleEntryKind.Program && entry.Item is not null
                && !entry.IsSlotIntro && !entry.IsSlotOutro)
            .MaxBy(entry => entry.Start);
        return last?.Item is { } item ? Listing(item, last.Start) : null;
    }

    /// <summary>
    /// The programme scheduled at a promoted airtime; the build-time prediction stands in when the
    /// schedule does not reach that far.
    /// </summary>
    public static UpNextListing? At(ChannelSchedule schedule, DateTimeOffset at, string? predicted)
    {
        ScheduleEntry? scheduled = schedule.Entries
            .Where(entry => entry.Start >= at - TimeSpan.FromSeconds(1) && entry.Start <= at + TimeSpan.FromMinutes(5) && IsListing(entry))
            .MinBy(entry => entry.Start);
        if (scheduled?.Item is { } item)
        {
            return Listing(item, at);
        }

        return string.IsNullOrWhiteSpace(predicted) ? null : new UpNextListing(predicted, at);
    }

    /// <summary>
    /// The first programme the schedule airs from <paramref name="at"/> on (within <paramref name="within"/>), for a
    /// promo with no predicted show. Titles in <paramref name="skip"/> (what's on either side of the promo) are passed over.
    /// </summary>
    public static UpNextListing? FirstFrom(ChannelSchedule schedule, DateTimeOffset at, TimeSpan within, IReadOnlyCollection<string> skip)
    {
        ScheduleEntry? scheduled = schedule.Entries
            .Where(entry => entry.Start >= at - TimeSpan.FromSeconds(1) && entry.Start <= at + within && IsListing(entry)
                && entry.Item is { } item && !skip.Contains(TitleOf(item), StringComparer.OrdinalIgnoreCase))
            .MinBy(entry => entry.Start);
        return scheduled?.Item is { } found ? Listing(found, scheduled.Start) : null;
    }

    /// <summary>
    /// The read-out for an Up Next promo. With <paramref name="variety"/>, the later shows are linked in one of
    /// several ways and whole hours are said as "at 5", so back-to-back promos don't all sound the same.
    /// </summary>
    public static string Script(
        IReadOnlyList<UpNextListing> listings,
        TimeZoneInfo zone,
        string? preMessage = null,
        string? postMessage = null,
        string? variety = null)
    {
        if (listings.Count == 0)
        {
            throw new ArgumentException("An Up Next promo needs at least one listing.", nameof(listings));
        }

        List<string> parts = [preMessage ?? "Coming up next...", Sentence(listings[0].Title, '.')];
        if (variety is null)
        {
            parts.AddRange(listings.Skip(1).Select(listing => $"Then {listing.Title} at {Clock(listing.Start, zone)}!"));
        }
        else if (listings.Count > 1)
        {
            (string first, string middle, string last) = Links[Pick(variety, Links.Length)];
            List<string> later = [.. listings.Skip(1).Select(listing => $"{listing.Title} at {SpokenClock(listing.Start, zone)}")];
            string linked = later.Count == 1
                ? $"{first} {later[0]}"
                : $"{first} {string.Join(", ", later.Take(later.Count - 1).Select((show, index) => index == 0 ? show : $"{middle} {show}"))}, {last} {later[^1]}";
            parts.Add(Sentence(linked, '!'));
        }

        if (!string.IsNullOrWhiteSpace(postMessage))
        {
            parts.Add(postMessage);
        }

        return string.Join(' ', parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    // Opening link, links between the middle shows, and the link before the last one.
    private static readonly (string First, string Middle, string Last)[] Links =
    [
        ("Then", "then", "and then"),
        ("Followed by", "then", "and"),
        ("After that,", "then", "and finally"),
        ("Then", "followed by", "and"),
        ("Stick around for", "then", "and"),
        ("And then", "then", "and after that,"),
        ("Later,", "then", "and")
    ];

    /// <summary>A stable choice from <paramref name="count"/> options for the same seed.</summary>
    public static int Pick(string seed, int count) =>
        (int)(BitConverter.ToUInt32(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed))) % (uint)count);

    /// <summary>A time as it's said aloud: <c>5</c> on the hour, otherwise <c>4:30</c>.</summary>
    public static string SpokenClock(DateTimeOffset time, TimeZoneInfo zone)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(time, zone);
        return local.Minute == 0
            ? local.ToString("%h", CultureInfo.InvariantCulture)
            : local.ToString("h:mm", CultureInfo.InvariantCulture);
    }

    public static string Clock(DateTimeOffset time, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(time, zone).ToString("h:mm", CultureInfo.InvariantCulture);

    /// <summary>Today, Tonight (from 5pm), Tomorrow, or the weekday, relative to when the promo airs.</summary>
    public static string When(DateTimeOffset target, DateTimeOffset airsAt, TimeZoneInfo zone, bool day, bool time)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(target, zone);
        int days = (local.Date - TimeZoneInfo.ConvertTime(airsAt, zone).Date).Days;
        string? dayText = !day ? null : days switch
        {
            <= 0 => local.Hour >= 17 ? "Tonight" : "Today",
            1 => "Tomorrow",
            _ => local.DayOfWeek.ToString()
        };
        string? clock = time ? Clock(target, zone) : null;
        return dayText is null ? clock ?? string.Empty : clock is null ? dayText : $"{dayText} at {clock}";
    }

    public static string Sentence(string text, char ending) =>
        text.TrimEnd().EndsWith('.') || text.TrimEnd().EndsWith('!') || text.TrimEnd().EndsWith('?') ? text.TrimEnd() : text.TrimEnd() + ending;

    private static bool IsListing(ScheduleEntry entry) =>
        entry.Kind == ScheduleEntryKind.Program && entry.Item is not null
        && !entry.IsSlotIntro && !entry.IsSlotOutro && !entry.ContinuesSlot && entry.SegmentNumber <= 1;

    private static string TitleOf(MediaItem item) => string.IsNullOrWhiteSpace(item.SeriesName) ? item.Title : item.SeriesName;

    public static UpNextListing Listing(MediaItem item, DateTimeOffset start) =>
        new(TitleOf(item), start, EpisodeOf(item), item.Path, item.Duration > TimeSpan.Zero ? item.Duration : null,
            string.IsNullOrWhiteSpace(item.Overview) ? null : item.Overview.Trim());

    private static string? EpisodeOf(MediaItem item) =>
        !string.IsNullOrWhiteSpace(item.SeriesName) && !string.Equals(item.Title, item.SeriesName, StringComparison.OrdinalIgnoreCase)
            ? item.Title
            : null;
}
