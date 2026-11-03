using System.Collections.Concurrent;

namespace SchedularrTV.Core.Ordering;

public sealed class InMemoryPlaybackStateStore : IPlaybackStateStore
{
    private readonly ConcurrentDictionary<string, PlaybackState> _states = new(StringComparer.Ordinal);

    public ValueTask<PlaybackState?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_states.TryGetValue(key, out PlaybackState? state) ? state.Clone() : null);

    private readonly Lock _clock = new();
    private long _lastSaved;

    public ValueTask SaveAsync(PlaybackState state, CancellationToken cancellationToken = default)
    {
        PlaybackState copy = state.Clone();
        // Dated like the file store's, strictly later each time, so "the most recent" is well defined even within a tick.
        lock (_clock)
        {
            _lastSaved = Math.Max(_lastSaved + 1, DateTimeOffset.UtcNow.UtcTicks);
            copy.UpdatedAt = new DateTimeOffset(_lastSaved, TimeSpan.Zero);
        }

        _states[state.Key] = copy;
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<PlaybackState>> FindAsync(string keyPrefix, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<PlaybackState>>(
            [.. _states.Where(pair => pair.Key.StartsWith(keyPrefix, StringComparison.Ordinal)).Select(pair => pair.Value.Clone())]);

    public ValueTask ResetAsync(string keyPrefix, CancellationToken cancellationToken = default)
    {
        foreach (string key in _states.Keys.Where(k => k.StartsWith(keyPrefix, StringComparison.Ordinal)))
        {
            _states.TryRemove(key, out _);
        }

        return ValueTask.CompletedTask;
    }
}
