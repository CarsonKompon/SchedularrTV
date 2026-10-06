using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SchedularrTV.Providers.Jellyfin;

/// <summary>
/// Thin Jellyfin REST wrapper. Only the read paths the scheduler needs are implemented.
/// </summary>
public sealed partial class JellyfinClient(IHttpClientFactory httpClientFactory, IOptions<JellyfinOptions> options, ILogger<JellyfinClient> logger)
{
    public const string HttpClientName = "jellyfin";

    public sealed record JellyfinImage(byte[] Content, string ContentType);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Read on each use: settings saved in the app apply straight away.
    private JellyfinOptions _options => options.Value;

    /// <summary>Crawls every configured item type, paging until the server reports no more results.</summary>
    internal async Task<List<JellyfinItem>> GetAllItemsAsync(CancellationToken cancellationToken, Action<int, int>? progress = null)
    {
        List<JellyfinItem> all = [];
        int startIndex = 0;

        // PremiereDate and ProductionYear are returned on every item, so they are not named here:
        // Jellyfin 12 dropped them from the fields enum.
        const string fields = "Overview,Tags,Path,ParentId,Chapters,MediaSourceCount";

        while (true)
        {
            string url = BuildUrl("/Items", new Dictionary<string, string?>
            {
                ["recursive"] = "true",
                ["includeItemTypes"] = string.Join(',', _options.IncludeItemTypes),
                ["fields"] = fields,
                ["enableImages"] = "false",

                // Episodes a series is known to have but no file exists for. They carry no path or
                // runtime, so they can never air, and on a large library they are a third of the crawl.
                ["isMissing"] = "false",
                ["startIndex"] = startIndex.ToString(),
                ["limit"] = _options.PageSize.ToString(),
                ["sortBy"] = "SortName",
                ["userId"] = _options.UserId
            });

            JellyfinItemsResponse? page = await GetAsync<JellyfinItemsResponse>(url, cancellationToken).ConfigureAwait(false);
            if (page is null || page.Items.Count == 0)
            {
                break;
            }

            all.AddRange(page.Items);
            startIndex += page.Items.Count;
            progress?.Invoke(all.Count, page.TotalRecordCount);

            if (page.TotalRecordCount > 0 && startIndex >= page.TotalRecordCount)
            {
                break;
            }
        }

        logger.LogInformation("Fetched {Count} items from Jellyfin.", all.Count);
        return all;
    }

    /// <summary>
    /// Series-level metadata per series id. Episodes expose only a single <c>SeriesStudio</c> and no
    /// series year at all, so the series items are fetched separately.
    /// </summary>
    internal async Task<Dictionary<string, JellyfinSeriesInfo>> GetSeriesInfoAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, JellyfinSeriesInfo> info = new(StringComparer.Ordinal);

        string url = BuildUrl("/Items", new Dictionary<string, string?>
        {
            ["recursive"] = "true",
            ["includeItemTypes"] = "Series",
            ["fields"] = "Studios",
            ["enableImages"] = "false",
            ["limit"] = "5000",
            ["userId"] = _options.UserId
        });

        JellyfinItemsResponse? response = await GetAsync<JellyfinItemsResponse>(url, cancellationToken).ConfigureAwait(false);

        foreach (JellyfinItem series in response?.Items ?? [])
        {
            List<string> names = [.. (series.Studios ?? [])
                .Select(s => s.Name?.Trim())
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .Distinct(StringComparer.OrdinalIgnoreCase)];

            info[series.Id] = new JellyfinSeriesInfo(series.Name, names, series.ProductionYear,
                string.IsNullOrWhiteSpace(series.OfficialRating) ? null : series.OfficialRating.Trim());
        }

