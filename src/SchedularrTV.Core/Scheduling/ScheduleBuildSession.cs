using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Query;

namespace SchedularrTV.Core.Scheduling;

public sealed class SchedulerConfigurationException(string message) : Exception(message);

/// <summary>
/// Per-build cache that turns <see cref="ContentSelector"/>s into stateful <see cref="MediaSequence"/>s.
/// One session spans a single channel build so two rules referencing the same collection share a cursor.
/// </summary>
internal sealed class ScheduleBuildSession(
    SchedulerConfig config,
    IMediaLibrary library,
    IPlaybackStateStore stateStore,
    ChannelConfig channel,
    TimeZoneInfo timeZone,
    DateTimeOffset planStart)
{
    private const int MaxCollectionDepth = 8;

    private readonly Dictionary<string, MediaSequence> _sequences = new(StringComparer.Ordinal);
    // Configuration is stable within a build. Keep the identity cache too: even a sequence-cache
    // hit otherwise parses collection queries and fingerprints their full text on every programme.
    private readonly Dictionary<ContentSelector, Dictionary<string, MediaSequence>> _selectorSequences = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, IReadOnlySet<string>> _titles = new(StringComparer.Ordinal);
    private readonly Dictionary<TimeSpan, ChannelOutlook> _outlooks = [];
    private readonly Dictionary<string, IReadOnlyList<MediaItem>> _queryResults = new(StringComparer.Ordinal);

    public ChannelConfig Channel => channel;

    public SchedulerConfig Config => config;

    public int InterstitialShowsInSlot { get; set; }

    /// <summary>Viewer-aware scheduling's factors by show title for this build; null when it isn't in effect.</summary>
    public IReadOnlyDictionary<string, double>? TitleFactors { get; init; }

    /// <summary>Each block's turn credits among its shows, while viewer-aware scheduling weights them.</summary>
    public Dictionary<string, double[]> SelectorCredits { get; } = new(StringComparer.Ordinal);

    /// <summary>The features that lay slots over the timeline (Archive Follow). Set by the engine.</summary>
    public IReadOnlyList<ITimelineContributor> Overlays { get; init; } = EngineModules.Default.Overlays;

    /// <summary>Overlay slots of the timeline being built, by their synthetic block.</summary>
    public IReadOnlyDictionary<BlockConfig, IOverlaySlot> OverlaySlots { get; private set; } = new Dictionary<BlockConfig, IOverlaySlot>();

    public async Task<Timeline> BuildTimelineAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        Timeline timeline = await Timeline.BuildAsync(config, channel, library, timeZone, start, end, Overlays, cancellationToken).ConfigureAwait(false);
        OverlaySlots = timeline.Slots;
        return timeline;
    }

    public async ValueTask<MediaSequence?> GetSequenceAsync(
        ContentSelector? selector,
        string scope,
        CancellationToken cancellationToken)
    {
        if (selector is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_selectorSequences.TryGetValue(selector, out var scopes)
            && scopes.TryGetValue(scope, out MediaSequence? resolved))
        {
            return resolved;
        }

        QueryExpression expression = ResolveExpression(selector, 0);
        PlaybackOrder order = ResolveOrder(selector, 0);
        TitleOrder? titleOrder = ResolveTitleOrder(selector, 0);
        string key = BuildKey(selector, scope, expression);

        if (_sequences.TryGetValue(key, out MediaSequence? cached))
        {
            CacheSelector(cached);
            return cached;
        }

        IReadOnlyList<MediaItem> items = await SearchAsync(expression, cancellationToken).ConfigureAwait(false);
        items = ApplyDurationBounds(items, selector);
        items = VersionCollapser.Collapse(items, ResolveVersions(selector, 0));

        PlaybackState? state = await stateStore.GetAsync(key, cancellationToken).ConfigureAwait(false);
        MediaSequence sequence;
        if (titleOrder is { } rotation)
        {
            sequence = BuildRotation(selector, items, rotation, order, key, state);
        }
        else if (order == PlaybackOrder.Drop || ResolveIncludes(selector, 0).Any(include => include.Order == PlaybackOrder.Drop))
        {
            // Drops are per show, so a collection with any drop show has to keep its shows apart.
            (Dictionary<string, TitleRotation.TitleRule> rules, _) = TitleRotation.RulesFrom(ResolveIncludes(selector, 0));
            sequence = MediaSequence.CreateMixed(key, items, order, rules, ResolveDrop(selector, 0), Drops, state);
        }
        else
        {
            sequence = MediaSequence.Create(key, order, items, state);
        }

        _sequences[key] = sequence;
        CacheSelector(sequence);
        return sequence;

        void CacheSelector(MediaSequence value)
        {
            if (scopes is null)
            {
                scopes = new Dictionary<string, MediaSequence>(StringComparer.Ordinal);
                _selectorSequences[selector] = scopes;
            }
            scopes[scope] = value;
        }
    }

    private MediaSequence BuildRotation(
        ContentSelector selector,
        IReadOnlyList<MediaItem> items,
        TitleOrder titleOrder,
        PlaybackOrder episodeOrder,
        string key,
        PlaybackState? state)
    {
        (Dictionary<string, TitleRotation.TitleRule> rules, List<string> order) =
            TitleRotation.RulesFrom(ResolveIncludes(selector, 0));

        // Viewer-aware scheduling: favourites earn more turns in the rotation, skipped shows fewer. Every title is
        // rescaled together (so an untouched title keeps its share relative to the others), and only when some
        // title in this pool actually moves, so pools viewers haven't judged are left exactly as they were.
        string[] titles = [.. items.Select(TitleRotation.TitleOf).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (ViewerWeights.Moves(TitleFactors, titles))
        {
            foreach (string title in titles)
            {
                rules.TryGetValue(title, out TitleRotation.TitleRule? rule);
                rules[title] = new TitleRotation.TitleRule(rule?.Order, ViewerWeights.Weight(rule?.Weight ?? 1, ViewerWeights.Of(TitleFactors, title)), rule?.Drop);
            }
        }

        return MediaSequence.CreateRotation(
            key,
            items,
            titleOrder,
            episodeOrder,
            rules,
            order,
            $"{config.Settings.Seed}|{key}", state,
            ResolveDrop(selector, 0),
            Drops);
    }

    /// <summary>Where and when this build happens, for working out which drop episodes are out.</summary>
    public DropContext Drops => _drops ??= new DropContext(timeZone, planStart);

    private DropContext? _drops;

    private DropConfig? ResolveDrop(ContentSelector selector, int depth)
    {
        if (selector.Drop is { } drop)
        {
            return drop;
        }

        if (depth <= MaxCollectionDepth
            && !string.IsNullOrWhiteSpace(selector.Collection)
            && config.GlobalCollections.TryGetValue(selector.Collection, out ContentSelector? global))
        {
            return ResolveDrop(global, depth + 1);
        }

        return null;
    }

    /// <summary>Ad-hoc pool lookup that bypasses sequencing (promos, one-off queries).</summary>
    public async ValueTask<IReadOnlyList<MediaItem>> SearchAsync(QueryExpression expression, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string key = expression.ToQueryString();
        if (_queryResults.TryGetValue(key, out var cached)) return cached;
        var items = await library.SearchAsync(expression, cancellationToken).ConfigureAwait(false);
        _queryResults[key] = items;
        return items;
    }

    /// <summary>
    /// Expands the channel's recurrence rules past the end of the window being built, so a promo can
    /// be checked against the slot it claims to advertise. Cached per horizon: the answer depends on
    /// the config rather than on the break asking.
    /// </summary>
    public async ValueTask<ChannelOutlook> GetOutlookAsync(TimeSpan horizon, CancellationToken cancellationToken)
    {
        if (_outlooks.TryGetValue(horizon, out ChannelOutlook? cached))
        {
            return cached;
        }

        DateTimeOffset end = planStart + horizon;
        // Built with the same overlays as the schedule, so trailers can name what an overlay slot will air.
        Timeline timeline = await Timeline.BuildAsync(config, channel, library, timeZone, planStart, end, Overlays, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<BlockOccurrence> blocks = timeline.Blocks;
        IReadOnlyList<AnchorOccurrence> anchors = timeline.Anchors;

        List<UpcomingAiring> airings = [];

        foreach (AnchorOccurrence anchor in anchors)
        {
            await AddAiringAsync(airings, anchor.Start, anchor.Anchor.AllSelectors(), cancellationToken).ConfigureAwait(false);
        }

        foreach (BlockOccurrence block in blocks)
        {
            await AddAiringAsync(airings, block.Start, block.Block.Shows, cancellationToken).ConfigureAwait(false);
        }

        HashSet<string> rotation = new(StringComparer.OrdinalIgnoreCase);
        if (channel.BaseRotation is { } baseRotation)
        {
            rotation.UnionWith(await TitlesOfAsync(baseRotation, cancellationToken).ConfigureAwait(false));
        }

        ChannelOutlook outlook = new([.. airings.OrderBy(a => a.At)], rotation);
        _outlooks[horizon] = outlook;
        return outlook;
    }

    private async ValueTask AddAiringAsync(
        List<UpcomingAiring> airings,
        DateTimeOffset at,
        IEnumerable<ContentSelector> selectors,
        CancellationToken cancellationToken)
    {
        HashSet<string> titles = new(StringComparer.OrdinalIgnoreCase);

        foreach (ContentSelector selector in selectors)
        {
            titles.UnionWith(await TitlesOfAsync(selector, cancellationToken).ConfigureAwait(false));
        }

        if (titles.Count > 0)
        {
            string day = TimeZoneInfo.ConvertTime(at, timeZone).DayOfWeek.ToString().ToLowerInvariant();
            airings.Add(new UpcomingAiring(at, day, titles));
        }
    }

    /// <summary>Every title a selector could play, keyed by the query so two rules sharing a pool cost one search.</summary>
    public async ValueTask<IReadOnlySet<string>> TitlesOfAsync(ContentSelector selector, CancellationToken cancellationToken)
    {
        QueryExpression expression;
        try
        {
            expression = ResolveExpression(selector, 0);
        }
        catch (SchedulerConfigurationException)
        {
            // A broken selector is reported where it is scheduled; it must not take promos down too.
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        string key = expression.ToQueryString();
        if (_titles.TryGetValue(key, out IReadOnlySet<string>? cached))
        {
            return cached;
        }

        IReadOnlyList<MediaItem> items = await SearchAsync(expression, cancellationToken).ConfigureAwait(false);
        HashSet<string> titles = new(items.Select(TitleRotation.TitleOf), StringComparer.OrdinalIgnoreCase);

        _titles[key] = titles;
        return titles;
    }

    public async ValueTask<MediaSequence> GetPoolSequenceAsync(
        QueryExpression expression,
        PlaybackOrder order,
        TimeSpan? minDuration,
        TimeSpan? maxDuration,
        string scope,
        CancellationToken cancellationToken)
    {
        // Temporal tags change the pool from day to day, so the resolved query is part of the key.
        string key = $"{channel.Id}|{scope}|{Fingerprint($"{expression.ToQueryString()}|{order}|{minDuration}|{maxDuration}")}";

        if (_sequences.TryGetValue(key, out MediaSequence? cached))
        {
            return cached;
        }

        IReadOnlyList<MediaItem> items = await SearchAsync(expression, cancellationToken).ConfigureAwait(false);

        if (minDuration is { } min)
        {
            items = items.Where(i => i.Duration >= min).ToList();
        }

        if (maxDuration is { } max)
        {
            items = items.Where(i => i.Duration <= max).ToList();
        }

        items = VersionCollapser.Collapse(items, null);

        PlaybackState? state = await stateStore.GetAsync(key, cancellationToken).ConfigureAwait(false);
        MediaSequence sequence = MediaSequence.Create(key, order, items, state);
        _sequences[key] = sequence;
        return sequence;
    }

    /// <summary>Saves every sequence's place together (one write of the state file for the whole channel).</summary>
    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        if (_sequences.Count == 0)
        {
            return;
        }

        await stateStore.SaveAllAsync([.. _sequences.Values.Select(sequence => sequence.ToState())], cancellationToken).ConfigureAwait(false);
    }

    public QueryExpression ResolveExpression(ContentSelector selector, int depth)
    {
        if (depth > MaxCollectionDepth)
        {
            throw new SchedulerConfigurationException($"Global collection reference cycle detected at '{selector.Collection}'.");
        }

        List<QueryExpression> pool = [];

        if (!string.IsNullOrWhiteSpace(selector.Collection))
        {
            if (!config.GlobalCollections.TryGetValue(selector.Collection, out ContentSelector? global))
            {
                throw new SchedulerConfigurationException($"Unknown global collection '{selector.Collection}'.");
            }

            pool.Add(ResolveExpression(global, depth + 1));
        }

        if (!string.IsNullOrWhiteSpace(selector.Query))
        {
            QueryExpression parsed = QueryParser.Parse(selector.Query);

            // A raw query names no titles, so the exclusion simply narrows the whole thing.
            pool.Add(selector.ExcludeSeasons.Count > 0
                ? QueryExpression.And([parsed, ContentIncludeCompiler.ExcludingSeasons(selector.ExcludeSeasons)])
                : parsed);
        }

        if (selector.Include.Count > 0)
        {
            pool.Add(ContentIncludeCompiler.Compile(selector.Include, selector.ExcludeSeasons));
        }

        if (pool.Count == 0)
        {
            throw new SchedulerConfigurationException("A content selector requires 'collection', 'query' or 'include'.");
        }

        // Several sources union together: a collection is everything it names plus anything it matches.
        QueryExpression baseExpression = QueryExpression.Or(pool);

        if (!string.IsNullOrWhiteSpace(selector.Filter))
        {
            QueryExpression filter = QueryParser.Parse(selector.Filter);

            // Library scope deliberately discards the collection so smart searches can reach
            // outside the block's own shows (spec 2.2 scoped layering).
            baseExpression = selector.FilterScope == FilterScope.Library
                ? filter
                : QueryExpression.And([baseExpression, filter]);
        }

        return baseExpression;
    }

    private PlaybackOrder ResolveOrder(ContentSelector selector, int depth)
    {
        if (selector.Order is { } order)
        {
            return order;
        }

        if (depth <= MaxCollectionDepth
            && !string.IsNullOrWhiteSpace(selector.Collection)
            && config.GlobalCollections.TryGetValue(selector.Collection, out ContentSelector? global))
        {
            return ResolveOrder(global, depth + 1);
        }

        return PlaybackOrder.Shuffle;
    }

    private TitleOrder? ResolveTitleOrder(ContentSelector selector, int depth)
    {
        if (selector.TitleOrder is { } titleOrder)
        {
            return titleOrder;
        }

        if (depth <= MaxCollectionDepth
            && !string.IsNullOrWhiteSpace(selector.Collection)
            && config.GlobalCollections.TryGetValue(selector.Collection, out ContentSelector? global))
        {
            return ResolveTitleOrder(global, depth + 1);
        }

        return null;
    }

    private VersionSelection? ResolveVersions(ContentSelector selector, int depth)
    {
        if (selector.Versions is { IsEmpty: false } versions)
        {
            return versions;
        }

        if (depth <= MaxCollectionDepth
            && !string.IsNullOrWhiteSpace(selector.Collection)
            && config.GlobalCollections.TryGetValue(selector.Collection, out ContentSelector? global))
        {
            return ResolveVersions(global, depth + 1);
        }

        return null;
    }

    /// <summary>
    /// The clip blocks configured for a title, with every clip resolved to a library item. Extras
    /// are ordinary items, so this is a lookup by path rather than anything the pool already found.
    /// </summary>
    public async ValueTask<IReadOnlyList<ResolvedBumper>> GetBumpersAsync(
        ContentSelector selector,
        string title,
        CancellationToken cancellationToken)
    {
        // A collection naming its own blocks for a title replaces the show's own, so one channel can
        // run a themed version without every other channel inheriting it.
        List<BumperBlock> blocks = ResolveIncludes(selector, 0)
            .Where(i => i.Bumpers.Count > 0 && string.Equals(i.Show, title, StringComparison.OrdinalIgnoreCase))
            .SelectMany(i => i.Bumpers)
            .ToList();

        if (blocks.Count == 0 && config.Titles.TryGetValue(title, out TitleDefaults? defaults))
        {
            blocks = defaults.Bumpers;
        }

        if (blocks.Count == 0)
        {
            return [];
        }

        List<ResolvedBumper> resolved = [];

        foreach (BumperBlock block in blocks)
        {
            List<ResolvedClip> clips = [];

            foreach (BumperClip clip in block.Clips)
            {
                MediaItem? item = await GetExtraAsync(clip.Path, cancellationToken).ConfigureAwait(false);

                if (item is not null)
                {
                    clips.Add(new ResolvedClip(item, clip.Episodes));
                }
            }

            if (clips.Count > 0)
            {
                resolved.Add(new ResolvedBumper(block, clips));
            }
        }

        return resolved;
    }

    private async ValueTask<MediaItem?> GetExtraAsync(string path, CancellationToken cancellationToken)
    {
        if (_extras.TryGetValue(path, out MediaItem? cached))
        {
            return cached;
        }

        IReadOnlyList<MediaItem> found = await library
            .SearchAsync(new FieldExpression("path", ComparisonOperator.Equals, [path]), cancellationToken)
            .ConfigureAwait(false);

        MediaItem? extra = found.FirstOrDefault(i => i.Duration > TimeSpan.Zero);
        _extras[path] = extra;
        return extra;
    }

    private readonly Dictionary<string, MediaItem?> _extras = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-title rules live on whichever selector actually names the titles.</summary>
    private List<ContentInclude> ResolveIncludes(ContentSelector selector, int depth)
    {
        List<ContentInclude> includes = [.. selector.Include];

        if (depth <= MaxCollectionDepth
            && !string.IsNullOrWhiteSpace(selector.Collection)
            && config.GlobalCollections.TryGetValue(selector.Collection, out ContentSelector? global))
        {
            includes.AddRange(ResolveIncludes(global, depth + 1));
        }

        return includes;
    }

    private static IReadOnlyList<MediaItem> ApplyDurationBounds(IReadOnlyList<MediaItem> items, ContentSelector selector)
    {
        if (selector.MinDuration is null && selector.MaxDuration is null)
        {
            return items;
        }

        return items
            .Where(i => (selector.MinDuration is not { } min || i.Duration >= min)
                && (selector.MaxDuration is not { } max || i.Duration <= max))
            .ToList();
    }

    private string BuildKey(ContentSelector selector, string scope, QueryExpression expression)
    {
        // Ordering is deliberately not part of the identity: it is still the same collection, so
        // changing how it is ordered should not lose the viewer's place in it.
        string identity = selector.StateKey ?? Fingerprint(
            $"{expression.ToQueryString()}|{selector.MinDuration}|{selector.MaxDuration}");
        return $"{channel.Id}|{scope}|{identity}";
    }

    private static string Fingerprint(string text) => DeterministicRandom.Hash(text).ToString("x16");
}
