using System.Globalization;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.CreditSqueeze;

/// <summary>A programme named beside the squeezed credits.</summary>
/// <param name="When">Its start as the viewer reads it ("8:30 PM").</param>
/// <param name="ImageId">The Jellyfin item to take its picture from (the series, else the item), if any.</param>
public sealed record SqueezeListing(string Title, string? Episode, string When, string? ImageId);

/// <summary>
/// One squeeze to make: <paramref name="Length"/> of <paramref name="SourcePath"/> from <paramref name="SourceStart"/>,
/// shrunk into a box on one <paramref name="Side"/> beside the next programme, with a promo (<paramref name="PromoPath"/>)
/// playing in the panel's picture when one was picked.
/// </summary>
public sealed record SqueezeJob(
    string SourcePath,
    TimeSpan SourceStart,
    TimeSpan Length,
    SqueezeListing Next,
    IReadOnlyList<SqueezeListing> Later,
    string ChannelName,
    string Heading,
    string AccentColor,
    string BackgroundColor,
    double CreditsVolume,
    bool Animate,
    CreditsSide Side = CreditsSide.Left,
    int SplitPercent = 60,
    string? PromoPath = null,
    TimeSpan PromoLength = default,
    SqueezeStyle Style = SqueezeStyle.SideBySide,
    string? BackgroundPath = null,
    SqueezeFit CreditsFit = SqueezeFit.Letterbox,
    bool CreditsText = false,
    SqueezeFit PanelFit = SqueezeFit.Letterbox,
    bool PanelText = true,
    string? EndingShow = null,
    string? CreditsCaption = null,
    string? CreditsLine = null,
    string HeadingFont = "Arial",
    string TextFont = "Arial")
{
    public SqueezeLayout Layout(int width = 1280, int height = 720) =>
        SqueezeLayout.Of(Style, Side, SplitPercent, width, height, CreditsFit, CreditsText, PanelText);
}

/// <summary>
/// Where to squeeze in a schedule: entry <paramref name="Index"/>, from <paramref name="From"/> in its file (the whole
/// entry when <paramref name="WholeEntry"/>, for a show's own outro). <paramref name="PromoPending"/>: the promo picked for
/// it is a generated one not made yet.
/// </summary>
public sealed record SqueezePlan(int Index, TimeSpan From, bool WholeEntry, SqueezeJob Job, bool PromoPending = false);

/// <summary>
/// Finds the credits to squeeze in a channel's schedule: the end credits of an episode where they're marked, and the
/// outros shows play after their episodes, each by its own odds, as long as a programme follows to promote. Seeded by
/// the channel and airtime, so the same airing always comes out the same.
/// </summary>
public static class SqueezePlanner
{
    // Credits starting this close to the start of the part that plays are more likely a mistake than credits.
    private static readonly TimeSpan LeastBefore = TimeSpan.FromMinutes(1);

    // Less than this isn't worth squeezing.
    private static readonly TimeSpan Shortest = TimeSpan.FromSeconds(8);

