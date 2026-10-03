using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;

namespace SchedularrTV.WeatherStar;

/// <summary>
/// One station on air: a hidden browser showing the WeatherStar page, its frames paced into ffmpeg with the station's
/// music, and the resulting MPEG-TS handed to everyone watching (normally ErsatzTV, once per channel airing it).
/// </summary>
/// <remarks>
/// The stream starts at once with a plain frame and switches to the forecast as soon as the page draws, so ErsatzTV
/// never waits on the browser. If the browser dies it's started again; if ffmpeg dies the stream ends and the next
/// request starts a fresh one.
/// </remarks>
public sealed class StationStream : IAsyncDisposable
{
    public const int Width = 1280;
    public const int Height = 720;
    public const int OutputFrameRate = 30;

    private const int PacketSize = 188;
    private static readonly string[] MusicExtensions = [".mp3", ".m4a", ".aac", ".ogg", ".opus", ".flac", ".wav"];

    private readonly WeatherStation _station;
    private readonly WeatherStarSettings _settings;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _life = new();
    private readonly List<Channel<byte[]>> _viewers = [];
    private readonly string _workFolder;
    private Process? _ffmpeg;
    private HeadlessBrowser? _browser;
    private byte[] _placeholder = [];
    private volatile byte[]? _latest;
    private long _lastFrameAt;
    private Task[] _tasks = [];

    private StationStream(WeatherStation station, WeatherStarSettings settings, ILogger logger)
    {
        _station = station;
        _settings = settings;
        _logger = logger;
        _workFolder = Path.Combine(Path.GetTempPath(), "schedularrtv-ws4kp", $"stream-{station.Id}-{Guid.NewGuid():N}");
    }

    public WeatherStation Station => _station;

    /// <summary>The latest frame the page drew, for previews; null until it draws.</summary>
    public byte[]? LatestFrame => _latest;

    /// <summary>Why the forecast isn't showing (no browser, a page error), or null when it is.</summary>
    public string? Problem { get; private set; }

    public bool Ended => _life.IsCancellationRequested;

    public int Viewers
    {
        get
        {
            lock (_viewers) return _viewers.Count;
        }
    }

    /// <summary>Raised once when the stream ends by itself (ffmpeg stopped).</summary>
    public event Action<StationStream>? Stopped;

    public static async Task<StationStream> StartAsync(WeatherStation station, WeatherStarSettings settings, ILogger logger, CancellationToken cancellationToken)
    {
        var stream = new StationStream(station, settings, logger);
        try
        {
            await stream.StartCoreAsync(cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_workFolder);
        string ffmpeg = _settings.Ffmpeg;
        _placeholder = await PlaceholderAsync(ffmpeg, cancellationToken).ConfigureAwait(false);

        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in Arguments(_settings.CaptureFrameRate, MusicPlaylist(), _station.MusicVolume))
        {
            start.ArgumentList.Add(argument);
        }

        _ffmpeg = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start ffmpeg ({ffmpeg}).");
        _ffmpeg.ErrorDataReceived += (_, line) =>
        {
            if (!string.IsNullOrWhiteSpace(line.Data))
            {
                _logger.LogDebug("ffmpeg ({Station}): {Line}", _station.Id, line.Data);
            }
        };
        _ffmpeg.BeginErrorReadLine();

        CancellationToken token = _life.Token;
        _tasks =
        [
            Task.Run(() => PumpFramesAsync(token), CancellationToken.None),
            Task.Run(() => ReadOutputAsync(token), CancellationToken.None),
            Task.Run(() => KeepBrowserAsync(token), CancellationToken.None)
        ];
    }

