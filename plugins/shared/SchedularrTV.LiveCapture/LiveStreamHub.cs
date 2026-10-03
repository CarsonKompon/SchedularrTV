using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace SchedularrTV.LiveCapture;

/// <summary>A running live channel, for status displays.</summary>
public sealed record LiveStatus(string Id, int Viewers, string? Problem);

/// <summary>
/// Runs a live page channel only while something is watching it (plus a short grace period), and shares one running
/// stream between every viewer.
/// </summary>
/// <param name="resolve">What a channel id should play now, or null when there's no such channel.</param>
/// <param name="keepAlive">How long a channel stays on after its last viewer leaves.</param>
public sealed class LiveStreamHub(Func<string, LiveStreamSpec?> resolve, Func<TimeSpan> keepAlive, ILogger logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, LiveStream> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _idle = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when a channel starts or stops or its viewers change.</summary>
    public event Action? Changed;

    public IReadOnlyList<LiveStatus> Running
    {
        get
        {
            lock (_running)
            {
                return [.. _running.Values.Select(stream => new LiveStatus(stream.Spec.Id, stream.Viewers, stream.Problem))];
            }
        }
    }

    public LiveStatus? Status(string id) => Running.FirstOrDefault(status => string.Equals(status.Id, id, StringComparison.OrdinalIgnoreCase));

    private readonly Dictionary<string, (string Problem, DateTimeOffset At)> _failures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Why a channel's stream last stopped by itself (ffmpeg gave up), and when; null when it hasn't.</summary>
    public (string Problem, DateTimeOffset At)? LastFailure(string id)
    {
        lock (_failures)
        {
            return _failures.TryGetValue(id, out var failure) ? failure : null;
        }
    }

    public byte[]? Snapshot(string id)
    {
        lock (_running)
        {
            return _running.TryGetValue(id, out LiveStream? stream) ? stream.LatestFrame : null;
        }
    }

    /// <summary>Streams a channel's MPEG-TS to an HTTP response until the client goes away.</summary>
    public async Task ServeAsync(string id, HttpContext context)
    {
        if (resolve(id) is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        LiveStream stream;
        try
        {
            stream = await AcquireAsync(id, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Live channel {Id} couldn't start.", id);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync($"The channel couldn't start: {ex.Message}").ConfigureAwait(false);
            return;
        }

        var reader = stream.Join();
        Changed?.Invoke();
        try
        {
            context.Response.ContentType = "video/mp2t";
            context.Response.Headers.CacheControl = "no-cache, no-store";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);
            await foreach (byte[] chunk in reader.ReadAllAsync(context.RequestAborted).ConfigureAwait(false))
            {
                await context.Response.Body.WriteAsync(chunk, context.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // The viewer left.
        }
        finally
        {
            stream.Leave(reader);
            Release(stream);
        }
    }

    private async Task<LiveStream> AcquireAsync(string id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CancelIdle(id);
            lock (_running)
            {
                if (_running.TryGetValue(id, out LiveStream? running) && !running.Ended)
                {
                    return running;
                }
            }

            LiveStreamSpec spec = resolve(id) ?? throw new InvalidOperationException($"There's no channel '{id}'.");
            LiveStream stream = await LiveStream.StartAsync(spec, logger, cancellationToken).ConfigureAwait(false);
            stream.Stopped += ended =>
            {
                lock (_failures)
                {
                    _failures[ended.Spec.Id] = (ended.Problem ?? "ffmpeg stopped without saying why.", DateTimeOffset.UtcNow);
                }

                _ = RemoveAsync(ended);
            };
            if (stream.Ended)
            {
                // ffmpeg died before Stopped was hooked up.
                await stream.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException("ffmpeg stopped as soon as it started; check the plugin's ffmpeg setting.");
            }

            lock (_running)
            {
                _running[id] = stream;
            }

            logger.LogInformation("Live channel {Name} is on air.", spec.Name);
            Changed?.Invoke();
            return stream;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>When the last viewer leaves, the channel stops after the grace period unless someone tunes back in.</summary>
    private void Release(LiveStream stream)
    {
        Changed?.Invoke();
        if (stream.Viewers > 0 || stream.Ended)
        {
            return;
        }

        var idle = new CancellationTokenSource();
        CancellationToken token = idle.Token;
        lock (_idle)
        {
            if (_idle.Remove(stream.Spec.Id, out CancellationTokenSource? previous))
            {
                previous.Cancel();
                previous.Dispose();
            }

            _idle[stream.Spec.Id] = idle;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(keepAlive(), token).ConfigureAwait(false);
                if (stream.Viewers == 0)
                {
                    await RemoveAsync(stream).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void CancelIdle(string id)
    {
        lock (_idle)
        {
            if (_idle.Remove(id, out CancellationTokenSource? idle))
            {
                idle.Cancel();
                idle.Dispose();
            }
        }
    }

    private async Task RemoveAsync(LiveStream stream)
    {
        bool removed;
        lock (_running)
        {
            removed = _running.TryGetValue(stream.Spec.Id, out LiveStream? current) && ReferenceEquals(current, stream)
                && _running.Remove(stream.Spec.Id);
        }

        if (removed)
        {
            logger.LogInformation("Live channel {Name} is off air.", stream.Spec.Name);
            Changed?.Invoke();
        }

        await stream.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Stops running channels whose settings changed (or that were deleted). Viewers are disconnected; ErsatzTV
    /// reconnects and gets the channel with its new settings.
    /// </summary>
    public async Task RestartChangedAsync()
    {
        List<LiveStream> stale;
        lock (_running)
        {
            stale = [.. _running.Values.Where(stream => resolve(stream.Spec.Id) is not { } now || now.Fingerprint != stream.RequestedFingerprint)];
        }

        foreach (LiveStream stream in stale)
        {
            await RemoveAsync(stream).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<LiveStream> all;
        lock (_running)
        {
            all = [.. _running.Values];
            _running.Clear();
        }

        foreach (LiveStream stream in all)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }
}
