using System.Collections.Concurrent;
using System.Text.Json;

namespace SchedularrTV.Core.Ordering;

/// <summary>
/// File-backed playback state. Kept in memory and flushed as a whole document because the state is
/// small (a few KB per channel) and atomic replacement avoids partially written rotation cursors.
/// </summary>
public sealed class JsonPlaybackStateStore : IPlaybackStateStore, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, PlaybackState> _states;
    private bool _dirty;

    public JsonPlaybackStateStore(string path)
    {
        _path = path;
        _states = Load(path);
    }

    public ValueTask<PlaybackState?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_states.TryGetValue(key, out PlaybackState? state) ? state.Clone() : null);

    public async ValueTask SaveAsync(PlaybackState state, CancellationToken cancellationToken = default)
    {
        _states[state.Key] = state.Clone();
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
            _states[state.Key] = state.Clone();
            _dirty = true;
        }

        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ResetAsync(string keyPrefix, CancellationToken cancellationToken = default)
    {
        foreach (string key in _states.Keys.Where(k => k.StartsWith(keyPrefix, StringComparison.Ordinal)))
        {
            _states.TryRemove(key, out _);
        }

        _dirty = true;
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        if (!_dirty)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _dirty = false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        _writeLock.Dispose();
    }

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
            return new ConcurrentDictionary<string, PlaybackState>(loaded ?? [], StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // A corrupt state file must not stop the scheduler; rotations simply restart.
            return new ConcurrentDictionary<string, PlaybackState>(StringComparer.Ordinal);
        }
    }
}
