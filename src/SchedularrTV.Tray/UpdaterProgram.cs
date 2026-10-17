using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SchedularrTV.Hosting;

namespace SchedularrTV.Tray;

/// <summary>
/// The tray program started as the update installer (<c>--apply-update</c>), from its temporary copy: a small window
/// following <see cref="UpdateApplier"/> as it waits for the app to close, downloads, installs and starts it again.
/// Without a screen (a headless Linux box) it does the same, writing its progress to the console and the update log.
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
        bool hasScreen = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        if (hasScreen)
        {
            var app = new UpdaterApplication(applier, request);
            try
            {
                AppBuilder.Configure(() => app)
                    .UsePlatformDetect()
                    .LogToTrace()
                    .StartWithClassicDesktopLifetime(args, ShutdownMode.OnMainWindowClose);
                return app.Succeeded ? 0 : 1;
            }
            // A window that couldn't be shown before the install began: it runs without one instead (never twice).
            catch (Exception ex) when (ex is not OutOfMemoryException && !app.Started)
            {
                Console.Error.WriteLine($"Couldn't show the update window ({ex.Message}); installing without it.");
            }
        }

        UpdateOutcome outcome = applier.RunAsync(new Progress<UpdateProgress>(step =>
            Console.WriteLine(step.Fraction is { } fraction ? $"{step.Title} {step.Detail} {fraction:P0}" : $"{step.Title} {step.Detail}")), CancellationToken.None)
            .GetAwaiter().GetResult();
        Console.WriteLine(outcome.Message);
        return outcome.Succeeded ? 0 : 1;
    }
}

/// <summary>The update window: what it's doing, a progress bar, and at the end what happened.</summary>
internal sealed class UpdaterApplication(UpdateApplier applier, UpdateRequest request) : Application
{
    private TextBlock _title = null!;
    private TextBlock _detail = null!;
    private ProgressBar _bar = null!;
    private Button _close = null!;
    private Window _window = null!;

    public bool Succeeded { get; private set; }

    /// <summary>The install has begun (so it mustn't be started again without the window).</summary>
    public bool Started { get; private set; }

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

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = _window;
        }

        _window.Opened += (_, _) => _ = RunAsync();
        base.OnFrameworkInitializationCompleted();
    }

    private async Task RunAsync()
    {
        if (Started)
        {
            return;
        }

        Started = true;
        var progress = new Progress<UpdateProgress>(step => Dispatcher.UIThread.Post(() => Show(step)));
        UpdateOutcome outcome = await Task.Run(() => applier.RunAsync(progress, CancellationToken.None)).ConfigureAwait(true);
        Succeeded = outcome.Succeeded;
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            _bar.IsIndeterminate = false;
            _bar.Value = outcome.Succeeded ? 1 : 0;
            _title.Text = outcome.Succeeded ? "SchedularrTV is up to date" : outcome.RolledBack ? "The update didn't work" : "The update couldn't be installed";
            _detail.Text = outcome.Message;
            if (outcome.Succeeded)
            {
                // Nothing to read on success: the window goes by itself.
                await Task.Delay(TimeSpan.FromSeconds(3));
                _window.Close();
            }
            else
            {
                _close.IsVisible = true;
            }
        });
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
