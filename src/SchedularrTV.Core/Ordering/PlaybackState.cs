namespace SchedularrTV.Core.Ordering;

/// <summary>
/// Serializable cursor for one (channel, collection, order) pairing. Persisting this is what makes
/// "shuffle without repeats" and "chronological continues where it left off" work across days/restarts.
/// </summary>
public sealed class PlaybackState
{
    public required string Key { get; set; }

    /// <summary>Index into the deterministically sorted pool for ordered modes.</summary>
    public int Cursor { get; set; }

    /// <summary>Remaining item ids for <see cref="PlaybackOrder.Shuffle"/>, in play order.</summary>
    public List<string> Bag { get; set; } = [];

    /// <summary>Hex-encoded PRNG state so random/shuffle streams resume exactly.</summary>
    public string? RandomState { get; set; }

    /// <summary>Fingerprint of the pool contents; a change forces the bag to be rebuilt.</summary>
    public string? PoolFingerprint { get; set; }

    /// <summary>
    /// The item this sequence last handed out. <see cref="Cursor"/> is only an index, so it means
    /// nothing once the pool changes; resuming from the item itself is what stops a new episode
    /// appearing in the library from sending the channel back to the first episode.
    /// </summary>
    public string? LastItemId { get; set; }

    public Dictionary<string, PlaybackState>? Titles { get; set; }

    public Dictionary<string, long>? TitleCredits { get; set; }

    /// <summary>For <see cref="PlaybackOrder.Drop"/>: how many episodes, in release order, have had their first (new) airing.</summary>
    public int? Premiered { get; set; }

    /// <summary>For <see cref="PlaybackOrder.Drop"/>: the last episode that premiered, so a changed pool resumes after it.</summary>
    public string? LastPremieredId { get; set; }

    /// <summary>For <see cref="PlaybackOrder.Drop"/> without a configured start: when drops began.</summary>
    public DateTimeOffset? DropOrigin { get; set; }

    /// <summary>For <see cref="PlaybackOrder.Drop"/> shows that start over: how many times the show has started over.</summary>
    public int? DropCycle { get; set; }

    /// <summary>For <see cref="PlaybackOrder.Drop"/>: a manual reset from the channel page, which drops count from instead.</summary>
    public DropReset? DropReset { get; set; }

    public PlaybackState Clone() => new()
    {
        Premiered = Premiered,
        LastPremieredId = LastPremieredId,
        DropOrigin = DropOrigin,
        DropCycle = DropCycle,
        DropReset = DropReset?.Copy(),
        Key = Key,
        Cursor = Cursor,
        Bag = [.. Bag],
        RandomState = RandomState,
        PoolFingerprint = PoolFingerprint,
        LastItemId = LastItemId,
        Titles = Titles?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase),
        TitleCredits = TitleCredits is null ? null : new(TitleCredits, StringComparer.OrdinalIgnoreCase)
    };
}

/// <summary>
/// A drop show put back to a chosen place by hand: <see cref="Out"/> episodes count as already out at <see cref="At"/>,
/// and drops carry on from there on the show's cadence.
/// </summary>
public sealed class DropReset
{
    /// <summary>When the reset happened.</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>Episodes (in release order) already out, as repeats.</summary>
    public int Out { get; set; }

    /// <summary>The next episode drops at <see cref="At"/> rather than at the next drop time.</summary>
    public bool Immediate { get; set; }

    /// <summary>
    /// The drop's configured first-drop date when it was reset. Changing that date afterwards means the config is
    /// meant to win, so the reset no longer applies.
    /// </summary>
    public string? Start { get; set; }

    public DropReset Copy() => new() { At = At, Out = Out, Immediate = Immediate, Start = Start };
}

public interface IPlaybackStateStore
{
    ValueTask<PlaybackState?> GetAsync(string key, CancellationToken cancellationToken = default);

    ValueTask SaveAsync(PlaybackState state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves several states together, as at the end of a channel build. The same as saving each in turn; a store that
    /// writes to disk overrides it to write once rather than once per state.
    /// </summary>
    async ValueTask SaveAllAsync(IEnumerable<PlaybackState> states, CancellationToken cancellationToken = default)
    {
        foreach (PlaybackState state in states)
        {
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }
    }

    ValueTask ResetAsync(string keyPrefix, CancellationToken cancellationToken = default);
}
