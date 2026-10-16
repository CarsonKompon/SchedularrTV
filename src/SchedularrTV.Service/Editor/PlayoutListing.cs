using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.Service.Editor;

/// <summary>A line of the playout listing on the Channels page's guide preview.</summary>
public abstract record PlayoutListingRow(DateTimeOffset Start);

/// <summary>The start of a day (local time), so a long listing reads like a printed guide.</summary>
public sealed record PlayoutListingDay(DateTimeOffset Start, DateOnly Date) : PlayoutListingRow(Start);

/// <summary>
/// A programme. With breaks hidden it's the whole airing: every part of a programme split by mid-rolls, with the break
/// time between them counted in <see cref="BreakTime"/>. With breaks shown it's one part (<see cref="Part"/> of
/// <see cref="Parts"/>).
/// </summary>
public sealed record PlayoutListingProgramme(
    DateTimeOffset Start,
    DateTimeOffset End,
    ScheduleEntry Entry,
    int Part,
    int Parts,
    int Breaks,
    TimeSpan BreakTime) : PlayoutListingRow(Start)
{
    /// <summary>Time the programme itself is on screen (the airing less its mid-roll breaks).</summary>
    public TimeSpan Runtime => End - Start - BreakTime;

    public bool IsSplit => Parts > 1;
}

/// <summary>What plays between programmes (or between the parts of one): commercials, promos, interstitials, filler.</summary>
public sealed record PlayoutListingBreak(DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<ScheduleEntry> Clips, bool MidRoll)
    : PlayoutListingRow(Start)
{
    public TimeSpan Duration => End - Start;

    /// <summary>Only a show's own intro or outro, rather than an ad break.</summary>
    public bool IsTitleSequence => Clips.All(clip => clip.IsSlotIntro || clip.IsSlotOutro);
}

/// <summary>
/// Turns a channel's playout into the rows of the guide preview: what airs and when, how many parts mid-rolls split
/// each programme into, and optionally the breaks between.
/// </summary>
public static class PlayoutListing
{
    public static IReadOnlyList<PlayoutListingRow> Build(
        IEnumerable<ScheduleEntry> entries,
        DateTimeOffset from,
        DateTimeOffset until,
        bool showBreaks,
        TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        ScheduleEntry[] window = [.. entries
            .Where(entry => entry.Duration > TimeSpan.Zero && entry.End > from && entry.Start < until)
            .OrderBy(entry => entry.Start)];

        List<PlayoutListingRow> rows = showBreaks ? WithBreaks(window) : Airings(window);
        return WithDays(rows, zone);
    }

    /// <summary>Whether an entry is programming the guide lists, rather than something played around it.</summary>
    public static bool IsProgramme(ScheduleEntry entry) =>
        entry.Kind is ScheduleEntryKind.Program or ScheduleEntryKind.Interstitial && !entry.IsSlotIntro && !entry.IsSlotOutro;

    /// <summary>One row per airing, its parts and mid-roll breaks folded in.</summary>
    private static List<PlayoutListingRow> Airings(ScheduleEntry[] entries)
    {
        List<PlayoutListingRow> rows = [];
        PlayoutListingProgramme? open = null;
        TimeSpan pendingBreak = TimeSpan.Zero;

        foreach (ScheduleEntry entry in entries)
        {
            if (!IsProgramme(entry))
            {
                // Only counted if the same programme comes back after it (a mid-roll), not between programmes.
                if (!entry.IsSlotIntro && !entry.IsSlotOutro)
                {
                    pendingBreak += entry.Duration;
                }

                continue;
            }

            if (open is not null && ContinuesAiring(open.Entry, entry))
            {
                PlayoutListingProgramme extended = open with
                {
                    End = entry.End,
                    Breaks = open.Breaks + (pendingBreak > TimeSpan.Zero ? 1 : 0),
                    BreakTime = open.BreakTime + pendingBreak
                };
                rows[^1] = extended;
                open = extended;
            }
            else
            {
                open = new PlayoutListingProgramme(entry.Start, entry.End, entry, 1, Math.Max(1, entry.SegmentCount), 0, TimeSpan.Zero);
                rows.Add(open);
            }

            pendingBreak = TimeSpan.Zero;
        }

        return rows;
    }

    /// <summary>A later part of the same split programme.</summary>
    private static bool ContinuesAiring(ScheduleEntry first, ScheduleEntry next) =>
        next.SegmentNumber > 1
        && first.SegmentCount > 1
        && next.Item is not null
        && first.Item is not null
        && string.Equals(first.Item.Id, next.Item.Id, StringComparison.Ordinal);

