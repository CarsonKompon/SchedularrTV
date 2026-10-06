using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;

namespace SchedularrTV.LiveCapture;

/// <summary>A rectangle on the 1280×720 frame, in pixels.</summary>
public sealed record FrameRect(int X, int Y, int Width, int Height);

public enum LiveAudio
{
    Silence,

    /// <summary>Songs from <see cref="LiveStreamSpec.MusicFolder"/>, shuffled and looped.</summary>
    Music,

    /// <summary>The sound of <see cref="LiveStreamSpec.AudioStreamUrl"/>, e.g. another channel.</summary>
    Stream
}

/// <summary>What a live page channel shows and plays.</summary>
public sealed record LiveStreamSpec
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>The page to show (it must be reachable from this computer).</summary>
    public required string PageUrl { get; init; }

    public required string Ffmpeg { get; init; }

    public string? BrowserPath { get; init; }

    /// <summary>Frames per second taken from the page (5–30); the stream itself is always 30.</summary>
    public int CaptureFrameRate { get; init; } = 15;

    /// <summary>Another stream (another channel, say) shown in <see cref="Window"/> over the page.</summary>
    public string? WindowStreamUrl { get; init; }

    public FrameRect? Window { get; init; }

    public LiveAudio Audio { get; init; } = LiveAudio.Silence;

    public string? MusicFolder { get; init; }

    /// <summary>0 to 1.</summary>
    public double Volume { get; init; } = 0.6;

    public string? AudioStreamUrl { get; init; }

    /// <summary>
    /// ffmpeg filters that draw the sound as a picture (<c>showwaves</c>, <c>showfreqs</c>…), laid over the page at
    /// <see cref="VisualizerRect"/>, in step with what's heard. Black in their picture is see-through.
    /// </summary>
    public string? Visualizer { get; init; }

    public FrameRect? VisualizerRect { get; init; }

    /// <summary>
    /// Draws the page over the window's stream instead of under it: the page is captured with a see-through background
    /// (PNG frames), so whatever it leaves clear shows the stream, and what it draws (a chat box, a pop-up) sits on top.
    /// Without a window stream the page is laid over a plain dark background.
    /// </summary>
    public bool PageOnTop { get; init; }

    /// <summary>
    /// The window's (and a separate sound stream's) streams are recordings rather than live, started at
    /// <see cref="SeekTo"/>. They aren't read with <c>-re</c>: the page's frames, fed in at the clock's pace, already
    /// hold the whole stream to real time, and <c>-re</c> after a seek makes some ffmpegs wait out the skipped part first.
    /// </summary>
    public bool Recorded { get; init; }

    /// <summary>Where to start a recorded window stream. Not part of <see cref="Fingerprint"/>: it's worked out afresh at each start.</summary>
    public TimeSpan? SeekTo { get; init; }

    /// <summary>Anything else that should restart the stream when it changes (the same recording starting over, say).</summary>
    public string? RestartToken { get; init; }

    /// <summary>
    /// How the stream is encoded (the <c>LiveEncoding</c> app settings): encoder, preset, size, frame rate and bitrate.
    /// Pages are still laid out on the 1280×720 frame; a smaller size is scaled down at the end.
    /// </summary>
    public LiveEncodingSettings Encoding { get; init; } = LiveEncodingSettings.Current;

    /// <summary>
    /// ffmpeg's <c>-hwaccel</c> for decoding the window's stream on a graphics chip (<c>auto</c>, <c>cuda</c>, <c>qsv</c>,
    /// <c>d3d11va</c>, <c>vaapi</c>, <c>videotoolbox</c>…), or null to decode it on the CPU. Decoded pictures come back to
    /// memory for the rest of the work, and ffmpeg falls back to the CPU by itself where the chip can't decode it.
    /// </summary>
    public string? HardwareDecoding { get; init; }

    /// <summary>Changes whenever anything that affects the running stream does, so it's started again with the new settings.</summary>
    public string Fingerprint =>
        string.Join('|', PageUrl, Ffmpeg, BrowserPath, CaptureFrameRate, WindowStreamUrl, Window, Audio, MusicFolder,
            Volume.ToString(CultureInfo.InvariantCulture), AudioStreamUrl, Visualizer, VisualizerRect, PageOnTop, Recorded, RestartToken,
            Encoding.Fingerprint, HardwareDecoding);

    /// <summary>The options before a window or sound stream's <c>-i</c>.</summary>
    internal IEnumerable<string> InputOptions()
    {
        if (Recorded && SeekTo is { } seek && seek > TimeSpan.Zero)
        {
            yield return "-ss";
            yield return seek.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        }

        yield return "-thread_queue_size";
        yield return "1024";
    }
}

