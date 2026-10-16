using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Plugins.Images;
using SkiaSharp;

namespace SchedularrTV.Service.Images;

/// <summary>One automatic image to find: a query with its tokens filled in, which result, and whether it must be transparent.</summary>
/// <param name="Result">1 is the first usable result.</param>
/// <param name="Provider">The picture search's id; null uses the first one set up.</param>
public sealed record AutomaticImageRequest(string Query, int Result, bool Transparent, string? Provider)
{
    /// <summary>What the request is kept under: two requests with the same key always get the same picture.</summary>
    public string Key => AutomaticImages.Hash($"{Query.Trim().ToLowerInvariant()}|{Math.Max(1, Result)}|{Transparent}|{Provider}");

    /// <summary>A request for <paramref name="config"/> with its query filled in; null when there's nothing to search for.</summary>
    public static AutomaticImageRequest? For(AutomaticImageConfig? config, string filledQuery) =>
        config is null || string.IsNullOrWhiteSpace(filledQuery)
            ? null
            : new AutomaticImageRequest(filledQuery.Trim(), Math.Clamp(config.Result, 1, 50), config.Transparent,
                string.IsNullOrWhiteSpace(config.Provider) ? null : config.Provider);
}

/// <summary>Variable images' automatic pictures, as found so far; see <see cref="AutomaticImages.Lookup"/>.</summary>
public sealed class AutomaticImageLookup(AutomaticImages images, bool queue) : IAutomaticImageLookup
{
    private readonly List<AutomaticImageRequest> _pending = [];

    /// <summary>Whether a picture asked for is still being searched for (so what used it may change once it's found).</summary>
    public bool Pending => _pending.Count > 0;

    public string? Find(AutomaticImageConfig config, string query)
    {
        if (AutomaticImageRequest.For(config, query) is not { } request)
        {
            return null;
        }

        string? found = images.Peek(request, queue);
        if (found is null && images.IsSearching(request))
        {
            lock (_pending) _pending.Add(request);
        }

        return found;
    }

