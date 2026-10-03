using System.Collections.Concurrent;
using System.Text.Json;

namespace SchedularrTV.Streamers;

/// <summary>
/// BetterTTV, FrankerFaceZ and 7TV emotes for Twitch chat: the global sets and each channel's own, from the services'
/// public APIs (no key or login). Twitch sends these as plain words, so they're turned into pictures by name.
/// </summary>
/// <remarks>
/// Asked for while a chat is shown: the emotes known so far are used at once, and a fresh copy is fetched in the
/// background once they're an hour old. A service that can't be reached just adds nothing.
/// </remarks>
public sealed class ThirdPartyEmotes(IHttpClientFactory http, TimeProvider clock, ILogger<ThirdPartyEmotes> logger)
{
    private static readonly TimeSpan FreshFor = TimeSpan.FromHours(1);

    private static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>(StringComparer.Ordinal);

    // "" is the global sets; otherwise a Twitch user id.
    private readonly ConcurrentDictionary<string, Set> _sets = new(StringComparer.Ordinal);

    private sealed record Set(IReadOnlyDictionary<string, string> Emotes, DateTimeOffset At, bool Loading);

    /// <summary>The emotes for a Twitch channel (its own over the global ones), by name; picture addresses as values.</summary>
    public IReadOnlyDictionary<string, string> For(string? twitchUserId)
    {
        IReadOnlyDictionary<string, string> global = Current(string.Empty);
        if (twitchUserId is not { Length: > 0 } id)
        {
            return global;
        }

        IReadOnlyDictionary<string, string> own = Current(id);
        if (own.Count == 0)
        {
            return global;
        }

        var merged = new Dictionary<string, string>(global, StringComparer.Ordinal);
        foreach ((string name, string url) in own)
        {
            merged[name] = url;
        }

        return merged;
    }

    private IReadOnlyDictionary<string, string> Current(string key)
    {
        DateTimeOffset now = clock.GetUtcNow();
        Set set = _sets.GetOrAdd(key, _ => new Set(None, DateTimeOffset.MinValue, Loading: false));
        if (!set.Loading && now - set.At > FreshFor && _sets.TryUpdate(key, set with { Loading = true }, set))
        {
            _ = Task.Run(() => LoadAsync(key, set.Emotes));
        }

        return set.Emotes;
    }

    private async Task LoadAsync(string key, IReadOnlyDictionary<string, string> previous)
    {
        var emotes = new Dictionary<string, string>(StringComparer.Ordinal);
        bool any = false;
        string[] sources = key.Length == 0
            ? new[]
            {
                "https://api.betterttv.net/3/cached/emotes/global",
                "https://api.frankerfacez.com/v1/set/global",
                "https://7tv.io/v3/emote-sets/global"
            }
            : new[]
            {
                $"https://api.betterttv.net/3/cached/users/twitch/{Uri.EscapeDataString(key)}",
                $"https://api.frankerfacez.com/v1/room/id/{Uri.EscapeDataString(key)}",
                $"https://7tv.io/v3/users/twitch/{Uri.EscapeDataString(key)}"
            };

        // Later services win a name both have, in the order viewers' extensions usually apply them.
        foreach (string url in sources)
        {
            try
            {
                using HttpResponseMessage response = await http.CreateClient(StreamerHttp.Name).GetAsync(url).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    // A channel that doesn't use the service answers 404: nothing to add, not a failure.
                    any |= response.StatusCode == System.Net.HttpStatusCode.NotFound;
                    continue;
                }

                await using Stream body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using JsonDocument json = await JsonDocument.ParseAsync(body).ConfigureAwait(false);
                foreach ((string name, string picture) in Parse(url, json.RootElement))
                {
                    emotes[name] = picture;
                }

                any = true;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or IOException)
            {
                logger.LogDebug(ex, "Streamers: couldn't load emotes from {Url}.", url);
            }
        }