/// <summary>
/// One page channel on air: a hidden browser showing the page, its frames paced into ffmpeg (with another stream
/// in a window over it, if wanted, and music or a stream's sound), and the resulting MPEG-TS handed to everyone
/// watching (normally ErsatzTV).
/// </summary>
/// <remarks>
/// The stream starts at once with a plain frame and switches to the page as soon as it draws, so ErsatzTV never waits
/// on the browser. If the browser dies or stops drawing it's started again; if ffmpeg dies the stream ends and the
/// next request starts a fresh one.
/// </remarks>
public sealed class LiveStream : IAsyncDisposable
{
    /// <summary>The frame pages are laid out on (and window and visualizer rectangles measured against).</summary>
    public const int Width = 1280;
    public const int Height = 720;

    /// <summary>The standard output frame rate; the stream's own is <see cref="LiveEncodingSettings.FrameRate"/>.</summary>
    public const int OutputFrameRate = 30;

    /// <summary>What ErsatzTV is told about these streams, with the encoding settings in use now.</summary>
    public static string ProbeHint => ProbeHintFor(LiveEncodingSettings.Current);

    /// <summary>
    /// What ErsatzTV is told about streams encoded with <paramref name="encoding"/>, so it needn't open one twice to find
    /// out. It must match what <see cref="Arguments"/> produces.
    /// </summary>
    public static string ProbeHintFor(LiveEncodingSettings encoding)
    {
        // The Pi's V4L2 encoder is left on its own profile, so none is claimed for it.
        string profile = encoding.Video.Encoder.Name == "h264_v4l2m2m" ? "" : ",\"profile\":\"high\"";
        string video = string.Create(CultureInfo.InvariantCulture,
            $"\"stream_index\":0,\"codec\":\"h264\",\"width\":{encoding.Width},\"height\":{encoding.Height},\"pix_fmt\":\"yuv420p\",\"frame_rate\":\"{encoding.FrameRate}\"{profile}");
        return "{\"format_name\":\"mpegts\",\"video\":[{" + video + "}],\"audio\":[{\"stream_index\":1,\"codec\":\"aac\",\"channels\":2}]}";
    }

    private const int PacketSize = 188;
    private static readonly string[] MusicExtensions = [".mp3", ".m4a", ".aac", ".ogg", ".opus", ".flac", ".wav"];

    private readonly ILogger _logger;
    private readonly CancellationTokenSource _life = new();
    private readonly List<Channel<byte[]>> _viewers = [];
    private readonly string _workFolder;
    private Process? _ffmpeg;
    private HeadlessBrowser? _browser;
    private byte[] _placeholder = [];
    private volatile byte[]? _latest;
    private long _lastFrameAt;
    private long _startedAt;
    private volatile bool _drawing;
    private Task[] _tasks = [];

    private LiveStream(LiveStreamSpec spec, ILogger logger)
    {
        Spec = spec;
        RequestedFingerprint = spec.Fingerprint;
        _logger = logger;
        _workFolder = Path.Combine(Path.GetTempPath(), "schedularrtv-live", $"{spec.Id}-{Guid.NewGuid():N}");
    }

    /// <summary>What's playing: as asked for, less any channel that couldn't be reached.</summary>
    public LiveStreamSpec Spec { get; private set; }

    /// <summary>The fingerprint of the spec as asked for, to tell when the settings have changed since.</summary>
    public string RequestedFingerprint { get; }

    private const string NotDrawing = "The page hasn't drawn anything yet.";

    /// <summary>The latest frame the page drew, for previews; null until it draws.</summary>
    public byte[]? LatestFrame => _latest;

    /// <summary>Why something isn't showing (no browser, a page or channel that can't be reached), or null.</summary>
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
    public event Action<LiveStream>? Stopped;