    /// <summary>Waits for the searches still going when pictures were asked for (each ends found or not).</summary>
    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        AutomaticImageRequest[] waiting;
        lock (_pending) waiting = [.. _pending];
        foreach (AutomaticImageRequest request in waiting)
        {
            await images.GetAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Finds variable images' automatic pictures with the installed picture searches (plugins), and keeps them: each search's
/// results, each picture downloaded (checked, and saved as a PNG), and which picture each request got. A request is
/// only ever searched for once, so pictures stay the same from airing to airing and searches (often paid for) aren't
/// repeated. Callers that can't wait <see cref="Peek"/>, which starts the search in the background and raises
/// <see cref="Fetched"/> when the picture's there.
/// </summary>
public sealed class AutomaticImages
{
    public const string HttpClientName = "automatic-images";

    private readonly IReadOnlyList<IImageSearchProvider> _providers;
    private readonly IHttpClientFactory? _http;
    private readonly ILogger? _logger;
    private readonly string _folder;
    private readonly ConcurrentDictionary<string, Task<string?>> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTime At, string Problem)> _failed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _transparent = new(StringComparer.Ordinal);

    /// <summary>A request that found nothing isn't tried again for this long.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(30);

    /// <summary>A search's results are reused this long before it's searched again (pictures already picked stay).</summary>
    private static readonly TimeSpan SearchesLast = TimeSpan.FromDays(30);

    private const long MaxDownloadBytes = 25 * 1024 * 1024;
    private const int MaxSide = 2048;
    private const int Candidates = 20;

    public AutomaticImages(IEnumerable<IImageSearchProvider> providers, IHttpClientFactory? http, string folder, ILogger<AutomaticImages>? logger = null)
    {
        _providers = [.. providers];
        _http = http;
        _folder = folder;
        _logger = logger;
    }

    /// <summary>A picture has been found (or the search for it failed), from a worker thread.</summary>
    public event Action<AutomaticImageRequest>? Fetched;

    public IReadOnlyList<IImageSearchProvider> Providers => _providers;

    /// <summary>The picture search a request uses: the one it names, else the first set up, else the first.</summary>
    public IImageSearchProvider? ProviderFor(string? id) =>
        (id is { Length: > 0 } ? _providers.FirstOrDefault(provider => string.Equals(provider.Id, id, StringComparison.OrdinalIgnoreCase)) : null)
        ?? _providers.FirstOrDefault(provider => provider.SetupProblem is null)
        ?? _providers.FirstOrDefault();

    /// <summary>Why automatic images can't be found with this picture search right now; null when they can.</summary>
    public string? SetupProblem(string? providerId) => ProviderFor(providerId) switch
    {
        null => "No picture search is installed. Install one (such as SerpApi image search) from the Plugins page.",
        { SetupProblem: { } problem } provider => $"{provider.DisplayName}: {problem}",
        _ => null
    };

    /// <summary>Why the last try at this request found nothing, while it isn't being tried again; null otherwise.</summary>
    public string? Problem(AutomaticImageRequest request) =>
        _failed.TryGetValue(request.Key, out var failure) && DateTime.UtcNow - failure.At < RetryAfter ? failure.Problem : null;

    /// <summary>
    /// The request's picture if it's been found already; otherwise null, and (with <paramref name="queue"/>) it's
    /// looked for in the background, raising <see cref="Fetched"/> when done.
    /// </summary>
    public string? Peek(AutomaticImageRequest request, bool queue = true)
    {
        if (Picked(request) is { } picked)
        {
            return picked;
        }

        if (queue && Problem(request) is null && SetupProblem(request.Provider) is null)
        {
            _ = Start(request);
        }

        return null;
    }

    /// <summary>Whether the request is being searched for right now.</summary>
    public bool IsSearching(AutomaticImageRequest request) => _running.ContainsKey(request.Key);

    /// <summary>
    /// A lookup for resolving variable images with: pictures already found, and (with <paramref name="queue"/>) the rest
    /// looked for in the background. <see cref="AutomaticImageLookup.Pending"/> then says whether any is still coming.
    /// </summary>
    public AutomaticImageLookup Lookup(bool queue = true) => new(this, queue);

    /// <summary>The request's picture, looked for now if it hasn't been yet; null when none can be found.</summary>
    public async Task<string?> GetAsync(AutomaticImageRequest request, CancellationToken cancellationToken = default)
    {
        if (Picked(request) is { } picked)
        {
            return picked;
        }

        if (Problem(request) is not null || SetupProblem(request.Provider) is not null)
        {
            return null;
        }

        // Shared with any other caller after the same picture; a caller giving up doesn't stop it for the rest.
        return await Start(request).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<string?> Start(AutomaticImageRequest request) =>
        _running.GetOrAdd(request.Key, _ => Task.Run(async () =>
        {
            try
            {
                string? found = await FindAsync(request, CancellationToken.None).ConfigureAwait(false);
                return found;
            }
            catch (Exception ex)
            {
                Fail(request, ex.Message);
                _logger?.LogWarning("Automatic image for '{Query}' couldn't be found: {Message}", request.Query, ex.Message);
                return null;
            }
            finally
            {
                _running.TryRemove(request.Key, out _);
                Fetched?.Invoke(request);
            }
        }));

    private void Fail(AutomaticImageRequest request, string problem) => _failed[request.Key] = (DateTime.UtcNow, problem);

    private string PickFile(AutomaticImageRequest request) => Path.Combine(_folder, "picks", request.Key + ".txt");

    /// <summary>The picture already found for the request, if it's still there.</summary>
    private string? Picked(AutomaticImageRequest request)
    {
        string pick = PickFile(request);
        try
        {
            if (File.Exists(pick) && File.ReadAllText(pick).Trim() is { Length: > 0 } name)
            {
                string picture = Path.Combine(_folder, "pictures", Path.GetFileName(name));
                if (File.Exists(picture))
                {
                    Touch(picture);
                    Touch(pick);
                    return picture;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private async Task<string?> FindAsync(AutomaticImageRequest request, CancellationToken cancellationToken)
    {
        if (ProviderFor(request.Provider) is not { } provider)
        {
            Fail(request, "No picture search is installed.");
            return null;
        }

        IReadOnlyList<string> urls = await SearchAsync(provider, request, cancellationToken).ConfigureAwait(false);
        int wanted = Math.Max(1, request.Result);
        int usable = 0;
        foreach (string url in urls.Take(Candidates + wanted))
        {
            if (await DownloadAsync(url, cancellationToken).ConfigureAwait(false) is not { } picture)
            {
                continue;
            }

            if (request.Transparent && !IsTransparent(picture))
            {
                continue;
            }

            if (++usable < wanted)
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(PickFile(request))!);
            await File.WriteAllTextAsync(PickFile(request), Path.GetFileName(picture), cancellationToken).ConfigureAwait(false);
            _failed.TryRemove(request.Key, out _);
            return picture;
        }

        Fail(request, urls.Count == 0
            ? $"{provider.DisplayName} found nothing for “{request.Query}”."
            : $"Only {usable} usable{(request.Transparent ? " transparent" : "")} picture{(usable == 1 ? "" : "s")} found for “{request.Query}”, so there's no result {wanted}.");
        return null;
    }

    private sealed record SearchFile(DateTime At, List<string> Urls);

    /// <summary>The search's results (picture addresses, best first), kept so the same search isn't made twice.</summary>
    private async Task<IReadOnlyList<string>> SearchAsync(IImageSearchProvider provider, AutomaticImageRequest request, CancellationToken cancellationToken)
    {
        string file = Path.Combine(_folder, "searches", Hash($"{provider.Id}|{request.Query.Trim().ToLowerInvariant()}|{request.Transparent}") + ".json");
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<SearchFile>(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false)) is { } kept
                && DateTime.UtcNow - kept.At < SearchesLast)
            {
                return kept.Urls;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        IReadOnlyList<ImageSearchResult> results = await provider.SearchAsync(
            new ImageSearchRequest(request.Query) { TransparentOnly = request.Transparent, Count = Candidates + request.Result },
            cancellationToken).ConfigureAwait(false);
        List<string> urls = [.. results.Select(result => result.Url).Where(url => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && uri.Scheme is "http" or "https").Distinct(StringComparer.Ordinal)];
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new SearchFile(DateTime.UtcNow, urls)), cancellationToken).ConfigureAwait(false);
        return urls;
    }

    /// <summary>The picture at <paramref name="url"/>, downloaded, checked and saved as a PNG (at most 2048 pixels on a side); null when it can't be.</summary>
    private async Task<string?> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        string picture = Path.Combine(_folder, "pictures", Hash(url) + ".png");
        if (File.Exists(picture))
        {
            return picture;
        }

        string bad = Path.ChangeExtension(picture, ".bad");
        if (File.Exists(bad) || _http is null)
        {
            return null;
        }

        try
        {
            HttpClient client = _http.CreateClient(HttpClientName);
            using var message = new HttpRequestMessage(HttpMethod.Get, url);
            message.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (compatible; SchedularrTV automatic images)");
            message.Headers.TryAddWithoutValidation("Accept", "image/png,image/webp,image/*;q=0.9,*/*;q=0.5");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using HttpResponseMessage response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxDownloadBytes)
            {
                MarkBad(bad);
                return null;
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxDownloadBytes)
                {
                    MarkBad(bad);
                    return null;
                }
            }

            byte[]? png = Normalise(buffer.ToArray());
            if (png is null)
            {
                MarkBad(bad);
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(picture)!);
            string temp = $"{picture}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(temp, png, cancellationToken).ConfigureAwait(false);
            File.Move(temp, picture, overwrite: true);
            return picture;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException
                                       && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static void MarkBad(string bad)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(bad)!);
            File.WriteAllText(bad, string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A downloaded file as a PNG, shrunk to at most <see cref="MaxSide"/> on a side; null when it isn't a picture Skia can read.</summary>
    public static byte[]? Normalise(byte[] data)
    {
        using SKBitmap? decoded = SKBitmap.Decode(data);
        if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0)
        {
            return null;
        }

        SKBitmap bitmap = decoded;
        SKBitmap? resized = null;
        try
        {
            float scale = Math.Min(1f, MaxSide / (float)Math.Max(decoded.Width, decoded.Height));
            if (scale < 1f)
            {
                var info = new SKImageInfo(Math.Max(1, (int)(decoded.Width * scale)), Math.Max(1, (int)(decoded.Height * scale)), SKColorType.Rgba8888, SKAlphaType.Premul);
                resized = decoded.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                if (resized is not null)
                {
                    bitmap = resized;
                }
            }

            using SKImage image = SKImage.FromBitmap(bitmap);
            using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            return encoded?.ToArray();
        }
        finally
        {
            resized?.Dispose();
        }
    }

