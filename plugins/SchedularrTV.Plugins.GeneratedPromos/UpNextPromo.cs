using System.Globalization;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.GeneratedPromos;

/// <summary>
/// A programme a promo names; <paramref name="Path"/> and <paramref name="Runtime"/> locate its file for background
/// clips, <paramref name="Overview"/> (what the episode is about) lets an AI-written voice-over talk about it, and
/// <paramref name="Block"/> is the schedule block it airs in, for block-matched variable images.
/// </summary>
public sealed record UpNextListing(string Title, DateTimeOffset Start, string? Episode = null, string? Path = null, TimeSpan? Runtime = null,
    string? Overview = null, string? Block = null)
{
    /// <summary>Scheduled end of the listed show, including its later segments and stacked episodes.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? End { get; init; }

    /// <summary>A new episode: the first airing of an episode a <c>drop</c> order released.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsNew { get; init; }
}

/// <summary>Reads programmes from a schedule and phrases them for a voice-over.</summary>
public static class UpNextPromo
{
    private static readonly System.Text.RegularExpressions.Regex TitleYear = new(@"\s*\(\s*[0-9]{4}\s*\)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex TitleSeparators = new(@"[:\-\u2010-\u2015\u2212]",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex TitleWhitespace = new(@"\s+",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Release-year disambiguators belong in metadata, not in spoken show names.</summary>
    public static string WithoutTitleYears(string text) => TitleYear.Replace(text, string.Empty).Trim();

    /// <summary>Speak show titles without release years or colon/dash separators that make TTS pause.</summary>
    public static string SpokenTitle(string title) =>
        TitleWhitespace.Replace(TitleSeparators.Replace(WithoutTitleYears(title), " "), " ").Trim();

    /// <summary>Upcoming scheduled slots, optionally merging consecutive slots of the same show.</summary>
    public static IReadOnlyList<UpNextListing> Listings(ChannelSchedule schedule, DateTimeOffset after, int count,
        DateTimeOffset? before = null, GeneratedUpcomingGrouping grouping = GeneratedUpcomingGrouping.Timeslot)
    {
        if (count <= 0) return [];
        List<UpNextListing> listings = [];
        bool inUpcomingSlot = false;
        foreach (ScheduleEntry entry in schedule.Entries.OrderBy(entry => entry.Start))
        {
            if (entry.Kind != ScheduleEntryKind.Program || entry.Item is not { } item || entry.IsSlotIntro || entry.IsSlotOutro)
                continue;
            if (IsListing(entry))
            {
                inUpcomingSlot = entry.Start >= after;
                if (!inUpcomingSlot) continue;
                string title = TitleOf(item);
                bool merge = grouping == GeneratedUpcomingGrouping.Show && listings.Count > 0
                    && string.Equals(listings[^1].Title, title, StringComparison.OrdinalIgnoreCase);
                if (!merge)
                {
                    if (listings.Count >= count || before is { } until && entry.Start > until) break;
                    listings.Add(Listing(item, entry.Start, entry.BlockName) with { End = entry.End, IsNew = entry.IsPremiere });
                }
            }
            // Later mid-roll segments and episodes packed into this slot extend it; they don't increment lookahead.
            if (inUpcomingSlot && listings.Count > 0 && entry.End > listings[^1].End)
            {
                listings[^1] = listings[^1] with { End = entry.End };
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
        return last?.Item is { } item ? Listing(item, last.Start, last.BlockName) with { IsNew = last.IsPremiere } : null;
    }

    /// <summary>
    /// The next new episode (a drop's first airing) the schedule airs after <paramref name="after"/>, skipping
    /// <paramref name="skip"/> (what's on either side of the promo, which other promos cover).
    /// </summary>
    public static UpNextListing? NextNew(ChannelSchedule schedule, DateTimeOffset after, IReadOnlyCollection<string> skip)
    {
        ScheduleEntry? scheduled = schedule.Entries
            .Where(entry => entry.Start >= after && entry.IsPremiere && IsListing(entry)
                && entry.Item is { } item && !skip.Contains(TitleOf(item), StringComparer.OrdinalIgnoreCase))
            .MinBy(entry => entry.Start);
        scheduled ??= schedule.Entries
            .Where(entry => entry.Start >= after && entry.IsPremiere && IsListing(entry))
            .MinBy(entry => entry.Start);
        return scheduled?.Item is { } found ? Listing(found, scheduled.Start, scheduled.BlockName) with { IsNew = true } : null;
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
            return Listing(item, at, scheduled.BlockName) with { IsNew = scheduled.IsPremiere };
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
        return scheduled?.Item is { } found ? Listing(found, scheduled.Start, scheduled.BlockName) with { IsNew = scheduled.IsPremiere } : null;
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

        string headline = listings[0].IsNew ? $"{GeneratedPromoResolver.SpokenNew} {SpokenTitle(listings[0].Title)}" : SpokenTitle(listings[0].Title);
        List<string> parts = [preMessage ?? "Coming up next...", Sentence(headline, '.')];
        if (variety is null)
        {
            parts.AddRange(listings.Skip(1).Select(listing => $"Then {SpokenTitle(listing.Title)} at {Clock(listing.Start, zone)}!"));
        }
        else if (listings.Count > 1)
        {
            (string first, string middle, string last) = Links[Pick(variety, Links.Length)];
            List<string> later = [.. listings.Skip(1).Select(listing => $"{SpokenTitle(listing.Title)} at {SpokenClock(listing.Start, zone)}")];
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

    public static UpNextListing Listing(MediaItem item, DateTimeOffset start, string? block = null) =>
        new(TitleOf(item), start, EpisodeOf(item), item.Path, item.Duration > TimeSpan.Zero ? item.Duration : null,
            string.IsNullOrWhiteSpace(item.Overview) ? null : item.Overview.Trim(), string.IsNullOrWhiteSpace(block) ? null : block);

    private static string? EpisodeOf(MediaItem item) =>
        !string.IsNullOrWhiteSpace(item.SeriesName) && !string.Equals(item.Title, item.SeriesName, StringComparison.OrdinalIgnoreCase)
            ? item.Title
            : null;
}
