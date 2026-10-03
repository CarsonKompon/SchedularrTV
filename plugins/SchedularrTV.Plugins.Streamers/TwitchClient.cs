using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SchedularrTV.Streamers;

/// <summary>
/// Asks Twitch about a streamer through the GraphQL API its own website uses, so no key or app registration is needed:
/// whether they're live (with the title, game and viewers), their saved past broadcasts, and their schedule.
/// </summary>
/// <remarks>Unofficial: Twitch can change it. Each question is asked separately, so one that stops working doesn't take the others with it.</remarks>
public sealed class TwitchClient(IHttpClientFactory http)
{
    private const string Endpoint = "https://gql.twitch.tv/gql";

    // The public client id of twitch.tv itself.
    private const string ClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";

    private const string StatusQuery = """
        query($login: String!) {
          user(login: $login) {
            id login displayName profileImageURL(width: 150)
            broadcastSettings { title game { displayName } }
            stream { id createdAt viewersCount game { displayName } }
            lastBroadcast { startedAt title }
          }
        }
        """;

    private const string VideosQuery = """
        query($login: String!) {
          user(login: $login) {
            videos(first: 40, type: ARCHIVE, sort: TIME) { edges { node { createdAt lengthSeconds title } } }
          }
        }
        """;

    private const string ScheduleQuery = """
        query($login: String!) {
          user(login: $login) { channel { schedule { nextSegment { startAt title } } } }
        }
        """;

    public async Task<StreamerSnapshot> StatusAsync(string login, CancellationToken cancellationToken)
    {
        JsonElement user = await AskAsync(StatusQuery, login, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Twitch has no streamer called {login}.");
        StreamerSnapshot snapshot = ParseStatus(user);

        // The schedule is extra: a streamer without one, or a change on Twitch's side, just means no announced stream.
        try
        {
            if (await AskAsync(ScheduleQuery, login, cancellationToken).ConfigureAwait(false) is { } scheduled
                && ParseSchedule(scheduled) is { } next)
            {
                snapshot = snapshot with { Scheduled = next.At, ScheduledTitle = next.Title };
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
        {
        }

        return snapshot;
    }

    /// <summary>The saved past broadcasts (most streamers keep 7 to 60 days of them), plus the last broadcast's start.</summary>
    public async Task<IReadOnlyList<StreamSession>> PastStreamsAsync(string login, CancellationToken cancellationToken)
    {
        JsonElement? user = await AskAsync(VideosQuery, login, cancellationToken).ConfigureAwait(false);
        return user is { } found ? ParseVideos(found) : [];
    }

    /// <summary>The <c>user</c> object of a reply, or null when there's no such user. Throws when Twitch answers with errors only.</summary>
    private async Task<JsonElement?> AskAsync(string query, string login, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(new JsonObject { ["query"] = query, ["variables"] = new JsonObject { ["login"] = login } })
        };
        request.Headers.Add("Client-Id", ClientId);
        using HttpResponseMessage response = await http.CreateClient(StreamerHttp.Name).SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
        return User(json.RootElement);
    }

    public static JsonElement? User(JsonElement root)
    {
        if (root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("user", out JsonElement user))
        {
            return user.ValueKind == JsonValueKind.Object ? user.Clone() : null;
        }

        string problem = root.TryGetProperty("errors", out JsonElement errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0
            ? Text(errors[0], "message") ?? "an error"
            : "an unexpected reply";
        throw new InvalidOperationException($"Twitch answered with {problem}.");
    }

    public static StreamerSnapshot ParseStatus(JsonElement user)
    {
        JsonElement stream = user.TryGetProperty("stream", out JsonElement found) ? found : default;
        bool live = stream.ValueKind == JsonValueKind.Object;
        JsonElement settings = user.TryGetProperty("broadcastSettings", out JsonElement broadcast) ? broadcast : default;
        return new StreamerSnapshot
        {
            Live = live,
            UserId = Text(user, "id"),
            DisplayName = Text(user, "displayName") ?? Text(user, "login"),
            Avatar = Text(user, "profileImageURL"),
            Title = Text(settings, "title"),
            Game = live ? Text(Child(stream, "game"), "displayName") ?? Text(Child(settings, "game"), "displayName") : Text(Child(settings, "game"), "displayName"),
            Viewers = live && stream.TryGetProperty("viewersCount", out JsonElement viewers) && viewers.TryGetInt32(out int count) ? count : null,
            StartedAt = live ? Time(stream, "createdAt") : Time(Child(user, "lastBroadcast"), "startedAt")
        };
    }

    public static List<StreamSession> ParseVideos(JsonElement user)
    {
        List<StreamSession> sessions = [];
        if (Child(Child(user, "videos"), "edges") is { ValueKind: JsonValueKind.Array } edges)
        {
            foreach (JsonElement edge in edges.EnumerateArray())
            {
                JsonElement node = Child(edge, "node");
                if (Time(node, "createdAt") is { } start)
                {
                    int seconds = node.TryGetProperty("lengthSeconds", out JsonElement length) && length.TryGetInt32(out int value) ? value : 0;
                    sessions.Add(new StreamSession(start, TimeSpan.FromSeconds(seconds), Text(node, "title")));
                }
            }
        }

        return sessions;
    }

    public static (DateTimeOffset At, string? Title)? ParseSchedule(JsonElement user)
    {
        JsonElement next = Child(Child(Child(user, "channel"), "schedule"), "nextSegment");
        return Time(next, "startAt") is { } at ? (at, Text(next, "title")) : null;
    }

    private static JsonElement Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement child) ? child : default;

    private static string? Text(JsonElement element, string name) =>
        Child(element, name) is { ValueKind: JsonValueKind.String } value && value.GetString() is { Length: > 0 } text ? text : null;

    private static DateTimeOffset? Time(JsonElement element, string name) =>
        Text(element, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset at)
            ? at
            : null;
}

/// <summary>The HTTP client the streamer plugin asks the platforms with.</summary>
public static class StreamerHttp
{
    public const string Name = "streamers";

    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36";
}
