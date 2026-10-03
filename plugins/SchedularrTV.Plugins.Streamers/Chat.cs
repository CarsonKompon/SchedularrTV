using System.Globalization;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SchedularrTV.Streamers;

/// <summary>A piece of a chat message: some text, or a picture (an emote or emoji).</summary>
public sealed record ChatPart(string? Text, string? Image = null);

/// <summary>A chat message as the page shows it. <paramref name="Seq"/> counts up, so the page asks only for newer ones.</summary>
public sealed record ChatMessage(long Seq, string Author, string Color, IReadOnlyList<ChatPart> Parts, long At, string? Highlight = null);

/// <summary>One streamer's chat as it arrives, the latest messages kept for the page to collect.</summary>
public abstract class ChatSource : IAsyncDisposable
{
    private const int Kept = 120;

    private readonly CancellationTokenSource _life = new();
    private readonly LinkedList<ChatMessage> _messages = new();
    private Task _run = Task.CompletedTask;
    private long _seq;

    protected ChatSource(string key, ILogger logger)
    {
        Key = key;
        Logger = logger;
    }

    public string Key { get; }

    /// <summary>What the source is following (a YouTube stream's video, say); a different one needs a new source.</summary>
    public virtual string Target => Key;

    public DateTimeOffset LastWanted { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Why the chat can't be read right now, if it can't.</summary>
    public string? Problem { get; protected set; }

    protected ILogger Logger { get; }

    public void Start() => _run = Task.Run(() => RunAsync(_life.Token));

    public IReadOnlyList<ChatMessage> After(long seq)
    {
        lock (_messages)
        {
            return [.. _messages.Where(message => message.Seq > seq)];
        }
    }

    protected void Add(string author, string? color, IReadOnlyList<ChatPart> parts, string? highlight = null)
    {
        if (parts.Count == 0 && highlight is null)
        {
            return;
        }

        lock (_messages)
        {
            _messages.AddLast(new ChatMessage(++_seq, author, color is { Length: > 0 } ? color : ColourFor(author), parts,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), highlight));
            while (_messages.Count > Kept)
            {
                _messages.RemoveFirst();
            }
        }
    }

