using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace SchedularrTV.LiveCapture;

/// <summary>
/// A live page plugin's running channels. Each plugin derives a type of its own, so one plugin's hub can't be mistaken
/// for another's should they ever share a service container.
/// </summary>
public abstract class LiveChannelHub(LiveStreamHub streams) : IAsyncDisposable
{
    public LiveStreamHub Streams { get; } = streams;

    public ValueTask DisposeAsync() => Streams.DisposeAsync();

    /// <summary>
    /// A plugin's hub: its channels resolved by <paramref name="resolve"/> (null for a channel there's no such of), kept
    /// alive as <paramref name="settings"/> says, and restarted with new settings whenever one of
    /// <paramref name="changes"/> fires (each is handed the handler to subscribe, e.g. <c>handler =&gt; store.Changed += handler</c>).
    /// </summary>
    public static THub Create<THub>(IServiceProvider services, string category, LivePageSettings settings, Func<string, LiveStreamSpec?> resolve,
        Func<LiveStreamHub, THub> wrap, params Action<Action>[] changes)
        where THub : LiveChannelHub
    {
        var hub = new LiveStreamHub(resolve, () => settings.KeepAlive, services.GetRequiredService<ILoggerFactory>().CreateLogger(category));

        // A channel whose settings changed starts again with them; ErsatzTV reconnects.
        foreach (Action<Action> subscribe in changes)
        {
            subscribe(() => _ = hub.RestartChangedAsync());
        }

        return wrap(hub);
    }
}

public static class LiveChannelEndpoints
{
    /// <summary>
    /// The endpoints every live page plugin has: <c>{prefix}/live/{id}.ts</c>, what ErsatzTV plays (the channel's live
    /// MPEG-TS, started on demand), and <paramref name="snapshot"/> (under the prefix), the latest frame of a running
    /// channel for the plugin's page.
    /// </summary>
    public static void MapLiveChannels<THub>(this IEndpointRouteBuilder endpoints, string prefix, string snapshot = "/snapshot/{id}.jpg")
        where THub : LiveChannelHub
    {
        endpoints.MapGet(prefix + "/live/{id}.ts", (string id, HttpContext http, THub hub) => hub.Streams.ServeAsync(id, http));
        endpoints.MapGet(prefix + snapshot, (string id, THub hub) =>
            hub.Streams.Snapshot(id) is { } frame ? Results.File(frame, LiveStream.ImageType(frame)) : Results.NotFound());
    }
}
