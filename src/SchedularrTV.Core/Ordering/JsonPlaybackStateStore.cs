using System.Collections.Concurrent;
using System.Text.Json;

namespace SchedularrTV.Core.Ordering;

/// <summary>
/// File-backed playback state. Kept in memory and flushed as a whole document because the state is
/// small (a few KB per channel) and atomic replacement avoids partially written rotation cursors.
/// </summary>
public sealed class JsonPlaybackStateStore : IPlaybackStateStore, IAsyncDisposable
{
    // Not indented: the file holds every channel's shuffle bags (thousands of ids), and is rewritten after each build.
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _path;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, PlaybackState> _states;
    private volatile bool _dirty;

    public JsonPlaybackStateStore(string path)
    {
        _path = path;
        _states = Load(path);
    }

    public ValueTask<PlaybackState?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_states.TryGetValue(key, out PlaybackState? state) ? state.Clone() : null);

    public async ValueTask SaveAsync(PlaybackState state, CancellationToken cancellationToken = default)
    {
        _states[state.Key] = Stamped(state);
        _dirty = true;
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Every state of a build, then one write. The file holds every channel's shuffle bags, so writing it per state
    /// (dozens per channel each hour) wore out SD cards for nothing: readers always get the in-memory copy.
    /// </summary>
    public async ValueTask SaveAllAsync(IEnumerable<PlaybackState> states, CancellationToken cancellationToken = default)
    {
        foreach (PlaybackState state in states)
        {
            _states[state.Key] = Stamped(state);
            _dirty = true;
        }

        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IReadOnlyList<PlaybackState>> FindAsync(string keyPrefix, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<PlaybackState>>(
            [.. _states.Where(pair => pair.Key.StartsWith(keyPrefix, StringComparison.Ordinal)).Select(pair => pair.Value.Clone())]);

    public async ValueTask ResetAsync(string keyPrefix, CancellationToken cancellationToken = default)
    {
        foreach (string key in _states.Keys.Where(k => k.StartsWith(keyPrefix, StringComparison.Ordinal)))
        {
            _states.TryRemove(key, out _);
        }

        _dirty = true;
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the file when anything changed. Channels planned side by side save at about the same time: one write
    /// waiting its turn takes in every change made before it starts, so the others needn't queue up behind it.
    /// </summary>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        if (!_dirty || Interlocked.Exchange(ref _queued, 1) == 1)
        {
            return;
        }

        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Never got its turn: the next save has to be free to write.
            Interlocked.Exchange(ref _queued, 0);
            throw;
        }

        try
        {
            Interlocked.Exchange(ref _queued, 0);
            await WriteAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private int _queued;

    // Under the write lock.
    private async Task WriteAsync(CancellationToken cancellationToken)
    {
        if (!_dirty)
        {
            return;
        }

        // Cleared first: a change made while this writes marks it again, for the next write.
        _dirty = false;
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temp = _path + ".tmp";
            await using (FileStream stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, _states, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            _dirty = true;
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _writeLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await WriteAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        _writeLock.Dispose();
    }

    private static PlaybackState Stamped(PlaybackState state)
    {
        PlaybackState copy = state.Clone();
        copy.UpdatedAt = DateTimeOffset.UtcNow;
        return copy;
    }

    /// <summary>How long an ad or filler pool's state is kept after it was last used.</summary>
    private static readonly TimeSpan PoolStateLifetime = TimeSpan.FromDays(30);

    /// <summary>
    /// Whether a state can go: an ad or filler pool's (which a build recreates whenever it's needed) that hasn't been
    /// saved for a month, or was saved before saves were dated. Those pools used to be keyed by the day's query, so
    /// the file collected a new state for each of them every day; none of those are read again.
    /// </summary>
    private static bool Stale(string key, PlaybackState state, DateTimeOffset now) =>
        PoolScopes.Any(scope => key.Contains(scope, StringComparison.Ordinal))
        && (state.UpdatedAt is not { } saved || now - saved > PoolStateLifetime);

    private static readonly string[] PoolScopes = ["|commercials|", "|filler|", "|bumpers-opening|", "|bumpers-closing|", "|block-Block"];

    private static ConcurrentDictionary<string, PlaybackState> Load(string path)
    {
        if (!File.Exists(path))
        {
            return new ConcurrentDictionary<string, PlaybackState>(StringComparer.Ordinal);
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            Dictionary<string, PlaybackState>? loaded = JsonSerializer.Deserialize<Dictionary<string, PlaybackState>>(stream, JsonOptions);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return new ConcurrentDictionary<string, PlaybackState>(
                (loaded ?? []).Where(pair => !Stale(pair.Key, pair.Value, now)), StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // A corrupt state file must not stop the scheduler; rotations simply restart.
            return new ConcurrentDictionary<string, PlaybackState>(StringComparer.Ordinal);
        }
    }
}
