using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace SchedularrTV.WeatherStar;

/// <summary>
/// Runs a station only while something is watching it (plus a short grace period), and shares one running
/// station between every viewer.
/// </summary>
public sealed class StreamHub : IAsyncDisposable
{
    private readonly StationStore _stations;
    private readonly WeatherStarSettings _settings;
    private readonly ILogger<StreamHub> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, StationStream> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _idle = new(StringComparer.OrdinalIgnoreCase);

    public StreamHub(StationStore stations, WeatherStarSettings settings, ILogger<StreamHub> logger)
    {
        _stations = stations;
        _settings = settings;
        _logger = logger;

        // A station whose settings changed starts again with them on its next viewer.
        _stations.Changed += () => _ = RestartChangedAsync();
    }

    /// <summary>Raised when a station starts or stops, for status displays.</summary>
    public event Action? Changed;

    public sealed record Status(string StationId, int Viewers, string? Problem);

    public IReadOnlyList<Status> Running
    {
        get
        {
            lock (_running)
            {
                return [.. _running.Values.Select(stream => new Status(stream.Station.Id, stream.Viewers, stream.Problem))];
            }
        }
    }

    public byte[]? Snapshot(string stationId)
    {
        lock (_running)
        {
            return _running.TryGetValue(stationId, out StationStream? stream) ? stream.LatestFrame : null;
        }
    }

    /// <summary>Streams a station's MPEG-TS to an HTTP response until the client goes away.</summary>
    public async Task ServeAsync(string stationId, HttpContext context)
    {
        if (_stations.Find(stationId) is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        StationStream stream;
        try
        {
            stream = await AcquireAsync(stationId, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Weather station {Station} couldn't start.", stationId);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync($"The weather station couldn't start: {ex.Message}").ConfigureAwait(false);
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

    private async Task<StationStream> AcquireAsync(string stationId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CancelIdle(stationId);
            lock (_running)
            {
                if (_running.TryGetValue(stationId, out StationStream? running) && !running.Ended)
                {
                    return running;
                }
            }

            WeatherStation station = _stations.Find(stationId) ?? throw new InvalidOperationException($"There's no station '{stationId}'.");
            StationStream stream = await StationStream.StartAsync(station, _settings, _logger, cancellationToken).ConfigureAwait(false);
            stream.Stopped += ended => _ = RemoveAsync(ended);
            if (stream.Ended)
            {
                // ffmpeg died before Stopped was hooked up.
                await stream.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException("ffmpeg stopped as soon as it started; check the plugin's ffmpeg setting.");
            }

            lock (_running)
            {
                _running[stationId] = stream;
            }

            _logger.LogInformation("Weather station {Station} is on air.", station.Name);
            Changed?.Invoke();
            return stream;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>When the last viewer leaves, the station stops after the grace period unless someone tunes back in.</summary>
    private void Release(StationStream stream)
    {
        Changed?.Invoke();
        if (stream.Viewers > 0 || stream.Ended)
        {
            return;
        }

        var idle = new CancellationTokenSource();
        // Read the token now: CancelIdle may cancel and dispose the source before the timer task starts.
        CancellationToken idleToken = idle.Token;
        lock (_idle)
        {
            if (_idle.Remove(stream.Station.Id, out CancellationTokenSource? previous))
            {
                previous.Cancel();
                previous.Dispose();
            }

            _idle[stream.Station.Id] = idle;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_settings.KeepAlive, idleToken).ConfigureAwait(false);
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

    private void CancelIdle(string stationId)
    {
        lock (_idle)
        {
            if (_idle.Remove(stationId, out CancellationTokenSource? idle))
            {
                idle.Cancel();
                idle.Dispose();
            }
        }
    }

    private async Task RemoveAsync(StationStream stream)
    {
        bool removed;
        lock (_running)
        {
            removed = _running.TryGetValue(stream.Station.Id, out StationStream? current) && ReferenceEquals(current, stream)
                && _running.Remove(stream.Station.Id);
        }

        if (removed)
        {
            _logger.LogInformation("Weather station {Station} is off air.", stream.Station.Name);
            Changed?.Invoke();
        }

        await stream.DisposeAsync().ConfigureAwait(false);
    }

    private async Task RestartChangedAsync()
    {
        List<StationStream> stale;
        lock (_running)
        {
            stale = [.. _running.Values.Where(stream => _stations.Find(stream.Station.Id) is not { } now
                || System.Text.Json.JsonSerializer.Serialize(now) != System.Text.Json.JsonSerializer.Serialize(stream.Station))];
        }

        // Viewers are disconnected; ErsatzTV reconnects and gets the station with its new settings.
        foreach (StationStream stream in stale)
        {
            await RemoveAsync(stream).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<StationStream> all;
        lock (_running)
        {
            all = [.. _running.Values];
            _running.Clear();
        }

        foreach (StationStream stream in all)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }
}
