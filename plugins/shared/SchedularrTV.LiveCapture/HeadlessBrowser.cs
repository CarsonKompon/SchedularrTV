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
    public static async Task<HeadlessBrowser> StartAsync(string browser, string url, int width, int height, CancellationToken cancellationToken)
    {
        List<string> problems = [];
        foreach (string[] mode in Modes)
        {
            try
            {
                return await StartAsync(browser, mode, url, width, height, cancellationToken).ConfigureAwait(false);
            }
            catch (BrowserClosedException ex)
            {
                problems.Add(ex.Message);
            }
        }

        throw new InvalidOperationException(
            $"{Path.GetFileNameWithoutExtension(browser)} closed straight away each time it was started. {problems.LastOrDefault()}".Trim());
    }

    private static async Task<HeadlessBrowser> StartAsync(string browser, string[] mode, string url, int width, int height,
        CancellationToken cancellationToken)
    {
        string profile = Path.Combine(Path.GetTempPath(), "schedularrtv-browser", Guid.NewGuid().ToString("N"));
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

                     // Opened straight on the page rather than navigated there later: a navigation that moves the page to
                     // another process detaches it for a moment, and commands sent then fail with "Not attached to an
                     // active page".
                     url
                 ]))
        {
            start.ArgumentList.Add(argument);
        }

        Process process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start {browser}.");
        var browserProcess = new HeadlessBrowser(process, profile);
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

            // Without a screencast the stream takes a picture every second instead, so this isn't fatal either.
            try
            {
                await browserProcess.RetryAsync("Page.startScreencast", new JsonObject
                {
                    ["format"] = "jpeg",
                    ["quality"] = 85,
                    ["maxWidth"] = width,
                    ["maxHeight"] = height,
                    ["everyNthFrame"] = 1
                }, cancellationToken).ConfigureAwait(false);
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

                Handle(JsonNode.Parse(message.GetBuffer().AsSpan(0, (int)message.Length)) as JsonObject);
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
            if ((string?)frame["data"] is { } data)
            {
                Frame?.Invoke(Convert.FromBase64String(data));
            }

            // Frames stop coming until each one is acknowledged.
            if (frame["sessionId"] is JsonNode session)
            {
                _ = SendRawAsync(new JsonObject
                {
                    ["id"] = Interlocked.Increment(ref _nextId),
                    ["method"] = "Page.screencastFrameAck",
                    ["params"] = new JsonObject { ["sessionId"] = session.DeepClone() }
                }, _life.Token).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    /// <summary>
    /// Takes a picture of the page now. Screencast frames only come when Chrome repaints (and some setups send none
    /// at all), so the stream asks for one whenever the screencast has gone quiet.
    /// </summary>
    public async Task<byte[]?> CaptureAsync(CancellationToken cancellationToken)
    {
        JsonNode? result = await RetryAsync("Page.captureScreenshot", new JsonObject
        {
            ["format"] = "jpeg",
            ["quality"] = 85,
            ["fromSurface"] = true
        }, cancellationToken).ConfigureAwait(false);
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
