using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SchedularrTV.Hosting;

namespace SchedularrTV.Service.Hosting;

/// <summary>Facts about this running copy of the app, and the switch that restarts it in place.</summary>
public sealed class AppRuntime(string dataDirectory, string contentRoot, IHostApplicationLifetime lifetime, IConfiguration? configuration = null)
{
    public string DataDirectory { get; } = dataDirectory;

    public string ContentRoot { get; } = contentRoot;

    /// <summary>Differs for every host, including one restarted in place, so a browser can tell a restart has finished.</summary>
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    public AppSettingsFile Settings { get; } = new(AppPaths.SettingsPath(dataDirectory));

    /// <summary>Raised after <see cref="SaveSettings"/> has written settings.json and the configuration has re-read it.</summary>
    public event Action? SettingsSaved;

    /// <summary>
    /// Writes settings.json and re-reads it into the running configuration, so options (read through
    /// <c>IOptions</c>, which follows the configuration here) take the new values straight away.
    /// </summary>
    public void SaveSettings(System.Text.Json.Nodes.JsonObject file)
    {
        Settings.Save(file);
        (configuration as IConfigurationRoot)?.Reload();
        SettingsSaved?.Invoke();
    }

    public static string Version { get; } =
        typeof(AppRuntime).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppRuntime).Assembly.GetName().Version?.ToString()
        ?? "dev";

    /// <summary>True when the tray launched this server; the tray then owns starting and stopping it.</summary>
    public bool UnderTray => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AppPaths.TrayPathVariable));

    public bool RestartRequested { get; private set; }

    /// <summary>Stops the host; <c>Program</c> builds a fresh one, picking up changed settings.</summary>
    public void Restart()
    {
        RestartRequested = true;
        lifetime.StopApplication();
    }

    public void Shutdown()
    {
        RestartRequested = false;
        lifetime.StopApplication();
    }

    /// <summary>Checks the tray's control token in constant time; no token configured means no remote control.</summary>
    public static bool IsControlToken(string? presented)
    {
        string? expected = Environment.GetEnvironmentVariable(AppPaths.ControlTokenVariable);
        return !string.IsNullOrEmpty(expected) && presented is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(presented));
    }
}

/// <summary>Shuts the server down when the tray that launched it goes away, so it never lingers on its own.</summary>
public sealed class ParentProcessWatcher(AppRuntime runtime, ILogger<ParentProcessWatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable(AppPaths.ParentProcessVariable), out int pid))
        {
            return;
        }

        Process parent;
        try
        {
            parent = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            logger.LogWarning("The tray (pid {Pid}) has already exited; shutting down.", pid);
            runtime.Shutdown();
            return;
        }

        using (parent)
        {
            try
            {
                await parent.WaitForExitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        logger.LogWarning("The tray (pid {Pid}) exited; shutting down.", pid);
        runtime.Shutdown();
    }
}
