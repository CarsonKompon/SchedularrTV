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

    public static MediaSequence Create(string key, PlaybackOrder order, IEnumerable<MediaItem> items, PlaybackState? state) =>
        Create(key, order, Sort(items, order), state, preordered: false);

    /// <summary>
    /// Wraps a running order the caller has already decided, e.g. one produced by
    /// <see cref="TitleRotation"/>. The pool is walked exactly as supplied.
    /// </summary>
    public static MediaSequence CreatePreordered(string key, IEnumerable<MediaItem> ordered, PlaybackState? state) =>
        Create(key, PlaybackOrder.Chronological, [.. ordered], state, preordered: true);

    public static MediaSequence CreateRotation(string key, IReadOnlyList<MediaItem> items, TitleOrder titleOrder,
        PlaybackOrder episodeOrder, IReadOnlyDictionary<string, TitleRotation.TitleRule> rules,
        IReadOnlyList<string> preferredOrder, string seed, PlaybackState? state)
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
            var sequence = Create($"{key}|title|{group.Key}", rule?.Order ?? episodeOrder, group, titleState);
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
        if (_pool.Count == 0)
        {
            return false;
        }

        bool taken = _titles is not null ? TryTakeTitle(predicate, out item) : Order switch
        {
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
        if (_titles is null)
        {
            return TryTake(candidate => string.Equals(TitleRotation.TitleOf(candidate), title, StringComparison.OrdinalIgnoreCase)
                && predicate(candidate), out item);
        }
        var selected = _titles.FirstOrDefault(candidate => string.Equals(candidate.Title, title, StringComparison.OrdinalIgnoreCase));
        item = null!;
        if (selected.Sequence is null || !selected.Sequence.TryTake(predicate, out item)) return false;
        _lastItemId = item.Id;
        return true;
    }

    private bool TryTakeTitle(Func<MediaItem, bool>? predicate, out MediaItem item)
    {
        var eligible = _titles!.Where(title => predicate is null || title.Sequence.Pool.Any(predicate)).ToArray();
        var credits = eligible.ToDictionary(title => title.Title,
            title => _titleCredits[title.Title] + title.Weight, StringComparer.OrdinalIgnoreCase);
        foreach (var title in eligible.OrderByDescending(title => credits[title.Title]))
        {
            if (!title.Sequence.TryTake(predicate, out item)) continue;
            foreach (var candidate in eligible) _titleCredits[candidate.Title] = credits[candidate.Title];
            _titleCredits[title.Title] -= eligible.Sum(candidate => (long)candidate.Weight);
            return true;
        }
        item = null!;
        return false;
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
        TitleCredits = _titles is null ? null : new(_titleCredits, StringComparer.OrdinalIgnoreCase)
    };

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

        if (_titles is not null)
        {
            var previewSequence = CreatePreordered(Key, _pool, ToState());
            previewSequence._titles = _titles.Select(title => (title.Title, title.Weight,
                Create(title.Sequence.Key, title.Sequence.Order, title.Sequence.Pool, title.Sequence.ToState()))).ToList();
            previewSequence._titleCredits = new(_titleCredits, StringComparer.OrdinalIgnoreCase);
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
            var selected = _titles.First(title => string.Equals(title.Title, TitleRotation.TitleOf(consumed), StringComparison.OrdinalIgnoreCase));
            selected.Sequence.Consume(id);
            foreach (var title in _titles) _titleCredits[title.Title] += title.Weight;
            _titleCredits[selected.Title] -= _titles.Sum(title => (long)title.Weight);
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
            PlaybackOrder.SeasonEpisode => items
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
