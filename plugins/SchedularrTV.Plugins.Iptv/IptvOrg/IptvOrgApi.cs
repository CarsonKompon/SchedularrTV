using System.Text.Json;

namespace SchedularrTV.Iptv.IptvOrg;

/// <summary>Where iptv-org/epg can find a channel's listings: a listings site and the channel's id there.</summary>
/// <param name="Channel">The iptv-org channel id, e.g. <c>CNN.us</c>.</param>
/// <param name="Feed">The channel's feed (<c>SD</c>, <c>East</c>…), or null for the channel as a whole.</param>
public sealed record IptvOrgGuideEntry(string Channel, string? Feed, string Site, string SiteId, string SiteName, string Lang);

/// <summary>A country, category or language iptv-org publishes a playlist for.</summary>
public sealed record IptvOrgChoice(string Code, string Name);

public enum IptvOrgPlaylistKind
{
    Country,
    Category,
    Language,
    Everything
}

/// <summary>
/// iptv-org's public database (<c>iptv-org.github.io/api</c>): which listings site covers each channel, channel names
/// (to find the id of a playlist entry that has none), and the countries, categories and languages with playlists.
/// Each list is kept on disk and fetched again once a day; a copy that can't be refreshed is used as it is.
/// </summary>
public sealed class IptvOrgApi(IHttpClientFactory http, IptvSettings settings, ILogger<IptvOrgApi> logger)
{
    private const string Base = "https://iptv-org.github.io/api/";
    private const string Playlists = "https://iptv-org.github.io/iptv/";
    private static readonly TimeSpan FreshFor = TimeSpan.FromDays(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset At, object Value)> _memory = new(StringComparer.Ordinal);

    private string Folder => Path.Combine(settings.DataDirectory, "iptv-org", "api");

    public static string PlaylistUrl(IptvOrgPlaylistKind kind, string code) => kind switch
    {
        IptvOrgPlaylistKind.Country => $"{Playlists}countries/{Uri.EscapeDataString(code.ToLowerInvariant())}.m3u",
        IptvOrgPlaylistKind.Category => $"{Playlists}categories/{Uri.EscapeDataString(code.ToLowerInvariant())}.m3u",
        IptvOrgPlaylistKind.Language => $"{Playlists}languages/{Uri.EscapeDataString(code.ToLowerInvariant())}.m3u",
        _ => $"{Playlists}index.m3u"
    };

    /// <summary>Whether a playlist address is one of iptv-org's (so its channels use iptv-org's ids).</summary>
    public static bool IsIptvOrgPlaylist(string? url) =>
        url?.Trim().StartsWith(Playlists, StringComparison.OrdinalIgnoreCase) == true;

    public Task<IReadOnlyList<IptvOrgGuideEntry>> GuidesAsync(CancellationToken cancellationToken) =>
        GetAsync("guides", root => (IReadOnlyList<IptvOrgGuideEntry>)ParseGuides(root), cancellationToken);

    /// <summary>Every channel's name and other names, lower-cased, to its id (the first channel to use a name keeps it).</summary>
    public Task<IReadOnlyDictionary<string, string>> ChannelIdsByNameAsync(CancellationToken cancellationToken) =>
        GetAsync("channels", root => (IReadOnlyDictionary<string, string>)ParseChannelNames(root), cancellationToken);

    public Task<IReadOnlyList<IptvOrgChoice>> CountriesAsync(CancellationToken cancellationToken) =>
        GetAsync("countries", root => Choices(root, "code", "name", "flag"), cancellationToken);

    public Task<IReadOnlyList<IptvOrgChoice>> CategoriesAsync(CancellationToken cancellationToken) =>
        GetAsync("categories", root => Choices(root, "id", "name", null), cancellationToken);

    public Task<IReadOnlyList<IptvOrgChoice>> LanguagesAsync(CancellationToken cancellationToken) =>
        GetAsync("languages", root => Choices(root, "code", "name", null), cancellationToken);

    private async Task<T> GetAsync<T>(string name, Func<JsonElement, T> parse, CancellationToken cancellationToken) where T : class
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (_memory.TryGetValue(name, out var held) && now - held.At < FreshFor && held.Value is T kept)
            {
                return kept;
            }

            string file = Path.Combine(Folder, name + ".json");
            bool fresh = File.Exists(file) && now - File.GetLastWriteTimeUtc(file) < FreshFor;
            if (!fresh)
            {
                try
                {
                    Directory.CreateDirectory(Folder);
                    using HttpResponseMessage response = await http.CreateClient(IptvFetcher.ClientName)
                        .GetAsync($"{Base}{name}.json", HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    string temp = file + ".download";
                    await using (FileStream output = File.Create(temp))
                    {
                        await response.Content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    }

                    File.Move(temp, file, overwrite: true);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    if (!File.Exists(file))
                    {
                        throw new InvalidOperationException($"Couldn't download iptv-org's {name} list: {ex.Message}", ex);
                    }

                    logger.LogWarning("IPTV: couldn't refresh iptv-org's {Name} list ({Message}); using the copy from before.", name, ex.Message);
                }
            }

            await using FileStream stream = File.OpenRead(file);
            using JsonDocument json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            T value = parse(json.RootElement);
            _memory[name] = (fresh ? File.GetLastWriteTimeUtc(file) : now, value);
            return value;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static List<IptvOrgGuideEntry> ParseGuides(JsonElement root)
    {
        List<IptvOrgGuideEntry> entries = [];
        if (root.ValueKind != JsonValueKind.Array)
        {
            return entries;
        }

        foreach (JsonElement entry in root.EnumerateArray())
        {
            if (Text(entry, "channel") is { } channel && Text(entry, "site") is { } site && Text(entry, "site_id") is { } siteId)
            {
                entries.Add(new IptvOrgGuideEntry(channel, Text(entry, "feed"), site, siteId, Text(entry, "site_name") ?? channel, Text(entry, "lang") ?? "en"));
            }
        }

        return entries;
    }

    public static Dictionary<string, string> ParseChannelNames(JsonElement root)
    {
        Dictionary<string, string> ids = new(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind != JsonValueKind.Array)
        {
            return ids;
        }

        foreach (JsonElement channel in root.EnumerateArray())
        {
            if (Text(channel, "id") is not { } id || Flag(channel, "is_nsfw"))
            {
                continue;
            }

            if (Text(channel, "name") is { } name)
            {
                ids.TryAdd(name.Trim(), id);
            }

            if (channel.TryGetProperty("alt_names", out JsonElement others) && others.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement other in others.EnumerateArray())
                {
                    if (other.ValueKind == JsonValueKind.String && other.GetString() is { Length: > 0 } alias)
                    {
                        ids.TryAdd(alias.Trim(), id);
                    }
                }
            }
        }

        return ids;
    }

    private static IReadOnlyList<IptvOrgChoice> Choices(JsonElement root, string code, string name, string? prefix)
    {
        List<IptvOrgChoice> choices = [];
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in root.EnumerateArray())
            {
                if (Text(item, code) is { } value && Text(item, name) is { } label)
                {
                    choices.Add(new IptvOrgChoice(value, prefix is not null && Text(item, prefix) is { } flag ? $"{flag} {label}" : label));
                }
            }
        }

        return [.. choices.OrderBy(choice => choice.Name.TrimStart().Split(' ', 2) is [_, var rest] && prefix is not null ? rest : choice.Name,
            StringComparer.CurrentCultureIgnoreCase)];
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static bool Flag(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
}
