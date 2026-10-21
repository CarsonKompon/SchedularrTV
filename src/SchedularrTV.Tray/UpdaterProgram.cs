using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SchedularrTV.Hosting;

namespace SchedularrTV.Tray;

/// <summary>
/// The tray program started as the update installer (<c>--apply-update</c>), from its temporary copy. The install
/// itself runs on its own, independent of any window: a small window follows <see cref="UpdateApplier"/> as it waits
/// for the app to close, downloads, installs and starts it again, but if that window can't be shown, crashes or is
/// closed, the install carries on to the end regardless (half an install is worse than no window). Without a screen (a
/// headless Linux box) progress goes to the console. Whatever happens is written to the update log.
/// </summary>
internal static class UpdaterProgram
{
    public static int Run(string[] args, string requestPath)
    {
        string? root = AppPaths.Argument(args, UpdateRequest.AppRootArgument);
        string? data = AppPaths.Argument(args, AppPaths.DataDirectoryArgument);
        int waitFor = int.TryParse(AppPaths.Argument(args, UpdateRequest.WaitForArgument), out int pid) ? pid : -1;
        if (root is null || data is null || UpdateRequest.Load(requestPath) is not { } request)
        {
            Console.Error.WriteLine("The update request is incomplete; nothing was changed.");
            return 2;
        }

        var applier = new UpdateApplier(request, requestPath, root, data, waitFor);

        // Anything that would end this process unannounced is at least written down.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => applier.Log($"The installer crashed: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            applier.Log($"Unobserved installer error: {e.Exception}");
            e.SetObserved();
        };

        var progress = new UpdateProgressRelay();
        Task<UpdateOutcome> install = Task.Run(() => applier.RunAsync(progress, CancellationToken.None));

        bool hasScreen = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        if (hasScreen)
        {
            try
            {
                AppBuilder.Configure(() => new UpdaterApplication(progress, install, request))
                    .UsePlatformDetect()
                    .LogToTrace()
                    .StartWithClassicDesktopLifetime(args, ShutdownMode.OnMainWindowClose);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                applier.Log($"The update window failed ({ex.GetBaseException().Message}); carrying on without it.");
            }
            finally
            {
                progress.Detach();
            }
        }
        else
        {
            progress.Attach(step =>
                Console.WriteLine(step.Fraction is { } fraction ? $"{step.Title} {step.Detail} {fraction:P0}" : $"{step.Title} {step.Detail}"));
        }

        // The window is gone (or there never was one): the install still runs to its end, relaunch included.
        UpdateOutcome outcome = install.GetAwaiter().GetResult();
        Console.WriteLine(outcome.Message);
        return outcome.Succeeded ? 0 : 1;
    }
}

/// <summary>
/// Progress from the install to whoever is watching: the window once it's up, the console, or nobody. The latest step
/// is kept so a window that opens late starts from where the install has got to.
/// </summary>
internal sealed class UpdateProgressRelay : IProgress<UpdateProgress>
{
    private readonly Lock _gate = new();
    private Action<UpdateProgress>? _listener;
    private UpdateProgress? _last;

    public void Report(UpdateProgress value)
    {
        Action<UpdateProgress>? listener;
        lock (_gate)
        {
            _last = value;
            listener = _listener;
        }

        listener?.Invoke(value);
    }

    public void Attach(Action<UpdateProgress> listener)
    {
        UpdateProgress? last;
        lock (_gate)
        {
            _listener = listener;
            last = _last;
        }

        if (last is not null)
        {
            listener(last);
        }
    }

    public void Detach()
    {
        lock (_gate)
        {
            _listener = null;
        }
    }
}

/// <summary>The update window: what it's doing, a progress bar, and at the end what happened.</summary>
internal sealed class UpdaterApplication(UpdateProgressRelay progress, Task<UpdateOutcome> install, UpdateRequest request) : Application
{
    private TextBlock _title = null!;
    private TextBlock _detail = null!;
    private ProgressBar _bar = null!;
    private Button _close = null!;
    private Window _window = null!;

    public override void Initialize()
    {
        Name = "SchedularrTV update";
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _title = new TextBlock { Text = "Waiting for SchedularrTV to close…", FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        _detail = new TextBlock { Text = string.Empty, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        _bar = new ProgressBar { IsIndeterminate = true, Minimum = 0, Maximum = 1, Height = 8 };
        _close = new Button { Content = "Close", IsVisible = false, HorizontalAlignment = HorizontalAlignment.Right };
        _close.Click += (_, _) => _window.Close();

        _window = new Window
        {
            Title = $"Updating SchedularrTV to {Describe(request.Version)}",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            // Started by a background program (the tray), so Windows won't bring it forward by itself.
            Topmost = true,
            ShowActivated = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new StackPanel
            {
                Margin = new Thickness(22),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = $"Updating SchedularrTV to {Describe(request.Version)}", Opacity = 0.6, FontSize = 12 },
                    _title,
                    _bar,
                    _detail,
                    _close
                }
            }
        };

        // Closing mid-install would only hide the progress (the install carries on), so it isn't offered until the end.
        _window.Closing += (_, e) =>
        {
            if (!install.IsCompleted)
            {
                e.Cancel = true;
            }
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = _window;
        }

        _window.Opened += (_, _) =>
        {
            _window.Activate();
            progress.Attach(step => Dispatcher.UIThread.Post(() => Show(step)));
            _ = FinishAsync();
        };
        base.OnFrameworkInitializationCompleted();
    }

    private async Task FinishAsync()
    {
        UpdateOutcome outcome;
        try
        {
            outcome = await install.ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            outcome = new UpdateOutcome(false, $"The update failed: {ex.GetBaseException().Message}");
        }

        progress.Detach();
        _window.Topmost = false;
        _bar.IsIndeterminate = false;
        _bar.Value = outcome.Succeeded ? 1 : 0;
        _title.Text = outcome.Succeeded ? "SchedularrTV is up to date" : outcome.RolledBack ? "The update didn't work" : "The update couldn't be installed";
        _detail.Text = outcome.Message;
        if (outcome.Succeeded)
        {
            // Nothing to read on success: the window goes by itself.
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            _window.Close();
        }
        else
        {
            _close.IsVisible = true;
        }
    }

    private void Show(UpdateProgress step)
    {
        _title.Text = step.Title;
        _detail.Text = step.Detail ?? string.Empty;
        _bar.IsIndeterminate = step.Fraction is null;
        if (step.Fraction is { } fraction)
        {
            _bar.Value = Math.Clamp(fraction, 0, 1);
        }
    }

    private static string Describe(string version) =>
        version.Contains("-nightly.", StringComparison.Ordinal)
            ? $"nightly {version[(version.IndexOf("-nightly.", StringComparison.Ordinal) + 9)..]}"
            : $"v{version}";
}