    /// <summary>Every part of every programme, with the clips between gathered into breaks.</summary>
    private static List<PlayoutListingRow> WithBreaks(ScheduleEntry[] entries)
    {
        List<PlayoutListingRow> rows = [];
        List<ScheduleEntry> clips = [];
        ScheduleEntry? lastProgramme = null;

        void FlushBreak(ScheduleEntry? next)
        {
            if (clips.Count == 0)
            {
                return;
            }

            // Between two parts of one programme rather than between programmes.
            bool midRoll = lastProgramme is not null && next is not null && ContinuesAiring(lastProgramme, next);
            rows.Add(new PlayoutListingBreak(clips[0].Start, clips[^1].End, [.. clips], midRoll));
            clips.Clear();
        }

        foreach (ScheduleEntry entry in entries)
        {
            if (!IsProgramme(entry))
            {
                clips.Add(entry);
                continue;
            }

            FlushBreak(entry);
            rows.Add(new PlayoutListingProgramme(entry.Start, entry.End, entry, Math.Max(1, entry.SegmentNumber),
                Math.Max(1, entry.SegmentCount), 0, TimeSpan.Zero));
            lastProgramme = entry;
        }

        FlushBreak(null);
        return rows;
    }

    private static IReadOnlyList<PlayoutListingRow> WithDays(List<PlayoutListingRow> rows, TimeZoneInfo zone)
    {
        List<PlayoutListingRow> withDays = new(rows.Count + 4);
        DateOnly? day = null;

        foreach (PlayoutListingRow row in rows)
        {
            DateOnly date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(row.Start, zone).DateTime);
            if (date != day)
            {
                withDays.Add(new PlayoutListingDay(row.Start, date));
                day = date;
            }

            withDays.Add(row);
        }

        return withDays;
    }

    /// <summary>The name a listing gives an entry: the show (or film), and beneath it the episode.</summary>
    public static (string Title, string? Subtitle) Describe(ScheduleEntry entry)
    {
        MediaItem? item = entry.Item;
        if (item is null)
        {
            return entry.Kind switch
            {
                ScheduleEntryKind.Padding => ("Padding", "Filled by the playout"),
                _ when entry.GeneratedTemplate is not null => (entry.GeneratedSubject ?? "Generated promo", "Generated promo"),
                _ => (KindLabel(entry), null)
            };
        }

        if (item.Kind == MediaKind.Episode && !string.IsNullOrWhiteSpace(item.SeriesName))
        {
            string? code = item.SeasonNumber is { } season && item.EpisodeNumber is { } episode ? $"S{season:D2}E{episode:D2}" : null;
            string? subtitle = (code, string.Equals(item.Title, item.SeriesName, StringComparison.OrdinalIgnoreCase) ? null : item.Title) switch
            {
                ({ } c, { } t) => $"{c} · {t}",
                ({ } c, null) => c,
                (null, { } t) => t,
                _ => null
            };
            return (item.SeriesName!, subtitle);
        }

        if (item.Kind == MediaKind.Movie)
        {
            return (item.Title, item.PremiereDate is { } premiere ? $"Film · {premiere.Year}" : "Film");
        }

        return (item.Title, null);
    }

    /// <summary>What an entry is, as the listing's colour key names it.</summary>
    public static string KindLabel(ScheduleEntry entry) =>
        entry.IsSlotIntro ? "Intro"
        : entry.IsSlotOutro ? "Outro"
        : entry.Kind switch
        {
            ScheduleEntryKind.Program => "Programme",
            ScheduleEntryKind.Interstitial => "Interstitial",
            ScheduleEntryKind.Commercial => "Commercial",
            ScheduleEntryKind.Filler => "Filler",
            ScheduleEntryKind.Promo => "Promo",
            ScheduleEntryKind.UpNextPromo => "Promo · Up next",
            ScheduleEntryKind.NowPlayingPromo => "Promo · Now playing",
            ScheduleEntryKind.ThisHasBeenPromo => "Promo · This has been",
            ScheduleEntryKind.YoureWatchingPromo => "Promo · You're watching",
            ScheduleEntryKind.BackToTheShowPromo => "Promo · Back to the show",
            ScheduleEntryKind.EventuallyPromo => "Promo · Later this week",
            ScheduleEntryKind.NewEpisodePromo => "Promo · New episode",
            ScheduleEntryKind.RightBackPromo => "Promo · We'll be right back",
            ScheduleEntryKind.Padding => "Padding",
            _ => entry.Kind.ToString()
        };

    /// <summary>The colour group an entry belongs to, a CSS class suffix.</summary>
    public static string ColourKey(ScheduleEntry entry) =>
        entry.IsSlotIntro || entry.IsSlotOutro ? "intro"
        : entry.Kind switch
        {
            ScheduleEntryKind.Program => "programme",
            ScheduleEntryKind.Interstitial => "interstitial",
            ScheduleEntryKind.Commercial => "commercial",
            ScheduleEntryKind.Filler => "filler",
            ScheduleEntryKind.EventuallyPromo or ScheduleEntryKind.NewEpisodePromo => "eventually",
            ScheduleEntryKind.Padding => "padding",
            _ => "promo"
        };

    /// <summary>"1h 02m", "22m 10s", "45s".</summary>
    public static string Length(TimeSpan duration) =>
        duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}h {duration.Minutes:00}m"
        : duration.TotalMinutes >= 1 ? (duration.Seconds == 0 ? $"{duration.Minutes}m" : $"{duration.Minutes}m {duration.Seconds:00}s")
        : $"{Math.Max(0, (int)Math.Round(duration.TotalSeconds))}s";
}
