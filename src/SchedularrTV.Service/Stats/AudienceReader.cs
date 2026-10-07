using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Publishing;
using SchedularrTV.Providers.Jellyfin;

namespace SchedularrTV.Service.Stats;

/// <summary>Someone watching: a Jellyfin or Plex user (with their device), or a player straight from ErsatzTV.</summary>
/// <param name="Via">Jellyfin, Plex or Direct.</param>
public sealed record Viewer(string Name, string Via, string? Device = null);

/// <summary>A media server's session on a live TV channel, before it's matched to one of ours.</summary>
public sealed record LiveSession(int? Number, string? ChannelName, string User, string Via, string? Device);

/// <summary>The channel as the audience is matched to it.</summary>
public sealed record AudienceChannel(string Id, int Number, string Name);

/// <summary>Plex's address and token, for counting viewers watching through Plex Live TV.</summary>
public sealed class StatsOptions
{
    public const string SectionName = "Stats";

    public string PlexUrl { get; set; } = string.Empty;

    public string PlexToken { get; set; } = string.Empty;

    /// <summary>How long the viewing history is kept, in days.</summary>
    public int KeepDays { get; set; } = 365;

    /// <summary>Whether viewing is recorded at all.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Who's watching each channel right now, from every source there is:
/// <list type="bullet">
/// <item>Jellyfin's sessions (<c>/Sessions</c>): each user watching a Live TV channel, by its number or name.</item>
/// <item>Plex's sessions (<c>/status/sessions</c>): each user watching Live TV, by its channel number or name.</item>
/// <item>ErsatzTV's heartbeat: a channel being pulled straight from ErsatzTV (VLC, an IPTV app) with nobody from Jellyfin
/// or Plex on it counts as one "Direct" viewer; ErsatzTV can't tell how many.</item>
/// </list>
/// A source that can't be reached is left out, and said why in <see cref="Problems"/>.
/// </summary>
public sealed class AudienceReader(IServiceProvider services, IHttpClientFactory httpClients, IOptionsMonitor<StatsOptions> options, ILogger<AudienceReader> logger)
{
    private readonly Dictionary<string, string> _problems = new(StringComparer.Ordinal);

