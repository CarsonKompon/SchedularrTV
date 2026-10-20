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
    string TextFont = "Arial",
    IReadOnlyList<SqueezePromo>? MakeKinds = null,
    string? ChannelId = null,
    string? BlockName = null,
    DateTimeOffset NextStart = default,
    IReadOnlyList<string>? MakeHeadings = null,
    TimeSpan MaxExtra = default,
    bool FitToPromo = false,
    TimeSpan Extra = default,
    SqueezePanel Panel = SqueezePanel.Promo,
    IReadOnlyList<SqueezeClip>? Commercials = null)
{
    /// <summary>How long the squeeze runs: its credits, then (with <see cref="Extra"/>) the rest of its promo, full screen.</summary>
    public TimeSpan Total => Length + Extra;

    /// <summary>
    /// With no promo found in the schedule: the kinds (in the order to try) to have the channel's generated promos make
    /// one of for the panel, if it has templates for them. Null or empty for none.
    /// </summary>
    public bool WantsMadePromo => PromoPath is null && MakeKinds is { Count: > 0 } && ChannelId is not null;

    /// <summary>
    /// The credits' shape as they're shown (width ÷ height: 1.333 for 4:3), read from the file as it's made; null (taken as
    /// widescreen) until then. Their box is made that shape, so a 4:3 show's credits aren't shrunk into a 16:9 box with bars.
    /// </summary>
    public double? CreditsAspect { get; init; }

    /// <summary>The panel's promo (or commercials') shape, read the same way; its picture slot is made that shape.</summary>
    public double? PromoAspect { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SqueezeCanvas>? Canvases { get; init; }
    public CreditsRecognition Recognition { get; init; } = new();
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool CanvasCrop { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CreditsOcrOptions? CreditsOcr { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? CreditsOcrSourceStamp { get; init; }

    /// <summary>Resolved just before rendering; the source/options above identify this data in the render cache.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public CreditsOcrResult? OcrTranscript { get; init; }

    public SqueezeLayout Layout(int width = 1280, int height = 720) =>
        SqueezeLayout.Of(Style, Side, SplitPercent, width, height, CreditsFit, CreditsText, PanelText,
            CreditsAspect ?? SqueezeLayout.Widescreen, PromoAspect ?? SqueezeLayout.Widescreen);
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

        // The section's own look, or one of its templates, picked for this airing.
        SqueezeLook look = section.LookFor(channelId, entries[index].Start);
        List<SqueezeListing> later = [];
        if (look.ShowLater)
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

        // Commercials only: no promo is looked for.
        bool hasPanel = !look.UseCanvas || look.Canvases.Any(canvas => canvas.Layers.Any(layer => layer.Video?.Episode == SqueezeCanvas.Panel));
        IReadOnlyList<SqueezePromo> wantedPromos = !hasPanel || look.Panel == SqueezePanel.Commercials ? Array.Empty<SqueezePromo>() : look.Promos;
        (ScheduleEntry? promoEntry, SqueezePromo? promoKind, bool pending) = PickPromoEntry(entries, index, next, wantedPromos, channelId);
        IReadOnlyList<SqueezeClip>? commercials = !hasPanel || look.Panel == SqueezePanel.Promo ? null : CommercialPool(entries, index, next, channelId);
        MediaItem? promo = pending ? null : promoEntry?.Item;

        // The panel says what the promo in it is for: the next programme, or (Coming Up, Eventually) the airing it promotes.
        SqueezeListing nextListing = Listing(entries[next], zone);
        string heading = look.HeadingFor(null);
        if (promo is not null && promoKind is { } kind)
        {
            heading = look.HeadingFor(kind);
            nextListing = Subject(entries, promoEntry!, kind, zone, entries[index].Start) ?? nextListing;
        }

        string? ending = whole ? ShowBefore(entries, index) : ShowOf(item);
        SqueezeLayoutChoice layout = section.LayoutFor(ending, everywhere, look);

        // No promo of the wanted kinds ready nearby: the channel's generated promos may make one (tried in a seeded order).
        // One the schedule is still making is waited for first (see Pending), then made here if it isn't ready in time.
        IReadOnlyList<SqueezePromo>? make = null;
        if (promo is null && wantedPromos.Count > 0)
        {
            DeterministicRandom random = DeterministicRandom.FromSeedText(channelId, entries[index].Start.ToString("O"), "credit-squeeze-make");
            make = [.. wantedPromos.Distinct().OrderBy(_ => random.Next(10000))];
        }

        return new SqueezePlan(index, from, whole, new SqueezeJob(item.Path!, from, length, nextListing, later, channelName,
            heading, look.AccentColor, look.BackgroundColor, Math.Clamp(look.CreditsVolume, 0, 2), look.Animate,
            layout.Side, Math.Clamp(layout.SplitPercent, SqueezeLayout.MinSplit, SqueezeLayout.MaxSplit), promo?.Path, promo?.Duration ?? TimeSpan.Zero,
            layout.Style, string.IsNullOrWhiteSpace(look.BackgroundPath) ? null : look.BackgroundPath.Trim(),
            layout.CreditsFit, layout.CreditsText, layout.PanelFit, layout.PanelText,
            layout.CreditsText || look.UseCanvas ? ending : null,
            layout.CreditsText ? look.CreditsCaption : null,
            layout.CreditsText && !string.IsNullOrWhiteSpace(look.CreditsLine) ? look.CreditsLine.Trim() : null,
            string.IsNullOrWhiteSpace(look.HeadingFont) ? "Arial" : look.HeadingFont,
            string.IsNullOrWhiteSpace(look.TextFont) ? "Arial" : look.TextFont,
            make, make is null ? null : channelId, make is null ? null : entries[index].BlockName, make is null ? default : entries[next].Start,
            make?.Select(made => look.HeadingFor(made)).ToList(),
            BreakRoom(entries, index, next), Panel: look.Panel, Commercials: commercials)
            { Canvases = look.UseCanvas ? look.Canvases.Select(canvas => canvas.Copy()).ToList() : null, Recognition = look.Recognition.Normalized() }, pending);
    }

    // How far ahead commercials for the panel are gathered from, and how many.
    private static readonly TimeSpan CommercialsWithin = TimeSpan.FromHours(6);
    private const int MostCommercials = 40;

    /// <summary>
    /// Commercials the panel may play, in a seeded order: ones the channel airs in its breaks over the next few hours
    /// (not the break straight after the credits, which would show them twice in a row), each once.
    /// </summary>
    public static IReadOnlyList<SqueezeClip> CommercialPool(IReadOnlyList<ScheduleEntry> entries, int index, int next, string channelId)
    {
        DateTimeOffset from = entries[index].Start;
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<SqueezeClip> pool = [];
        for (int other = next; other < entries.Count && pool.Count < MostCommercials; other++)
        {
            ScheduleEntry candidate = entries[other];
            if (candidate.Start - from > CommercialsWithin)
            {
                break;
            }

            if (candidate.Kind == ScheduleEntryKind.Commercial && candidate.Item is { Path.Length: > 0 } ad && ad.Duration > TimeSpan.FromSeconds(2)
                && seen.Add(ad.Path))
            {
                pool.Add(new SqueezeClip(ad.Path, ad.Duration));
            }
        }

        DeterministicRandom random = DeterministicRandom.FromSeedText(channelId, from.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), "credit-squeeze", "ads");
        return [.. pool.OrderBy(_ => random.Next(1_000_000))];
    }

    /// <summary>
    /// Commercials the channel airs in its breaks over the few hours from <paramref name="from"/>, in a seeded order: for a
    /// preview made from a stand-in schedule (an episode and its outro from the library), which has no breaks of its own.
    /// </summary>
    public static IReadOnlyList<SqueezeClip> CommercialsFrom(IReadOnlyList<ScheduleEntry> entries, DateTimeOffset from, string channelId)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        int start = 0;
        while (start < entries.Count - 1 && entries[start].End <= from)
        {
            start++;
        }

        return CommercialPool(entries, start, start, channelId);
    }

    /// <summary>
    /// Commercials from <paramref name="pool"/> (in its order) for <paramref name="room"/>: each that still fits, until
    /// what's left is too short for any.
    /// </summary>
    public static List<SqueezeClip> FillWith(IReadOnlyList<SqueezeClip>? pool, TimeSpan room)
    {
        List<SqueezeClip> picked = [];
        if (pool is null)
        {
            return picked;
        }

        foreach (SqueezeClip ad in pool)
        {
            if (ad.Length <= room)
            {
                picked.Add(ad);
                room -= ad.Length;
            }

            if (room < TimeSpan.FromSeconds(5))
            {
                break;
            }
        }

        return picked;
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
        (ScheduleEntry? picked, _, bool pending) = PickPromoEntry(entries, index, next, kinds, channelId);
        return (pending ? null : picked?.Item, pending);
    }

    /// <summary>As <see cref="PickPromo"/>, with the promo's entry in the schedule and its kind.</summary>
    public static (ScheduleEntry? Entry, SqueezePromo? Kind, bool Pending) PickPromoEntry(IReadOnlyList<ScheduleEntry> entries, int index, int next,
        IReadOnlyList<SqueezePromo> kinds, string channelId)
    {
        if (kinds.Count == 0)
        {
            return (null, null, false);
        }

        ScheduleEntry entry = entries[index];
        DateTimeOffset nextStart = entries[next].Start;
        List<(ScheduleEntry Entry, SqueezePromo Kind)> candidates = [];
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

            SqueezePromo? kind = candidate.Kind switch
            {
                ScheduleEntryKind.UpNextPromo when kinds.Contains(SqueezePromo.UpNext) && candidate.Start <= nextStart => SqueezePromo.UpNext,
                ScheduleEntryKind.NowPlayingPromo when kinds.Contains(SqueezePromo.NowPlaying) && candidate.Start <= nextStart => SqueezePromo.NowPlaying,
                ScheduleEntryKind.Promo when kinds.Contains(SqueezePromo.ComingUp) && ahead <= ComingUpWithin => SqueezePromo.ComingUp,
                ScheduleEntryKind.EventuallyPromo when kinds.Contains(SqueezePromo.Eventually) => SqueezePromo.Eventually,
                _ => null
            };
            if (kind is { } wanted && item.Duration > TimeSpan.FromSeconds(2))
            {
                candidates.Add((candidate, wanted));
            }
        }

        if (candidates.Count == 0)
        {
            return (null, null, false);
        }

        (ScheduleEntry picked, SqueezePromo pickedKind) = candidates[DeterministicRandom
            .FromSeedText(channelId, entry.Start.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), "credit-squeeze", "promo")
            .Next(candidates.Count)];
        return (picked, pickedKind, picked.Item!.Path is not { Length: > 0 });
    }

    /// <summary>
    /// What a Coming Up or Eventually promo is for, as the panel lists it: the airing it targets (a generated promo's
    /// target time, or a clip's show next on after it), else its predicted show at its target time. Null when it can't
    /// be told, or for kinds that promote the next programme anyway.
    /// </summary>
    public static SqueezeListing? Subject(IReadOnlyList<ScheduleEntry> entries, ScheduleEntry promo, SqueezePromo kind, TimeZoneInfo zone, DateTimeOffset from)
    {
        if (kind is not (SqueezePromo.ComingUp or SqueezePromo.Eventually))
        {
            return null;
        }

        if (promo.GeneratedTargetAt is { } at)
        {
            ScheduleEntry? airing = entries.FirstOrDefault(entry => IsProgramme(entry, includeOutros: false) && entry.Start >= at - TimeSpan.FromMinutes(1)
                && (string.IsNullOrWhiteSpace(promo.GeneratedSubject) || string.Equals(ShowOf(entry.Item!), promo.GeneratedSubject, StringComparison.OrdinalIgnoreCase)));
            return airing is not null && airing.Start - at < TimeSpan.FromHours(2)
                ? Listing(airing, zone, from)
                : promo.GeneratedSubject is { Length: > 0 } subject ? new SqueezeListing(subject, null, When(at, zone, from), null) : null;
        }

        if (promo.PromoTargetId is { Length: > 0 } target)
        {
            ScheduleEntry? airing = entries.FirstOrDefault(entry => entry.Start >= promo.Start && IsProgramme(entry, includeOutros: false)
                && (string.Equals(entry.Item!.SeriesId, target, StringComparison.OrdinalIgnoreCase) || string.Equals(entry.Item.Id, target, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ShowOf(entry.Item), target, StringComparison.OrdinalIgnoreCase)));
            return airing is null ? null : Listing(airing, zone, from);
        }

        return null;
    }

    /// <summary>
    /// The schedule with each plan's credits replaced by its squeeze (<paramref name="rendered"/> gives a plan's file, or
    /// null while it isn't made yet, leaving those credits as they are). Every entry keeps its place; nothing moves.
    /// </summary>
    public static List<ScheduleEntry> Apply(IReadOnlyList<ScheduleEntry> entries, IReadOnlyList<SqueezePlan> plans, Func<SqueezePlan, string?> rendered,
        Func<SqueezePlan, TimeSpan>? extraOf = null)
    {
        Dictionary<int, SqueezePlan> byIndex = plans.ToDictionary(plan => plan.Index);

        // A squeeze that runs on past its credits (its promo plays to the end) takes that time from the end of the break
        // after it: the last clips there are cut short, or dropped, so the next programme still starts on time.
        Dictionary<int, TimeSpan> extras = [];
        Dictionary<int, TimeSpan?> breakChanges = [];
        foreach (SqueezePlan plan in plans)
        {
            TimeSpan extra = extraOf?.Invoke(plan) ?? TimeSpan.Zero;
            if (extra <= TimeSpan.Zero || rendered(plan) is null || NextProgramme(entries, plan.Index) is not { } next)
            {
                continue;
            }

            extra = extra < BreakRoom(entries, plan.Index, next) ? extra : BreakRoom(entries, plan.Index, next);
            if (extra <= TimeSpan.Zero)
            {
                continue;
            }

            extras[plan.Index] = extra;
            TimeSpan remaining = extra;
            for (int at = next - 1; at > plan.Index && remaining > TimeSpan.Zero; at--)
            {
                if (entries[at].Kind == ScheduleEntryKind.Program)
                {
                    continue;
                }

                if (entries[at].Duration <= remaining)
                {
                    breakChanges[at] = null;
                    remaining -= entries[at].Duration;
                }
                else
                {
                    breakChanges[at] = entries[at].Duration - remaining;
                    remaining = TimeSpan.Zero;
                }
            }
        }

        List<ScheduleEntry> result = new(entries.Count + plans.Count);
        TimeSpan shift = TimeSpan.Zero;
        for (int index = 0; index < entries.Count; index++)
        {
            ScheduleEntry entry = shift == TimeSpan.Zero ? entries[index] : entries[index] with { Start = entries[index].Start + shift };
            if (breakChanges.TryGetValue(index, out TimeSpan? kept))
            {
                if (kept is not { } duration)
                {
                    shift -= entry.Duration;
                    continue;
                }

                shift -= entry.Duration - duration;
                result.Add(entry with
                {
                    Duration = duration, IsTrimmed = true, SourceDuration = entry.SourceDuration ?? entry.Item?.Duration ?? entry.Duration
                });
                continue;
            }

            if (!byIndex.TryGetValue(index, out SqueezePlan? plan) || rendered(plan) is not { } file || entry.Item is not { } item)
            {
                result.Add(entry);
                continue;
            }

            TimeSpan over = extras.GetValueOrDefault(index);
            shift += over;

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
                Duration = plan.Job.Length + over,
                Path = file,
                Attributes = new Dictionary<string, string> { ["credit_squeeze.source"] = item.Id }
            };

            // Folded into the programme's guide listing like an outro (and kept clear of screen bugs).
            ScheduleEntry squeeze = entry with
            {
                Start = entry.Start + (plan.From - entry.InPoint),
                Duration = plan.Job.Length + over,
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

    /// <summary>
    /// How far a squeeze of entry <paramref name="index"/> may run past its credits: the clips in the break before the
    /// next programme (<paramref name="next"/>), less a second, as that's where the time comes from.
    /// </summary>
    public static TimeSpan BreakRoom(IReadOnlyList<ScheduleEntry> entries, int index, int next)
    {
        TimeSpan room = TimeSpan.Zero;
        for (int at = index + 1; at < next; at++)
        {
            if (entries[at].Kind != ScheduleEntryKind.Program)
            {
                room += entries[at].Duration;
            }
        }

        return room > TimeSpan.FromSeconds(1) ? room - TimeSpan.FromSeconds(1) : TimeSpan.Zero;
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

    private static SqueezeListing Listing(ScheduleEntry entry, TimeZoneInfo zone, DateTimeOffset? from = null)
    {
        MediaItem item = entry.Item!;
        bool episode = !string.IsNullOrWhiteSpace(item.SeriesName);
        return new SqueezeListing(ShowOf(item), episode && item.Title != item.SeriesName ? item.Title : null, When(entry.Start, zone, from ?? entry.Start),
            // Whichever artwork source owns the id has its pictures; one none owns (commercials, say) just has none.
            item.SeriesId ?? item.Id);
    }

    /// <summary>A time as the viewer reads it ("8:30 PM", "9 PM"), with the day ("Sat 4 PM") when it isn't the same day as <paramref name="from"/>.</summary>
    public static string When(DateTimeOffset at, TimeZoneInfo zone, DateTimeOffset from)
    {
        DateTime local = TimeZoneInfo.ConvertTime(at, zone).DateTime;
        string time = local.ToString(local.Minute == 0 ? "h tt" : "h:mm tt", CultureInfo.InvariantCulture);
        return local.Date == TimeZoneInfo.ConvertTime(from, zone).Date ? time : $"{local.ToString("ddd", CultureInfo.InvariantCulture)} {time}";
    }
}
