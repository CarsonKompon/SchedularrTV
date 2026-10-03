using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace SchedularrTV.Iptv;

/// <summary>What to relay: a provider stream, and how to ask for it.</summary>
public sealed record RelayTarget(string Key, string Name, string Url, string? UserAgent, string? Referrer);

/// <summary>A stream being relayed, for the IPTV page.</summary>
public sealed record RelayStatus(string Key, string Name, int Viewers, DateTimeOffset Since);

/// <summary>
/// Relays provider streams to ErsatzTV as MPEG-TS. One ffmpeg per stream, shared by everyone watching it, copying
/// (never re-encoding) whatever the provider sends, HLS included. A stream stays open for a short while after its last
/// viewer leaves, because ErsatzTV reconnects at every programme change and some providers allow only one connection.
/// </summary>
public sealed class RelayHub(IptvSettings settings, ILogger<RelayHub> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Relay> _running = new(StringComparer.Ordinal);

    /// <summary>Raised when a stream starts or stops, or its viewers change.</summary>
    public event Action? Changed;

    public IReadOnlyList<RelayStatus> Running
    {
        get
        {
            lock (_running)
            {
                return [.. _running.Values.Select(relay => new RelayStatus(relay.Target.Key, relay.Target.Name, relay.Viewers, relay.Since))];
            }
        }
    }

    /// <summary>The ffmpeg command: the provider's stream in, its best video and audio copied, MPEG-TS out.</summary>
    public static IReadOnlyList<string> Arguments(RelayTarget target)
    {
        List<string> arguments = ["-hide_banner", "-loglevel", "warning", "-nostdin"];
        if (IptvFetcher.IsWeb(target.Url))
        {
            arguments.AddRange(["-reconnect", "1", "-reconnect_streamed", "1", "-reconnect_on_network_error", "1", "-reconnect_delay_max", "5",
                "-rw_timeout", "15000000"]);
            if (!string.IsNullOrWhiteSpace(target.UserAgent))
            {
                arguments.AddRange(["-user_agent", target.UserAgent]);
            }

            if (!string.IsNullOrWhiteSpace(target.Referrer))
            {
                arguments.AddRange(["-referer", target.Referrer]);
            }
        }

        arguments.AddRange(
        [
            "-fflags", "+genpts+discardcorrupt", "-i", target.Url,
            "-c", "copy", "-f", "mpegts", "-mpegts_flags", "+resend_headers", "-muxdelay", "0", "pipe:1"
        ]);
        return arguments;
    }

    /// <summary>Streams a channel to an HTTP response until the client goes away.</summary>
    public async Task ServeAsync(RelayTarget target, HttpContext context)
    {
        Relay relay;
        try
        {
            relay = await AcquireAsync(target, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "IPTV: couldn't start relaying {Channel}.", target.Name);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync($"Couldn't start the stream: {ex.Message}").ConfigureAwait(false);
            return;
        }

        ChannelReader<byte[]> reader = relay.Join();
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
            relay.Leave(reader);
            Release(relay);
        }
    }

    private async Task<Relay> AcquireAsync(RelayTarget target, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_running)
            {
                if (_running.TryGetValue(target.Key, out Relay? running) && !running.Ended && running.Target == target)
                {
                    running.CancelIdle();
                    return running;
                }
            }

            // The provider's address may have changed since (a refreshed playlist); the old relay makes way.
            Relay? stale;
            lock (_running)
            {
                _running.Remove(target.Key, out stale);
            }

            if (stale is not null)
            {
                await stale.DisposeAsync().ConfigureAwait(false);
            }

            Relay relay = Relay.Start(target, settings.Ffmpeg, logger);
            relay.Stopped += ended => _ = RemoveAsync(ended);
            lock (_running)
            {
                _running[target.Key] = relay;
            }

            logger.LogInformation("IPTV: relaying {Channel}.", target.Name);
            Changed?.Invoke();
            return relay;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>When the last viewer leaves, the relay stops after the keep-alive unless someone tunes back in.</summary>
    private void Release(Relay relay)
    {
        Changed?.Invoke();
        if (relay.Viewers > 0 || relay.Ended)
        {
            return;
        }

        CancellationToken idle = relay.StartIdle();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(settings.KeepAlive, idle).ConfigureAwait(false);
                if (relay.Viewers == 0)
                {
                    await RemoveAsync(relay).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private async Task RemoveAsync(Relay relay)
    {
        bool removed;
        lock (_running)
        {
            removed = _running.TryGetValue(relay.Target.Key, out Relay? current) && ReferenceEquals(current, relay) && _running.Remove(relay.Target.Key);
        }

        if (removed)
        {
            logger.LogInformation("IPTV: stopped relaying {Channel}.", relay.Target.Name);
            Changed?.Invoke();
        }

        await relay.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        List<Relay> all;
        lock (_running)
        {
            all = [.. _running.Values];
            _running.Clear();
        }

        foreach (Relay relay in all)
        {
            await relay.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    /// <summary>One ffmpeg relaying one stream, its output handed out in whole TS packets.</summary>
    private sealed class Relay : IAsyncDisposable
    {
        private const int PacketSize = 188;

        private readonly Process _ffmpeg;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _life = new();
        private readonly List<Channel<byte[]>> _viewers = [];
        private CancellationTokenSource? _idle;
        private Task _pump = Task.CompletedTask;
        private int _disposed;

        private Relay(RelayTarget target, Process ffmpeg, ILogger logger)
        {
            Target = target;
            _ffmpeg = ffmpeg;
            _logger = logger;
        }

        public RelayTarget Target { get; }

        public DateTimeOffset Since { get; } = DateTimeOffset.Now;

        public bool Ended => _life.IsCancellationRequested;

        public int Viewers
        {
            get
            {
                lock (_viewers) return _viewers.Count;
            }
        }

        public event Action<Relay>? Stopped;

        public static Relay Start(RelayTarget target, string ffmpeg, ILogger logger)
        {
            var start = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true
            };
            foreach (string argument in Arguments(target))
            {
                start.ArgumentList.Add(argument);
            }

            Process process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start ffmpeg ({ffmpeg}).");
            var relay = new Relay(target, process, logger);
            process.ErrorDataReceived += (_, line) =>
            {
                if (!string.IsNullOrWhiteSpace(line.Data))
                {
                    logger.LogDebug("ffmpeg (IPTV {Channel}): {Line}", target.Name, line.Data);
                }
            };
            process.BeginErrorReadLine();
            relay._pump = Task.Run(() => relay.PumpAsync(relay._life.Token));
            return relay;
        }

        private async Task PumpAsync(CancellationToken cancellationToken)
        {
            Stream output = _ffmpeg.StandardOutput.BaseStream;
            byte[] buffer = new byte[PacketSize * 700];
            int filled = 0;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int read = await output.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    filled += read;
                    int whole = filled / PacketSize * PacketSize;
                    if (whole == 0)
                    {
                        continue;
                    }

                    Broadcast(buffer.AsSpan(0, whole).ToArray());
                    buffer.AsSpan(whole, filled - whole).CopyTo(buffer);
                    filled -= whole;
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("IPTV {Channel}: the provider's stream ended (ffmpeg exit code {Code}).", Target.Name,
                    _ffmpeg.HasExited ? _ffmpeg.ExitCode : -1);
                End();
            }
        }

        private void Broadcast(byte[] chunk)
        {
            lock (_viewers)
            {
                foreach (Channel<byte[]> viewer in _viewers.ToList())
                {
                    // A viewer that can't keep up is dropped rather than let it hold everyone else back.
                    if (!viewer.Writer.TryWrite(chunk))
                    {
                        viewer.Writer.TryComplete();
                        _viewers.Remove(viewer);
                    }
                }
            }
        }

        public ChannelReader<byte[]> Join()
        {
            Channel<byte[]> viewer = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2048) { SingleReader = true, SingleWriter = true });
            lock (_viewers)
            {
                if (Ended)
                {
                    viewer.Writer.TryComplete();
                }
                else
                {
                    _viewers.Add(viewer);
                }
            }

            return viewer.Reader;
        }

        public void Leave(ChannelReader<byte[]> reader)
        {
            lock (_viewers)
            {
                _viewers.RemoveAll(viewer => viewer.Reader == reader);
            }
        }

        public CancellationToken StartIdle()
        {
            lock (_viewers)
            {
                _idle?.Cancel();
                _idle?.Dispose();
                _idle = new CancellationTokenSource();
                return _idle.Token;
            }
        }

        public void CancelIdle()
        {
            lock (_viewers)
            {
                _idle?.Cancel();
                _idle?.Dispose();
                _idle = null;
            }
        }

        private void End()
        {
            if (_life.IsCancellationRequested)
            {
                return;
            }

            _life.Cancel();
            lock (_viewers)
            {
                foreach (Channel<byte[]> viewer in _viewers)
                {
                    viewer.Writer.TryComplete();
                }

                _viewers.Clear();
            }

            Stopped?.Invoke(this);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            if (!_life.IsCancellationRequested)
            {
                _life.Cancel();
                lock (_viewers)
                {
                    foreach (Channel<byte[]> viewer in _viewers)
                    {
                        viewer.Writer.TryComplete();
                    }

                    _viewers.Clear();
                }
            }

            try
            {
                if (!_ffmpeg.HasExited)
                {
                    _ffmpeg.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            try
            {
                await _pump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }

            _ffmpeg.Dispose();
            CancelIdle();
            _life.Dispose();
        }
    }
}