    public static async Task<LiveStream> StartAsync(LiveStreamSpec spec, ILogger logger, CancellationToken cancellationToken)
    {
        var stream = new LiveStream(spec, logger);
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
        _placeholder = await PlaceholderAsync(Spec.Ffmpeg, Spec.PageOnTop, cancellationToken).ConfigureAwait(false);

        // A channel that can't be reached would stop ffmpeg from starting at all, so check first and leave it out.
        List<string> problems = [];
        string? unreachable = null;
        if (Spec.WindowStreamUrl is { } window && !await ReachableAsync(window, cancellationToken).ConfigureAwait(false))
        {
            problems.Add("The channel for the video window isn't playing, so the window is left empty.");
            Spec = Spec with { WindowStreamUrl = null };
            unreachable = window;
        }

        // The window's channel already failed (up to 35 seconds): don't wait for it a second time for its sound.
        if (Spec.Audio == LiveAudio.Stream && Spec.AudioStreamUrl is { } sound && sound != Spec.WindowStreamUrl
            && (sound == unreachable || !await ReachableAsync(sound, cancellationToken).ConfigureAwait(false)))
        {
            problems.Add("The channel to listen to isn't playing; using music instead.");
            Spec = Spec with { Audio = LiveAudio.Music };
        }

        var start = new ProcessStartInfo(Spec.Ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in Arguments(Spec, MusicPlaylist()))
        {
            start.ArgumentList.Add(argument);
        }

        _ffmpeg = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start ffmpeg ({Spec.Ffmpeg}).");
        _ffmpeg.ErrorDataReceived += (_, line) =>
        {
            if (!string.IsNullOrWhiteSpace(line.Data))
            {
                _logger.LogDebug("ffmpeg ({Stream}): {Line}", Spec.Id, line.Data);
                lock (_said)
                {
                    if (_firstError is null && LooksLikeError(line.Data))
                    {
                        _firstError = line.Data.Trim();
                    }

                    _said.Enqueue(line.Data.Trim());
                    while (_said.Count > 12)
                    {
                        _said.Dequeue();
                    }
                }
            }
        };
        _ffmpeg.BeginErrorReadLine();
        Problem = problems.Count > 0 ? string.Join(" ", problems) : null;

        CancellationToken token = _life.Token;
        _tasks =
        [
            Task.Run(() => PumpFramesAsync(token), CancellationToken.None),
            Task.Run(() => ReadOutputAsync(token), CancellationToken.None),
            Task.Run(() => KeepBrowserAsync(token), CancellationToken.None)
        ];
    }

