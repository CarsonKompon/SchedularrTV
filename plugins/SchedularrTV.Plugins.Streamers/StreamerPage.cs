using System.Text.Json;
using System.Text.Json.Serialization;

namespace SchedularrTV.Streamers;

/// <summary>What the channel's page needs to know about the channel (also what the editor's live preview sends it).</summary>
public sealed record StreamerPageConfig(
    string Id,
    string Name,
    ChatOptions Chat,
    AlsoLiveOptions AlsoLive,
    OfflineOptions Offline,
    PixelBox Video,
    bool Preview,
    PixelBox ReplayFrame)
{
    public static StreamerPageConfig For(StreamerChannel channel, bool preview) =>
        new(channel.Id, channel.Name, channel.Chat, channel.AlsoLive, channel.Offline, StreamerLayout.Video(channel.Chat), preview, StreamerLayout.ReplayFrame);
}

/// <summary>The recording being replayed, as the page shows it.</summary>
public sealed record ReplayCard(string Name, string Platform, string? Title, string? Avatar, long StartedAt, int Minutes);

/// <summary>A live streamer, as the page shows them.</summary>
public sealed record LiveCard(string Name, string Platform, string? Title, string? Game, int? Viewers, string? Avatar, long? StartedAt);

/// <summary>A streamer's next expected stream, as the page shows it; <paramref name="At"/> is null when it can't be guessed.</summary>
public sealed record UpcomingCard(string Name, string Platform, string? Avatar, long? At, int? Minutes, double Confidence, string Kind, string? Title);

/// <summary>
/// state.json: <c>live</c> (a stream is on, under the page), <c>opening</c> (someone's live but their stream isn't open yet),
/// <c>replay</c> (nobody's live; a recording is on, under the page) or <c>offline</c> (the countdown).
/// </summary>
public sealed record PageState(string Mode, LiveCard? Live, IReadOnlyList<LiveCard> AlsoLive, IReadOnlyList<UpcomingCard> Upcoming, string? Problem, long ServerTime,
    ReplayCard? Replay = null);

/// <summary>chat.json: the live streamer's newest messages. <paramref name="Source"/> changes when the chat does, so the page starts afresh.</summary>
public sealed record ChatResponse(string? Source, IReadOnlyList<ChatMessage> Messages, string? Problem);

/// <summary>Where the stream sits on the picture for each chat layout.</summary>
public static class StreamerLayout
{
    public const int PanelWidth = 320;

    /// <summary>Where a framed replay plays: a 16:9 window on the left, the countdown down the right.</summary>
    public static PixelBox ReplayFrame { get; } = new(40, 104, 864, 486);

    /// <summary>The whole picture, or (with the chat in a panel beside it) a 16:9 box beside the panel.</summary>
    public static PixelBox Video(ChatOptions chat) => !chat.Enabled ? new PixelBox(0, 0, 1280, 720) : chat.Layout switch
    {
        ChatLayout.PanelRight => new PixelBox(0, 90, 960, 540),
        ChatLayout.PanelLeft => new PixelBox(PanelWidth, 90, 960, 540),
        _ => new PixelBox(0, 0, 1280, 720)
    };
}

/// <summary>The channel's page: one self-contained HTML file (in the plugin) with the channel's settings written in.</summary>
public static class StreamerPage
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly Lazy<string> Template = new(() =>
    {
        using Stream stream = typeof(StreamerPage).Assembly.GetManifestResourceStream("streamers.html")
            ?? throw new InvalidOperationException("The streamers page is missing from the plugin.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>
    /// The page for a channel. The settings go in as JSON, with &lt; &gt; and &amp; escaped, so nothing in them (a chat
    /// template, say) can end the script early.
    /// </summary>
    public static string Html(StreamerPageConfig config) =>
        Template.Value.Replace("__CONFIG__", Script(JsonSerializer.Serialize(config, Json)), StringComparison.Ordinal);

    public static string Script(string json) =>
        json.Replace("<", "\\u003c", StringComparison.Ordinal).Replace(">", "\\u003e", StringComparison.Ordinal).Replace("&", "\\u0026", StringComparison.Ordinal);

    /// <summary>What the channel streams: the live streamer's video under the page, else the page itself (the countdown).</summary>
    public static LiveStreamSpec Spec(StreamerChannel channel, ChannelOnAir? onAir, StreamerSettings settings)
    {
        // The page's settings are part of its address, so a channel whose look changed is restarted with it.
        string look = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(StreamerPageConfig.For(channel, preview: false), Json))))[..12];
        LiveStreamSpec spec = new()
        {
            Id = channel.Id,
            Name = channel.Name,
            PageUrl = $"{settings.PageUrl(channel.Id)}?v={look}",
            Ffmpeg = settings.Ffmpeg,
            BrowserPath = settings.BrowserPath,
            CaptureFrameRate = settings.CaptureFrameRate
        };

        if (onAir is { ActiveKey: null, Replay: { } replay, StreamUrl: { } recording })
        {
            // A replay, picked up where it has got to; framed, it sits in the frame's window.
            PixelBox box = channel.Offline.Replay == ReplayStyle.Framed ? StreamerLayout.ReplayFrame : new PixelBox(0, 0, 1280, 720);
            return spec with
            {
                PageOnTop = true,
                WindowStreamUrl = recording,
                Window = new FrameRect(box.X, box.Y, box.Width, box.Height),
                Audio = LiveAudio.Stream,
                AudioStreamUrl = onAir.AudioUrl ?? recording,
                Volume = channel.Volume,
                Recorded = true,
                SeekTo = onAir.ReplayPosition(DateTimeOffset.UtcNow),
                RestartToken = $"replay:{replay.VideoId}:{onAir.Round}"
            };
        }

        if (onAir?.StreamUrl is { } url)
        {
            PixelBox video = StreamerLayout.Video(channel.Chat);
            return spec with
            {
                PageOnTop = true,
                WindowStreamUrl = url,
                Window = new FrameRect(video.X, video.Y, video.Width, video.Height),
                Audio = LiveAudio.Stream,
                AudioStreamUrl = url,
                Volume = channel.Volume
            };
        }

        return spec with
        {
            Audio = channel.Offline.MusicFolder is { Length: > 0 } ? LiveAudio.Music : LiveAudio.Silence,
            MusicFolder = channel.Offline.MusicFolder,
            Volume = channel.Offline.MusicVolume
        };
    }
}
