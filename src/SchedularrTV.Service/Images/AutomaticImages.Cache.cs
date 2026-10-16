using System.Collections.Concurrent;
using System.Text.Json;

namespace SchedularrTV.Service.Images;

public sealed record AutomaticImageCacheResult(string Url, string? Picture, bool Blocked, bool Picked, bool Unusable);

public sealed record AutomaticImageCacheSearch(string Id, string Query, string? Provider, DateTime At, bool Transparent,
    bool Animated, IReadOnlyList<AutomaticImageCacheResult> Results);

public sealed partial class AutomaticImages
{
    private sealed record RequestFile(string SearchId, AutomaticImageRequest Request);
    private sealed record BlockFile(SearchFile Search, List<string> Urls);

    private readonly ConcurrentDictionary<string, RequestFile> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HashSet<string>> _blocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _searchGates = new(StringComparer.Ordinal);

    private SemaphoreSlim SearchGate(string id) => _searchGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
    private string SearchFilePath(string id) => Path.Combine(_folder, "searches", id + ".json");
    private string BlockFilePath(string id) => Path.Combine(_folder, "blocked", id + ".json");

    private static string SearchId(string provider, AutomaticImageRequest request) =>
        Hash($"{provider}|{request.Query.Trim().ToLowerInvariant()}|{request.Transparent}" + (request.Animated ? "|animated" : string.Empty));

    private string? SearchId(AutomaticImageRequest request) => ProviderFor(request.Provider) is { } provider ? SearchId(provider.Id, request) : null;

