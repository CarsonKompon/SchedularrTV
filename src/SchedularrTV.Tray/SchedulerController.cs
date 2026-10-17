using System.Diagnostics;
using System.Security.Cryptography;
using SchedularrTV.Hosting;

namespace SchedularrTV.Tray;

public enum ServerState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Failed
}

/// <summary>
/// Runs the scheduler server as a child process: started on demand, stopped cleanly through its control
/// endpoint (so ErsatzTV and ffmpeg go with it), restarted if it crashes after running for a while.
/// </summary>
public sealed class SchedulerController : IAsyncDisposable
{
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(30);
    private const long MaxLogBytes = 5 * 1024 * 1024;

    private readonly string[] _args;
    private readonly string _serverDirectory;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private Process? _process;
    private string? _token;
    private StreamWriter? _log;
    private CancellationTokenSource? _monitor;
    private DateTime _startedAt;
    private bool _stopping;
    private bool _external;

    public SchedulerController(string[] args, string serverDirectory, string dataDirectory)
    {
        _args = args;
        _serverDirectory = serverDirectory;
        DataDirectory = dataDirectory;
        Settings = new AppSettingsFile(AppPaths.SettingsPath(dataDirectory));
        _port = ConfiguredPort;
    }

    public string DataDirectory { get; }

    public AppSettingsFile Settings { get; }

    public string LogPath => Path.Combine(DataDirectory, "logs", "server.log");

    public string ExecutablePath => Path.Combine(_serverDirectory, AppPaths.ServerExecutableName);

    /// <summary>
    /// The port the server is actually on. Saving a new port only takes effect when the server restarts, so this
    /// follows the settings file only once the server answers there.
    /// </summary>
    private int _port;

    public string BrowseUrl => AppPaths.BrowseUrl(_port);

    private int ConfiguredPort => AppSettingsFile.Port(Settings.Load());

    public ServerState State { get; private set; } = ServerState.Stopped;

    public string? LastError { get; private set; }

    public event Action? StateChanged;

    /// <summary>Where the server leaves an update the user asked to install (see <see cref="UpdateRequest"/>).</summary>
    public string UpdateRequestPath => UpdateRequest.PathFor(DataDirectory);

    /// <summary>
    /// Raised (once) from the health check when the server has asked for an update to be installed: the tray then starts
    /// the installer and quits.
    /// </summary>
    public event Action? UpdateRequested;

    private bool _updateRaised;

    // Asked for in the last few minutes: an old request left behind (the tray wasn't running then) isn't acted on.
    private static readonly TimeSpan UpdateRequestFresh = TimeSpan.FromMinutes(15);

