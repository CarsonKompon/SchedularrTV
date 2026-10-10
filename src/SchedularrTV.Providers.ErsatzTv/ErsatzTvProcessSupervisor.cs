using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SchedularrTV.Providers.ErsatzTv;

/// <summary>
/// Runs ErsatzTV Next as a supervised child process: started only once a playout exists, restarted
/// if it dies, and killed (with its ffmpeg children) when the scheduler shuts down.
/// </summary>
/// <remarks>
/// Next reads its channel list once at startup, so the supervisor also restarts it when the set of
/// channels changes. Playout content changes are picked up from disk and need no restart.
/// </remarks>
public sealed class ErsatzTvProcessSupervisor(
    IOptions<ErsatzTvOptions> options,
    ILogger<ErsatzTvProcessSupervisor> logger) : BackgroundService
{
    // Read on each use: settings saved in the app apply straight away.
    private ErsatzTvOptions _options => options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TaskCompletionSource _lineupReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Process? _process;
    private string? _channelFingerprint;
    private volatile bool _restartRequested;

    /// <summary>True while the supervised ErsatzTV process is alive.</summary>
    public bool IsRunning
    {
        get
        {
            try
            {
                return _process is { HasExited: false };
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>The supervised ErsatzTV process's id while it's alive; null when it isn't running or isn't managed here.</summary>
    public int? ProcessId
    {
        get
        {
            try
            {
                return _process is { HasExited: false } process ? process.Id : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <summary>Signals that a lineup has been published and the process may start.</summary>
    public void NotifyLineupPublished(string channelFingerprint)
    {
        if (_channelFingerprint is not null && _channelFingerprint != channelFingerprint)
        {
            logger.LogInformation("Channel list changed; ErsatzTV will be restarted to pick it up.");
            _restartRequested = true;
        }

        _channelFingerprint = channelFingerprint;
        _lineupReady.TrySetResult();
    }

    // What the running process was started with; changing any of it in App settings starts it again (or stops it).
    private string Settings() => $"{_options.ManageProcess}|{_options.ExecutablePath}|{_options.ConfigRoot}";

    private string? _startedWith;

    /// <summary>
    /// Waits until managing ErsatzTV is switched on with a program that exists, so turning it on in App settings
    /// takes effect without a restart. Returns false when the scheduler is stopping.
    /// </summary>
    private async Task<bool> WaitUntilWantedAsync(CancellationToken stoppingToken)
    {
        string? reported = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_options.ManageProcess)
            {
                if (!string.IsNullOrWhiteSpace(_options.ExecutablePath) && File.Exists(_options.ExecutablePath))
                {
                    return true;
                }

                if (reported != _options.ExecutablePath)
                {
                    reported = _options.ExecutablePath;
                    logger.LogError(
                        "ErsatzTv:ManageProcess is on but ErsatzTv:ExecutablePath does not exist: '{Path}'.",
                        _options.ExecutablePath);
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await WaitUntilWantedAsync(stoppingToken).ConfigureAwait(false))
        {
            return;
        }

        // Starting before the first publish would load an empty or stale channel list.
        try
        {
            await _lineupReady.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await WaitUntilWantedAsync(stoppingToken).ConfigureAwait(false))
            {
                break;
            }

            int exitCode;
            try
            {
                exitCode = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not start ErsatzTV; retrying.");
                exitCode = -1;
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (_startedWith != Settings())
            {
                // Settings changed: start again at once with the new ones, or wait if managing was switched off.
                logger.LogInformation(_options.ManageProcess ? "ErsatzTV settings changed; starting it again." : "Stopped managing ErsatzTV.");
                _restartRequested = false;
                continue;
            }

            if (_restartRequested)
            {
                _restartRequested = false;
                logger.LogInformation("Restarting ErsatzTV for the new channel list.");
            }
            else
            {
                logger.LogWarning("ErsatzTV exited with code {Code}; restarting in {Delay}.", exitCode, _options.RestartDelay);
            }

            try
            {
                await Task.Delay(_options.RestartDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<int> RunOnceAsync(CancellationToken stoppingToken)
    {
        _startedWith = Settings();
        string lineupPath = Path.GetFullPath(Path.Combine(_options.ConfigRoot, "lineup.json"));

        var startInfo = new ProcessStartInfo
        {
            FileName = _options.ExecutablePath,
            WorkingDirectory = Path.GetFullPath(_options.ConfigRoot),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add(lineupPath);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Log(e.Data);
        process.ErrorDataReceived += (_, e) => Log(e.Data);

        await _gate.WaitAsync(stoppingToken).ConfigureAwait(false);
        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
        }
        finally
        {
            _gate.Release();
        }

        logger.LogInformation("Started ErsatzTV (pid {Pid}) with {Lineup}.", process.Id, lineupPath);

        using var restartPoll = new CancellationTokenSource();
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, restartPoll.Token);

        Task exited = process.WaitForExitAsync(CancellationToken.None);
        Task restartWatch = WatchForRestartAsync(linked.Token);

        await Task.WhenAny(exited, restartWatch).ConfigureAwait(false);
        restartPoll.Cancel();

        if (!process.HasExited)
        {
            Terminate(process);
            await exited.ConfigureAwait(false);
        }

        // Read before disposing; the handle is gone afterwards.
        int exitCode = process.ExitCode;

        _process = null;
        process.Dispose();

        return exitCode;
    }

    private async Task WatchForRestartAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_restartRequested && _startedWith == Settings())
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        Process? process = _process;
        if (process is { HasExited: false })
        {
            Terminate(process);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting down anyway; the tree kill has already been issued.
            }
        }
    }

    private void Terminate(Process process)
    {
        try
        {
            // ErsatzTV spawns ersatztv-channel, which spawns ffmpeg; killing only the parent
            // would leave transcoders running and the HLS folder locked.
            process.Kill(entireProcessTree: true);
            logger.LogInformation("Stopped ErsatzTV (pid {Pid}) and its child processes.", process.Id);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(ex, "Could not stop the ErsatzTV process cleanly.");
        }
    }

    private void Log(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (line.Contains("ERROR", StringComparison.Ordinal))
        {
            logger.LogError("[ersatztv] {Line}", line);
        }
        else if (line.Contains("WARN", StringComparison.Ordinal))
        {
            logger.LogWarning("[ersatztv] {Line}", line);
        }
        else
        {
            logger.LogDebug("[ersatztv] {Line}", line);
        }
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }
}
