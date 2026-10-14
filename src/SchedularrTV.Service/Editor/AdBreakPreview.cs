using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.Service.Editor;

public enum PreviewKind
{
    Show,
    Ad,
    Bumper,
    Filler,
    Promo,
    Padding
}

/// <summary>One thing that plays in the preview.</summary>
/// <param name="Note">What it is or why it's there, e.g. "Up Next" or "cut short to fit".</param>
public sealed record PreviewItem(PreviewKind Kind, string Label, TimeSpan Duration, string? Note = null);

/// <summary>One stretch of the preview: a part of the example show, or a break and what's in it.</summary>
public sealed record PreviewSegment(string Title, bool IsBreak, IReadOnlyList<PreviewItem> Items)
{
    public TimeSpan Duration => TimeSpan.FromTicks(Items.Sum(item => item.Duration.Ticks));
}

/// <summary>
/// An illustration of an ad break in a playout: an example half-hour slot whose show is split by two mid-rolls, with
/// each break filled from the ad break's real clips in the order a build plays them (opening promo, opening bumper,
/// Coming Up, ads with Up Next and Eventually mixed in, filler, closing bumper, closing promos). Picks are random rather
/// than following any channel's rotation, so it shows the shape of a break rather than what will actually air.
/// </summary>
public static class AdBreakPreview
{
    public static readonly TimeSpan Slot = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ShowLength = TimeSpan.FromMinutes(22);
    private static readonly TimeSpan MidRoll = TimeSpan.FromMinutes(2);
    private const int Parts = 3;
    private const string ExampleShow = "Example show";

    public static IReadOnlyList<PreviewSegment> Build(
        string name, AdBreakConfig adBreak, SchedulerSettings settings, IReadOnlyList<MediaItem> clips, DateTimeOffset now, int seed)
    {
        SchedulerConfig config = new() { Settings = settings };
        config.AdBreaks[name] = adBreak;
        ChannelConfig channel = new() { Id = "preview", Name = "Preview", AdBreaks = [name] };

        InterstitialsConfig breaks = SchedulingEngine.ResolvedBreaks(config, channel);
        (PromoConfig promos, List<string>? promoBrands) = SchedulingEngine.ResolvedPromos(config, channel);
        Pools pools = new(breaks, promos, promoBrands, settings, clips, now, new Random(seed));

        List<PreviewSegment> segments = [];
        TimeSpan part = TimeSpan.FromTicks(ShowLength.Ticks / Parts);
        TimeSpan endBreak = Slot - ShowLength - TimeSpan.FromTicks(MidRoll.Ticks * (Parts - 1));
        for (int i = 0; i < Parts; i++)
        {
            segments.Add(new PreviewSegment($"{ExampleShow}, part {i + 1} of {Parts}", false,
                [new PreviewItem(PreviewKind.Show, $"{ExampleShow} (part {i + 1})", part)]));

            bool last = i == Parts - 1;
            segments.Add(new PreviewSegment(
                last ? "Break before the next show" : $"Mid-roll {i + 1}",
                true,
                FillBreak(pools, last ? endBreak : MidRoll, endsShow: last, upNextHere: promos.UpNext.Placement == UpNextPlacement.AnyBreak ? i == Parts - 2 : last)));
        }

        segments.Add(new PreviewSegment("Next show", false, [new PreviewItem(PreviewKind.Show, "Next show", TimeSpan.FromMinutes(1), "Starts on the half hour")]));
        return segments;
    }

