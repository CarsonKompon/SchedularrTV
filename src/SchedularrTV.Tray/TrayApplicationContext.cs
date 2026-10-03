using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using SchedularrTV.Hosting;

namespace SchedularrTV.Tray;

/// <summary>
/// The tray icon and its menu. There are no windows: everything else happens in the web UI.
/// </summary>
public sealed class TrayApplicationContext(SchedulerController server) : Application
{
    private TrayIcon? _icon;
    private NativeMenuItem _status = null!;
    private NativeMenuItem _open = null!;
    private NativeMenuItem _start = null!;
    private NativeMenuItem _stop = null!;
    private NativeMenuItem _restart = null!;
    private NativeMenuItem _autostart = null!;
    private ServerState? _shownState;
    private bool _exiting;

    public override void Initialize()
    {
        Name = "SchedularrTV";
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _status = new NativeMenuItem("Starting…") { IsEnabled = false };
        _open = Item("Open SchedularrTV", OpenBrowser);
        _start = Item("Start server", () => _ = server.StartAsync());
        _stop = Item("Stop server", () => _ = server.StopAsync());
        _restart = Item("Restart server", () => _ = server.RestartAsync());
        _autostart = Item("Start when I sign in", ToggleAutostart);
        _autostart.ToggleType = NativeMenuItemToggleType.CheckBox;

        var menu = new NativeMenu();
        menu.Items.Add(_status);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_open);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_start);
        menu.Items.Add(_stop);
        menu.Items.Add(_restart);
        menu.Items.Add(new NativeMenuItemSeparator());
        if (Autostart.IsSupported)
        {
            menu.Items.Add(_autostart);
        }

        menu.Items.Add(Item("Open data folder", () => Try(() => Autostart.OpenFolder(server.DataDirectory))));
        menu.Items.Add(Item("Open server log", () => Try(() => Autostart.OpenFolder(Path.GetDirectoryName(server.LogPath)!))));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item("Quit SchedularrTV", () => _ = ExitAsync()));

        _icon = new TrayIcon
        {
            Icon = TrayIconArt.Create(ServerState.Stopped),
            ToolTipText = "SchedularrTV",
            Menu = menu,
            IsVisible = true
        };
        _icon.Clicked += (_, _) => OpenBrowser();
        TrayIcon.SetIcons(this, new TrayIcons { _icon });

        server.StateChanged += () => Dispatcher.UIThread.Post(Update);
        Update();
        _ = server.StartAsync();

        base.OnFrameworkInitializationCompleted();
    }

    private static NativeMenuItem Item(string header, Action onClick)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => onClick();
        return item;
    }

    private void Update()
    {
        if (_icon is null)
        {
            return;
        }

        ServerState state = server.State;
        _status.Header = state switch
        {
            ServerState.Running => $"Running · {server.BrowseUrl}",
            ServerState.Starting => "Starting…",
            ServerState.Stopping => "Stopping…",
            ServerState.Failed => $"Problem: {Truncate(server.LastError ?? "the server stopped")}",
            _ => "Stopped"
        };
        _open.IsEnabled = state == ServerState.Running;
        _start.IsEnabled = state is ServerState.Stopped or ServerState.Failed;
        _stop.IsEnabled = state is ServerState.Running or ServerState.Starting;
        _restart.IsEnabled = state is ServerState.Running or ServerState.Starting or ServerState.Failed;
        _autostart.IsChecked = Autostart.IsSupported && Autostart.IsEnabled();

        if (_shownState != state)
        {
            _shownState = state;
            _icon.Icon = TrayIconArt.Create(state);
        }

        _icon.ToolTipText = state switch
        {
            ServerState.Running => "SchedularrTV — running",
            ServerState.Failed => "SchedularrTV — needs attention",
            ServerState.Starting => "SchedularrTV — starting",
            _ => "SchedularrTV — stopped"
        };
    }

    private void OpenBrowser()
    {
        if (server.State == ServerState.Running)
        {
            Try(() => Autostart.OpenUrl(server.BrowseUrl));
        }
    }

    private void ToggleAutostart()
    {
        Try(() =>
        {
            if (Autostart.IsEnabled())
            {
                Autostart.Disable();
            }
            else
            {
                // Only a non-default data folder needs passing on; the default is found again at sign-in.
                string[] arguments = string.Equals(server.DataDirectory, AppPaths.PerUserDirectory(), StringComparison.Ordinal)
                    ? []
                    : [AppPaths.DataDirectoryArgument, server.DataDirectory];
                Autostart.Enable(Environment.ProcessPath ?? throw new InvalidOperationException("Can't tell where this program is."), arguments);
            }
        });
        Update();
    }

    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _status.Header = "Quitting…";
        await server.DisposeAsync();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_icon is not null)
            {
                _icon.IsVisible = false;
                _icon.Dispose();
            }

            (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        });
    }

    private void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _status.Header = $"Problem: {Truncate(ex.Message)}";
        }
    }

    private static string Truncate(string value) => value.Length <= 60 ? value : value[..57] + "…";
}