    public static List<SqueezePlan> Plan(
        IReadOnlyList<ScheduleEntry> entries,
        string channelId,
        string channelName,
        TimeZoneInfo zone,
        Func<ScheduleEntry, CreditSqueezeSection?> settings,
        Func<MediaItem, TimeSpan?> creditsOf,
        CreditSqueezeSection? everywhere = null,
        TimeSpan assumedCredits = default)
    {
        List<SqueezePlan> plans = [];
        for (int index = 0; index < entries.Count; index++)
        {
            ScheduleEntry entry = entries[index];
            if (!IsProgramme(entry, includeOutros: true) || entry.Item is not { Path.Length: > 0 } item
                || settings(entry) is not { Enabled: true } section || NextProgramme(entries, index) is not { } next)
            {
                continue;
            }

            TimeSpan from;
            TimeSpan length;
            bool whole = entry.IsSlotOutro;
            if (whole)
            {
                if (!Rolls(channelId, entry, "outro", section.OutroChanceFor(ShowBefore(entries, index))))
                {
                    continue;
                }

                from = entry.InPoint;
                length = entry.Duration;
            }
            else
            {
                // The outro after this episode gets the squeeze instead, if it wins its own roll.
                if (index + 1 < entries.Count && entries[index + 1] is { IsSlotOutro: true } outro && settings(outro) is { Enabled: true } outroSection
                    && Rolls(channelId, outro, "outro", outroSection.OutroChanceFor(ShowOf(item))))
                {
                    continue;
                }

                TimeSpan end = entry.InPoint + entry.Duration;
                TimeSpan? known = creditsOf(item) ?? Assumed(entry, item, section.AssumedCredits(assumedCredits));
                if (known is not { } credits || credits < entry.InPoint + LeastBefore || credits >= end
                    || end - credits > section.LongestCredits || !Rolls(channelId, entry, "credits", section.CreditsChance))
                {
                    continue;
                }

                from = credits;
                length = end - credits;
            }

            if (length < Shortest)
            {
                continue;
            }

            plans.Add(Build(entries, index, next, from, length, whole, section, everywhere, channelId, channelName, zone));
        }

        return plans;
    }

    /// <summary>
    /// The squeeze of entry <paramref name="index"/> from <paramref name="from"/> for <paramref name="length"/>, beside
    /// programme <paramref name="next"/>: the layout for its show, what's later, and a promo if one's wanted.
    /// </summary>
    public static SqueezePlan Build(IReadOnlyList<ScheduleEntry> entries, int index, int next, TimeSpan from, TimeSpan length, bool whole,
        CreditSqueezeSection section, CreditSqueezeSection? everywhere, string channelId, string channelName, TimeZoneInfo zone)
    {
        MediaItem item = entries[index].Item!;
        List<SqueezeListing> later = [];
        if (section.ShowLater)
        {
            string nextTitle = ShowOf(entries[next].Item!);
            for (int after = NextProgramme(entries, next) ?? entries.Count; after < entries.Count && later.Count < 2; after = NextProgramme(entries, after) ?? entries.Count)
            {
                SqueezeListing listing = Listing(entries[after], zone);
                if (!string.Equals(listing.Title, nextTitle, StringComparison.OrdinalIgnoreCase)
                    && later.All(other => !string.Equals(other.Title, listing.Title, StringComparison.OrdinalIgnoreCase)))
                {
                    later.Add(listing);
                }
            }
        }

        (MediaItem? promo, bool pending) = PickPromo(entries, index, next, section.Promos, channelId);
        string? ending = whole ? ShowBefore(entries, index) : ShowOf(item);
        SqueezeLayoutChoice layout = section.LayoutFor(ending, everywhere);
        return new SqueezePlan(index, from, whole, new SqueezeJob(item.Path!, from, length, Listing(entries[next], zone), later, channelName,
            section.Heading, section.AccentColor, section.BackgroundColor, Math.Clamp(section.CreditsVolume, 0, 2), section.Animate,
            layout.Side, Math.Clamp(layout.SplitPercent, SqueezeLayout.MinSplit, SqueezeLayout.MaxSplit), promo?.Path, promo?.Duration ?? TimeSpan.Zero,
            layout.Style, string.IsNullOrWhiteSpace(section.BackgroundPath) ? null : section.BackgroundPath.Trim(),
            layout.CreditsFit, layout.CreditsText, layout.PanelFit, layout.PanelText,
            layout.CreditsText ? ending : null,
            layout.CreditsText ? section.CreditsCaption : null,
            layout.CreditsText && !string.IsNullOrWhiteSpace(section.CreditsLine) ? section.CreditsLine.Trim() : null,
            string.IsNullOrWhiteSpace(section.HeadingFont) ? "Arial" : section.HeadingFont,
            string.IsNullOrWhiteSpace(section.TextFont) ? "Arial" : section.TextFont), pending);
    }

