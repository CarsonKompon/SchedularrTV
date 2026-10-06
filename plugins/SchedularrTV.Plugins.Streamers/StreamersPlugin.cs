using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace SchedularrTV.Streamers;

/// <summary>
/// Streamer channels: Twitch and YouTube streamers in priority order on a channel of their own. Whoever is live is shown
/// (with their chat, and a word about the others who are live too); while nobody is, a countdown to the next stream.
/// </summary>
public sealed class StreamersPlugin : IPlugin, IPluginEndpoints
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton(new StreamerStore(context.DataDirectory));
        services.AddSingleton(new StreamerSettings(context));
        services.AddHttpClient(StreamerHttp.Name, http =>
        {
            http.Timeout = TimeSpan.FromSeconds(20);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(StreamerHttp.UserAgent);
        });
        services.AddSingleton<TwitchClient>();
        services.AddSingleton<YouTubeClient>();
        services.AddSingleton<YtDlp>();
        services.AddSingleton<StreamerChat>();
        services.AddSingleton<ThirdPartyEmotes>();
        services.AddSingleton<StreamerMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<StreamerMonitor>());
        services.AddSingleton(sp =>
        {
            StreamerStore store = sp.GetRequiredService<StreamerStore>();
            StreamerSettings settings = sp.GetRequiredService<StreamerSettings>();
            StreamerMonitor monitor = sp.GetRequiredService<StreamerMonitor>();

            // A channel whose streamer, stream address or look changed starts again with it.
            return LiveChannelHub.Create(sp, "Streamers", settings,
                id => store.Find(id) is { Enabled: true } channel ? StreamerPage.Spec(channel, monitor.OnAir(channel.Id), settings) : null,
                hub => new StreamersHub(hub),
                changed => monitor.OnAirChanged += changed,
                changed => store.Changed += changed);
        });
        services.AddExternalChannels<StreamerChannels>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, IPluginContext context)
    {
        const string prefix = StreamerSettings.RoutePrefix;

        // The page the hidden browser shows ("preview" is the editor's).
        endpoints.MapGet(prefix + "/view/{id}", (string id, bool? preview, StreamerStore store) =>
        {
            StreamerChannel channel = store.Find(id) ?? new StreamerChannel { Id = id, Name = "Streamers" };
            return Results.Content(StreamerPage.Html(StreamerPageConfig.For(channel, preview == true)), "text/html; charset=utf-8");
        });

        // Who's live and who's next, polled by the page.
        endpoints.MapGet(prefix + "/state/{id}.json", (string id, StreamerStore store, StreamerMonitor monitor, TimeProvider clock) =>
            store.Find(id) is { } channel
                ? Results.Json(StreamerViews.State(channel, monitor, clock.GetUtcNow()), StreamerPage.Json)
                : Results.NotFound());

        // The live streamer's chat, newer than the last message the page has.
        endpoints.MapGet(prefix + "/chat/{id}.json", (string id, long? after, StreamerStore store, StreamerMonitor monitor, StreamerChat chat,
            ThirdPartyEmotes emotes) =>
        {
            if (store.Find(id) is not { Chat.Enabled: true } channel || monitor.Active(channel) is not { } active)
            {
                return Results.Json(new ChatResponse(null, [], null), StreamerPage.Json);
            }

            StreamerState? state = monitor.State(active.Key);
            ChatSource? source = chat.For(active, state);
            IReadOnlyList<ChatMessage> messages = source?.After(after ?? 0) ?? [];

            // BetterTTV, FrankerFaceZ and 7TV emotes are words in Twitch chat; they're made pictures here, per channel's choice.
            if (channel.Chat.ThirdPartyEmotes && active.Platform == StreamPlatform.Twitch && messages.Count > 0)
            {
                IReadOnlyDictionary<string, string> known = emotes.For(state?.Latest?.UserId);
                messages = [.. messages.Select(message => message with { Parts = ThirdPartyEmotes.Apply(message.Parts, known) })];
            }

            return Results.Json(new ChatResponse(source is null ? null : $"{source.Key}/{source.Target}", messages, source?.Problem), StreamerPage.Json);
        });

        // What ErsatzTV plays, and the latest frame of a running channel for the Streamers page (a PNG while live).
        endpoints.MapLiveChannels<StreamersHub>(prefix, snapshot: "/snapshot/{id}");
    }
}

/// <summary>This plugin's running channels.</summary>
public sealed class StreamersHub(LiveStreamHub streams) : LiveChannelHub(streams);

/// <summary>What the page is told about a channel.</summary>
public static class StreamerViews
{
    public static PageState State(StreamerChannel channel, StreamerMonitor monitor, DateTimeOffset now)
    {
        StreamerEntry? active = monitor.Active(channel);
        ChannelOnAir? onAir = monitor.OnAir(channel.Id);
        LiveCard? live = active is null ? null : Card(active, monitor.State(active.Key));
        List<LiveCard> others = [.. monitor.Live(channel).Where(streamer => !ReferenceEquals(streamer, active)).Select(streamer => Card(streamer, monitor.State(streamer.Key)))];
        List<UpcomingCard> upcoming = [.. monitor.Upcoming(channel).Select(pair =>
        {
            StreamerState? state = monitor.State(pair.Streamer.Key);
            StreamPrediction? next = pair.Prediction;
            return new UpcomingCard(
                state?.Name(pair.Streamer) ?? pair.Streamer.Handle,
                StreamerHandles.PlatformName(pair.Streamer.Platform),
                state?.Latest?.Avatar,
                next?.At.ToUnixTimeMilliseconds(),
                next?.Length is { } length ? (int)length.TotalMinutes : null,
                next?.Confidence ?? 0,
                next?.Kind.ToString() ?? "Unknown",
                next?.Kind == PredictionKind.Scheduled ? state?.Latest?.ScheduledTitle : null);
        })];

        ReplayCard? replay = null;
        if (active is null && onAir is { ActiveKey: null, StreamUrl: not null, Replay: { } recording, ReplayKey: { } key }
            && channel.Streamers.FirstOrDefault(streamer => string.Equals(streamer.Key, key, StringComparison.OrdinalIgnoreCase)) is { } who)
        {
            StreamerState? state = monitor.State(who.Key);
            replay = new ReplayCard(state?.Name(who) ?? who.Handle, StreamerHandles.PlatformName(who.Platform), recording.Title, state?.Latest?.Avatar,
                recording.Start.ToUnixTimeMilliseconds(), (int)recording.Length.TotalMinutes);
        }

        string mode = active is null
            ? replay is null ? "offline" : "replay"
            : onAir?.StreamUrl is not null && string.Equals(onAir.ActiveKey, active.Key, StringComparison.OrdinalIgnoreCase) ? "live" : "opening";
        return new PageState(mode, live, others, upcoming, active is not null ? onAir?.Problem : null, now.ToUnixTimeMilliseconds(), replay);
    }

    private static LiveCard Card(StreamerEntry streamer, StreamerState? state) => new(
        state?.Name(streamer) ?? streamer.Handle,
        StreamerHandles.PlatformName(streamer.Platform),
        state?.Latest?.Title,
        state?.Latest?.Game,
        state?.Latest?.Viewers,
        state?.Latest?.Avatar,
        state?.Latest?.StartedAt?.ToUnixTimeMilliseconds());
}