    /// <summary>Reads the chat until stopped, starting again after a pause whenever the connection drops.</summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        TimeSpan pause = TimeSpan.FromSeconds(3);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ReadAsync(cancellationToken).ConfigureAwait(false);
                Problem = null;
                pause = TimeSpan.FromSeconds(3);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Problem = ex.Message;
                Logger.LogDebug(ex, "Streamers: the chat for {Key} dropped.", Key);
                pause = TimeSpan.FromSeconds(Math.Min(60, pause.TotalSeconds * 2));
            }

            try
            {
                await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    protected abstract Task ReadAsync(CancellationToken cancellationToken);

    private static readonly string[] Palette =
        ["#ff7a7a", "#ffb35c", "#ffe066", "#8ce99a", "#63e6be", "#74c0fc", "#91a7ff", "#b197fc", "#f783ac", "#e599f7"];

    /// <summary>A steady colour for someone whose platform gives none.</summary>
    public static string ColourFor(string author)
    {
        int hash = 17;
        foreach (char c in author)
        {
            hash = unchecked(hash * 31 + char.ToLowerInvariant(c));
        }

        return Palette[(hash & 0x7fffffff) % Palette.Length];
    }

    public async ValueTask DisposeAsync()
    {
        await _life.CancelAsync().ConfigureAwait(false);
        try
        {
            await _run.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or WebSocketException or HttpRequestException)
        {
        }

        _life.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>A Twitch channel's chat, read anonymously over Twitch's chat (IRC) websocket.</summary>
public sealed class TwitchChat(string key, string login, ILogger logger) : ChatSource(key, logger)
{
    protected override async Task ReadAsync(CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri("wss://irc-ws.chat.twitch.tv:443"), cancellationToken).ConfigureAwait(false);
        string anonymous = "justinfan" + Random.Shared.Next(10_000, 99_999).ToString(CultureInfo.InvariantCulture);
        foreach (string line in new[] { "CAP REQ :twitch.tv/tags twitch.tv/commands", "PASS SCHMOOPIIE", $"NICK {anonymous}", $"JOIN #{login}" })
        {
            await SendAsync(socket, line, cancellationToken).ConfigureAwait(false);
        }

        Problem = null;
        byte[] buffer = new byte[16 * 1024];
        using var pending = new MemoryStream();
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            WebSocketReceiveResult received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            // Decoded only once the whole message is in, so a character split between reads isn't broken.
            pending.Write(buffer, 0, received.Count);
            if (!received.EndOfMessage)
            {
                continue;
            }

            string text = Encoding.UTF8.GetString(pending.GetBuffer(), 0, (int)pending.Length);
            pending.SetLength(0);
            foreach (string line in text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("PING", StringComparison.Ordinal))
                {
                    await SendAsync(socket, "PONG" + line[4..], cancellationToken).ConfigureAwait(false);
                }
                else if (Parse(line) is { } message)
                {
                    Add(message.Author, message.Color, message.Parts);
                }
            }
        }

        throw new WebSocketException("Twitch closed the chat connection.");
    }

    private static Task SendAsync(ClientWebSocket socket, string line, CancellationToken cancellationToken) =>
        socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(line + "\r\n")), WebSocketMessageType.Text, true, cancellationToken);

    /// <summary>A chat line's author, colour and message (emotes as pictures), or null for anything that isn't a message.</summary>
    public static (string Author, string? Color, IReadOnlyList<ChatPart> Parts)? Parse(string line)
    {
        Dictionary<string, string> tags = new(StringComparer.Ordinal);
        string rest = line;
        if (rest.StartsWith('@'))
        {
            int space = rest.IndexOf(' ');
            if (space < 0)
            {
                return null;
            }

            foreach (string tag in rest[1..space].Split(';'))
            {
                int equals = tag.IndexOf('=');
                if (equals > 0)
                {
                    tags[tag[..equals]] = tag[(equals + 1)..];
                }
            }

            rest = rest[(space + 1)..];
        }

        int command = rest.IndexOf(" PRIVMSG #", StringComparison.Ordinal);
        if (!rest.StartsWith(':') || command < 0)
        {
            return null;
        }

        int body = rest.IndexOf(" :", command + 1, StringComparison.Ordinal);
        if (body < 0)
        {
            return null;
        }

        string nick = rest[1..command].Split('!')[0];
        string text = rest[(body + 2)..];
        if (text.StartsWith("\u0001ACTION ", StringComparison.Ordinal))
        {
            text = text["\u0001ACTION ".Length..].TrimEnd('\u0001');
        }

        string author = tags.TryGetValue("display-name", out string? display) && display.Length > 0 ? display.Replace("\\s", " ", StringComparison.Ordinal) : nick;
        string? color = tags.TryGetValue("color", out string? given) && given.Length == 7 ? given : null;
        return (author, color, Emotes(text, tags.TryGetValue("emotes", out string? emotes) ? emotes : null));
    }

    /// <summary>Splits a message into text and emote pictures; Twitch gives emote positions in characters (code points).</summary>
    public static IReadOnlyList<ChatPart> Emotes(string text, string? emotes)
    {
        Rune[] runes = [.. text.EnumerateRunes()];
        List<(int Start, int End, string Id)> places = [];
        foreach (string emote in (emotes ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] halves = emote.Split(':', 2);
            if (halves.Length != 2)
            {
                continue;
            }

            foreach (string range in halves[1].Split(','))
            {
                string[] ends = range.Split('-');
                if (ends.Length == 2 && int.TryParse(ends[0], out int start) && int.TryParse(ends[1], out int end) && start <= end && end < runes.Length)
                {
                    places.Add((start, end, halves[0]));
                }
            }
        }

        List<ChatPart> parts = [];
        int at = 0;
        foreach ((int start, int end, string id) in places.OrderBy(place => place.Start))
        {
            if (start < at)
            {
                continue;
            }

            if (start > at)
            {
                parts.Add(new ChatPart(Join(runes, at, start)));
            }

            parts.Add(new ChatPart(Join(runes, start, end + 1), $"https://static-cdn.jtvnw.net/emoticons/v2/{Uri.EscapeDataString(id)}/default/dark/2.0"));
            at = end + 1;
        }

        if (at < runes.Length)
        {
            parts.Add(new ChatPart(Join(runes, at, runes.Length)));
        }

        return parts;
    }

    private static string Join(Rune[] runes, int from, int to)
    {
        var text = new StringBuilder();
        for (int index = from; index < to; index++)
        {
            text.Append(runes[index].ToString());
        }

        return text.ToString();
    }
}

