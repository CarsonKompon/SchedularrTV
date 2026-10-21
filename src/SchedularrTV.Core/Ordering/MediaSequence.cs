using System.Text;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;

namespace SchedularrTV.Core.Ordering;

/// <summary>
/// A stateful, order-aware cursor over a media pool. The scheduler takes items one at a time and may
/// constrain the take with a predicate (e.g. "must fit in the remaining gap"), which is why this is not
/// modelled as a plain <see cref="IEnumerable{T}"/>.
/// </summary>
public sealed class MediaSequence
{
    private readonly List<MediaItem> _pool;
    private readonly Dictionary<string, MediaItem> _byId;
    private readonly DeterministicRandom _random;
    private readonly List<string> _bag;
    private int _cursor;
    private string? _lastItemId;
    private List<(string Title, int Weight, MediaSequence Sequence)>? _titles;
    private Dictionary<string, long> _titleCredits = new(StringComparer.OrdinalIgnoreCase);

    // Drop: the cadence, and how many episodes (in release order) have had their new airing.
    // The cycle counts how many times the show has started over; _premiered counts within it.
    private DropPlan? _drop;
    private int _premiered;
    private int _cycle;
    private string? _lastPremieredId;
    private Dictionary<string, int>? _releaseIndex;

    // A collection mixing drop shows with ordinary ones (no title rotation): shows are picked in
    // proportion to how much each has available, and everything not on a drop shares one child.
    private bool _mixed;
    private DateTimeOffset? _at;

    /// <summary>Title key of the child holding a mixed collection's non-drop items.</summary>
    internal const string RestTitle = "\u0000rest";