    private static async Task<bool> ReachableAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            // ErsatzTV waits up to 30 seconds for a channel to be ready before answering.
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(35) };
            using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// The ffmpeg command: paced JPEG frames of the page in (input 0), the window's stream scaled over it, the chosen
    /// sound (music, a stream, or silence), MPEG-TS out. <paramref name="playlist"/> is the music list, if there is one.
    /// </summary>
    public static IReadOnlyList<string> Arguments(LiveStreamSpec spec, string? playlist)
    {
        string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        LiveEncodingSettings encoding = spec.Encoding;
        int fps = encoding.FrameRate;
        List<string> arguments =
        [
            "-hide_banner", "-loglevel", "warning", "-nostdin",
            "-f", "image2pipe", "-c:v", spec.PageOnTop ? "png" : "mjpeg", "-framerate", Math.Clamp(spec.CaptureFrameRate, 5, 30).ToString(CultureInfo.InvariantCulture)
        ];

        // A PNG can come as RGBA or RGB (the browser drops the alpha channel when a picture is fully opaque, and the
        // placeholder is ffmpeg's own). Rebuilding the whole filter graph for that, mid-stream, fails with "Invalid
        // argument" once the window's stream is in it; the page's first filter (scale) copes with the change by itself.
        if (spec.PageOnTop)
        {
            arguments.AddRange(["-reinit_filter", "0"]);
        }

        arguments.AddRange(["-i", "pipe:0"]);

        int next = 1;
        int? window = null;
        if (spec.WindowStreamUrl is { } windowUrl && spec.Window is not null)
        {
            if (spec.HardwareDecoding is { Length: > 0 } hwaccel)
            {
                arguments.AddRange(["-hwaccel", hwaccel]);
            }

            arguments.AddRange([.. spec.InputOptions(), "-i", windowUrl]);
            window = next++;
        }

        int audio;
        string audioFilter;
        if (spec.Audio == LiveAudio.Stream && spec.AudioStreamUrl is { } soundUrl)
        {
            if (window is { } shared && soundUrl == spec.WindowStreamUrl)
            {
                audio = shared;
            }
            else
            {
                arguments.AddRange([.. spec.InputOptions(), "-i", soundUrl]);
                audio = next++;
            }

            // apad: if the channel drops out, the stream carries on with silence rather than losing its sound track.
            audioFilter = $"[{audio}:a]asetpts=PTS-STARTPTS,aresample=48000:async=1,aformat=channel_layouts=stereo,volume={Number(Math.Clamp(spec.Volume, 0, 2))},apad[a]";
        }
        else if (spec.Audio == LiveAudio.Music && playlist is not null)
        {
            arguments.AddRange(["-stream_loop", "-1", "-f", "concat", "-safe", "0", "-i", playlist]);
            audio = next++;
            audioFilter = $"[{audio}:a]aresample=48000,aformat=channel_layouts=stereo,volume={Number(Math.Clamp(spec.Volume, 0, 2))}[a]";
        }
        else
        {
            arguments.AddRange(["-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"]);
            audio = next++;
            audioFilter = $"[{audio}:a]anull[a]";
        }

        // Every picture is turned into the format it's finally mixed in as early as possible, in the same pass as any
        // scaling, and before fps repeats it: a page captured at 15 frames a second is converted 15 times a second, not 30.
        List<string> graph = [];
        string current;
        if (spec.PageOnTop)
        {
            // A plain dark background (made once by ffmpeg, at the stream's rate); the window's stream goes over that,
            // and the page (see-through where it draws nothing) over both. The page needs no fps of its own: overlay
            // shows its latest frame under each background frame, and waits for it, so the page still keeps the timing.
            graph.Add($"color=c=0x0b0d12:s={Width}x{Height}:r={fps},format=yuv420p[base]");
            graph.Add($"[0:v]scale={Width}:{Height}:force_original_aspect_ratio=decrease:force_divisible_by=2,format=yuva420p,"
                      + $"pad={Width}:{Height}:(ow-iw)/2:(oh-ih)/2:color=black@0,setsar=1[pagetop]");
            current = "[base]";
        }
        else
        {
            graph.Add($"[0:v]scale={Width}:{Height}:force_original_aspect_ratio=decrease:force_divisible_by=2,format=yuv420p,"
                      + $"pad={Width}:{Height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={fps}[page]");
            current = "[page]";
        }

        if (window is { } windowInput && spec.Window is { } asked)
        {
            // Even sizes only: 4:2:0 video can't be an odd number of pixels high or wide, and scale rounds such a size up,
            // past the pad that should hold it ("Padded dimensions cannot be smaller than input dimensions").
            // fps comes first, so a 60 frames a second stream has only the frames that are kept scaled.
            FrameRect rect = asked with { Width = Math.Max(2, asked.Width / 2 * 2), Height = Math.Max(2, asked.Height / 2 * 2) };
            graph.Add($"[{windowInput}:v]setpts=PTS-STARTPTS,fps={fps},scale={rect.Width}:{rect.Height}:force_original_aspect_ratio=decrease:force_divisible_by=2,"
                      + $"format=yuv420p,pad={rect.Width}:{rect.Height}:(ow-iw)/2:(oh-ih)/2,setsar=1[window]");
            graph.Add($"{current}[window]overlay={rect.X}:{rect.Y}:eof_action=pass:shortest=0[windowed]");
            current = "[windowed]";
        }

        if (spec.PageOnTop)
        {
            graph.Add($"{current}[pagetop]overlay=0:0:eof_action=pass:shortest=0:format=auto[paged]");
            current = "[paged]";
        }

        if (spec.Visualizer is { Length: > 0 } visualizer && spec.VisualizerRect is { } box)
        {
            // The sound is split: one copy is heard, the other drawn. Black in the drawing is made see-through.
            audioFilter = audioFilter[..^"[a]".Length] + ",asplit=2[a][drawn]";
            graph.Add($"[drawn]{visualizer},scale={box.Width}:{box.Height},format=rgba,colorkey=0x000000:0.08:0.12[visual]");
            graph.Add($"{current}[visual]overlay={box.X}:{box.Y}:eof_action=pass:shortest=0[visualized]");
            current = "[visualized]";
        }

        // A smaller stream (the low-power default) is laid out at full size and scaled once at the end, so every
        // rectangle above stays in 1280×720 terms.
        string resize = encoding.Height != Height ? $"scale={encoding.Width}:{encoding.Height}:flags=bilinear," : "";
        graph.Add($"{current}{resize}format=yuv420p[v]");
        graph.Add(audioFilter);

        string gop = (fps * 2).ToString(CultureInfo.InvariantCulture);
        arguments.AddRange(["-filter_complex", string.Join(';', graph), "-map", "[v]", "-map", "[a]"]);
        arguments.AddRange(["-r", fps.ToString(CultureInfo.InvariantCulture)]);
        arguments.AddRange(encoding.Video.VideoArguments(encoding.BitrateKbps));
        arguments.AddRange(
        [
            "-g", gop, "-keyint_min", gop, "-sc_threshold", "0",
            "-c:a", "aac", "-b:a", "160k", "-ar", "48000", "-ac", "2",
            "-f", "mpegts", "-mpegts_flags", "+resend_headers", "-muxdelay", "0", "pipe:1"
        ]);
        return arguments;
    }

    /// <summary>The songs in a fresh random order, as an ffmpeg concat list; null for none.</summary>
    private string? MusicPlaylist()
    {
        if (Spec.Audio != LiveAudio.Music || string.IsNullOrWhiteSpace(Spec.MusicFolder) || !Directory.Exists(Spec.MusicFolder))
        {
            return null;
        }

        string[] songs = [.. Directory.EnumerateFiles(Spec.MusicFolder, "*", SearchOption.AllDirectories)
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

    /// <summary>A plain dark frame (or, for a page drawn on top, a see-through one), shown until the page has drawn.</summary>
    private static async Task<byte[]> PlaceholderAsync(string ffmpeg, bool transparent, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        string[] frame = transparent
            ? ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", $"color=c=black@0.0:s={Width}x{Height},format=rgba",
                "-frames:v", "1", "-c:v", "png", "-f", "image2pipe", "pipe:1"]
            : ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", $"color=c=0x101828:s={Width}x{Height}",
                "-frames:v", "1", "-f", "mjpeg", "-q:v", "3", "pipe:1"];
        foreach (string argument in frame)
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

    /// <summary>Writes the newest frame at a steady rate, so the stream keeps time even when the page draws nothing new.</summary>
    private async Task PumpFramesAsync(CancellationToken cancellationToken)
    {
        Stream input = _ffmpeg!.StandardInput.BaseStream;
        int rate = Math.Clamp(Spec.CaptureFrameRate, 5, 30);
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
                _logger.LogWarning("Live channel {Stream}: ffmpeg stopped taking frames. It said: {Said}", Spec.Name, Said());
                Problem = $"ffmpeg stopped: {Said()}";
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
            // A moment for its last words to arrive.
            await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);
            _logger.LogWarning("Live channel {Stream}: ffmpeg ended (exit code {Code}). It said: {Said}", Spec.Name,
                _ffmpeg.HasExited ? _ffmpeg.ExitCode : -1, Said());
            Problem = $"ffmpeg stopped: {Said()}";
            End();
        }
    }

    // The last lines ffmpeg printed (warnings and errors only), for when it stops.
    private readonly Queue<string> _said = new();

    // The first error it gave (usually the cause; what follows is the fallout), then its last few lines.
    private string Said()
    {
        lock (_said)
        {
            if (_said.Count == 0)
            {
                return "nothing";
            }

            string? cause = _firstError;
            IEnumerable<string> last = _said.TakeLast(3).Where(line => line != cause);
            return string.Join(" | ", (cause is null ? [] : new[] { cause }).Concat(last));
        }
    }

    private string? _firstError;

    private static bool LooksLikeError(string line) =>
        line.Contains("rror", StringComparison.Ordinal) || line.Contains("Invalid", StringComparison.Ordinal)
        || line.Contains("failed", StringComparison.OrdinalIgnoreCase) || line.Contains("not ", StringComparison.Ordinal);

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

    /// <summary>Starts the browser, and again whenever it goes away or stops drawing, for as long as the stream runs.</summary>
    private async Task KeepBrowserAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            bool stalled = _browser is not null && Environment.TickCount64 - Interlocked.Read(ref _lastFrameAt) > 60_000;
            if (stalled)
            {
                _logger.LogWarning("Live channel {Stream}: the page stopped drawing; starting the browser again.", Spec.Name);
            }

            if (_browser is null || _browser.HasExited || stalled)
            {
                if (_browser is not null)
                {
                    await _browser.DisposeAsync().ConfigureAwait(false);
                    _browser = null;
                }

                IReadOnlyList<string> browsers = BrowserLocator.FindAll(Spec.BrowserPath);
                if (browsers.Count == 0)
                {
                    Problem = "No Chrome, Edge or Chromium found. Install one, or choose it in the plugin's settings.";
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // Each installed browser in turn: one that won't start headless here (Edge with some policies, say)
                // shouldn't keep the channel dark when another would.
                List<string> problems = [];
                foreach (string browser in browsers)
                {
                    try
                    {
                        // Frames beyond the capture rate would only be thrown away, so the browser isn't asked for them.
                        _browser = await HeadlessBrowser.StartAsync(browser, Spec.PageUrl, Width, Height, cancellationToken, transparent: Spec.PageOnTop,
                                maxFrameRate: Math.Clamp(Spec.CaptureFrameRate, 5, 30))
                            .ConfigureAwait(false);
                        _screencastSeen = 0;
                        _settled = false;
                        Interlocked.Exchange(ref _lastFrameAt, Environment.TickCount64);
                        _startedAt = Environment.TickCount64;
                        _drawing = false;
                        _browser.Frame += OnFrame;
                        _logger.LogInformation("Live channel {Stream}: {Browser} started on {Page}.", Spec.Name, Path.GetFileName(browser), Spec.PageUrl);
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        problems.Add(ex.Message);
                        _logger.LogWarning("Live channel {Stream}: {Browser} couldn't show the page: {Message}", Spec.Name, browser, ex.Message);
                    }
                }

                if (_browser is null)
                {
                    Problem = $"The browser couldn't show the page: {string.Join(" Then: ", problems)}";
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            await CatchUpAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnFrame(byte[] frame)
    {
        _latest = frame;
        Interlocked.Exchange(ref _lastFrameAt, Environment.TickCount64);
        if (!_drawing)
        {
            _drawing = true;
            // Problems with drawing are over; ones about a channel that couldn't be reached still stand.
            if (Problem == NotDrawing || Problem?.StartsWith("The browser", StringComparison.Ordinal) == true)
            {
                Problem = null;
            }

            _logger.LogInformation("Live channel {Stream}: the page is on screen.", Spec.Name);
        }
    }

    // Screencast frames counted at the last check, and whether a picture has been taken since the last of them.
    private long _screencastSeen;
    private bool _settled;

    /// <summary>
    /// Takes a picture when the screencast has gone quiet; says why if nothing has come for a while. With the screencast
    /// working, that's once after each burst of frames (the browser may have drawn the last one while its frames were
    /// held back to the capture rate), and then only now and then while the page is still; without it, every second.
    /// </summary>
    private async Task CatchUpAsync(CancellationToken cancellationToken)
    {
        if (_browser is not { HasExited: false } browser)
        {
            return;
        }

        long quiet = Environment.TickCount64 - Interlocked.Read(ref _lastFrameAt);
        long screencast = browser.ScreencastFrames;
        if (screencast != _screencastSeen)
        {
            _screencastSeen = screencast;
            // Only a frame acknowledged late can have hidden a later one; otherwise the screencast already sent the last.
            _settled = !browser.MayHaveHeldBackFrame;
        }

        bool due = screencast == 0 ? quiet >= 1_000
            : !_settled ? quiet >= 300
            : quiet >= 20_000;
        if (!due)
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
                _settled = true;
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Live channel {Stream}: couldn't take a picture of the page.", Spec.Name);
        }

        if (!_drawing && Environment.TickCount64 - _startedAt > 20_000 && Problem is null)
        {
            Problem = NotDrawing;
            _logger.LogWarning("Live channel {Stream}: the browser has drawn nothing in 20 seconds.", Spec.Name);
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
        CloseViewers();
        Stopped?.Invoke(this);
    }

    private void CloseViewers()
    {
        lock (_viewers)
        {
            foreach (Channel<byte[]> viewer in _viewers)
            {
                viewer.Writer.TryComplete();
            }

            _viewers.Clear();
        }
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (!_life.IsCancellationRequested)
        {
            _life.Cancel();
            CloseViewers();
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
            _logger.LogDebug(ex, "Live channel {Stream}: a background task ended badly while stopping.", Spec.Name);
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
