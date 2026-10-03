using System.Text.RegularExpressions;

namespace SchedularrTV.Streamers;

public enum StreamPlatform
{
    Twitch,
    YouTube
}

/// <summary>One streamer on a channel. Their place in the channel's list is their priority (first wins).</summary>
public sealed class StreamerEntry
{
    public StreamPlatform Platform { get; set; } = StreamPlatform.Twitch;

    /// <summary>
    /// Who: a Twitch login (<c>xqc</c>) or a YouTube handle (<c>@LinusTechTips</c>) or channel id (<c>UC…</c>). A pasted
    /// link to either works too; it's tidied to one of these when saved.
    /// </summary>
    public string Handle { get; set; } = string.Empty;

    /// <summary>Shown instead of the name the platform gives, when set.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Identifies the streamer across channels (and in the history kept for predictions).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Key => StreamerHandles.Key(Platform, Handle);

    public StreamerEntry Copy() => new() { Platform = Platform, Handle = Handle, DisplayName = DisplayName };
}

public enum ChatLayout
{
    /// <summary>Over the stream, down the right-hand side.</summary>
    OverlayRight,
    OverlayLeft,
    OverlayBottomRight,
    OverlayBottomLeft,

    /// <summary>A panel of its own beside the stream, which is shrunk to make room.</summary>
    PanelRight,
    PanelLeft,

    /// <summary>Where <see cref="ChatOptions.Box"/> says.</summary>
    Custom
}

public enum ChatStyle
{
    Classic,
    Bubbles,
    Minimal,

    /// <summary>Each message drawn from <see cref="ChatOptions.Template"/>.</summary>
    Custom
}

/// <summary>The live streamer's chat, drawn on the channel.</summary>
public sealed class ChatOptions
{
    public bool Enabled { get; set; }

    public ChatLayout Layout { get; set; } = ChatLayout.OverlayRight;

    /// <summary>For <see cref="ChatLayout.Custom"/>: the chat's box on the 1280×720 picture.</summary>
    public PixelBox Box { get; set; } = new(900, 120, 360, 480);

    public ChatStyle Style { get; set; } = ChatStyle.Classic;

    /// <summary>
    /// For <see cref="ChatStyle.Custom"/>: HTML for one message. <c>{author}</c>, <c>{message}</c>, <c>{color}</c>,
    /// <c>{platform}</c> and <c>{time}</c> are filled in (the author and message are made safe first).
    /// </summary>
    public string Template { get; set; } = DefaultTemplate;

    public const string DefaultTemplate = "<span style=\"color:{color};font-weight:700\">{author}</span> {message}";

    public int FontSize { get; set; } = 18;

    /// <summary>0 (none) to 1 (solid) for the box behind the messages.</summary>
    public double BackgroundOpacity { get; set; } = 0.45;

    public int MaxMessages { get; set; } = 14;

    /// <summary>A message fades this long after it arrives; 0 keeps it until it scrolls away.</summary>
    public int FadeAfterSeconds { get; set; } = 45;

    // Every member is a value or immutable (the box is a record), so a shallow copy is a full one.
    public ChatOptions Copy() => (ChatOptions)MemberwiseClone();
}

/// <summary>A box on the 1280×720 picture, in pixels.</summary>
public sealed record PixelBox(int X, int Y, int Width, int Height)
{
    public PixelBox Clamped()
    {
        int width = Math.Clamp(Width, 80, 1280);
        int height = Math.Clamp(Height, 60, 720);
        return new PixelBox(Math.Clamp(X, 0, 1280 - width), Math.Clamp(Y, 0, 720 - height), width, height);
    }
}

public enum AlsoLiveStyle
{
    /// <summary>A card that slides in with the streamer's picture, name and title.</summary>
    Popup,

    /// <summary>A line of text scrolling along the bottom.</summary>
    Ticker,

    Both
}

public enum ScreenCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

/// <summary>Lets viewers know when the channel's other (lower-priority) streamers are live too.</summary>
public sealed class AlsoLiveOptions
{
    public bool Enabled { get; set; } = true;

    public AlsoLiveStyle Style { get; set; } = AlsoLiveStyle.Popup;

    /// <summary>How often a reminder comes round.</summary>
    public int EveryMinutes { get; set; } = 10;

    /// <summary>How long each one stays on screen.</summary>
    public int ShowSeconds { get; set; } = 12;

    public ScreenCorner Corner { get; set; } = ScreenCorner.TopLeft;

    /// <summary><c>{name}</c>, <c>{title}</c>, <c>{game}</c>, <c>{platform}</c> and <c>{viewers}</c> are filled in.</summary>
    public string Text { get; set; } = "{name} is also live on {platform}: {title}";

    public AlsoLiveOptions Copy() => (AlsoLiveOptions)MemberwiseClone();
}

/// <summary>What the channel shows while nobody is live.</summary>
public sealed class OfflineOptions
{
    public string Heading { get; set; } = "Next stream";

    /// <summary>Optional line under the countdown.</summary>
    public string? Message { get; set; }

    public string AccentColor { get; set; } = "#9146ff";

    public string BackgroundColor { get; set; } = "#0e0e14";

    /// <summary>Lists every streamer's next predicted stream, not just the soonest.</summary>
    public bool ShowEveryone { get; set; } = true;

    /// <summary>Songs played while nobody is live; silent when empty.</summary>
    public string? MusicFolder { get; set; }

    public double MusicVolume { get; set; } = 0.4;

    public OfflineOptions Copy() => (OfflineOptions)MemberwiseClone();
}

/// <summary>A channel of streamers: whoever is live (the highest in the list wins), else a countdown to the next stream.</summary>
public sealed class StreamerChannel
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>The channel's number in ErsatzTV; it's left out while another channel has it.</summary>
    public int Number { get; set; }

    public string? Group { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>In priority order: the first one live is the one shown.</summary>
    public List<StreamerEntry> Streamers { get; set; } = [];

    /// <summary>The stream's volume, 0 to 2.</summary>
    public double Volume { get; set; } = 1;

    public ChatOptions Chat { get; set; } = new();

    public AlsoLiveOptions AlsoLive { get; set; } = new();

    public OfflineOptions Offline { get; set; } = new();

    public StreamerChannel Copy() => new()
    {
        Id = Id,
        Name = Name,
        Number = Number,
        Group = Group,
        Enabled = Enabled,
        Streamers = [.. Streamers.Select(streamer => streamer.Copy())],
        Volume = Volume,
        Chat = Chat.Copy(),
        AlsoLive = AlsoLive.Copy(),
        Offline = Offline.Copy()
    };

    public static string Slug(string name)
    {
        string slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "streamers" : slug.Length > 40 ? slug[..40].Trim('-') : slug;
    }
}