    // How far ahead each kind of promo is looked for.
    private static readonly TimeSpan ComingUpWithin = TimeSpan.FromHours(3);
    private static readonly TimeSpan EventuallyWithin = TimeSpan.FromHours(24);

    /// <summary>
    /// A promo for the panel, picked at random (seeded by the airing) among those of the wanted kinds the schedule airs
    /// nearby: the next programme's Up Next or ident (in the breaks before it), Coming Up promos in the next few hours,
    /// trailers in the next day. Library clips and generated promos alike. Null when none are wanted or there are none;
    /// <c>Pending</c> when the one picked is a generated promo not made yet (the squeeze waits for it, if there's time).
    /// </summary>
    public static (MediaItem? Promo, bool Pending) PickPromo(IReadOnlyList<ScheduleEntry> entries, int index, int next, IReadOnlyList<SqueezePromo> kinds,
        string channelId)
    {
        if (kinds.Count == 0)
        {
            return (null, false);
        }

        ScheduleEntry entry = entries[index];
        DateTimeOffset nextStart = entries[next].Start;
        List<MediaItem> candidates = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        for (int other = index + 1; other < entries.Count; other++)
        {
            ScheduleEntry candidate = entries[other];
            TimeSpan ahead = candidate.Start - entry.Start;
            if (ahead > EventuallyWithin)
            {
                break;
            }

            // A generated promo without its file yet still counts (by its slot), so the pick doesn't change once it's made.
            if (candidate.Item is not { } item
                || !seen.Add(item.Path is { Length: > 0 } path ? path : candidate.GeneratedTemplate is not null ? $"generated@{candidate.Start:O}" : string.Empty)
                || (item.Path is not { Length: > 0 } && candidate.GeneratedTemplate is null))
            {
                continue;
            }

            bool wanted = candidate.Kind switch
            {
                ScheduleEntryKind.UpNextPromo => kinds.Contains(SqueezePromo.UpNext) && candidate.Start <= nextStart,
                ScheduleEntryKind.NowPlayingPromo => kinds.Contains(SqueezePromo.NowPlaying) && candidate.Start <= nextStart,
                ScheduleEntryKind.Promo => kinds.Contains(SqueezePromo.ComingUp) && ahead <= ComingUpWithin,
                ScheduleEntryKind.EventuallyPromo => kinds.Contains(SqueezePromo.Eventually),
                _ => false
            };
            if (wanted && item.Duration > TimeSpan.FromSeconds(2))
            {
                candidates.Add(item);
            }
        }

        if (candidates.Count == 0)
        {
            return (null, false);
        }

        MediaItem picked = candidates[DeterministicRandom.FromSeedText(channelId, entry.Start.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), "credit-squeeze", "promo")
            .Next(candidates.Count)];
        return picked.Path is { Length: > 0 } ? (picked, false) : (null, true);
    }

    /// <summary>
    /// The schedule with each plan's credits replaced by its squeeze (<paramref name="rendered"/> gives a plan's file, or
    /// null while it isn't made yet, leaving those credits as they are). Every entry keeps its place; nothing moves.
    /// </summary>
    public static List<ScheduleEntry> Apply(IReadOnlyList<ScheduleEntry> entries, IReadOnlyList<SqueezePlan> plans, Func<SqueezePlan, string?> rendered)
    {
        Dictionary<int, SqueezePlan> byIndex = plans.ToDictionary(plan => plan.Index);
        List<ScheduleEntry> result = new(entries.Count + plans.Count);
        for (int index = 0; index < entries.Count; index++)
        {
            ScheduleEntry entry = entries[index];
            if (!byIndex.TryGetValue(index, out SqueezePlan? plan) || rendered(plan) is not { } file || entry.Item is not { } item)
            {
                result.Add(entry);
                continue;
            }

            MediaItem squeezed = new()
            {
                Id = $"squeeze:{item.Id}:{plan.From.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}",
                Title = item.Title,
                Kind = item.Kind,
                SeriesId = item.SeriesId,
                SeriesName = item.SeriesName,
                SeasonNumber = item.SeasonNumber,
                EpisodeNumber = item.EpisodeNumber,
                Overview = item.Overview,
                Duration = plan.Job.Length,
                Path = file,
                Attributes = new Dictionary<string, string> { ["credit_squeeze.source"] = item.Id }
            };

            // Folded into the programme's guide listing like an outro (and kept clear of screen bugs).
            ScheduleEntry squeeze = entry with
            {
                Start = entry.Start + (plan.From - entry.InPoint),
                Duration = plan.Job.Length,
                Item = squeezed,
                InPoint = TimeSpan.Zero,
                IsTrimmed = false,
                SourceDuration = null,
                IsSlotOutro = true,
                Source = "credit squeeze"
            };

            if (!plan.WholeEntry)
            {
                result.Add(entry with { Duration = plan.From - entry.InPoint, IsTrimmed = true, SourceDuration = entry.SourceDuration ?? item.Duration });
            }

            result.Add(squeeze);
        }

        return result;
    }

    /// <summary>Whether a squeeze happens, by the odds, the same way every time for the same airing.</summary>
    public static bool Rolls(string channelId, ScheduleEntry entry, string what, double chance) =>
        chance > 0 && (chance >= 1
            || DeterministicRandom.FromSeedText(channelId, entry.Start.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), "credit-squeeze", what)
                .Next(10_000) < (int)Math.Round(Math.Clamp(chance, 0, 1) * 10_000));

    /// <summary>
    /// Where the credits are taken to start in an episode whose credits aren't known: <paramref name="length"/> before
    /// its end. Only for the part of it that plays to the end (not one cut short by a break); null when it's off.
    /// </summary>
    public static TimeSpan? Assumed(ScheduleEntry entry, MediaItem item, TimeSpan length)
    {
        if (length <= TimeSpan.Zero)
        {
            return null;
        }

        TimeSpan end = entry.InPoint + entry.Duration;
        if (item.Duration > TimeSpan.Zero && end < item.Duration - TimeSpan.FromSeconds(2))
        {
            return null;
        }

        return end - length;
    }

    /// <summary>The shows a schedule airs (that a programme follows), for picking one to preview.</summary>
    public static List<string> ShowsOn(IReadOnlyList<ScheduleEntry> entries)
    {
        HashSet<string> shows = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < entries.Count; index++)
        {
            if (IsProgramme(entries[index], includeOutros: false) && entries[index].Item is { Path.Length: > 0 } item && NextProgramme(entries, index) is not null)
            {
                shows.Add(ShowOf(item));
            }
        }

        return [.. shows.Order(StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>How a preview of a show's squeeze was found.</summary>
    public enum PreviewSource { Credits, Outro, Assumed, LastSeconds }

    /// <summary>
    /// A squeeze of <paramref name="show"/> for the editor's preview, whatever the odds: its outro if it plays one, else an
    /// episode's marked credits, else the last <paramref name="length"/> of an episode (so the layout can still be seen
    /// against the end of the show). Null when the schedule doesn't air the show with something after it.
    /// </summary>
    public static (SqueezePlan Plan, PreviewSource Source)? PreviewFor(IReadOnlyList<ScheduleEntry> entries, string show, CreditSqueezeSection section,
        CreditSqueezeSection? everywhere, string channelId, string channelName, TimeZoneInfo zone, Func<MediaItem, TimeSpan?> creditsOf, TimeSpan length,
        TimeSpan assumedCredits = default)
    {
        (int Index, int Next, TimeSpan From)? assumed = null;
        (int Index, int Next)? fallback = null;
        for (int index = 0; index < entries.Count; index++)
        {
            ScheduleEntry entry = entries[index];
            if (!IsProgramme(entry, includeOutros: false) || entry.Item is not { Path.Length: > 0 } item
                || !string.Equals(ShowOf(item), show, StringComparison.OrdinalIgnoreCase) || NextProgramme(entries, index) is not { } next)
            {
                continue;
            }

            if (index + 1 < entries.Count && entries[index + 1] is { IsSlotOutro: true, Item.Path.Length: > 0 } outro && outro.Duration >= Shortest)
            {
                TimeSpan outroLength = outro.Duration < length ? outro.Duration : length;
                return (Build(entries, index + 1, next, outro.InPoint, outroLength, true, section, everywhere, channelId, channelName, zone), PreviewSource.Outro);
            }

            TimeSpan end = entry.InPoint + entry.Duration;
            if (creditsOf(item) is { } credits && credits >= entry.InPoint + LeastBefore && end - credits >= Shortest)
            {
                TimeSpan creditsLength = end - credits < length ? end - credits : length;
                return (Build(entries, index, next, credits, creditsLength, false, section, everywhere, channelId, channelName, zone), PreviewSource.Credits);
            }

            if (assumed is null && Assumed(entry, item, section.AssumedCredits(assumedCredits)) is { } guess && guess >= entry.InPoint + LeastBefore
                && end - guess >= Shortest)
            {
                assumed = (index, next, guess);
            }

            fallback ??= (index, next);
        }

        if (assumed is { } taken)
        {
            ScheduleEntry episodeAssumed = entries[taken.Index];
            TimeSpan stop = episodeAssumed.InPoint + episodeAssumed.Duration;
            TimeSpan assumedLength = stop - taken.From < length ? stop - taken.From : length;
            return (Build(entries, taken.Index, taken.Next, taken.From, assumedLength, false, section, everywhere, channelId, channelName, zone),
                PreviewSource.Assumed);
        }

        if (fallback is not { } last)
        {
            return null;
        }

        ScheduleEntry episode = entries[last.Index];
        TimeSpan finish = episode.InPoint + episode.Duration;
        TimeSpan from = finish - length > episode.InPoint ? finish - length : episode.InPoint;
        return (Build(entries, last.Index, last.Next, from, finish - from, false, section, everywhere, channelId, channelName, zone), PreviewSource.LastSeconds);
    }

    private static bool IsProgramme(ScheduleEntry entry, bool includeOutros) =>
        entry.Kind == ScheduleEntryKind.Program && entry.Item is not null && entry.GeneratedTemplate is null && !entry.IsSlotIntro
        && (includeOutros || !entry.IsSlotOutro);

    /// <summary>The next programme after entry <paramref name="index"/> (not another part of the same one, nor an intro or outro).</summary>
    public static int? NextProgramme(IReadOnlyList<ScheduleEntry> entries, int index)
    {
        string? current = entries[index].Item?.Id;
        for (int next = index + 1; next < entries.Count; next++)
        {
            if (IsProgramme(entries[next], includeOutros: false) && entries[next].Item!.Id != current)
            {
                return next;
            }
        }

        return null;
    }

    public static string? ShowBefore(IReadOnlyList<ScheduleEntry> entries, int index)
    {
        for (int before = index - 1; before >= 0; before--)
        {
            if (IsProgramme(entries[before], includeOutros: false))
            {
                return ShowOf(entries[before].Item!);
            }
        }

        return null;
    }

    public static string ShowOf(MediaItem item) => string.IsNullOrWhiteSpace(item.SeriesName) ? item.Title : item.SeriesName;

    private static SqueezeListing Listing(ScheduleEntry entry, TimeZoneInfo zone)
    {
        MediaItem item = entry.Item!;
        DateTime local = TimeZoneInfo.ConvertTime(entry.Start, zone).DateTime;
        string when = local.ToString(local.Minute == 0 ? "h tt" : "h:mm tt", CultureInfo.InvariantCulture);
        bool episode = !string.IsNullOrWhiteSpace(item.SeriesName);
        return new SqueezeListing(ShowOf(item), episode && item.Title != item.SeriesName ? item.Title : null, when,
            item.SeriesId ?? (Guid.TryParse(item.Id, out _) ? item.Id : null));
    }
}
