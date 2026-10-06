using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SchedularrTV.LiveCapture;

/// <summary>Finds a Chromium-based browser (Chrome, Edge, Chromium, Brave) already on this computer.</summary>
public static class BrowserLocator
{
    public static string? Find(string? configured) => FindAll(configured).FirstOrDefault();

    /// <summary>
    /// Every browser that could be used, best first: the configured one alone when set, otherwise each one installed,
    /// so another can be tried when the first won't start.
    /// </summary>
    public static IReadOnlyList<string> FindAll(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? [configured] : [];
        }

        List<string> found = [];
        foreach (string candidate in Candidates())
        {
            string? path = Path.IsPathRooted(candidate) ? (File.Exists(candidate) ? candidate : null) : OnPath(candidate);
            if (path is not null && !found.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(path);
            }
        }

        return found;
    }

    private static IEnumerable<string> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (string root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                     }.Where(root => root.Length > 0))
            {
                yield return Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe");
                yield return Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
                yield return Path.Combine(root, "Chromium", "Application", "chrome.exe");
                yield return Path.Combine(root, "BraveSoftware", "Brave-Browser", "Application", "brave.exe");
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
            yield return "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
            yield return "/Applications/Chromium.app/Contents/MacOS/Chromium";
            yield return "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser";
        }
        else
        {
            yield return "google-chrome";
            yield return "google-chrome-stable";
            yield return "chromium";
            yield return "chromium-browser";
            yield return "microsoft-edge";
            yield return "brave-browser";
        }
    }

    private static string? OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder, name))
            .FirstOrDefault(File.Exists);
}