    private void RememberRequest(AutomaticImageRequest request)
    {
        if (SearchId(request) is not { } id) return;
        var record = new RequestFile(id, request);
        if (!_requests.TryAdd(request.Key, record)) return;
        try
        {
            string file = Path.Combine(_folder, "requests", request.Key + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            // A small record written once per request per process, including when reusing a legacy pick.
            string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(record));
                File.Move(temp, file, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogDebug("Couldn't label an automatic image cache entry: {Message}", ex.Message);
        }
    }

    private HashSet<string> BlockedUrls(string id) => _blocks.GetOrAdd(id, key =>
        new HashSet<string>(ReadJson<BlockFile>(BlockFilePath(key))?.Urls ?? [], StringComparer.Ordinal));

    private bool BlockedPicture(AutomaticImageRequest request, string name) =>
        SearchId(request) is { } id && BlockedUrls(id).Any(url => Hash(url) == Path.GetFileNameWithoutExtension(name));

    private string? CachedPicture(string url)
    {
        string stem = Path.Combine(_folder, "pictures", Hash(url));
        return new[] { ".png", ".gif", ".webp" }.Select(extension => stem + extension).FirstOrDefault(File.Exists);
    }

    /// <summary>Reads cache metadata only: opening the browser never searches or downloads pictures.</summary>
    public Task<IReadOnlyList<AutomaticImageCacheSearch>> CachedSearchesAsync(CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<AutomaticImageCacheSearch>>(() =>
    {
        List<RequestFile> requests = ReadRequests();
        List<AutomaticImageCacheSearch> searches = [];
        var ids = FilesIn("searches").Concat(FilesIn("blocked")).Select(Path.GetFileNameWithoutExtension).OfType<string>().Distinct();
        foreach (string id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ValidId(id)) continue;
            BlockFile? blocked = ReadJson<BlockFile>(BlockFilePath(id));
            SearchFile? search = ReadJson<SearchFile>(SearchFilePath(id)) ?? blocked?.Search;
            if (search?.Urls is null) continue;
            RequestFile[] uses = [.. requests.Where(record => record.SearchId == id)];
            AutomaticImageRequest? known = uses.FirstOrDefault()?.Request;
            HashSet<string> picked = [];
            foreach (RequestFile use in uses)
            {
                if (Picked(use.Request) is { } picture) picked.Add(Path.GetFileNameWithoutExtension(picture));
            }
            HashSet<string> blockedUrls = BlockedUrls(id);
            // Blocked URLs stay manageable even after search results expire or are pruned.
            string[] urls = [.. search.Urls.Concat(blocked?.Urls ?? []).Where(url => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                && uri.Scheme is "http" or "https").Distinct(StringComparer.Ordinal)];
            searches.Add(new(id, search.Query ?? known?.Query ?? $"Older cached search ({id[..8]})", search.Provider ?? known?.Provider,
                search.At, search.Query is null ? known?.Transparent ?? false : search.Transparent,
                search.Query is null ? known?.Animated ?? false : search.Animated,
                [.. urls.Select(url => new AutomaticImageCacheResult(url, CachedPicture(url), blockedUrls.Contains(url), picked.Contains(Hash(url)),
                    File.Exists(Path.Combine(_folder, "pictures", Hash(url) + ".bad"))))]));
        }
        return searches.OrderByDescending(search => search.At).ToArray();
    }, cancellationToken);

    /// <summary>Downloads one cached candidate for inspection, without performing a provider search or changing the pick.</summary>
    public async Task<string?> PreviewCachedAsync(string searchId, string url, CancellationToken cancellationToken = default)
    {
        ValidateCandidate(searchId, url);
        return await DownloadAsync(url, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Blocks a URL for this resolved query, provider and filters. All result ranks share the blocklist. Invalidates
    /// only that search's picks; other shows/queries can still use the same downloaded image. Blocks are never pruned.
    /// </summary>
    public async Task SetBlockedAsync(string searchId, string url, bool blocked, CancellationToken cancellationToken = default)
    {
        if (!ValidId(searchId)) throw new ArgumentException("Invalid cached search id.", nameof(searchId));
        SemaphoreSlim gate = SearchGate(searchId);
        List<RequestFile> affected = [];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SearchFile search = ValidateCandidate(searchId, url);
            var urls = new HashSet<string>(BlockedUrls(searchId), StringComparer.Ordinal);
            if (!(blocked ? urls.Add(url) : urls.Remove(url))) return;
            // Persist first so a failed write never pretends the block was saved.
            if (urls.Count == 0) File.Delete(BlockFilePath(searchId));
            else await WriteJsonAsync(BlockFilePath(searchId), new BlockFile(search, [.. urls]), cancellationToken).ConfigureAwait(false);
            _blocks[searchId] = urls;
            affected = [.. ReadRequests().Where(record => record.SearchId == searchId)];
            foreach (RequestFile use in affected)
            {
                File.Delete(PickFile(use.Request));
                _failed.TryRemove(use.Request.Key, out _);
            }
        }
        finally { gate.Release(); }

        // Existing subscribers redraw previews and republish overlays; the next lookup reuses the cached candidates.
        foreach (RequestFile use in affected) Fetched?.Invoke(use.Request);
    }

    private SearchFile ValidateCandidate(string id, string url)
    {
        if (!ValidId(id)) throw new ArgumentException("Invalid cached search id.", nameof(id));
        BlockFile? blocked = ReadJson<BlockFile>(BlockFilePath(id));
        SearchFile? search = ReadJson<SearchFile>(SearchFilePath(id)) ?? blocked?.Search;
        if (search?.Urls is null || (!search.Urls.Contains(url, StringComparer.Ordinal) && !(blocked?.Urls?.Contains(url, StringComparer.Ordinal) ?? false)))
            throw new InvalidOperationException("This result is no longer in the cache. Refresh the list and try again.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("This cached result isn't an HTTP image URL.");
        if (search.Query is null && ReadRequests().FirstOrDefault(record => record.SearchId == id) is { Request: { } request })
            search = search with { Query = request.Query, Provider = ProviderFor(request.Provider)?.Id, Transparent = request.Transparent, Animated = request.Animated };
        return search;
    }

    private List<RequestFile> ReadRequests() => [.. _requests.Values.Concat(FilesIn("requests")
        .Select(ReadJson<RequestFile>).OfType<RequestFile>()).Where(record => record.Request is not null && !string.IsNullOrWhiteSpace(record.Request.Query))
        .DistinctBy(record => record.Request.Key)];

    private IEnumerable<string> FilesIn(string folder)
    {
        string directory = Path.Combine(_folder, folder);
        return Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json") : [];
    }

    private static bool ValidId(string id) => id.Length == 32 && id.All(char.IsAsciiHexDigit);

    private static T? ReadJson<T>(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file)) : default; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return default; }
    }

    private static async Task WriteJsonAsync<T>(string file, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value), cancellationToken).ConfigureAwait(false);
            File.Move(temp, file, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
