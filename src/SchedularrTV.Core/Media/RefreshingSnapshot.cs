using Microsoft.Extensions.Logging;

namespace SchedularrTV.Core.Media;

/// <summary>
/// A media source's snapshot of its library that's re-read in the background once it's old, while the one in hand
/// keeps being served. Only the first read (or one made after the source was pointed somewhere else) waits.
/// </summary>
/// <remarks>
/// Reading a large library takes minutes. If every reader waited for each periodic re-read, the editor's library pages,
/// tag pickers and autocompletes would all come up empty for that long, every cache window, with nothing saying why.
/// <para>
/// The <c>key</c> describes what the snapshot was read from (server, user, folders, excluded libraries…). A snapshot
/// read with a different key is never served: what was read no longer applies, so that read waits for the new one.
/// </para>
/// </remarks>
public sealed class RefreshingSnapshot<T> where T : class
{
    /// <summary>How long to wait before trying again after a background re-read failed.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(1);

    private sealed record Entry(T Value, string Key, DateTimeOffset TakenAt);

    private readonly Func<string, CancellationToken, Task<T>> _load;
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;
    private readonly string _name;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private volatile Entry? _entry;
    private long _retryAtTicks;
    private int _backgroundRunning;

    /// <param name="load">Reads the library for a key. Called one at a time.</param>
    /// <param name="name">The source's name, for the log.</param>
    public RefreshingSnapshot(Func<string, CancellationToken, Task<T>> load, string name, TimeProvider? time = null, ILogger? logger = null)
    {
        _load = load;
        _name = name;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>
    /// Raised (on a pool thread) after a background re-read replaced the snapshot, so whatever was built from the old
    /// one can be built again.
    /// </summary>
    public event Action? RefreshedInBackground;

    /// <summary>True while a background re-read is running.</summary>
    public bool Refreshing => Volatile.Read(ref _backgroundRunning) == 1;

    /// <summary>
    /// The snapshot for <paramref name="key"/>. Returns the one in hand straight away when it was read with this key,
    /// starting a background re-read when it's older than <paramref name="maxAge"/>; otherwise waits for a read.
    /// </summary>
    public async ValueTask<T> GetAsync(string key, TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        Entry? entry = _entry;
        if (entry is not null && entry.Key == key)
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (now - entry.TakenAt >= maxAge && now.UtcTicks >= Interlocked.Read(ref _retryAtTicks))
            {
                RefreshInBackground(key, maxAge);
            }

            return entry.Value;
        }

        return await LoadAsync(key, maxAge, force: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the library again now and waits for it (a Rescan button). Readers keep getting the previous snapshot until
    /// the new one is in hand.
    /// </summary>
    public Task<T> RefreshAsync(string key, CancellationToken cancellationToken = default) =>
        LoadAsync(key, TimeSpan.Zero, force: true, cancellationToken);

    private async Task<T> LoadAsync(string key, TimeSpan maxAge, bool force, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Someone else read it while this waited.
            Entry? entry = _entry;
            if (!force && entry is not null && entry.Key == key && _time.GetUtcNow() - entry.TakenAt < maxAge)
            {
                return entry.Value;
            }

            T value = await _load(key, cancellationToken).ConfigureAwait(false);
            _entry = new Entry(value, key, _time.GetUtcNow());
            Interlocked.Exchange(ref _retryAtTicks, 0);
            return value;
        }
        finally
        {
            _lock.Release();
        }
    }

    private void RefreshInBackground(string key, TimeSpan maxAge)
    {
        if (Interlocked.Exchange(ref _backgroundRunning, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            bool replaced = false;
            try
            {
                Entry? before = _entry;
                await LoadAsync(key, maxAge, force: false, CancellationToken.None).ConfigureAwait(false);
                replaced = !ReferenceEquals(before, _entry);
            }
            catch (Exception ex)
            {
                // The previous snapshot stays: an unreachable server shouldn't empty the library.
                Interlocked.Exchange(ref _retryAtTicks, (_time.GetUtcNow() + RetryAfterFailure).UtcTicks);
                _logger?.LogWarning("Couldn't re-read {Source}; still using the library read earlier. {Message}",
                    _name, ex.GetBaseException().Message);
            }
            finally
            {
                Volatile.Write(ref _backgroundRunning, 0);
            }

            if (replaced)
            {
                try
                {
                    RefreshedInBackground?.Invoke();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "A listener failed after {Source} was re-read.", _name);
                }
            }
        });
    }
}