    /// <summary>The ffmpeg command: paced JPEG frames in, the station's music (or silence) mixed under, MPEG-TS out.</summary>
    public static IReadOnlyList<string> Arguments(int captureFrameRate, string? playlist, double musicVolume)
    {
        List<string> arguments =
        [
            "-hide_banner", "-loglevel", "warning", "-nostdin",
            "-f", "image2pipe", "-c:v", "mjpeg", "-framerate", captureFrameRate.ToString(CultureInfo.InvariantCulture), "-i", "pipe:0"
        ];

        if (playlist is not null)
        {
            arguments.AddRange(["-stream_loop", "-1", "-f", "concat", "-safe", "0", "-i", playlist]);
        }
        else
        {
            arguments.AddRange(["-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"]);
        }

        arguments.AddRange(
        [
            "-map", "0:v", "-map", "1:a",
            "-vf", $"scale={Width}:{Height}:force_original_aspect_ratio=decrease,pad={Width}:{Height}:(ow-iw)/2:(oh-ih)/2,format=yuv420p",
            "-r", OutputFrameRate.ToString(CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-preset", "veryfast", "-tune", "stillimage", "-profile:v", "high",
            "-g", (OutputFrameRate * 2).ToString(CultureInfo.InvariantCulture), "-keyint_min", (OutputFrameRate * 2).ToString(CultureInfo.InvariantCulture),
            "-sc_threshold", "0", "-b:v", "3000k", "-maxrate", "3000k", "-bufsize", "6000k"
        ]);

        if (playlist is not null)
        {
            arguments.AddRange(["-af", $"volume={Math.Clamp(musicVolume, 0, 1).ToString("0.###", CultureInfo.InvariantCulture)}"]);
        }

        arguments.AddRange(["-c:a", "aac", "-b:a", "160k", "-ar", "48000", "-ac", "2", "-f", "mpegts", "-mpegts_flags", "+resend_headers", "-muxdelay", "0", "pipe:1"]);
        return arguments;
    }

    /// <summary>The station's songs in a fresh random order, as an ffmpeg concat list; null for no music.</summary>
    private string? MusicPlaylist()
    {
        if (string.IsNullOrWhiteSpace(_station.MusicFolder) || !Directory.Exists(_station.MusicFolder))
        {
            return null;
        }

        string[] songs = [.. Directory.EnumerateFiles(_station.MusicFolder, "*", SearchOption.AllDirectories)
            .Where(path => MusicExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))];
        if (songs.Length == 0)
        {
            return null;
        }

        Random.Shared.Shuffle(songs);
        string list = Path.Combine(_workFolder, "music.txt");
        File.WriteAllLines(list, songs.Select(song => $"file '{ConcatPath(song)}'").Prepend("ffconcat version 1.0"));
        return list;
    }

    public static string ConcatPath(string path) =>
        (OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path).Replace("'", "'\\''", StringComparison.Ordinal);

    /// <summary>A plain WeatherStar-blue frame, shown until the page has drawn.</summary>
    private static async Task<byte[]> PlaceholderAsync(string ffmpeg, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", $"color=c=0x1b2a6b:s={Width}x{Height}",
                     "-frames:v", "1", "-f", "mjpeg", "-q:v", "3", "pipe:1"
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start ffmpeg ({ffmpeg}).");
        Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);
        using var image = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(image, cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0 || image.Length == 0)
        {
            throw new InvalidOperationException($"ffmpeg couldn't make a frame: {(await errors.ConfigureAwait(false)).Trim()}");
        }

        return image.ToArray();
    }

    /// <summary>
    /// Writes the newest frame at a steady rate, so the stream keeps time even when the page draws nothing new. If
    /// ffmpeg falls behind, the missed frames are written at once so timestamps stay with the clock.
    /// </summary>
    private async Task PumpFramesAsync(CancellationToken cancellationToken)
    {
        Stream input = _ffmpeg!.StandardInput.BaseStream;
        int rate = _settings.CaptureFrameRate;
        var clock = Stopwatch.StartNew();
        long written = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                long due = (long)(clock.Elapsed.TotalSeconds * rate) + 1;
                while (written < due)
                {
                    await input.WriteAsync(_latest ?? _placeholder, cancellationToken).ConfigureAwait(false);
                    written++;
                }

                await input.FlushAsync(cancellationToken).ConfigureAwait(false);
                TimeSpan next = TimeSpan.FromSeconds((double)written / rate) - clock.Elapsed;
                if (next > TimeSpan.Zero)
                {
                    await Task.Delay(next, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Weather station {Station}: ffmpeg stopped taking frames.", _station.Name);
                End();
            }
        }
    }

    /// <summary>Hands ffmpeg's output to every viewer in whole TS packets, so anyone can join between packets.</summary>
    private async Task ReadOutputAsync(CancellationToken cancellationToken)
    {
        Stream output = _ffmpeg!.StandardOutput.BaseStream;
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
            _logger.LogWarning("Weather station {Station}: ffmpeg ended (exit code {Code}).", _station.Name,
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

    /// <summary>Starts the browser, and starts it again whenever it goes away, for as long as the stream runs.</summary>
    private async Task KeepBrowserAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // WeatherStar is always moving (its clock, the scrolling text), so a page that has drawn nothing for a
            // minute has lost its connection or hung, and is started again.
            bool stalled = _browser is not null && Environment.TickCount64 - Interlocked.Read(ref _lastFrameAt) > 60_000;
            if (stalled)
            {
                _logger.LogWarning("Weather station {Station}: the page stopped drawing; starting the browser again.", _station.Name);
            }

            if (_browser is null || _browser.HasExited || stalled)
            {
                if (_browser is not null)
                {
                    await _browser.DisposeAsync().ConfigureAwait(false);
                    _browser = null;
                }

                string? browser = BrowserLocator.Find(_settings.BrowserPath);
                if (browser is null)
                {
                    Problem = "No Chrome, Edge or Chromium found. Install one, or choose it in the plugin's settings.";
                }
                else
                {
                    try
                    {
                        _browser = await HeadlessBrowser.StartAsync(browser, _station.PageUrl(_settings.Site), Width, Height, cancellationToken).ConfigureAwait(false);
                        Interlocked.Exchange(ref _lastFrameAt, Environment.TickCount64);
                        _startedAt = Environment.TickCount64;
                        _drawing = false;
                        _browser.Frame += OnFrame;
                        Problem = null;
                        _logger.LogInformation("Weather station {Station}: browser started on {Page}.", _station.Name, _station.PageUrl(_settings.Site));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Problem = $"The browser couldn't show the forecast: {ex.Message}";
                        _logger.LogWarning(ex, "Weather station {Station}: couldn't start the browser.", _station.Name);
                    }
                }

                if (Problem is not null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            await CatchUpAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private long _startedAt;
    private volatile bool _drawing;

    private void OnFrame(byte[] frame)
    {
        _latest = frame;
        Interlocked.Exchange(ref _lastFrameAt, Environment.TickCount64);
        if (!_drawing)
        {
            _drawing = true;
            Problem = null;
            _logger.LogInformation("Weather station {Station}: the forecast is on screen.", _station.Name);
        }
    }

    /// <summary>
    /// Chrome only sends screencast frames when it repaints, and some setups send none at all, so when it's been
    /// quiet for a moment a picture is taken instead. If nothing has come for a while, the page says why.
    /// </summary>
    private async Task CatchUpAsync(CancellationToken cancellationToken)
    {
        if (_browser is not { HasExited: false } browser || Environment.TickCount64 - Interlocked.Read(ref _lastFrameAt) < 1_000)
        {
            return;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            if (await browser.CaptureAsync(timeout.Token).ConfigureAwait(false) is { Length: > 0 } picture)
            {
                OnFrame(picture);
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Weather station {Station}: couldn't take a picture of the page.", _station.Name);
        }

        if (!_drawing && Environment.TickCount64 - _startedAt > 20_000 && Problem is null)
        {
            Problem = $"The WeatherStar page hasn't drawn anything yet. Check that {_settings.Site} opens in a browser on this computer.";
            _logger.LogWarning("Weather station {Station}: the browser has drawn nothing in 20 seconds.", _station.Name);
        }
    }

    public ChannelReader<byte[]> Join()
    {
        Channel<byte[]> viewer = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1024) { SingleReader = true, SingleWriter = true });
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

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        // Ending by itself and the idle timer can both get here.
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
            if (_ffmpeg is { HasExited: false })
            {
                _ffmpeg.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        try
        {
            await Task.WhenAll(_tasks).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Whatever a background task died of, the browser and ffmpeg below must still be cleaned up.
            if (ex is not (TimeoutException or OperationCanceledException))
            {
                _logger.LogDebug(ex, "Weather station {Station}: a stream task failed while stopping.", _station.Name);
            }
        }

        if (_browser is not null)
        {
            await _browser.DisposeAsync().ConfigureAwait(false);
        }

        _ffmpeg?.Dispose();
        try
        {
            if (Directory.Exists(_workFolder))
            {
                Directory.Delete(_workFolder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        _life.Dispose();
    }
}
