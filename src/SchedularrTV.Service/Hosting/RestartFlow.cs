using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace SchedularrTV.Service.Hosting;

/// <summary>
/// Restarts the server in place and brings the browser back once the new host is up. The page's
/// connection ends with the old host, so the reload is scheduled in the browser before restarting.
/// </summary>
public static class RestartFlow
{
    public static async Task RestartAndReloadAsync(
        AppRuntime runtime,
        IJSRuntime js,
        NavigationManager navigation,
        string path,
        string? query = null,
        int? newPort = null)
    {
        // Same host the browser used (it may be another device), on the new port if it changed.
        var target = new UriBuilder(navigation.BaseUri) { Path = path, Query = query ?? string.Empty };
        if (newPort is { } port)
        {
            target.Port = port;
        }

        string script = WaitThenGo
            .Replace("__TARGET__", JsonSerializer.Serialize(target.Uri.ToString()), StringComparison.Ordinal)
            .Replace("__INSTANCE__", JsonSerializer.Serialize(runtime.InstanceId), StringComparison.Ordinal);
        await js.InvokeVoidAsync("eval", script);

        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            runtime.Restart();
        });
    }

    /// <summary>
    /// Polls <c>/api/health</c> until a different host instance answers, then navigates; a fixed delay raced
    /// the restart and landed on "can't connect". The old host keeps answering while it shuts its services
    /// down, which is why the instance id is compared rather than just waiting for any answer.
    /// On a new port (another origin, so the answer can't be read) the new port answering at all is enough,
    /// since nothing listened there before. The address bar is switched to the target first, so if Blazor
    /// reloads the page itself when its connection comes back, it reloads the right page.
    /// </summary>
    private const string WaitThenGo = """
        (() => {
            const target = new URL(__TARGET__);
            const previous = __INSTANCE__;
            const sameOrigin = target.origin === location.origin;
            if (sameOrigin) history.replaceState(history.state, '', target.pathname + target.search);
            const health = new URL('/api/health', target);
            const started = Date.now();
            const go = () => { location.href = target.href; };
            const probe = async () => {
                try {
                    if (sameOrigin) {
                        const response = await fetch(health, { cache: 'no-store' });
                        if (response.ok) {
                            const body = await response.json();
                            if (body.instance && body.instance !== previous) { go(); return; }
                        }
                    } else {
                        await fetch(health, { mode: 'no-cors', cache: 'no-store' });
                        go();
                        return;
                    }
                } catch {
                    // Down between hosts; keep waiting.
                }
                if (Date.now() - started > 180000) { go(); return; }
                setTimeout(probe, 750);
            };
            setTimeout(probe, 750);
        })()
        """;
}

/// <summary>Per-browser-tab requests to show the setup wizard, e.g. from App settings.</summary>
public sealed class OnboardingState
{
    public event Action? OpenRequested;

    /// <summary>Raised when the wizard closes, so pages showing app settings can reload what it saved.</summary>
    public event Action? Closed;

    public void Open() => OpenRequested?.Invoke();

    public void NotifyClosed() => Closed?.Invoke();
}