/// <summary>
/// A hidden browser showing one page, sending what it draws as JPEG frames. Driven directly over the Chrome DevTools
/// Protocol (screencast, plus screenshots when the screencast goes quiet), so nothing beyond the browser itself has
/// to be installed.
/// </summary>
public sealed class HeadlessBrowser : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _profile;
    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _life = new();
    private readonly Dictionary<int, TaskCompletionSource<JsonNode?>> _pending = [];
    private readonly SemaphoreSlim _send = new(1, 1);
    private int _nextId;
    private Task? _receiver;
    private volatile bool _detached;

    private HeadlessBrowser(Process process, string profile)
    {
        _process = process;
        _profile = profile;
    }

    /// <summary>Raised from a background thread with each new frame the page draws.</summary>
    public event Action<byte[]>? Frame;

    /// <summary>How many screencast frames have arrived (0 on setups where the screencast sends none).</summary>
    public long ScreencastFrames => Interlocked.Read(ref _screencastFrames);

    private long _screencastFrames;

    // The shortest time between screencast frames: each frame is acknowledged no sooner than this after the last, and
    // Chrome sends nothing (and so encodes nothing) until it is. Zero acknowledges at once.
    private long _frameIntervalTicks;
    private long _lastFrameTimestamp;

    /// <summary>The browser closed, or the page went away for good (crashed, or the connection to it closed).</summary>
    public bool HasExited => _process.HasExited || _detached;

    /// <summary>
    /// Ways to start the browser headless, tried in turn when one closes straight away: the current headless mode;
    /// the plain switch (which some builds take where they refuse the other); and without the sandbox and GPU, which
    /// some locked-down or remote sessions need.
    /// </summary>
    private static readonly string[][] Modes =
    [
        ["--headless=new"],
        ["--headless"],
        ["--headless=new", "--no-sandbox", "--disable-gpu"]
    ];

    /// <summary>
    /// Starts <paramref name="browser"/> on the page, trying each way of starting it headless until one stays open.
    /// When none does, the error says what the browser printed, so the cause can be seen.
    /// </summary>
    /// <param name="transparent">
    /// Pictures are PNGs with a see-through background where the page draws none (for a page laid over a video).
    /// </param>
    /// <param name="maxFrameRate">
    /// The most screencast frames a second wanted (the capture rate); 0 for as many as the page draws. Chrome draws at
    /// up to 60 a second while anything moves, and encodes every frame it sends, so this saves most of its work.
    /// </param>
    /// <param name="paceAnimations">
    /// With <paramref name="maxFrameRate"/>, the page's animations are drawn at that rate too (see <see cref="PacingScript"/>).
    /// </param>
    public static async Task<HeadlessBrowser> StartAsync(string browser, string url, int width, int height, CancellationToken cancellationToken,
        bool transparent = false, int maxFrameRate = 0, bool paceAnimations = false)
    {
        List<string> problems = [];
        foreach (string[] mode in Modes)
        {
            try
            {
                HeadlessBrowser started = await StartAsync(browser, mode, url, width, height, transparent, maxFrameRate,
                    paceAnimations && maxFrameRate > 0, cancellationToken).ConfigureAwait(false);
                return started;
            }
            catch (BrowserClosedException ex)
            {
                problems.Add(ex.Message);
            }
        }

        throw new InvalidOperationException(
            $"{Path.GetFileNameWithoutExtension(browser)} closed straight away each time it was started. {problems.LastOrDefault()}".Trim());
    }

    private static async Task<HeadlessBrowser> StartAsync(string browser, string[] mode, string url, int width, int height, bool transparent,
        int maxFrameRate, bool paceAnimations, CancellationToken cancellationToken)
    {
        string profile = Path.Combine(BrowserProcesses.ProfilesRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);

        var start = new ProcessStartInfo(browser)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (string argument in mode.Concat(
                 [
                     "--remote-debugging-port=0", $"--user-data-dir={profile}", $"--window-size={width},{height}",
                     "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--disable-background-networking",
                     "--disable-background-timer-throttling", "--disable-renderer-backgrounding", "--disable-backgrounding-occluded-windows",
                     "--mute-audio", "--hide-scrollbars", "--autoplay-policy=no-user-gesture-required",

                     // Background work a hidden page never needs, which otherwise wakes the browser now and then.
                     "--disable-component-update", "--disable-sync", "--disable-default-apps", "--disable-breakpad",
                     "--disable-features=Translate,OptimizationHints,MediaRouter,DialMediaRouteProvider,AutofillServerCommunication",

                     // Opened straight on the page rather than navigated there later: a navigation that moves the page to
                     // another process detaches it for a moment, and commands sent then fail with "Not attached to an
                     // active page".
                     url
                 ]))
        {
            start.ArgumentList.Add(argument);
        }

        Process process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start {browser}.");

        // Ended with the scheduler, however it stops (and swept up at the next start if it somehow isn't).
        BrowserProcesses.Adopt(process, profile);
        var browserProcess = new HeadlessBrowser(process, profile)
        {
            _transparent = transparent,
            _frameIntervalTicks = maxFrameRate > 0 ? Stopwatch.Frequency / maxFrameRate : 0
        };
        process.OutputDataReceived += (_, e) => browserProcess.Remember(e.Data);
        process.ErrorDataReceived += (_, e) => browserProcess.Remember(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await browserProcess.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await browserProcess.RetryAsync("Page.enable", null, cancellationToken).ConfigureAwait(false);

            // The window is already this size; this just pins it. Pictures are still taken if it can't be set.
            try
            {
                await browserProcess.RetryAsync("Emulation.setDeviceMetricsOverride",
                    new JsonObject { ["width"] = width, ["height"] = height, ["deviceScaleFactor"] = 1, ["mobile"] = false }, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }

            if (transparent)
            {
                // What the page leaves clear stays clear in the pictures (the page's own background must be transparent too).
                // A browser that won't still draws the page, just not see-through.
                try
                {
                    await browserProcess.RetryAsync("Emulation.setDefaultBackgroundColorOverride",
                        new JsonObject { ["color"] = new JsonObject { ["r"] = 0, ["g"] = 0, ["b"] = 0, ["a"] = 0 } }, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                }
            }

            if (paceAnimations)
            {
                await browserProcess.PaceAsync(maxFrameRate, cancellationToken).ConfigureAwait(false);
            }

            // Without a screencast the stream takes a picture every second instead, so this isn't fatal either.
            try
            {
                JsonObject screencast = transparent
                    ? new JsonObject { ["format"] = "png" }
                    : new JsonObject { ["format"] = "jpeg", ["quality"] = 85 };
                screencast["maxWidth"] = width;
                screencast["maxHeight"] = height;
                screencast["everyNthFrame"] = 1;
                await browserProcess.RetryAsync("Page.startScreencast", screencast, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }

            return browserProcess;
        }
        catch
        {
            await browserProcess.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // Pictures are PNGs with a see-through background (see StartAsync).
    private bool _transparent;

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        // The browser writes the port it picked to DevToolsActivePort in its profile once it's listening.
        string portFile = Path.Combine(_profile, "DevToolsActivePort");
        DateTime giveUp = DateTime.UtcNow.AddSeconds(30);
        int port = 0;
        while (port == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process.HasExited)
            {
                // A moment for the last of what it printed to arrive.
                await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
                string said = Said();
                throw new BrowserClosedException(said.Length == 0
                    ? $"The browser closed straight away (exit code {_process.ExitCode}) without saying why."
                    : $"The browser closed straight away (exit code {_process.ExitCode}), saying: {said}");
            }

            if (DateTime.UtcNow > giveUp)
            {
                throw new TimeoutException("The browser didn't open its debugging port within 30 seconds.");
            }

            try
            {
                if (File.Exists(portFile) && int.TryParse((await File.ReadAllLinesAsync(portFile, cancellationToken).ConfigureAwait(false)).FirstOrDefault(), out int found))
                {
                    port = found;
                    break;
                }
            }
            catch (IOException)
            {
                // Still being written.
            }

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        string? socketUrl = null;
        for (int attempt = 0; attempt < 40 && socketUrl is null; attempt++)
        {
            JsonArray? targets = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", cancellationToken).ConfigureAwait(false)) as JsonArray;

            // The page itself, not a blank or internal one the browser may also have open.
            socketUrl = targets?.OfType<JsonObject>()
                .Where(target => (string?)target["type"] == "page")
                .OrderByDescending(target => ((string?)target["url"])?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true)
                .FirstOrDefault()?["webSocketDebuggerUrl"]?.GetValue<string>();
            if (socketUrl is null)
            {
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
        }

        if (socketUrl is null)
        {
            throw new InvalidOperationException("The browser has no page to draw in.");
        }

        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await _socket.ConnectAsync(new Uri(socketUrl), cancellationToken).ConfigureAwait(false);
        _receiver = Task.Run(() => ReceiveAsync(_life.Token));
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _output = new();

    /// <summary>Keeps the last few lines the browser printed, for when it won't start.</summary>
    private void Remember(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        _output.Enqueue(line.Trim());
        while (_output.Count > 12 && _output.TryDequeue(out _))
        {
        }
    }

    private string Said()
    {
        string text = string.Join(" | ", _output);
        return text.Length > 600 ? text[^600..] : text;
    }

    /// <summary>The browser process ended before it opened its debugging port.</summary>
    private sealed class BrowserClosedException(string message) : InvalidOperationException(message);

    private async Task<JsonNode?> SendAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int id = Interlocked.Increment(ref _nextId);
        lock (_pending)
        {
            _pending[id] = completion;
        }

        var message = new JsonObject { ["id"] = id, ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        await SendRawAsync(message, cancellationToken).ConfigureAwait(false);
        using CancellationTokenRegistration registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a command, trying again for a few seconds while the page is between processes (loading, or moved by a
    /// navigation), when the browser answers "Not attached to an active page".
    /// </summary>
    private async Task<JsonNode?> RetryAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                // Each attempt needs its own copy: a JsonNode can only belong to one message.
                return await SendAsync(method, (JsonObject?)parameters?.DeepClone(), cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (attempt < 30 && ex.Message.Contains("attached", StringComparison.OrdinalIgnoreCase) && !_detached)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task SendRawAsync(JsonObject message, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _send.Release();
        }
    }

    private async Task ReceiveAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[256 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                Dispatch(message.GetBuffer(), (int)message.Length);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or JsonException or ObjectDisposedException or FormatException)
        {
            // The browser went away; the stream notices and starts a new one.
        }
        finally
        {
            _detached = true;
            lock (_pending)
            {
                foreach (TaskCompletionSource<JsonNode?> waiting in _pending.Values)
                {
                    waiting.TrySetException(new InvalidOperationException("The browser connection closed."));
                }

                _pending.Clear();
            }
        }
    }

    private void Dispatch(byte[] buffer, int length)
    {
        ReadOnlySpan<byte> received = buffer.AsSpan(0, length);
        if (!TryHandleScreencastFrame(received))
        {
            Handle(JsonNode.Parse(received) as JsonObject);
        }
    }

    private void Handle(JsonObject? message)
    {
        if (message is null)
        {
            return;
        }

        if (message["id"] is JsonValue idValue && idValue.TryGetValue(out int id))
        {
            TaskCompletionSource<JsonNode?>? waiting;
            lock (_pending)
            {
                _pending.Remove(id, out waiting);
            }

            if (message["error"] is JsonNode error)
            {
                waiting?.TrySetException(new InvalidOperationException((string?)error["message"] ?? "The browser refused a command."));
            }
            else
            {
                waiting?.TrySetResult(message["result"]);
            }

            return;
        }

        if ((string?)message["method"] is "Inspector.detached" or "Inspector.targetCrashed")
        {
            // The page is gone for good; the stream starts a new browser.
            _detached = true;
            return;
        }

        if ((string?)message["method"] == "Page.screencastFrame" && message["params"] is JsonObject frame)
        {
            byte[]? picture = (string?)frame["data"] is { } data ? Convert.FromBase64String(data) : null;
            int? session = frame["sessionId"] is JsonValue value && value.TryGetValue(out int sessionId) ? sessionId : null;
            OnScreencastFrame(picture, session);
        }
    }

    private static ReadOnlySpan<byte> ScreencastMethod => "\"Page.screencastFrame\""u8;

    /// <summary>
    /// Handles a screencast frame straight from the bytes received: the picture (hundreds of kilobytes of base64, many
    /// times a second) is decoded in place, without building a JSON tree or a string of it first. False for any other
    /// message (or one this can't read), which is then handled the ordinary way.
    /// </summary>
    private bool TryHandleScreencastFrame(ReadOnlySpan<byte> message)
    {
        // Base64 has no quotes, so the method's name can't turn up inside the picture by chance.
        if (message.IndexOf(ScreencastMethod) < 0)
        {
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(message);
            bool isFrame = false;
            byte[]? picture = null;
            int? session = null;
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                // The message's own fields are at depth 1; params' (data, sessionId) at depth 2.
                int depth = reader.CurrentDepth;
                if (depth == 1 && reader.ValueTextEquals("method"u8))
                {
                    reader.Read();
                    isFrame = reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("Page.screencastFrame"u8);
                    if (!isFrame)
                    {
                        return false;
                    }
                }
                else if (depth == 2 && reader.ValueTextEquals("data"u8))
                {
                    reader.Read();
                    picture = reader.TokenType == JsonTokenType.String ? DecodeBase64(ref reader) : null;
                }
                else if (depth == 2 && reader.ValueTextEquals("sessionId"u8))
                {
                    reader.Read();
                    session = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int id) ? id : null;
                }
                else if (depth == 1 && reader.ValueTextEquals("params"u8))
                {
                    // Read into: data and sessionId are in here.
                }
                else
                {
                    // Anything else (the frame's metadata) is passed over whole.
                    reader.Read();
                    if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    {
                        reader.Skip();
                    }
                }
            }

            if (!isFrame)
            {
                return false;
            }

            OnScreencastFrame(picture, session);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return false;
        }
    }

    private static byte[] DecodeBase64(ref Utf8JsonReader reader)
    {
        if (reader.HasValueSequence || reader.ValueIsEscaped)
        {
            return Convert.FromBase64String(reader.GetString() ?? string.Empty);
        }

        ReadOnlySpan<byte> text = reader.ValueSpan;
        byte[] bytes = new byte[Base64.GetMaxDecodedFromUtf8Length(text.Length)];
        if (Base64.DecodeFromUtf8(text, bytes, out _, out int written) != OperationStatus.Done)
        {
            throw new FormatException("A screencast frame wasn't valid base64.");
        }

        return written == bytes.Length ? bytes : bytes.AsSpan(0, written).ToArray();
    }

    /// <summary>
    /// Passes a frame on, then acknowledges it: at once, or (with a frame rate set) once the frame's share of a second
    /// has gone by. Chrome sends no further frame until then, and those it would have sent in between are never encoded.
    /// </summary>
    private void OnScreencastFrame(byte[]? picture, int? session)
    {
        long now = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _screencastFrames);
        if (picture is { Length: > 0 })
        {
            Frame?.Invoke(picture);
        }

        if (session is not { } id)
        {
            return;
        }

        long wait = Interlocked.Read(ref _lastFrameTimestamp) + _frameIntervalTicks - now;
        Interlocked.Exchange(ref _lastFrameTimestamp, now);
        if (_frameIntervalTicks <= 0 || wait <= 0)
        {
            _heldBack = false;
            _ = AcknowledgeAsync(id, TimeSpan.Zero);
        }
        else
        {
            _heldBack = true;
            _ = AcknowledgeAsync(id, TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency));
        }
    }

    private volatile bool _heldBack;

    /// <summary>
    /// The latest screencast frame was acknowledged late (to keep to the frame rate), so the page may have drawn
    /// something since that the screencast never sent: a picture should be taken once it goes quiet.
    /// </summary>
    public bool MayHaveHeldBackFrame => _heldBack;

    // Frames stop coming until each one is acknowledged.
    private async Task AcknowledgeAsync(int session, TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _life.Token).ConfigureAwait(false);
            }

            await SendRawAsync(new JsonObject
            {
                ["id"] = Interlocked.Increment(ref _nextId),
                ["method"] = "Page.screencastFrameAck",
                ["params"] = new JsonObject { ["sessionId"] = session }
            }, _life.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            // The browser is going away.
        }
    }

    /// <summary>
    /// Has the page (and any it moves on to) draw its animations at <paramref name="frameRate"/>. Pacing that can't be
    /// set up just leaves the page drawing as often as it likes, as before.
    /// </summary>
    private async Task PaceAsync(int frameRate, CancellationToken cancellationToken)
    {
        string script = PacingScript(frameRate);
        try
        {
            // Pages loaded from now on (a reload, or a page that moves on), then the one already open.
            await RetryAsync("Page.addScriptToEvaluateOnNewDocument", new JsonObject { ["source"] = script }, cancellationToken).ConfigureAwait(false);
            await RetryAsync("Runtime.evaluate", new JsonObject { ["expression"] = script }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
        }
    }

    /// <summary>
    /// A script that makes a page draw only as often as it's captured. Chrome redraws up to 60 times a second while
    /// anything moves, and every redraw is layout, painting and compositing, for frames the capture never takes.
    /// <list type="bullet">
    /// <item>requestAnimationFrame callbacks run at most <paramref name="frameRate"/> times a second (still in step with a
    /// real frame, and with its timestamp, so anything timed by it moves at the same speed).</item>
    /// <item>CSS animations and transitions (and the Web Animations API's) are paused once seen, then moved on by hand at
    /// that rate: they keep their speed, place and events (one that reaches its end is finished, so animationend and
    /// transitionend still fire), but the browser has nothing to redraw in between.</item>
    /// </list>
    /// A page that pauses one of its own animations has it carry on here, which is why the plugins can switch pacing off.
    /// </summary>
    public static string PacingScript(int frameRate) => $$$"""
        (() => {
          if (window.__schedularrPacing) return;
          window.__schedularrPacing = true;
          const interval = 1000 / {{{Math.Clamp(frameRate, 1, 60)}}};

          // requestAnimationFrame, batched to the capture rate.
          const nativeFrame = window.requestAnimationFrame.bind(window);
          let queue = new Map(), running = null, nextId = 1, timer = 0, last = -Infinity;
          const flush = () => {
            timer = 0;
            last = performance.now();
            const due = running = queue;
            queue = new Map();
            nativeFrame(time => {
              for (const callback of due.values()) {
                try { callback(time); } catch (error) { setTimeout(() => { throw error; }); }
              }
              if (running === due) running = null;
            });
          };
          window.requestAnimationFrame = callback => {
            const id = nextId++;
            queue.set(id, callback);
            if (!timer) timer = setTimeout(flush, Math.max(0, last + interval - performance.now()));
            return id;
          };
          window.cancelAnimationFrame = id => { queue.delete(id); if (running) running.delete(id); };

          // CSS and Web Animations, stepped by hand.
          if (!document.getAnimations) return;
          const paced = new WeakSet();
          let previous = performance.now();
          setInterval(() => {
            const now = performance.now(), elapsed = now - previous;
            previous = now;
            for (const animation of document.getAnimations()) {
              try {
                const state = animation.playState;
                if (!paced.has(animation)) {
                  // Taken over where it has got to; one the page made paused is left alone.
                  if (state === "running") { paced.add(animation); animation.pause(); }
                  continue;
                }
                if (state === "running") { animation.pause(); continue; }
                if (state !== "paused") continue;
                const rate = animation.playbackRate;
                if (!rate) continue;
                const time = (animation.currentTime || 0) + elapsed * rate;
                const end = animation.effect ? animation.effect.getComputedTiming().endTime : Infinity;
                if ((rate > 0 && time >= end) || (rate < 0 && time <= 0)) animation.finish();
                else animation.currentTime = time;
              } catch { }
            }
          }, interval);
        })();
        """;

    /// <summary>
    /// Takes a picture of the page now. Screencast frames only come when Chrome repaints (and some setups send none
    /// at all), so the stream asks for one whenever the screencast has gone quiet.
    /// </summary>
    public async Task<byte[]?> CaptureAsync(CancellationToken cancellationToken)
    {
        // optimizeForSpeed: a quicker, lighter compression (browsers too old to know it ignore it).
        JsonNode? result = await RetryAsync("Page.captureScreenshot", _transparent
            ? new JsonObject { ["format"] = "png", ["fromSurface"] = true, ["optimizeForSpeed"] = true }
            : new JsonObject { ["format"] = "jpeg", ["quality"] = 85, ["fromSurface"] = true, ["optimizeForSpeed"] = true }, cancellationToken).ConfigureAwait(false);
        return (string?)result?["data"] is { Length: > 0 } data ? Convert.FromBase64String(data) : null;
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _life.Cancel();
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }

        _socket.Dispose();
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
        }

        _process.Dispose();
        if (_receiver is not null)
        {
            await _receiver.ConfigureAwait(false);
        }

        // The profile can stay locked for a moment after the browser exits.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(_profile))
                {
                    Directory.Delete(_profile, recursive: true);
                }

                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(400).ConfigureAwait(false);
            }
        }

        _life.Dispose();
        _send.Dispose();
    }
}
