using System.Runtime.InteropServices;
using Avalonia;
using SchedularrTV.Hosting;

namespace SchedularrTV.Tray;

internal static class Program
{
    private const string NoTrayFlag = "--no-tray";

    [STAThread]
    private static int Main(string[] args)
    {
        // Started as the update installer, from a temporary copy, while the app itself closes.
        if (AppPaths.Argument(args, UpdateRequest.ApplyArgument) is { Length: > 0 } updateRequest)
        {
            return UpdaterProgram.Run(args, updateRequest);
        }

        // Installer copies left in the temporary folder by earlier updates.
        _ = Task.Run(UpdateLauncher.ClearOldCopies);

        string serverDirectory = Path.Combine(AppContext.BaseDirectory, "server");
        // The old Windows tray kept its schedule beside the exe; keep using it after an in-place upgrade.
        string legacyRoot = File.Exists(Path.Combine(AppContext.BaseDirectory, "config", "schedule.yaml")) ? AppContext.BaseDirectory : serverDirectory;
        string dataDirectory = AppPaths.ResolveDataDirectory(args, legacyRoot);
        string[] serverArgs = [.. args.Where(arg => !string.Equals(arg, NoTrayFlag, StringComparison.OrdinalIgnoreCase))];
        var server = new SchedulerController(serverArgs, serverDirectory, dataDirectory);

        // A second copy would fight over the port, the config file and ErsatzTV; open the running one instead.
        using var single = new Mutex(initiallyOwned: true, "SchedularrTV.Tray.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            TryOpen(server.BrowseUrl);
            return 0;
        }

        if (AppPaths.Flag(args, NoTrayFlag) || (OperatingSystem.IsLinux() && !HasDisplay()))
        {
            return RunWithoutTray(server, args);
        }

        try
        {
            AppBuilder.Configure(() => new TrayApplicationContext(server, args))
                .UsePlatformDetect()
                .With(new MacOSPlatformOptions { ShowInDock = false })
                .LogToTrace()
                .StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No tray support (a bare window manager, a missing library): keep the scheduler usable.
            Console.Error.WriteLine($"Couldn't show the tray icon ({ex.Message}); running without it.");
            return RunWithoutTray(server, args);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    private static bool HasDisplay() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    /// <summary>Runs the server in the foreground until Ctrl+C or a stop signal, restarting it if it crashes.</summary>
    private static int RunWithoutTray(SchedulerController server, string[] args)
    {
        // A Windows GUI program has no console of its own; borrow the one it was started from, if any.
        if (OperatingSystem.IsWindows())
        {
            AttachConsole(-1);
        }

        using var stop = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Set();
        };
        using PosixSignalRegistration terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            stop.Set();
        });

        server.StateChanged += () =>
        {
            if (server.LastError is { } error)
            {
                Console.Error.WriteLine(error);
            }
        };

        // An update asked for from the web UI: the installer takes over once this has stopped.
        server.UpdateRequested += () =>
        {
            if (UpdateLauncher.Launch(server.UpdateRequestPath, server.DataDirectory, args) is { } problem)
            {
                Console.Error.WriteLine(problem);
                return;
            }

            Console.WriteLine("Closing to install the update…");
            stop.Set();
        };

        server.StartAsync().GetAwaiter().GetResult();
        Console.WriteLine($"SchedularrTV is running at {server.BrowseUrl} (data in {server.DataDirectory}). Press Ctrl+C to stop.");
        stop.Wait();
        Console.WriteLine("Stopping…");
        server.StopAsync().GetAwaiter().GetResult();
        return 0;
    }

    private static void TryOpen(string url)
    {
        try
        {
            Autostart.OpenUrl(url);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Console.Error.WriteLine($"SchedularrTV is already running at {url}.");
        }
    }
}