    /// <summary>Whether a picture has a see-through background: enough of it is clear (sampled across it).</summary>
    private bool IsTransparent(string picture) => _transparent.GetOrAdd(picture, path =>
    {
        try
        {
            using SKBitmap? bitmap = SKBitmap.Decode(path);
            return bitmap is not null && HasTransparentBackground(bitmap);
        }
        catch (Exception)
        {
            return false;
        }
    });

    /// <summary>At least 3% of the picture (sampled on a grid) is almost fully clear.</summary>
    public static bool HasTransparentBackground(SKBitmap bitmap)
    {
        if (bitmap.AlphaType == SKAlphaType.Opaque)
        {
            return false;
        }

        int stepX = Math.Max(1, bitmap.Width / 64);
        int stepY = Math.Max(1, bitmap.Height / 64);
        int clear = 0, total = 0;
        for (int y = 0; y < bitmap.Height; y += stepY)
        {
            for (int x = 0; x < bitmap.Width; x += stepX)
            {
                total++;
                if (bitmap.GetPixel(x, y).Alpha < 16)
                {
                    clear++;
                }
            }
        }

        return total > 0 && clear >= total * 0.03;
    }

    /// <summary>Removes pictures, searches and picks not used for <paramref name="age"/>.</summary>
    public void Prune(TimeSpan age)
    {
        DateTime cutoff = DateTime.UtcNow - age;
        foreach (string sub in (string[])["pictures", "searches", "picks"])
        {
            string directory = Path.Combine(_folder, sub);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(directory))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    // Marks a picture as in use (at most once a day), so pruning keeps what's still airing.
    private static void Touch(string path)
    {
        try
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(1))
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
}