        // Nothing reachable at all keeps what was known, and tries again in a few minutes.
        _sets[key] = any
            ? new Set(emotes, clock.GetUtcNow(), Loading: false)
            : new Set(previous, clock.GetUtcNow() - FreshFor + TimeSpan.FromMinutes(5), Loading: false);
    }

    private static IEnumerable<(string Name, string Url)> Parse(string url, JsonElement root) =>
        url.Contains("betterttv", StringComparison.Ordinal) ? BetterTtv(root)
        : url.Contains("frankerfacez", StringComparison.Ordinal) ? FrankerFaceZ(root)
        : SevenTv(root);

    /// <summary>BetterTTV: a list of emotes (global), or a user's <c>channelEmotes</c> and <c>sharedEmotes</c>.</summary>
    public static IEnumerable<(string Name, string Url)> BetterTtv(JsonElement root)
    {
        JsonElement[] lists = root.ValueKind == JsonValueKind.Array
            ? new[] { root }
            : new[] { Child(root, "channelEmotes"), Child(root, "sharedEmotes") };
        foreach (JsonElement list in lists.Where(list => list.ValueKind == JsonValueKind.Array))
        {
            foreach (JsonElement emote in list.EnumerateArray())
            {
                if (Text(emote, "id") is { } id && Text(emote, "code") is { } code)
                {
                    yield return (code, $"https://cdn.betterttv.net/emote/{Uri.EscapeDataString(id)}/2x");
                }
            }
        }
    }

    /// <summary>FrankerFaceZ: every set in <c>sets</c>, each emote with its pictures by size.</summary>
    public static IEnumerable<(string Name, string Url)> FrankerFaceZ(JsonElement root)
    {
        if (Child(root, "sets") is not { ValueKind: JsonValueKind.Object } sets)
        {
            yield break;
        }

        foreach (JsonProperty set in sets.EnumerateObject())
        {
            if (Child(set.Value, "emoticons") is not { ValueKind: JsonValueKind.Array } emoticons)
            {
                continue;
            }

            foreach (JsonElement emote in emoticons.EnumerateArray())
            {
                JsonElement urls = Child(emote, "urls");
                if (Text(emote, "name") is { } name && (Text(urls, "2") ?? Text(urls, "1")) is { } picture)
                {
                    yield return (name, Absolute(picture));
                }
            }
        }
    }

    /// <summary>7TV: an emote set's <c>emotes</c> (global), or a user's <c>emote_set.emotes</c>.</summary>
    public static IEnumerable<(string Name, string Url)> SevenTv(JsonElement root)
    {
        JsonElement emotes = Child(root, "emotes") is { ValueKind: JsonValueKind.Array } own ? own : Child(Child(root, "emote_set"), "emotes");
        if (emotes.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement emote in emotes.EnumerateArray())
        {
            if (Text(emote, "name") is { } name && Text(Child(Child(emote, "data"), "host"), "url") is { } host)
            {
                yield return (name, Absolute(host).TrimEnd('/') + "/2x.webp");
            }
        }
    }

    /// <summary>
    /// Turns the words of a message's text that are emote names into pictures. Text Twitch already made pictures of
    /// (its own emotes) is left alone.
    /// </summary>
    public static IReadOnlyList<ChatPart> Apply(IReadOnlyList<ChatPart> parts, IReadOnlyDictionary<string, string> emotes)
    {
        if (emotes.Count == 0)
        {
            return parts;
        }

        List<ChatPart> result = [];
        foreach (ChatPart part in parts)
        {
            if (part.Image is not null || string.IsNullOrEmpty(part.Text))
            {
                result.Add(part);
                continue;
            }

            var text = new System.Text.StringBuilder();
            foreach (string piece in SplitKeepingSpaces(part.Text))
            {
                if (piece.Length > 0 && !char.IsWhiteSpace(piece[0]) && emotes.TryGetValue(piece, out string? picture))
                {
                    if (text.Length > 0)
                    {
                        result.Add(new ChatPart(text.ToString()));
                        text.Clear();
                    }

                    result.Add(new ChatPart(piece, picture));
                }
                else
                {
                    text.Append(piece);
                }
            }

            if (text.Length > 0)
            {
                result.Add(new ChatPart(text.ToString()));
            }
        }

        return result;
    }

    /// <summary>Words and the spaces between them, in order, so putting them back together gives the text unchanged.</summary>
    private static IEnumerable<string> SplitKeepingSpaces(string text)
    {
        int start = 0;
        for (int index = 1; index <= text.Length; index++)
        {
            if (index == text.Length || char.IsWhiteSpace(text[index]) != char.IsWhiteSpace(text[start]))
            {
                yield return text[start..index];
                start = index;
            }
        }
    }

    private static string Absolute(string url) => url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url;

    private static JsonElement Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement child) ? child : default;

    private static string? Text(JsonElement element, string name) =>
        Child(element, name) is { ValueKind: JsonValueKind.String } value && value.GetString() is { Length: > 0 } text ? text : null;
}