        logger.LogInformation("Fetched metadata for {Count} series from Jellyfin.", info.Count);
        return info;
    }

    /// <summary>
    /// Season number per season id. An episode's own <c>ParentIndexNumber</c> is a denormalised copy
    /// that Jellyfin does not rewrite when a library is re-organised, so it can disagree with the
    /// season the episode actually sits in.
    /// </summary>
    internal async Task<Dictionary<string, int>> GetSeasonNumbersAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, int> seasons = new(StringComparer.Ordinal);

        string url = BuildUrl("/Items", new Dictionary<string, string?>
        {
            ["recursive"] = "true",
            ["includeItemTypes"] = "Season",
            ["enableImages"] = "false",
            ["limit"] = "20000",
            ["userId"] = _options.UserId
        });

        JellyfinItemsResponse? response = await GetAsync<JellyfinItemsResponse>(url, cancellationToken).ConfigureAwait(false);

        foreach (JellyfinItem season in response?.Items ?? [])
        {
            if (season.IndexNumber is { } index)
            {
                seasons[season.Id] = index;
            }
        }

        logger.LogInformation("Fetched {Count} season(s) from Jellyfin.", seasons.Count);
        return seasons;
    }

    /// <summary>
    /// Resolves merged version groups for the given items. Jellyfin returns each version as its own
    /// item, so without this every alternate would schedule as though it were a separate episode.
    /// </summary>
    /// <remarks>
    /// Requesting <c>MediaSources</c> is roughly four times the cost of a plain page, so only items
    /// that already reported more than one source are looked up.
    /// </remarks>
    internal async Task<Dictionary<string, JellyfinVersion>> GetVersionsAsync(
        IReadOnlyList<string> itemIds,
        CancellationToken cancellationToken,
        Action<int, int>? progress = null)
    {
        Dictionary<string, JellyfinVersion> versions = new(StringComparer.Ordinal);

        const int batchSize = 100;

        for (int offset = 0; offset < itemIds.Count; offset += batchSize)
        {
            string[] batch = [.. itemIds.Skip(offset).Take(batchSize)];

            string url = BuildUrl("/Items", new Dictionary<string, string?>
            {
                ["recursive"] = "true",
                ["ids"] = string.Join(',', batch),
                ["fields"] = "MediaSources",
                ["enableImages"] = "false",
                ["limit"] = batch.Length.ToString(),
                ["userId"] = _options.UserId
            });

            JellyfinItemsResponse? response = await GetAsync<JellyfinItemsResponse>(url, cancellationToken).ConfigureAwait(false);

            foreach (JellyfinItem item in response?.Items ?? [])
            {
                List<JellyfinMediaSource> sources = item.MediaSources ?? [];
                if (sources.Count < 2)
                {
                    continue;
                }

                // Every member lists the same source ids, just in a different order, so the lowest
                // id is a group identity all members independently agree on.
                string groupId = sources.Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).First();
                string? name = sources.FirstOrDefault(s => string.Equals(s.Id, item.Id, StringComparison.Ordinal))?.Name;

                versions[item.Id] = new JellyfinVersion(groupId, name ?? item.Name);
            }

            progress?.Invoke(Math.Min(itemIds.Count, offset + batch.Length), itemIds.Count);
        }

        logger.LogInformation("Resolved {Count} alternate version(s) from Jellyfin.", versions.Count);
        return versions;
    }

    /// <summary>
    /// Extras (intros, behind-the-scenes clips) that live in a title's <c>Extras</c> folder.
    /// </summary>
    /// <remarks>
    /// Jellyfin omits these when several item types are requested together, which is why the main
    /// crawl never sees them: asking for Episode+Movie+Video returns 19,070 items here while the
    /// same types asked for individually total 19,706. Requesting Video on its own returns them.
    /// </remarks>
    internal async Task<List<JellyfinItem>> GetExtrasAsync(CancellationToken cancellationToken)
    {
        string url = BuildUrl("/Items", new Dictionary<string, string?>
        {
            ["recursive"] = "true",
            ["includeItemTypes"] = "Video",
            ["fields"] = "Path,Tags,Overview",
            ["enableImages"] = "false",
            ["limit"] = "20000",
            ["userId"] = _options.UserId
        });

        JellyfinItemsResponse? response = await GetAsync<JellyfinItemsResponse>(url, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("Fetched {Count} standalone video(s) from Jellyfin.", response?.Items.Count ?? 0);
        return response?.Items ?? [];
    }

    /// <summary>
    /// A title's extras, which are the only place an intro or outro can be found. Needs a user
    /// context: the unscoped endpoint rejects the request outright.
    /// </summary>
    /// <summary>
    /// Where an item's end credits are, from Jellyfin's media segments (Jellyfin 10.10 and newer, with a segment provider
    /// such as Intro Skipper): the start and end of its first <c>Outro</c> segment. Null when there's none, or Jellyfin is
    /// older.
    /// </summary>
    public async Task<(TimeSpan Start, TimeSpan End)?> GetCreditsSegmentAsync(string itemId, CancellationToken cancellationToken)
    {
        JellyfinMediaSegments? segments = await GetIfFoundAsync<JellyfinMediaSegments>(
                BuildUrl($"/MediaSegments/{Uri.EscapeDataString(itemId)}", new Dictionary<string, string?> { ["includeSegmentTypes"] = "Outro" }),
                cancellationToken)
            .ConfigureAwait(false);
        JellyfinMediaSegment? outro = segments?.Items?
            .Where(segment => string.Equals(segment.Type, "Outro", StringComparison.OrdinalIgnoreCase) && segment.EndTicks > segment.StartTicks)
            .OrderBy(segment => segment.StartTicks)
            .FirstOrDefault();
        return outro is null ? null : (TimeSpan.FromTicks(outro.StartTicks), TimeSpan.FromTicks(outro.EndTicks));
    }

    public async Task<IReadOnlyList<JellyfinExtra>> GetSpecialFeaturesAsync(string itemId, CancellationToken cancellationToken)
    {
        string? userId = _options.UserId ?? await FirstUserIdAsync(cancellationToken).ConfigureAwait(false);

        if (userId is null)
        {
            return [];
        }

        List<JellyfinItem>? items = await GetAsync<List<JellyfinItem>>(
                BuildUrl($"/Items/{itemId}/SpecialFeatures", new Dictionary<string, string?> { ["userId"] = userId }),
                cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. (items ?? [])
                .Where(i => !string.IsNullOrWhiteSpace(i.Path))
                .Select(i => new JellyfinExtra(
                    i.Id,
                    i.Name ?? i.Id,
                    i.Path!,
                    TimeSpan.FromTicks(i.RunTimeTicks ?? 0)))
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
        ];
    }

    private async Task<string?> FirstUserIdAsync(CancellationToken cancellationToken)
    {
        if (_cachedUserId is not null)
        {
            return _cachedUserId;
        }

        List<JellyfinUser>? users = await GetAsync<List<JellyfinUser>>(BuildUrl("/Users", []), cancellationToken)
            .ConfigureAwait(false);

        _cachedUserId = users?.FirstOrDefault()?.Id;
        return _cachedUserId;
    }

    private string? _cachedUserId;

    /// <summary>Jellyfin's own "Scan Media Library" task while it runs, otherwise null.</summary>
    public async Task<JellyfinServerScan?> GetLibraryScanAsync(CancellationToken cancellationToken)
    {
        List<JellyfinScheduledTask>? tasks = await GetAsync<List<JellyfinScheduledTask>>(
            BuildUrl("/ScheduledTasks", new Dictionary<string, string?> { ["isHidden"] = "false" }), cancellationToken).ConfigureAwait(false);
        JellyfinScheduledTask? scan = tasks?.FirstOrDefault(task => string.Equals(task.Key, "RefreshLibrary", StringComparison.OrdinalIgnoreCase));
        return scan is { State: "Running" or "Cancelling" }
            ? new JellyfinServerScan(scan.Name ?? "Scan Media Library", scan.CurrentProgressPercentage)
            : null;
    }

    /// <summary>Every library configured on the server, for the settings picker.</summary>
    public async Task<IReadOnlyList<JellyfinLibrary>> GetLibrariesAsync(CancellationToken cancellationToken)
    {
        List<JellyfinVirtualFolder>? folders =
            await GetAsync<List<JellyfinVirtualFolder>>(BuildUrl("/Library/VirtualFolders", []), cancellationToken).ConfigureAwait(false);

        return
        [
            .. (folders ?? [])
                .Where(f => !string.IsNullOrWhiteSpace(f.Name))
                .Select(f => new JellyfinLibrary(f.Name!, f.Locations))
                .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>Root folders of the named libraries, used to filter their contents out of the snapshot.</summary>
    internal async Task<IReadOnlyList<string>> GetLibraryPathsAsync(
        IReadOnlyList<string> libraryNames,
        CancellationToken cancellationToken)
    {
        List<JellyfinVirtualFolder>? folders =
            await GetAsync<List<JellyfinVirtualFolder>>(BuildUrl("/Library/VirtualFolders", []), cancellationToken).ConfigureAwait(false);

        List<string> paths = [];

        foreach (JellyfinVirtualFolder folder in folders ?? [])
        {
            if (libraryNames.Any(n => string.Equals(n, folder.Name, StringComparison.OrdinalIgnoreCase)))
            {
                paths.AddRange(folder.Locations);
            }
        }

        foreach (string name in libraryNames.Where(n => !(folders ?? []).Any(f => string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase))))
        {
            logger.LogWarning("Excluded library '{Library}' does not exist in Jellyfin.", name);
        }

        return paths;
    }

    /// <summary>Maps every collection (BoxSet) to the ids of its members.</summary>
    internal async Task<Dictionary<string, List<string>>> GetCollectionMembershipAsync(CancellationToken cancellationToken, Action<int, int>? progress = null)
    {
        Dictionary<string, List<string>> membership = new(StringComparer.Ordinal);

        string boxSetsUrl = BuildUrl("/Items", new Dictionary<string, string?>
        {
            ["recursive"] = "true",
            ["includeItemTypes"] = "BoxSet",
            ["limit"] = "5000",
            ["userId"] = _options.UserId
        });

        JellyfinItemsResponse? boxSets = await GetAsync<JellyfinItemsResponse>(boxSetsUrl, cancellationToken).ConfigureAwait(false);
        if (boxSets is null)
        {
            return membership;
        }

        // One request per collection, so a server with hundreds of them is reported as it goes.
        int read = 0;
        foreach (JellyfinItem boxSet in boxSets.Items)
        {
            progress?.Invoke(read++, boxSets.Items.Count);
            string membersUrl = BuildUrl("/Items", new Dictionary<string, string?>
            {
                ["parentId"] = boxSet.Id,
                ["recursive"] = "true",
                ["limit"] = "10000",
                ["fields"] = "ParentId",
                ["userId"] = _options.UserId
            });

            JellyfinItemsResponse? members = await GetAsync<JellyfinItemsResponse>(membersUrl, cancellationToken).ConfigureAwait(false);
            if (members is null || string.IsNullOrWhiteSpace(boxSet.Name))
            {
                continue;
            }

            membership[boxSet.Name] = members.Items.Select(m => m.Id).ToList();
        }

        return membership;
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl) || string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new JellyfinConfigurationException(
                "Jellyfin is not configured. Set Jellyfin:BaseUrl and Jellyfin:ApiKey (Dashboard > API Keys).");
        }

        using HttpRequestMessage request = CreateRequest(url);

        HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string path = new Uri(url, UriKind.RelativeOrAbsolute).AbsolutePath;
            logger.LogError("Jellyfin request to {Path} failed with {Status}.", path, response.StatusCode);

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                throw new JellyfinConfigurationException(
                    $"Jellyfin rejected the API key ({(int)response.StatusCode}). Create one under Dashboard > API Keys and set Jellyfin:ApiKey.");
            }

            response.EnsureSuccessStatusCode();
        }

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One of an item's images, scaled down by the server. Null when it has none or Jellyfin is not configured.</summary>
    public async Task<JellyfinImage?> GetImageAsync(string itemId, string imageType, int maxWidth, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl) || string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return null;
        }

        string url = BuildUrl($"/Items/{Uri.EscapeDataString(itemId)}/Images/{Uri.EscapeDataString(imageType)}", new Dictionary<string, string?>
        {
            ["maxWidth"] = maxWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["quality"] = "90"
        });

        using HttpRequestMessage request = CreateRequest(url);
        HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        byte[] content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return new JellyfinImage(content, response.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
    }

    private HttpRequestMessage CreateRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        // Jellyfin accepts the api key in the Authorization header; keeping it out of the query
        // string avoids leaking it into proxy/access logs.
        request.Headers.Authorization = Authorization();

        return request;
    }

    private AuthenticationHeaderValue Authorization() => new(
        "MediaBrowser",
        $"Token=\"{_options.ApiKey}\", Client=\"SchedularrTV\", Device=\"SchedularrTV\", DeviceId=\"schedularrtv\", Version=\"1.0\"");

    /// <summary>Like <see cref="GetAsync{T}"/>, but a missing item (404) is null rather than an error worth logging.</summary>
    private async Task<T?> GetIfFoundAsync<T>(string url, CancellationToken cancellationToken) where T : class
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl) || string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return null;
        }

        using HttpRequestMessage request = CreateRequest(url);
        HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private string BuildUrl(string path, Dictionary<string, string?> query)
    {
        var builder = new UriBuilder(new Uri(new Uri(_options.BaseUrl.TrimEnd('/') + "/"), path.TrimStart('/')));
        builder.Query = string.Join('&', query
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));

        return builder.Uri.ToString();
    }
}
