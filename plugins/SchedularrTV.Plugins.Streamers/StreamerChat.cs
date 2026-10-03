namespace SchedularrTV.Streamers;

/// <summary>
/// The chats being read. One is started when a channel's page first asks for it, and closed once nothing has asked
/// for a couple of minutes (the channel went off air, or switched streamer).
/// </summary>
public sealed class StreamerChat(IHttpClientFactory http, ILoggerFactory loggers) : IAsyncDisposable
{
    private static readonly TimeSpan Unwanted = TimeSpan.FromMinutes(2);

    private readonly Dictionary<string, ChatSource> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger = loggers.CreateLogger("Streamers.Chat");

    /// <summary>The streamer's chat, started if it isn't already; null when there's nothing to read (a YouTube streamer who isn't live).</summary>
    public ChatSource? For(StreamerEntry streamer, StreamerState? state)
    {
        string? videoId = state?.Latest?.VideoId;
        if (streamer.Platform == StreamPlatform.YouTube && videoId is null)
        {
            return null;
        }

        ChatSource? stale = null;
        ChatSource source;
        lock (_sources)
        {
            string target = streamer.Platform == StreamPlatform.YouTube ? videoId! : streamer.Key;
            if (_sources.TryGetValue(streamer.Key, out ChatSource? running) && running.Target == target)
            {
                source = running;
            }
            else
            {
                stale = running;
                source = streamer.Platform == StreamPlatform.Twitch
                    ? new TwitchChat(streamer.Key, streamer.Handle, _logger)
                    : new YouTubeChat(streamer.Key, videoId!, http, _logger);
                _sources[streamer.Key] = source;
                source.Start();
            }

            source.LastWanted = DateTimeOffset.UtcNow;
        }

        if (stale is not null)
        {
            _ = stale.DisposeAsync().AsTask();
        }

        return source;
    }

    /// <summary>Closes chats nothing has asked for lately.</summary>
    public async Task SweepAsync()
    {
        List<ChatSource> unwanted;
        lock (_sources)
        {
            DateTimeOffset cutoff = DateTimeOffset.UtcNow - Unwanted;
            unwanted = [.. _sources.Values.Where(source => source.LastWanted < cutoff)];
            foreach (ChatSource source in unwanted)
            {
                _sources.Remove(source.Key);
            }
        }

        foreach (ChatSource source in unwanted)
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<ChatSource> all;
        lock (_sources)
        {
            all = [.. _sources.Values];
            _sources.Clear();
        }

        foreach (ChatSource source in all)
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }
}