    private void CheckForUpdateRequest()
    {
        if (_updateRaised || UpdateRequested is null || !File.Exists(UpdateRequestPath))
        {
            return;
        }

        if (UpdateRequest.Load(UpdateRequestPath) is not { } request || DateTimeOffset.UtcNow - request.RequestedAt > UpdateRequestFresh)
        {
            try
            {
                File.Delete(UpdateRequestPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            return;
        }

        _updateRaised = true;
        UpdateRequested?.Invoke();
    }

    public async Task StartAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsAlive(_process) || State is ServerState.Running or ServerState.Starting)
            {
                return;
            }

            LastError = null;
            _stopping = false;
            _port = ConfiguredPort;

            // Already serving (started by hand, or left behind by an earlier tray): use it rather than fail on the port.
            if (await IsHealthyAsync().ConfigureAwait(false))
            {
                _external = true;
                SetState(ServerState.Running);
                StartMonitor();
                return;
            }

            _external = false;
            if (!File.Exists(ExecutablePath))
            {
                Fail($"The server program is missing: {ExecutablePath}");
                return;
            }

            _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            var info = new ProcessStartInfo(ExecutablePath)
            {
                WorkingDirectory = _serverDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in _args)
            {
                info.ArgumentList.Add(argument);
            }

            info.Environment[AppPaths.DataDirectoryVariable] = DataDirectory;
            info.Environment[AppPaths.ControlTokenVariable] = _token;
            info.Environment[AppPaths.ParentProcessVariable] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            info.Environment[AppPaths.TrayPathVariable] = Environment.ProcessPath ?? string.Empty;

            OpenLog();
            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => WriteLog(e.Data);
            process.ErrorDataReceived += (_, e) => WriteLog(e.Data);
            process.Exited += (_, _) => OnExited(process);

            try
            {
                process.Start();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                process.Dispose();
                Fail($"Couldn't start the server: {ex.Message}");
                return;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            _startedAt = DateTime.UtcNow;
            SetState(ServerState.Starting);
            StartMonitor();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _stopping = true;
            _monitor?.Cancel();
            Process? process = _process;

            if (_external || !IsAlive(process))
            {
                // A server this tray didn't start isn't this tray's to stop.
                _external = false;
                SetState(ServerState.Stopped);
                return;
            }

            SetState(ServerState.Stopping);
            Process running = process!;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{BrowseUrl}/api/app/shutdown");
                request.Headers.Add(AppPaths.ControlTokenHeader, _token);
                using HttpResponseMessage response = await _http.SendAsync(request).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Not answering; fall through to the kill below.
            }

            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try
            {
                await running.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                WriteLog("[tray] The server didn't stop in time; ending it.");
                try
                {
                    running.Kill(entireProcessTree: true);
                    await running.WaitForExitAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }

            SetState(ServerState.Stopped);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task RestartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await StartAsync().ConfigureAwait(false);
    }

    private void OnExited(Process process)
    {
        int code;
        try
        {
            code = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            code = -1;
        }

        bool wasStable = DateTime.UtcNow - _startedAt > StableAfter;
        lock (_gate)
        {
            if (ReferenceEquals(_process, process))
            {
                _process = null;
            }
        }

        // Not disposed here: StopAsync may still be waiting on it, and the handle is released by the finaliser.
        CloseLog();

        if (_stopping)
        {
            return;
        }

        _monitor?.Cancel();
        Fail($"The server stopped unexpectedly (exit code {code}). Details are in {LogPath}.");

        // A crash after a good run is worth retrying; a crash on startup would just loop.
        if (wasStable)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                if (!_stopping && State == ServerState.Failed)
                {
                    await StartAsync().ConfigureAwait(false);
                }
            });
        }
    }

    private static bool IsAlive(Process? process)
    {
        try
        {
            return process is { HasExited: false };
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void StartMonitor()
    {
        _monitor?.Cancel();
        var monitor = new CancellationTokenSource();
        _monitor = monitor;
        _ = Task.Run(async () =>
        {
            while (!monitor.IsCancellationRequested)
            {
                bool healthy = await IsHealthyAsync().ConfigureAwait(false);
                if (monitor.IsCancellationRequested)
                {
                    break;
                }

                if (healthy)
                {
                    SetState(ServerState.Running);
                    CheckForUpdateRequest();
                }
                else if (_external)
                {
                    // The server we adopted went away.
                    _external = false;
                    SetState(ServerState.Stopped);
                    break;
                }
                else if (State == ServerState.Running)
                {
                    // Still alive but not answering: usually a restart after saving app settings.
                    SetState(ServerState.Starting);
                }

                try
                {
                    await Task.Delay(HealthInterval, monitor.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });
    }

    /// <summary>Checks the current port, then the configured one in case a restart moved the server.</summary>
    private async Task<bool> IsHealthyAsync()
    {
        if (await AnswersAsync(_port).ConfigureAwait(false))
        {
            return true;
        }

        int configured = ConfiguredPort;
        if (configured != _port && await AnswersAsync(configured).ConfigureAwait(false))
        {
            _port = configured;
            return true;
        }

        return false;
    }

    private async Task<bool> AnswersAsync(int port)
    {
        try
        {
            using HttpResponseMessage response = await _http.GetAsync($"{AppPaths.BrowseUrl(port)}/api/health").ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private void SetState(ServerState state)
    {
        if (State == state && state != ServerState.Failed)
        {
            return;
        }

        State = state;
        if (state is ServerState.Running or ServerState.Starting)
        {
            LastError = null;
        }

        StateChanged?.Invoke();
    }

    private void Fail(string message)
    {
        LastError = message;
        WriteLog($"[tray] {message}");
        SetState(ServerState.Failed);
    }

    private void OpenLog()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                bool tooBig = File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes;
                _log?.Dispose();
                _log = new StreamWriter(new FileStream(LogPath, tooBig ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                _log.WriteLine($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss} starting {ExecutablePath}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log = null;
            }
        }
    }

    private void WriteLog(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                _log?.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }
    }

    private void CloseLog()
    {
        lock (_gate)
        {
            _log?.Dispose();
            _log = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _http.Dispose();
    }
}