/// <summary>A YouTube stream's chat, read the way the chat window on youtube.com reads it.</summary>
public sealed partial class YouTubeChat(string key, string videoId, IHttpClientFactory http, ILogger logger) : ChatSource(key, logger)
{
    public override string Target => videoId;

    protected override async Task ReadAsync(CancellationToken cancellationToken)
    {
        HttpClient client = http.CreateClient(StreamerHttp.Name);
        using var page = new HttpRequestMessage(HttpMethod.Get, $"https://www.youtube.com/live_chat?is_popout=1&v={Uri.EscapeDataString(videoId)}");
        page.Headers.TryAddWithoutValidation("Cookie", "SOCS=CAI; CONSENT=YES+1");
        page.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        using HttpResponseMessage response = await client.SendAsync(page, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        string apiKey = ApiKey().Match(html) is { Success: true } keyMatch ? keyMatch.Groups[1].Value
            : throw new InvalidOperationException("YouTube's chat page has changed; the chat can't be read.");
        string version = ClientVersion().Match(html) is { Success: true } versionMatch ? versionMatch.Groups[1].Value : "2.20240101.00.00";
        string? continuation;
        using (JsonDocument? initial = YouTubeClient.Embedded(html, "ytInitialData"))
        {
            JsonElement renderer = initial is null ? default : Child(Child(initial.RootElement, "contents"), "liveChatRenderer");
            continuation = Continuation(Child(renderer, "continuations")).Continuation;
            foreach (var message in Messages(Child(renderer, "actions")).TakeLast(10))
            {
                Add(message.Author, null, message.Parts, message.Highlight);
            }
        }

        if (continuation is null)
        {
            throw new InvalidOperationException("This stream's chat is off or has ended.");
        }

        Problem = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var body = new JsonObject
            {
                ["context"] = new JsonObject { ["client"] = new JsonObject { ["clientName"] = "WEB", ["clientVersion"] = version, ["hl"] = "en" } },
                ["continuation"] = continuation
            };
            using HttpResponseMessage update = await client.PostAsync(
                $"https://www.youtube.com/youtubei/v1/live_chat/get_live_chat?key={Uri.EscapeDataString(apiKey)}&prettyPrint=false",
                JsonContent.Create(body), cancellationToken).ConfigureAwait(false);
            update.EnsureSuccessStatusCode();
            await using Stream stream = await update.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            JsonElement chat = Child(Child(json.RootElement, "continuationContents"), "liveChatContinuation");
            foreach (var message in Messages(Child(chat, "actions")))
            {
                Add(message.Author, null, message.Parts, message.Highlight);
            }

            (string? next, int wait) = Continuation(Child(chat, "continuations"));
            if (next is null)
            {
                throw new InvalidOperationException("The stream's chat ended.");
            }

            continuation = next;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(wait, 1000, 10_000)), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The next continuation token and how long YouTube asks to wait before using it.</summary>
    public static (string? Continuation, int WaitMs) Continuation(JsonElement continuations)
    {
        if (continuations.ValueKind != JsonValueKind.Array)
        {
            return (null, 0);
        }

        foreach (JsonElement entry in continuations.EnumerateArray())
        {
            foreach (string kind in new[] { "invalidationContinuationData", "timedContinuationData", "reloadContinuationData" })
            {
                JsonElement data = Child(entry, kind);
                if (Child(data, "continuation") is { ValueKind: JsonValueKind.String } token)
                {
                    int wait = Child(data, "timeoutMs") is { ValueKind: JsonValueKind.Number } timeout && timeout.TryGetInt32(out int ms) ? ms : 2000;
                    return (token.GetString(), wait);
                }
            }
        }

        return (null, 0);
    }

    /// <summary>The chat messages (and Super Chats) among a batch of YouTube chat actions.</summary>
    public static IEnumerable<(string Author, IReadOnlyList<ChatPart> Parts, string? Highlight)> Messages(JsonElement actions)
    {
        if (actions.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement action in actions.EnumerateArray())
        {
            JsonElement item = Child(Child(action, "addChatItemAction"), "item");
            JsonElement text = Child(item, "liveChatTextMessageRenderer");
            JsonElement paid = Child(item, "liveChatPaidMessageRenderer");
            JsonElement renderer = text.ValueKind == JsonValueKind.Object ? text : paid;
            if (renderer.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string author = Child(Child(renderer, "authorName"), "simpleText") is { ValueKind: JsonValueKind.String } name ? name.GetString() ?? "" : "";
            string? amount = Child(Child(paid, "purchaseAmountText"), "simpleText") is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
            yield return (author.TrimStart('@'), Runs(Child(Child(renderer, "message"), "runs")), amount);
        }
    }

    private static IReadOnlyList<ChatPart> Runs(JsonElement runs)
    {
        List<ChatPart> parts = [];
        if (runs.ValueKind != JsonValueKind.Array)
        {
            return parts;
        }

        foreach (JsonElement run in runs.EnumerateArray())
        {
            if (Child(run, "text") is { ValueKind: JsonValueKind.String } text)
            {
                parts.Add(new ChatPart(text.GetString()));
            }
            else if (Child(run, "emoji") is { ValueKind: JsonValueKind.Object } emoji)
            {
                JsonElement thumbnails = Child(Child(emoji, "image"), "thumbnails");
                string? url = thumbnails.ValueKind == JsonValueKind.Array && thumbnails.GetArrayLength() > 0
                    && Child(thumbnails[thumbnails.GetArrayLength() - 1], "url") is { ValueKind: JsonValueKind.String } found ? found.GetString() : null;
                string? id = Child(emoji, "emojiId") is { ValueKind: JsonValueKind.String } emojiId ? emojiId.GetString() : null;
                string? shortcut = Child(emoji, "shortcuts") is { ValueKind: JsonValueKind.Array } shortcuts && shortcuts.GetArrayLength() > 0
                    && shortcuts[0].ValueKind == JsonValueKind.String
                    ? shortcuts[0].GetString()
                    : id;

                // Standard emoji are characters already (their id is the character); custom ones are pictures.
                parts.Add(Flag(emoji, "isCustomEmoji") && url is not null ? new ChatPart(shortcut, url) : new ChatPart(id ?? shortcut));
            }
        }

        return parts;
    }

    private static bool Flag(JsonElement element, string name) => Child(element, name).ValueKind == JsonValueKind.True;

    private static JsonElement Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement child) ? child : default;

    [GeneratedRegex("\"INNERTUBE_API_KEY\":\"([^\"]+)\"")]
    private static partial Regex ApiKey();

    [GeneratedRegex("\"INNERTUBE_CLIENT_VERSION\":\"([^\"]+)\"")]
    private static partial Regex ClientVersion();
}
