using System.Text.Json;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Publishing;

namespace SchedularrTV.Service.Stats;

/// <summary>Someone watching: a media server's user (with their device), or a player straight from ErsatzTV.</summary>
/// <param name="Via">The media server's name ("Jellyfin", "Plex"…), or Direct.</param>
public sealed record Viewer(string Name, string Via, string? Device = null);

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
/// <item>Each media server that can say who's watching its live TV (<see cref="ILiveViewerSource"/>: Jellyfin's
/// plugin, Plex here, any library plugin), matched to channels by number or name.</item>
/// <item>ErsatzTV's heartbeat: a channel being pulled straight from ErsatzTV (VLC, an IPTV app) with nobody from a
/// media server on it counts as one "Direct" viewer; ErsatzTV can't tell how many.</item>
/// </list>
/// A source that can't be reached is left out, and said why in <see cref="Problems"/>.
/// </summary>
public sealed class AudienceReader(IServiceProvider services, ILogger<AudienceReader> logger)
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

    /// <summary>Every source of viewers there is, set up or not.</summary>
    public IReadOnlyList<ILiveViewerSource> ViewerSources => [.. services.GetServices<ILiveViewerSource>()];

    /// <summary>The sources that can be read: the media servers that are set up, and Direct when ErsatzTV can tell.</summary>
    public IReadOnlyList<string> Sources
    {
        get
        {
            List<string> sources = [.. ViewerSources.Where(source => source.IsConfigured).Select(source => source.Name)];
            if (services.GetServices<IChannelViewers>().Any())
            {
                sources.Add("Direct");
            }

            return sources;
        }
    }

    public bool PlexConfigured => services.GetService<PlexLiveViewers>()?.IsConfigured == true;

    /// <summary>Who's watching each of <paramref name="channels"/>, by channel id; channels nobody is watching are left out.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<Viewer>>> ReadAsync(IReadOnlyList<AudienceChannel> channels, CancellationToken cancellationToken)
    {
        List<LiveViewerSession> sessions = [];
        foreach (ILiveViewerSource source in ViewerSources)
        {
            if (!source.IsConfigured)
            {
                Clear(source.Name);
                continue;
            }

            try
            {
                sessions.AddRange(await source.GetSessionsAsync(cancellationToken).ConfigureAwait(false));
                Clear(source.Name);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or UriFormatException or InvalidOperationException
                                       or MediaSourceConfigurationException
                                       || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                Note(source.Name, ex);
            }
        }

        Dictionary<string, List<Viewer>> watching = new(StringComparer.Ordinal);
        foreach (LiveViewerSession session in sessions)
        {
            if (Match(channels, session) is { } channel)
            {
                (watching.TryGetValue(channel.Id, out List<Viewer>? list) ? list : watching[channel.Id] = []).Add(new Viewer(session.User, session.Via, session.Device));
            }
        }

        // ErsatzTV says a channel is being pulled; with nobody from a media server on it, a player is watching it directly.
        // A channel kept on around the clock is always being pulled, by us, so it's never taken for a direct viewer.
        IChannelViewers[] direct = [.. services.GetServices<IChannelViewers>()];
        AlwaysOnChannels? alwaysOn = services.GetService<AlwaysOnChannels>();
        foreach (AudienceChannel channel in channels)
        {
            if (!watching.ContainsKey(channel.Id) && alwaysOn?.IsAlwaysOn(channel.Number) != true
                && direct.Any(source => source.IsWatched(channel.Number) == true))
            {
                watching[channel.Id] = [new Viewer("Direct viewer", "Direct")];
            }
        }

        return watching.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<Viewer>)pair.Value, StringComparer.Ordinal);
    }

    /// <summary>A session's channel: by number when it has one we have, else by name.</summary>
    public static AudienceChannel? Match(IReadOnlyList<AudienceChannel> channels, LiveViewerSession session) =>
        (session.Number is { } number ? channels.FirstOrDefault(channel => channel.Number == number) : null)
        ?? (string.IsNullOrWhiteSpace(session.ChannelName) ? null
            : channels.FirstOrDefault(channel => string.Equals(channel.Name.Trim(), session.ChannelName.Trim(), StringComparison.OrdinalIgnoreCase)));

    public const string ClientName = "viewing-stats";

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

/// <summary>Who is watching Plex's Live TV (Plex isn't a library source here, only a way to watch), from Stats' settings.</summary>
public sealed class PlexLiveViewers(IHttpClientFactory httpClients, IOptionsMonitor<StatsOptions> options) : ILiveViewerSource
{
    public string Name => "Plex";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.CurrentValue.PlexUrl) && !string.IsNullOrWhiteSpace(options.CurrentValue.PlexToken);

    public async Task<IReadOnlyList<LiveViewerSession>> GetSessionsAsync(CancellationToken cancellationToken = default)
    {
        StatsOptions plex = options.CurrentValue;
        using HttpClient http = httpClients.CreateClient(AudienceReader.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{plex.PlexUrl.TrimEnd('/')}/status/sessions");
        request.Headers.Add("X-Plex-Token", plex.PlexToken.Trim());
        request.Headers.Add("Accept", "application/json");
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return Parse(json.RootElement);
    }

    /// <summary>
    /// Plex's sessions on Live TV (<c>live</c> set): the channel by its virtual number (<c>channelVcn</c>) or identifier,
    /// else its title or call sign, on the item or its media.
    /// </summary>
    public static IReadOnlyList<LiveViewerSession> Parse(JsonElement root)
    {
        List<LiveViewerSession> found = [];
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
            found.Add(new LiveViewerSession(LiveChannelNumbers.Read(vcn), title, user, "Plex", device));
        }

        return found;
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
}
