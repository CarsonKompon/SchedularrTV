using System.Collections.Concurrent;

namespace SchedularrTV.Core.Ordering;

public sealed class InMemoryPlaybackStateStore : IPlaybackStateStore
{
    private readonly ConcurrentDictionary<string, PlaybackState> _states = new(StringComparer.Ordinal);

    public ValueTask<PlaybackState?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_states.TryGetValue(key, out PlaybackState? state) ? state.Clone() : null);

    public ValueTask SaveAsync(PlaybackState state, CancellationToken cancellationToken = default)
    {
        _states[state.Key] = state.Clone();
        return ValueTask.CompletedTask;
    }

    public ValueTask ResetAsync(string keyPrefix, CancellationToken cancellationToken = default)
    {
        foreach (string key in _states.Keys.Where(k => k.StartsWith(keyPrefix, StringComparison.Ordinal)))
        {
            _states.TryRemove(key, out _);
        }

        return ValueTask.CompletedTask;
    }
}