    private static List<PreviewItem> FillBreak(Pools pools, TimeSpan length, bool endsShow, bool upNextHere)
    {
        TimeSpan free = length;
        List<PreviewItem> head = [];
        List<PreviewItem> tail = [];
        List<PreviewItem> mixed = [];

        PreviewItem? Take(List<MediaItem> pool, PreviewKind kind, string? note, TimeSpan room)
        {
            List<MediaItem> fits = [.. pool.Where(item => item.Duration > TimeSpan.Zero && item.Duration <= room)];
            if (fits.Count == 0)
            {
                return null;
            }

            MediaItem pick = fits[pools.Random.Next(fits.Count)];
            free -= pick.Duration;
            return new PreviewItem(kind, Label(pick), pick.Duration, note);
        }

        void Add(List<PreviewItem> into, PreviewItem? item, bool first = false)
        {
            if (item is not null)
            {
                if (first) into.Insert(0, item); else into.Add(item);
            }
        }

        // Openers: the sign-off or "you're watching", then a bumper.
        Add(head, endsShow
            ? Take(pools.Spot(pools.Promos.ThisHasBeen), PreviewKind.Promo, "This Has Been", free / 3)
            : Take(pools.Spot(pools.Promos.YoureWatching), PreviewKind.Promo, "You're Watching", free / 3));
        Add(head, Take(pools.Opening, PreviewKind.Bumper, "Opening bumper", free / 3));

        // Closers, reserved before the ads: bumper, then Up Next and Now Playing (or Back To The Show).
        Add(tail, Take(pools.Closing, PreviewKind.Bumper, "Closing bumper", free / 3));
        if (endsShow)
        {
            if (upNextHere && pools.Promos.UpNext.Placement == UpNextPlacement.BeforeShow)
            {
                Add(tail, Take(pools.Spot(pools.Promos.UpNext), PreviewKind.Promo, "Up Next", free / 3));
            }

            Add(tail, Take(pools.Spot(pools.Promos.NowPlaying), PreviewKind.Promo, "Now Playing", free / 3));
        }
        else
        {
            Add(tail, Take(pools.Spot(pools.Promos.BackToTheShow), PreviewKind.Promo, "Back To The Show", free / 3));
        }

        // Coming Up and Later Today, straight after the openers, in the break before a new show.
        if (endsShow)
        {
            int added = 0;
            foreach ((List<MediaItem> pool, string label) in pools.Windows)
            {
                if (added >= pools.Promos.MaxPerBreak)
                {
                    break;
                }

                if (Take(pool, PreviewKind.Promo, label, free / 3) is { } promo)
                {
                    head.Add(promo);
                    added++;
                }
            }
        }

        // Mixed in among the ads: Up Next (when it can go in any break) and an Eventually trailer.
        if (upNextHere && pools.Promos.UpNext.Placement == UpNextPlacement.AnyBreak)
        {
            Add(mixed, Take(pools.Spot(pools.Promos.UpNext), PreviewKind.Promo, "Up Next", free / 3));
        }

        if (endsShow)
        {
            Add(mixed, Take(pools.Eventually, PreviewKind.Promo, "Eventually", free / 3));
        }

        // The ads, then filler for what's left, packed the way a build packs them.
        List<PreviewItem> middle = [];
        if (free > TimeSpan.Zero)
        {
            CommercialsConfig commercials = pools.Breaks.Commercials!;
            FillerTierConfig filler = pools.Breaks.Filler!;
            List<MediaItem> ads = Shuffled(pools.Ads, pools.Random);
            List<MediaItem> fill = filler.IsEnabled ? Shuffled(pools.Filler, pools.Random) : [];
            PackResult packed = KnapsackPacker.Pack(
                [.. ads.Take(256)], free, commercials.Granularity ?? TimeSpan.FromMilliseconds(250), commercials.MaxRepeatsPerBreak ?? 1,
                [.. fill.Take(128)],
                new FillerPackOptions { Trimmable = filler.IsTrimmable, StopBeforeEnd = filler.StopBeforeEnd ?? false, Loop = filler.Loop ?? false });

            foreach (PackedItem item in packed.Items)
            {
                middle.Add(new PreviewItem(item.IsFiller ? PreviewKind.Filler : PreviewKind.Ad, Label(item.Item), item.Duration,
                    item.IsFiller ? item.Trimmed ? "Filler, cut short to fit" : "Filler" : null));
            }

            free -= packed.Filled;
        }

        // Mix-ins land part way through the ads.
        foreach (PreviewItem item in mixed)
        {
            middle.Insert(middle.Count == 0 ? 0 : pools.Random.Next(1, middle.Count + 1), item);
        }

        if (free > TimeSpan.FromSeconds(1))
        {
            middle.Add(new PreviewItem(PreviewKind.Padding, "Black screen", free, "Nothing left that fits"));
        }

        return [.. head, .. middle, .. tail];
    }

    private static string Label(MediaItem item) =>
        item.Attributes.TryGetValue("show_id", out string? show) && !string.IsNullOrWhiteSpace(show) && !item.Title.Contains(show, StringComparison.OrdinalIgnoreCase)
            ? $"{item.Title} ({show})"
            : item.Title;

    private static List<MediaItem> Shuffled(IReadOnlyList<MediaItem> items, Random random)
    {
        List<MediaItem> list = [.. items];
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }

