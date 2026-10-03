using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SchedularrTV.Streamers;

/// <summary>
/// Reads a YouTube channel's own pages, so no API key is needed: its <c>/live</c> page says whether it's live (or when
/// its next stream is scheduled), and its past streams' pages say when each started and ended.
/// </summary>
/// <remarks>Unofficial: YouTube can change its pages. A consent cookie is sent so visitors from the EU get the page, not the consent form.</remarks>
public sealed partial class YouTubeClient(IHttpClientFactory http)
{
    // A past stream's start and length never change, so each is fetched once.
    private readonly ConcurrentDictionary<string, StreamSession?> _videos = new(StringComparer.Ordinal);

    public async Task<StreamerSnapshot> StatusAsync(string path, CancellationToken cancellationToken)
    {
        string html = await GetAsync($"https://www.youtube.com/{path}/live", cancellationToken).ConfigureAwait(false);
        return ParseLivePage(html);
    }

    /// <summary>The channel's recent streams, with when they started and how long they ran.</summary>
    public async Task<IReadOnlyList<StreamSession>> PastStreamsAsync(string path, CancellationToken cancellationToken)
    {
        string html = await GetAsync($"https://www.youtube.com/{path}/streams", cancellationToken).ConfigureAwait(false);
        List<StreamSession> sessions = [];
        foreach (string id in VideoIds(html).Take(15))
        {
            if (!_videos.TryGetValue(id, out StreamSession? session))
            {
                try
                {
                    session = ParseWatchPage(await GetAsync($"https://www.youtube.com/watch?v={id}", cancellationToken).ConfigureAwait(false));
                }
                catch (HttpRequestException)
                {
                    continue;
                }

                // One still to come (or still going) is asked again next time.
                if (session is { Length.Ticks: > 0 })
                {
                    _videos[id] = session;
                }
            }

            if (session is not null)
            {
                sessions.Add(session);
            }
        }

        return sessions;
    }

    private async Task<string> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Cookie", "SOCS=CAI; CONSENT=YES+1");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        using HttpResponseMessage response = await http.CreateClient(StreamerHttp.Name).SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("YouTube has no such channel.");
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What a channel's <c>/live</c> page says: live with its video, or a stream scheduled for later; otherwise (YouTube
    /// shows the channel's home page) just the channel's name and picture.
    /// </summary>
    public static StreamerSnapshot ParseLivePage(string html)
    {
        string? name = Meta(html, "og:title");
        string? picture = Meta(html, "og:image");
        if (Embedded(html, "ytInitialPlayerResponse") is not { } player)
        {
            return new StreamerSnapshot { Live = false, DisplayName = name, Avatar = picture };
        }

        using JsonDocument document = player;
        JsonElement root = document.RootElement;
        JsonElement details = Child(root, "videoDetails");
        JsonElement broadcast = Child(Child(Child(root, "microformat"), "playerMicroformatRenderer"), "liveBroadcastDetails");
        bool live = Flag(details, "isLive") || Flag(broadcast, "isLiveNow");
        bool upcoming = Flag(details, "isUpcoming");
        string? owner = OwnerPicture().Match(html) is { Success: true } match ? Unescape(match.Groups[1].Value) : null;

        DateTimeOffset? scheduled = null;
        if (upcoming)
        {
            scheduled = Time(broadcast, "startTimestamp") ?? UnixSeconds(Text(Child(Child(Child(Child(Child(root, "playabilityStatus"),
                "liveStreamability"), "liveStreamabilityRenderer"), "offlineSlate"), "liveStreamOfflineSlateRenderer"), "scheduledStartTime"));
        }

        return new StreamerSnapshot
        {
            Live = live,
            DisplayName = Text(details, "author") ?? name,
            Avatar = owner,
            Title = live ? Text(details, "title") : null,
            VideoId = live || upcoming ? Text(details, "videoId") : null,
            StartedAt = live ? Time(broadcast, "startTimestamp") : null,
            Viewers = live && int.TryParse(Text(details, "viewCount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int viewers) ? viewers : null,
            Scheduled = scheduled,
            ScheduledTitle = upcoming ? Text(details, "title") : null
        };
    }

    /// <summary>A past stream's start and length from its watch page; null for a video that wasn't a stream.</summary>
    public static StreamSession? ParseWatchPage(string html)
    {
        if (Embedded(html, "ytInitialPlayerResponse") is not { } player)
        {
            return null;
        }

        using JsonDocument document = player;
        JsonElement root = document.RootElement;
        JsonElement broadcast = Child(Child(Child(root, "microformat"), "playerMicroformatRenderer"), "liveBroadcastDetails");
        if (Time(broadcast, "startTimestamp") is not { } start)
        {
            return null;
        }

        TimeSpan length = Time(broadcast, "endTimestamp") is { } end && end > start
            ? end - start
            : int.TryParse(Text(Child(root, "videoDetails"), "lengthSeconds"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.Zero;
        return new StreamSession(start, length, Text(Child(root, "videoDetails"), "title"), Text(Child(root, "videoDetails"), "videoId"));
    }

    /// <summary>The video ids on a channel's Live tab, newest first.</summary>
    public static IEnumerable<string> VideoIds(string html) =>
        VideoId().Matches(html).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal);

    /// <summary>
    /// The JSON a YouTube page sets as <c>var name = {…};</c> (or <c>window["name"] = …</c>), cut out by matching braces.
    /// </summary>
    public static JsonDocument? Embedded(string html, string name)
    {
        foreach (string marker in new[] { $"var {name} = ", $"{name} = ", $"window[\"{name}\"] = " })
        {
            int at = html.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            int start = html.IndexOf('{', at + marker.Length);
            if (start < 0 || start > at + marker.Length + 2)
            {
                continue;
            }

            if (Balanced(html, start) is { } end)
            {
                try
                {
                    return JsonDocument.Parse(html.AsMemory(start, end - start + 1));
                }
                catch (JsonException)
                {
                }
            }
        }

        return null;
    }

    /// <summary>The index of the brace closing the one at <paramref name="start"/>, skipping over strings.</summary>
    private static int? Balanced(string text, int start)
    {
        int depth = 0;
        bool quoted = false;
        for (int index = start; index < text.Length; index++)
        {
            char c = text[index];
            if (quoted)
            {
                if (c == '\\')
                {
                    index++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}' when --depth == 0:
                    return index;
            }
        }

        return null;
    }

    private static string? Meta(string html, string property)
    {
        Match match = Regex.Match(html, $"<meta\\s+property=\"{Regex.Escape(property)}\"\\s+content=\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return match.Success && match.Groups[1].Value.Length > 0 ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }

    private static string Unescape(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string>($"\"{json}\"") ?? json;
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static JsonElement Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement child) ? child : default;

    private static string? Text(JsonElement element, string name) =>
        Child(element, name) is { ValueKind: JsonValueKind.String } value && value.GetString() is { Length: > 0 } text ? text : null;

    private static bool Flag(JsonElement element, string name) => Child(element, name).ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Time(JsonElement element, string name) =>
        Text(element, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset at)
            ? at
            : null;

    private static DateTimeOffset? UnixSeconds(string? text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    [GeneratedRegex("\"videoId\":\"([A-Za-z0-9_-]{11})\"")]
    private static partial Regex VideoId();

    [GeneratedRegex("\"videoOwnerRenderer\":\\{\"thumbnail\":\\{\"thumbnails\":\\[\\{\"url\":\"([^\"]+)\"")]
    private static partial Regex OwnerPicture();
}