    /// <summary>Why a source couldn't be read last time, by source.</summary>
    public IReadOnlyDictionary<string, string> Problems
    {
        get
        {
            lock (_problems)
            {
                return new Dictionary<string, string>(_problems, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>The sources that can be read: Jellyfin and Plex when set up, Direct when ErsatzTV can tell.</summary>
    public IReadOnlyList<string> Sources
    {
        get
        {
            List<string> sources = [];
            if (Jellyfin() is not null)
            {
                sources.Add("Jellyfin");
            }

            if (PlexConfigured)
            {
                sources.Add("Plex");
            }

            if (services.GetServices<IChannelViewers>().Any())
            {
                sources.Add("Direct");
            }

            return sources;
        }
    }

    public bool PlexConfigured => !string.IsNullOrWhiteSpace(options.CurrentValue.PlexUrl) && !string.IsNullOrWhiteSpace(options.CurrentValue.PlexToken);

    private JellyfinOptions? Jellyfin() =>
        services.GetService<IOptions<JellyfinOptions>>()?.Value is { } jellyfin && !string.IsNullOrWhiteSpace(jellyfin.BaseUrl) && !string.IsNullOrWhiteSpace(jellyfin.ApiKey)
            ? jellyfin
            : null;

    /// <summary>Who's watching each of <paramref name="channels"/>, by channel id; channels nobody is watching are left out.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<Viewer>>> ReadAsync(IReadOnlyList<AudienceChannel> channels, CancellationToken cancellationToken)
    {
        List<LiveSession> sessions = [];
        sessions.AddRange(await JellyfinSessionsAsync(cancellationToken).ConfigureAwait(false));
        sessions.AddRange(await PlexSessionsAsync(cancellationToken).ConfigureAwait(false));

        Dictionary<string, List<Viewer>> watching = new(StringComparer.Ordinal);
        foreach (LiveSession session in sessions)
        {
            if (Match(channels, session) is { } channel)
            {
                (watching.TryGetValue(channel.Id, out List<Viewer>? list) ? list : watching[channel.Id] = []).Add(new Viewer(session.User, session.Via, session.Device));
            }
        }

        // ErsatzTV says a channel is being pulled; with nobody from Jellyfin or Plex on it, a player is watching it directly.
        IChannelViewers[] direct = [.. services.GetServices<IChannelViewers>()];
        foreach (AudienceChannel channel in channels)
        {
            if (!watching.ContainsKey(channel.Id) && direct.Any(source => source.IsWatched(channel.Number) == true))
            {
                watching[channel.Id] = [new Viewer("Direct viewer", "Direct")];
            }
        }

        return watching.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<Viewer>)pair.Value, StringComparer.Ordinal);
    }

    /// <summary>A session's channel: by number when it has one we have, else by name.</summary>
    public static AudienceChannel? Match(IReadOnlyList<AudienceChannel> channels, LiveSession session) =>
        (session.Number is { } number ? channels.FirstOrDefault(channel => channel.Number == number) : null)
        ?? (string.IsNullOrWhiteSpace(session.ChannelName) ? null
            : channels.FirstOrDefault(channel => string.Equals(channel.Name.Trim(), session.ChannelName.Trim(), StringComparison.OrdinalIgnoreCase)));

    private async Task<IReadOnlyList<LiveSession>> JellyfinSessionsAsync(CancellationToken cancellationToken)
    {
        if (Jellyfin() is not { } jellyfin)
        {
            Clear("Jellyfin");
            return [];
        }

        try
        {
            using HttpClient http = httpClients.CreateClient(ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{jellyfin.BaseUrl.TrimEnd('/')}/Sessions?ActiveWithinSeconds=150");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("MediaBrowser",
                $"Token=\"{jellyfin.ApiKey}\", Client=\"SchedularrTV\", Device=\"SchedularrTV\", DeviceId=\"schedularrtv\", Version=\"1.0\"");
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            Clear("Jellyfin");
            return ReadJellyfin(json.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or UriFormatException or InvalidOperationException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            Note("Jellyfin", ex);
            return [];
        }
    }

    private async Task<IReadOnlyList<LiveSession>> PlexSessionsAsync(CancellationToken cancellationToken)
    {
        StatsOptions plex = options.CurrentValue;
        if (!PlexConfigured)
        {
            Clear("Plex");
            return [];
        }

        try
        {
            using HttpClient http = httpClients.CreateClient(ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{plex.PlexUrl.TrimEnd('/')}/status/sessions");
            request.Headers.Add("X-Plex-Token", plex.PlexToken.Trim());
            request.Headers.Add("Accept", "application/json");
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            Clear("Plex");
            return ReadPlex(json.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or UriFormatException or InvalidOperationException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            Note("Plex", ex);
            return [];
        }
    }

    public const string ClientName = "viewing-stats";

    /// <summary>
    /// Jellyfin's sessions on Live TV: the item playing is the channel itself (<c>TvChannel</c>, with its <c>Number</c>
    /// or <c>ChannelNumber</c>), or a programme on one (with <c>ChannelNumber</c> / <c>ChannelName</c>).
    /// </summary>
    public static IReadOnlyList<LiveSession> ReadJellyfin(JsonElement sessions)
    {
        List<LiveSession> found = [];
        if (sessions.ValueKind != JsonValueKind.Array)
        {
            return found;
        }

        foreach (JsonElement session in sessions.EnumerateArray())
        {
            if (!session.TryGetProperty("NowPlayingItem", out JsonElement item) || item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? type = Text(item, "Type");
            bool channel = type == "TvChannel";
            bool onChannel = channel || type is "Program" or "LiveTvProgram" || Text(item, "ChannelId") is not null;
            if (!onChannel)
            {
                continue;
            }

            int? number = Number(Text(item, "ChannelNumber")) ?? (channel ? Number(Text(item, "Number")) : null);
            string? name = channel ? Text(item, "Name") ?? Text(item, "ChannelName") : Text(item, "ChannelName");
            found.Add(new LiveSession(number, name, Text(session, "UserName") ?? "Someone", "Jellyfin", Text(session, "DeviceName") ?? Text(session, "Client")));
        }

        return found;
    }

    /// <summary>
    /// Plex's sessions on Live TV (<c>live</c> set): the channel by its virtual number (<c>channelVcn</c>) or identifier,
    /// else its title or call sign, on the item or its media.
    /// </summary>
    public static IReadOnlyList<LiveSession> ReadPlex(JsonElement root)
    {
        List<LiveSession> found = [];
        if (!root.TryGetProperty("MediaContainer", out JsonElement container) || !container.TryGetProperty("Metadata", out JsonElement items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return found;
        }

        foreach (JsonElement item in items.EnumerateArray())
        {
            bool live = Text(item, "live") is "1" or "true" || (item.TryGetProperty("live", out JsonElement flag) && flag.ValueKind == JsonValueKind.True);
            JsonElement media = item.TryGetProperty("Media", out JsonElement list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0 ? list[0] : default;
            string? Pick(string name) => Text(item, name) ?? (media.ValueKind == JsonValueKind.Object ? Text(media, name) : null);
            string? vcn = Pick("channelVcn") ?? Pick("channelIdentifier");
            string? title = Pick("channelTitle") ?? Pick("channelCallSign");
            if (!live && vcn is null && title is null)
            {
                continue;
            }

            string user = item.TryGetProperty("User", out JsonElement owner) ? Text(owner, "title") ?? "Someone" : "Someone";
            string? device = item.TryGetProperty("Player", out JsonElement player) ? Text(player, "title") ?? Text(player, "product") : null;
            found.Add(new LiveSession(Number(vcn), title, user, "Plex", device));
        }

        return found;
    }

    /// <summary>The leading whole number in "5", "5.1" or "ch5"; null when there isn't one.</summary>
    public static int? Number(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string digits = string.Concat(text.SkipWhile(c => !char.IsAsciiDigit(c)).TakeWhile(char.IsAsciiDigit));
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            }
            : null;

    private void Note(string source, Exception ex)
    {
        string reason = ex is TaskCanceledException ? "no answer in time" : ex is HttpRequestException { StatusCode: { } code } ? $"answered {(int)code} {code}" : ex.Message;
        lock (_problems)
        {
            if (!_problems.TryGetValue(source, out string? before) || before != reason)
            {
                logger.LogInformation("Viewing stats: couldn't read {Source}'s sessions: {Reason}", source, reason);
            }

            _problems[source] = reason;
        }
    }

    private void Clear(string source)
    {
        lock (_problems)
        {
            _problems.Remove(source);
        }
    }
}