    /// <summary>The clips each part of a break can draw on, found once per preview.</summary>
    private sealed class Pools
    {
        private readonly IReadOnlyList<MediaItem> _clips;
        private readonly List<string>? _promoBrands;
        private readonly DateTimeOffset _now;

        public Pools(InterstitialsConfig breaks, PromoConfig promos, List<string>? promoBrands, SchedulerSettings settings,
            IReadOnlyList<MediaItem> clips, DateTimeOffset now, Random random)
        {
            Breaks = breaks;
            Promos = promos;
            Random = random;
            _clips = clips;
            _promoBrands = promoBrands;
            _now = now;

            CommercialsConfig commercials = breaks.Commercials!;
            Ads = [.. Find(InterstitialQueryBuilder.BuildCommercials(commercials, now, settings.TemporalTags))
                .Where(item => (commercials.MinItemDuration is not { } min || item.Duration >= min)
                               && (commercials.MaxItemDuration is not { } max || item.Duration <= max))];
            Filler = breaks.Filler is { IsEnabled: true } filler ? Find(InterstitialQueryBuilder.BuildFiller(filler, now, settings.TemporalTags)) : [];
            Opening = Find(AdBreakCompiler.Pool(breaks.OpeningBumpers ?? [], null, now, settings.TemporalTags));
            Closing = Find(AdBreakCompiler.Pool(breaks.ClosingBumpers ?? [], null, now, settings.TemporalTags));
            Windows = [.. promos.Windows.OrderBy(window => window.Within).Select(window =>
                (FromFolders(window.Folders).Concat(ByType(window.PromoTypes, null)).DistinctBy(item => item.Id).ToList(),
                 window.Within <= TimeSpan.FromHours(3) ? "Coming Up" : "Later Today"))];
            Eventually = promos.Eventually.Enabled
                ? [.. FromFolders(promos.Eventually.Folders).Concat(ByType(promos.Eventually.PromoTypes, promos.Eventually.Tag)).DistinctBy(item => item.Id)]
                : [];
        }

        public InterstitialsConfig Breaks { get; }

        public PromoConfig Promos { get; }

        public Random Random { get; }

        public List<MediaItem> Ads { get; }

        public List<MediaItem> Filler { get; }

        public List<MediaItem> Opening { get; }

        public List<MediaItem> Closing { get; }

        public List<(List<MediaItem> Pool, string Label)> Windows { get; }

        public List<MediaItem> Eventually { get; }

        /// <summary>The clips one promo spot can play, for any show.</summary>
        public List<MediaItem> Spot(PromoSpotConfig spot) =>
            !Promos.Enabled || !spot.Enabled ? [] : [.. FromFolders(spot.Folders).Concat(ByType(spot.PromoTypes, spot.Tag)).DistinctBy(item => item.Id)];

        private List<MediaItem> Find(QueryExpression query) => query is NoneExpression ? [] : [.. QueryEvaluator.Filter(_clips, query)];

        private IEnumerable<MediaItem> FromFolders(IReadOnlyList<PromoFolderSource>? folders) =>
            folders is not { Count: > 0 }
                ? []
                : Find(AdBreakCompiler.PromoPool(folders)).Select(item => AdBreakCompiler.Describe(item, folders, _now)).OfType<MediaItem>();

        /// <summary>Tag-based promos matched by <c>promo_type</c> or tag (and the brand folders and base query), for any show.</summary>
        private IEnumerable<MediaItem> ByType(IReadOnlyList<string> types, string? tag)
        {
            List<QueryExpression> markers = [];
            if (types.Count > 0)
            {
                markers.Add(new FieldExpression("promo_type", ComparisonOperator.In, types));
            }

            if (!string.IsNullOrWhiteSpace(tag))
            {
                markers.Add(InterstitialQueryBuilder.TagIs(tag));
            }

            if (markers.Count == 0)
            {
                return [];
            }

            List<QueryExpression> clauses = [QueryExpression.Or(markers)];
            if (!string.IsNullOrWhiteSpace(Promos.BaseQuery) && QueryParser.TryParse(Promos.BaseQuery, out QueryExpression baseQuery, out _))
            {
                clauses.Add(baseQuery);
            }

            if (_promoBrands is { Count: > 0 })
            {
                clauses.Add(QueryExpression.Or(_promoBrands.Select(InterstitialQueryBuilder.TagIs)));
            }

            return Find(QueryExpression.And(clauses));
        }
    }
}