    private MediaSequence(string key, PlaybackOrder order, List<MediaItem> pool, DeterministicRandom random, List<string> bag, int cursor, bool preordered)
    {
        Key = key;
        Order = order;
        _pool = pool;
        _byId = pool.GroupBy(p => p.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _random = random;
        _bag = bag;
        _cursor = cursor;
        IsPreordered = preordered;
    }

    public string Key { get; }

    public PlaybackOrder Order { get; }

    /// <summary>True when the pool was laid out by the caller and must be walked exactly as supplied.</summary>
    public bool IsPreordered { get; }

    public IReadOnlyList<MediaItem> Pool => _pool;

    public bool IsEmpty => _pool.Count == 0;

    /// <summary>
    /// When the next take airs. Only <see cref="PlaybackOrder.Drop"/> uses it, to know which episodes are out;
    /// set by the scheduler before each take (it is passed down to every show's own sequence).
    /// </summary>
    public DateTimeOffset? At
    {
        get => _at;
        set
        {
            _at = value;
            if (_titles is not null)
            {
                foreach (var title in _titles) title.Sequence.At = value;
            }
        }
    }

    /// <summary>
    /// True when the last item taken (by <see cref="TryTake"/> or <see cref="TryTakeSameTitle"/>) was a dropped
    /// episode's first airing, i.e. a new episode rather than a repeat.
    /// </summary>
    public bool LastTakeWasPremiere { get; private set; }

    /// <summary>The drop cadence, for a single show's <see cref="PlaybackOrder.Drop"/> sequence.</summary>
    public DropPlan? Drop => _drop;

    /// <summary>Every drop cadence in this sequence: its own, or its shows'.</summary>
    public IEnumerable<DropPlan> DropPlans
    {
        get
        {
            if (_drop is not null)
            {
                return new[] { _drop };
            }

            return _titles is null ? Array.Empty<DropPlan>() : _titles.SelectMany(title => title.Sequence.DropPlans);
        }
    }

    /// <summary>The shows in this sequence on <see cref="PlaybackOrder.Drop"/>, each with its own sequence.</summary>
    public IEnumerable<(string Title, MediaSequence Sequence)> DropShows =>
        _titles is null
            ? []
            : _titles.Where(title => title.Sequence._drop is not null).Select(title => (title.Title, title.Sequence));

    /// <summary>For a drop show: how many episodes (in release order, this cycle) have had their new airing.</summary>
    public int Premiered => _premiered;

    /// <summary>For a drop show: how many times it has started over.</summary>
    public int Cycle => _cycle;

    private string FingerprintOf() => Fingerprint(_pool);

    public static MediaSequence Create(string key, PlaybackOrder order, IEnumerable<MediaItem> items, PlaybackState? state) =>
        Create(key, order, Sort(items, order), state, preordered: false);

    /// <summary>
    /// One show's <see cref="PlaybackOrder.Drop"/> sequence: episodes are released in
    /// <see cref="DropConfig.ReleaseOrder"/> on <paramref name="config"/>'s cadence, each airs once as new as
    /// soon as it's out, and otherwise released episodes repeat in <see cref="DropConfig.Repeats"/> order.
    /// </summary>
    public static MediaSequence CreateDrop(string key, IEnumerable<MediaItem> items, DropConfig config, DropContext context, PlaybackState? state)
    {
        List<MediaItem> pool = Sort(items, config.ReleaseOrder == DropReleaseOrder.AirDate ? PlaybackOrder.Chronological : PlaybackOrder.SeasonEpisode);
        DropPlan plan = DropPlan.Create(config, context, state?.DropOrigin, pool, state?.DropReset);
        return CreateDrop(key, pool, plan, state, resuming: false);
    }

    /// <param name="resuming">A copy of a sequence mid-build, rather than one loaded at the start of a build.</param>
    private static MediaSequence CreateDrop(string key, List<MediaItem> pool, DropPlan plan, PlaybackState? state, bool resuming)
    {
        MediaSequence sequence = Create(key, PlaybackOrder.Drop, pool, state, preordered: false);
        sequence._drop = plan;

        int premiered;
        if (state?.Premiered is not { } saved)
        {
            // First time on a drop: everything out before the latest drop counts as already aired, and the
            // latest drop's episodes are new.
            premiered = plan.PremieredOnFirstBuild(pool.Count);
            sequence._cycle = plan.CycleAt(plan.Now);
        }
        else if (state.PoolFingerprint == sequence.FingerprintOf() || string.IsNullOrEmpty(state.LastPremieredId))
        {
            premiered = saved;
        }
        else
        {
            int index = pool.FindIndex(item => item.Id == state.LastPremieredId);
            premiered = index >= 0 ? index + 1 : saved;
        }

        if (state?.Premiered is not null)
        {
            sequence._cycle = Math.Max(0, state.DropCycle ?? 0);

            // A saved cycle the plan hasn't reached means the cadence changed (starting over switched off, say); carry
            // on from the plan's rather than waiting for it to catch up.
            int planned = plan.CycleAt(plan.Now);
            if (!resuming && sequence._cycle > planned)
            {
                sequence._cycle = planned;
            }
        }

        sequence._premiered = Math.Clamp(premiered, 0, pool.Count);
        sequence._lastPremieredId = state?.LastPremieredId;
        return sequence;
    }

    /// <summary>
    /// A collection without title rotation where some (or all) shows drop. Each drop show gets its own
    /// sequence; everything else keeps <paramref name="order"/> together, as it would in a flat pool.
    /// </summary>
    public static MediaSequence CreateMixed(string key, IReadOnlyList<MediaItem> items, PlaybackOrder order,
        IReadOnlyDictionary<string, TitleRotation.TitleRule> rules, DropConfig? collectionDrop, DropContext context, PlaybackState? state)
    {
        MediaSequence mixed = Create(key, PlaybackOrder.Chronological, Sort(items, PlaybackOrder.SeasonEpisode), state, preordered: true);
        mixed._mixed = true;
        mixed._titles = [];

        List<MediaItem> rest = [];
        foreach (var group in items.GroupBy(TitleRotation.TitleOf, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            rules.TryGetValue(group.Key, out var rule);
            if ((rule?.Order ?? order) != PlaybackOrder.Drop)
            {
                rest.AddRange(group);
                continue;
            }

            PlaybackState? titleState = state?.Titles?.GetValueOrDefault(group.Key);
            mixed._titles.Add((group.Key, Math.Max(1, rule?.Weight ?? 1),
                CreateDrop($"{key}|title|{group.Key}", group, rule?.Drop ?? collectionDrop ?? new DropConfig(), context, titleState)));
        }

        if (rest.Count > 0)
        {
            PlaybackOrder restOrder = order == PlaybackOrder.Drop ? PlaybackOrder.Shuffle : order;
            mixed._titles.Add((RestTitle, 1, Create($"{key}|rest", restOrder, rest, state?.Titles?.GetValueOrDefault(RestTitle))));
        }

        return mixed;
    }

    /// <summary>
    /// Wraps a running order the caller has already decided, e.g. one produced by
    /// <see cref="TitleRotation"/>. The pool is walked exactly as supplied.
    /// </summary>
    public static MediaSequence CreatePreordered(string key, IEnumerable<MediaItem> ordered, PlaybackState? state) =>
        Create(key, PlaybackOrder.Chronological, [.. ordered], state, preordered: true);

    public static MediaSequence CreateRotation(string key, IReadOnlyList<MediaItem> items, TitleOrder titleOrder,
        PlaybackOrder episodeOrder, IReadOnlyDictionary<string, TitleRotation.TitleRule> rules,
        IReadOnlyList<string> preferredOrder, string seed, PlaybackState? state,
        DropConfig? collectionDrop = null, DropContext? dropContext = null)
    {
        var legacy = TitleRotation.Build(items, titleOrder, episodeOrder, rules, preferredOrder, seed);
        var rotation = CreatePreordered(key, legacy, state);
        var previous = state?.Titles is { } saved
            ? new Dictionary<string, PlaybackState>(saved, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, PlaybackState>(StringComparer.OrdinalIgnoreCase);
        if (state is not null && state.Titles is null)
        {
            int played = rotation._cursor == 0 && legacy.Count > 0 && legacy[^1].Id == state.LastItemId
                ? legacy.Count : rotation._cursor;
            foreach (MediaItem item in legacy.Take(played))
            {
                previous[TitleRotation.TitleOf(item)] = new PlaybackState { Key = key, LastItemId = item.Id };
            }
        }
        rotation._titles = [];
        foreach (var group in TitleRotation.Arrange(items.GroupBy(TitleRotation.TitleOf, StringComparer.OrdinalIgnoreCase).ToList(),
                     titleOrder, preferredOrder, seed))
        {
            rules.TryGetValue(group.Key, out var rule);
            previous.TryGetValue(group.Key, out var titleState);
            PlaybackOrder titleOrderOf = rule?.Order ?? episodeOrder;
            var sequence = titleOrderOf == PlaybackOrder.Drop
                ? CreateDrop($"{key}|title|{group.Key}", group, rule?.Drop ?? collectionDrop ?? new DropConfig(),
                    dropContext ?? DropContext.Utc(DateTimeOffset.UtcNow), titleState)
                : Create($"{key}|title|{group.Key}", titleOrderOf, group, titleState);
            rotation._titles.Add((group.Key, Math.Max(1, rule?.Weight ?? 1), sequence));
            rotation._titleCredits[group.Key] = state?.TitleCredits?.GetValueOrDefault(group.Key) ?? 0;
        }
        return rotation;
    }

    private static MediaSequence Create(string key, PlaybackOrder order, List<MediaItem> pool, PlaybackState? state, bool preordered)
    {
        string fingerprint = Fingerprint(pool);
        bool samePool = state?.PoolFingerprint == fingerprint;

        // The PRNG stream is independent of the pool, so it always resumes.
        DeterministicRandom random = DeterministicRandom.Deserialize(state?.RandomState, DeterministicRandom.Hash(key));

        List<string> bag = samePool ? [.. state!.Bag] : [];
        int cursor = samePool ? state!.Cursor : Resume(pool, state?.LastItemId);

        var sequence = new MediaSequence(key, order, pool, random, bag, cursor, preordered)
        {
            _lastItemId = state?.LastItemId
        };

        if (!preordered && order == PlaybackOrder.Shuffle && bag.Count == 0)
        {
            sequence.RefillBag();

            // Send whatever just aired to the back rather than dropping it, so the cycle still plays
            // every item exactly once but does not open with a repeat.
            if (state?.LastItemId is { } last && sequence._bag.Remove(last))
            {
                sequence._bag.Add(last);
            }
        }

        return sequence;
    }

    /// <summary>Picks up immediately after the last item that played, wherever it now sits.</summary>
    private static int Resume(List<MediaItem> pool, string? lastItemId)
    {
        if (string.IsNullOrEmpty(lastItemId) || pool.Count == 0)
        {
            return 0;
        }

        int index = pool.FindIndex(p => p.Id == lastItemId);
        return index < 0 ? 0 : (index + 1) % pool.Count;
    }

    /// <summary>Deterministic sort for a single title's items, shared with <see cref="TitleRotation"/>.</summary>
    public static List<MediaItem> SortFor(IEnumerable<MediaItem> items, PlaybackOrder order) => Sort(items, order);

    /// <summary>
    /// Takes the next item honouring <see cref="Order"/>. When <paramref name="predicate"/> is supplied,
    /// non-matching candidates are skipped but left in the pool/bag so they play at the next opportunity.
    /// </summary>
    public bool TryTake(Func<MediaItem, bool>? predicate, out MediaItem item)
    {
        item = null!;
        LastTakeWasPremiere = false;
        if (_pool.Count == 0)
        {
            return false;
        }

        bool taken = _titles is not null ? TryTakeTitle(predicate, out item) : Order switch
        {
            PlaybackOrder.Drop when _drop is not null => TryTakeDrop(predicate, out item),
            PlaybackOrder.Random when !IsPreordered => TryTakeRandom(predicate, out item),
            PlaybackOrder.Shuffle when !IsPreordered => TryTakeFromBag(predicate, out item),
            _ => TryTakeOrdered(predicate, out item)
        };

        if (taken)
        {
            _lastItemId = item.Id;
        }

        return taken;
    }

    public bool TryTakeSameTitle(string title, Func<MediaItem, bool> predicate, out MediaItem item)
    {
        LastTakeWasPremiere = false;
        if (_titles is null)
        {
            return TryTake(candidate => string.Equals(TitleRotation.TitleOf(candidate), title, StringComparison.OrdinalIgnoreCase)
                && predicate(candidate), out item);
        }
        var selected = _titles.FirstOrDefault(candidate => string.Equals(candidate.Title, title, StringComparison.OrdinalIgnoreCase));
        item = null!;
        if (selected.Sequence is null)
        {
            // A mixed collection keeps its non-drop shows together.
            var rest = _titles.FirstOrDefault(candidate => candidate.Title == RestTitle);
            if (rest.Sequence is null || !rest.Sequence.TryTakeSameTitle(title, predicate, out item)) return false;
            _lastItemId = item.Id;
            return true;
        }
        if (!selected.Sequence.TryTake(predicate, out item)) return false;
        LastTakeWasPremiere = selected.Sequence.LastTakeWasPremiere;
        _lastItemId = item.Id;
        return true;
    }

    private bool TryTakeTitle(Func<MediaItem, bool>? predicate, out MediaItem item)
    {
        var eligible = _titles!.Where(title => predicate is null || title.Sequence.Pool.Any(predicate)).ToArray();

        // A show whose new episodes have a set air time gets the first chance at or after it, ahead of whoever's turn it
        // is. Otherwise its new episodes simply take its next normal turns.
        var premiering = eligible.FirstOrDefault(title => title.Sequence.Drop is { Config.HasAirTime: true }
            && title.Sequence.PendingPremiere(predicate) is not null);

        if (_mixed)
        {
            if (premiering.Sequence is not null && premiering.Sequence.TryTake(predicate, out item))
            {
                LastTakeWasPremiere = premiering.Sequence.LastTakeWasPremiere;
                return true;
            }

            return TryTakeProportional(eligible, predicate, out item);
        }

        var credits = eligible.ToDictionary(title => title.Title,
            title => _titleCredits[title.Title] + title.Weight, StringComparer.OrdinalIgnoreCase);
        var turns = eligible.OrderByDescending(title => credits[title.Title]);
        foreach (var title in premiering.Sequence is null ? turns : turns.OrderByDescending(title => title.Title == premiering.Title))
        {
            if (!title.Sequence.TryTake(predicate, out item)) continue;
            LastTakeWasPremiere = title.Sequence.LastTakeWasPremiere;
            foreach (var candidate in eligible) _titleCredits[candidate.Title] = credits[candidate.Title];
            _titleCredits[title.Title] -= eligible.Sum(candidate => (long)candidate.Weight);
            return true;
        }
        item = null!;
        return false;
    }

    /// <summary>
    /// Picks a show at random in proportion to how much it has to offer (a drop show counts only what's out),
    /// so a mixed collection plays like the flat pool it would otherwise be.
    /// </summary>
    private bool TryTakeProportional((string Title, int Weight, MediaSequence Sequence)[] eligible, Func<MediaItem, bool>? predicate, out MediaItem item)
    {
        List<(string Title, int Weight, MediaSequence Sequence)> left = [.. eligible];
        while (left.Count > 0)
        {
            long total = left.Sum(title => (long)title.Sequence.Available * title.Weight);
            if (total <= 0)
            {
                break;
            }

            long roll = (long)(_random.Next(int.MaxValue) / (double)int.MaxValue * total);
            int pick = 0;
            for (long seen = 0; pick < left.Count; pick++)
            {
                seen += (long)left[pick].Sequence.Available * left[pick].Weight;
                if (roll < seen) break;
            }

            var chosen = left[Math.Min(pick, left.Count - 1)];
            if (chosen.Sequence.TryTake(predicate, out item))
            {
                LastTakeWasPremiere = chosen.Sequence.LastTakeWasPremiere;
                return true;
            }

            left.Remove(chosen);
        }

        item = null!;
        return false;
    }

    /// <summary>How many items this sequence can play right now: a drop show counts only episodes that are out.</summary>
    internal int Available
    {
        get
        {
            if (_drop is not null)
            {
                (int released, int premiered) = DropNow();
                return Math.Min(_pool.Count, Math.Max(premiered, released));
            }

            return _titles is not null ? _titles.Sum(title => title.Sequence.Available) : _pool.Count;
        }
    }

    /// <summary>
    /// For a drop show at the next take: episodes out, and how many of them have premiered. Doesn't change anything, as
    /// promos ask about other times than the next take: once the show has started over since the last take, nothing
    /// of the new run has premiered yet; asked about a moment before a restart already taken, nothing new is due.
    /// </summary>
    private (int Released, int Premiered) DropNow()
    {
        DropRelease status = _drop!.StatusAt(_at ?? _drop.Now);
        int released = Math.Min(_pool.Count, status.Released);
        if (status.Cycle > _cycle)
        {
            return (released, 0);
        }

        if (status.Cycle < _cycle)
        {
            int premiered = Math.Min(_premiered, _pool.Count);
            return (premiered, premiered);
        }

        return (released, _premiered);
    }

    /// <summary>Catches up with a restart that has happened by the next take: the new run starts with nothing aired.</summary>
    private void SyncCycle()
    {
        if (_drop is null)
        {
            return;
        }

        int cycle = _drop.CycleAt(_at ?? _drop.Now);
        if (cycle <= _cycle)
        {
            return;
        }

        _cycle = cycle;
        _premiered = 0;
        _lastPremieredId = null;
        _bag.Clear();
        _cursor = 0;
    }

    /// <summary>The dropped episode waiting for its new airing (and fitting <paramref name="predicate"/>), if any.</summary>
    internal MediaItem? PendingPremiere(Func<MediaItem, bool>? predicate)
    {
        if (_titles is not null)
        {
            return _titles.Select(title => title.Sequence.PendingPremiere(predicate)).FirstOrDefault(item => item is not null);
        }

        if (_drop is null)
        {
            return null;
        }

        (int released, int premiered) = DropNow();
        if (premiered >= released)
        {
            return null;
        }

        MediaItem next = _pool[premiered];
        return predicate is null || predicate(next) ? next : null;
    }

    /// <summary>
    /// The next item would be a dropped episode's new airing. Used by promos to know whether what's up next is new.
    /// </summary>
    public bool WouldPremiere(MediaItem item) =>
        _titles is not null
            ? _titles.Any(title => title.Sequence.WouldPremiere(item))
            : _drop is not null && DropNow() is var (released, premiered) && premiered < released && _pool[premiered].Id == item.Id;

    private bool TryTakeDrop(Func<MediaItem, bool>? predicate, out MediaItem item)
    {
        SyncCycle();
        if (PendingPremiere(predicate) is { } premiere)
        {
            _premiered++;
            _lastPremieredId = premiere.Id;
            LastTakeWasPremiere = true;
            item = premiere;
            return true;
        }

        int aired = Math.Min(_premiered, _pool.Count);
        if (aired == 0)
        {
            // Nothing has dropped yet; let the rest of the collection have the slot.
            item = null!;
            return false;
        }

        switch (_drop!.Config.Repeats)
        {
            case PlaybackOrder.Random:
            {
                List<MediaItem> candidates = [.. _pool.Take(aired).Where(candidate => predicate is null || predicate(candidate))];
                if (candidates.Count == 0) break;
                item = candidates[_random.Next(candidates.Count)];
                return true;
            }

            case PlaybackOrder.Shuffle or PlaybackOrder.Drop:
            {
                if (!_bag.Any(id => Aired(id, aired)))
                {
                    _bag.Clear();
                    _bag.AddRange(_pool.Take(aired).Select(candidate => candidate.Id));
                    _random.Shuffle(_bag);
                }

                for (int i = 0; i < _bag.Count; i++)
                {
                    if (!Aired(_bag[i], aired))
                    {
                        _bag.RemoveAt(i--);
                        continue;
                    }

                    MediaItem candidate = _byId[_bag[i]];
                    if (predicate is null || predicate(candidate))
                    {
                        _bag.RemoveAt(i);
                        item = candidate;
                        return true;
                    }
                }

                break;
            }

            default:
                for (int probe = 0; probe < aired; probe++)
                {
                    int index = (_cursor + probe) % aired;
                    MediaItem candidate = _pool[index];
                    if (predicate is null || predicate(candidate))
                    {
                        _cursor = (index + 1) % aired;
                        item = candidate;
                        return true;
                    }
                }

                break;
        }

        item = null!;
        return false;
    }

    private bool Aired(string id, int aired)
    {
        _releaseIndex ??= _pool.Select((candidate, index) => (candidate.Id, index))
            .GroupBy(pair => pair.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.Ordinal);
        return _releaseIndex.TryGetValue(id, out int index) && index < aired;
    }

    public PlaybackState ToState() => new()
    {
        Key = Key,
        Cursor = _cursor,
        Bag = [.. _bag],
        RandomState = _random.Serialize(),
        PoolFingerprint = Fingerprint(_pool),
        LastItemId = _lastItemId,
        Titles = _titles?.ToDictionary(title => title.Title, title => title.Sequence.ToState(), StringComparer.OrdinalIgnoreCase),
        TitleCredits = _titles is null ? null : new(_titleCredits, StringComparer.OrdinalIgnoreCase),
        Premiered = _drop is null ? null : _premiered,
        LastPremieredId = _drop is null ? null : _lastPremieredId,
        DropOrigin = _drop?.PersistedOrigin,
        DropCycle = _drop is null || _cycle == 0 ? null : _cycle,
        DropReset = _drop?.Reset?.Copy()
    };

    /// <summary>An independent copy at the same place, for looking ahead without consuming anything.</summary>
    private MediaSequence Clone()
    {
        MediaSequence copy;
        if (_titles is not null)
        {
            copy = Create(Key, Order, _pool, ToState(), IsPreordered);
            copy._titles = _titles.Select(title => (title.Title, title.Weight, title.Sequence.Clone())).ToList();
            copy._titleCredits = new(_titleCredits, StringComparer.OrdinalIgnoreCase);
            copy._mixed = _mixed;
        }
        else if (_drop is not null)
        {
            copy = CreateDrop(Key, _pool, _drop, ToState(), resuming: true);
        }
        else
        {
            copy = Create(Key, Order, _pool, ToState(), IsPreordered);
        }

        copy._at = _at;
        return copy;
    }

    /// <summary>
    /// The next <paramref name="max"/> items in play order without consuming them. Used by the knapsack
    /// packer, which needs to see a candidate window before deciding which items to actually take.
    /// </summary>
    public IReadOnlyList<MediaItem> PreviewOrder(int max)
    {
        if (_pool.Count == 0 || max <= 0)
        {
            return [];
        }

        if (_titles is not null || _drop is not null)
        {
            var previewSequence = Clone();
            List<MediaItem> upcoming = [];
            for (int count = 0; count < Math.Min(max, _pool.Count) && previewSequence.TryTake(null, out var item); count++) upcoming.Add(item);
            return upcoming;
        }

        List<MediaItem> preview = new(Math.Min(max, _pool.Count));

        switch (Order)
        {
            case PlaybackOrder.Shuffle:
                if (_bag.Count == 0)
                {
                    RefillBag();
                }

                foreach (string id in _bag)
                {
                    if (preview.Count >= max)
                    {
                        break;
                    }

                    if (_byId.TryGetValue(id, out MediaItem? item))
                    {
                        preview.Add(item);
                    }
                }

                break;

            case PlaybackOrder.Random:
                preview.AddRange(_pool.Take(max));
                break;

            default:
                for (int i = 0; i < Math.Min(max, _pool.Count); i++)
                {
                    preview.Add(_pool[(_cursor + i) % _pool.Count]);
                }

                break;
        }

        return preview;
    }

    public IReadOnlyList<MediaItem> PreviewForGap(int max, Func<MediaItem, bool> predicate)
    {
        if (max <= 0) return [];
        if (_titles is not null || _drop is not null)
            return PreviewOrder(_pool.Count).Where(predicate).Take(max).ToArray();
        if (Order != PlaybackOrder.Shuffle)
        {
            // Walk only as far as the requested window needs, without copying the entire library.
            List<MediaItem> preview = new(Math.Min(max, _pool.Count));
            for (int i = 0; i < _pool.Count && preview.Count < max; i++)
            {
                MediaItem candidate = _pool[Order == PlaybackOrder.Random ? i : (_cursor + i) % _pool.Count];
                if (predicate(candidate)) preview.Add(candidate);
            }
            return preview;
        }
        if (Order != PlaybackOrder.Shuffle || IsPreordered)
            return PreviewOrder(_pool.Count).Where(predicate).Take(max).ToArray();

        if (_bag.Count == 0) RefillBag();
        List<MediaItem> candidates = _bag.Where(_byId.ContainsKey).Select(id => _byId[id])
            .Where(predicate).Take(max).ToList();
        if (candidates.Count > 0) return candidates;

        HashSet<string> remaining = new(_bag, StringComparer.Ordinal);
        List<string> recycled = _pool.Where(item => !remaining.Contains(item.Id) && predicate(item))
            .Select(item => item.Id).ToList();
        _random.Shuffle(recycled);
        _bag.AddRange(recycled);
        return recycled.Take(max).Select(id => _byId[id]).ToArray();
    }

    public bool TryTakeForGap(Func<MediaItem, bool> predicate, out MediaItem item)
    {
        if (TryTake(predicate, out item)) return true;
        if (Order != PlaybackOrder.Shuffle || IsPreordered) return false;
        MediaItem? candidate = PreviewForGap(1, predicate).FirstOrDefault();
        if (candidate is null) return false;
        Consume(candidate.Id);
        item = candidate;
        return true;
    }

    /// <summary>Marks a previously previewed item as played.</summary>
    public void Consume(string id)
    {
        _lastItemId = id;

        if (_titles is not null && _byId.TryGetValue(id, out var consumed))
        {
            var selected = _titles.FirstOrDefault(title => string.Equals(title.Title, TitleRotation.TitleOf(consumed), StringComparison.OrdinalIgnoreCase));
            if (selected.Sequence is null)
            {
                selected = _titles.FirstOrDefault(title => title.Title == RestTitle);
            }
            if (selected.Sequence is null) return;
            selected.Sequence.Consume(id);
            if (_mixed) return;
            foreach (var title in _titles) _titleCredits[title.Title] += title.Weight;
            _titleCredits[selected.Title] -= _titles.Sum(title => (long)title.Weight);
            return;
        }

        if (_drop is not null)
        {
            SyncCycle();
            if (_premiered < _pool.Count && _pool[_premiered].Id == id)
            {
                _premiered++;
                _lastPremieredId = id;
            }
            else
            {
                _bag.Remove(id);
                int index = _pool.FindIndex(p => p.Id == id);
                if (index >= 0 && _premiered > 0) _cursor = (index + 1) % _premiered;
            }

            return;
        }

        switch (Order)
        {
            case PlaybackOrder.Shuffle when !IsPreordered:
                _bag.Remove(id);
                if (_bag.Count == 0)
                {
                    RefillBag();
                }

                break;

            case PlaybackOrder.Random when !IsPreordered:
                // Repeats are allowed; nothing to track.
                break;

            default:
                int index = _pool.FindIndex(p => p.Id == id);
                if (index >= 0)
                {
                    _cursor = (index + 1) % _pool.Count;
                }

                break;
        }
    }

    private bool TryTakeOrdered(Func<MediaItem, bool>? predicate, out MediaItem item)
    {
        for (int probe = 0; probe < _pool.Count; probe++)
        {
            int index = (_cursor + probe) % _pool.Count;
            MediaItem candidate = _pool[index];
            if (predicate is null || predicate(candidate))
            {
                _cursor = (index + 1) % _pool.Count;
                item = candidate;
                return true;
            }
        }

        item = null!;
        return false;
    }

    private bool TryTakeRandom(Func<MediaItem, bool>? predicate, out MediaItem item)
    {
        List<MediaItem> candidates = predicate is null ? _pool : _pool.Where(predicate).ToList();
        if (candidates.Count == 0)
        {
            item = null!;
            return false;
        }

        item = candidates[_random.Next(candidates.Count)];
        return true;
    }

    private bool TryTakeFromBag(Func<MediaItem, bool>? predicate, out MediaItem item)
    {
        if (_bag.Count == 0)
        {
            RefillBag();
        }

        for (int i = 0; i < _bag.Count; i++)
        {
            if (!_byId.TryGetValue(_bag[i], out MediaItem? candidate))
            {
                // Item disappeared from the library mid-cycle.
                _bag.RemoveAt(i--);
                continue;
            }

            if (predicate is null || predicate(candidate))
            {
                _bag.RemoveAt(i);
                item = candidate;
                return true;
            }
        }

        // Nothing left in this cycle satisfies the predicate. Refilling here would replay items that
        // already aired, so report failure and let the caller fall back to micro-fill.
        item = null!;
        return false;
    }

    private void RefillBag()
    {
        _bag.Clear();
        _bag.AddRange(_pool.Select(p => p.Id));
        _random.Shuffle(_bag);
    }

    private static List<MediaItem> Sort(IEnumerable<MediaItem> items, PlaybackOrder order)
    {
        // Every mode sorts deterministically first (including Random/Shuffle) so the persisted
        // cursor/bag and the pool fingerprint are stable regardless of provider result ordering.
        IEnumerable<MediaItem> sorted = order switch
        {
            PlaybackOrder.Chronological => items
                .OrderBy(i => i.PremiereDate ?? DateTimeOffset.MaxValue)
                .ThenBy(i => i.SeasonNumber ?? int.MaxValue)
                .ThenBy(i => i.EpisodeNumber ?? int.MaxValue)
                .ThenBy(i => i.SortKey, StringComparer.Ordinal),
            PlaybackOrder.SeasonEpisode or PlaybackOrder.Drop => items
                .OrderBy(i => i.SeriesName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.SeasonNumber ?? int.MaxValue)
                .ThenBy(i => i.EpisodeNumber ?? int.MaxValue)
                .ThenBy(i => i.SortKey, StringComparer.Ordinal),
            _ => items.OrderBy(i => i.SortKey, StringComparer.Ordinal)
        };

        return sorted.ToList();
    }

    private static string Fingerprint(IReadOnlyList<MediaItem> pool)
    {
        var sb = new StringBuilder(pool.Count * 8);
        foreach (MediaItem item in pool)
        {
            sb.Append(item.Id).Append(';');
        }

        return DeterministicRandom.Hash(sb.ToString()).ToString("x16");
    }
}
