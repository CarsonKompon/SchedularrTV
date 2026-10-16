using System.Globalization;
using System.Text.Json;

namespace SchedularrTV.Images.Klipy;

/// <summary>Animated GIF results from Klipy, using stickers for transparent-background searches.</summary>
public sealed class KlipyImageSearch(IHttpClientFactory http, Func<string?> apiKey, Func<bool> safeSearch, Func<string?> country) : IImageSearchProvider
{
    public const string ProviderId = "klipy";
    public const string KeyVariable = "KLIPY_API_KEY";

    public string Id => ProviderId;
    public string DisplayName => "Klipy (GIFs)";
    public string? SetupProblem => string.IsNullOrWhiteSpace(apiKey())
        ? "Enter your Klipy API key in Plugins › Klipy GIF search › Settings."
        : null;

    public async Task<IReadOnlyList<ImageSearchResult>> SearchAsync(ImageSearchRequest request, CancellationToken cancellationToken = default)
    {
        string? key = apiKey()?.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(SetupProblem);
        }

        if (request.Count <= 0 || string.IsNullOrWhiteSpace(request.Query)) return [];

        List<ImageSearchResult> results = [];
        int pageSize = Math.Clamp(request.Count, 8, 50);
        int pages = (int)Math.Ceiling(request.Count / (double)pageSize);
        HttpClient client = http.CreateClient(ProviderId);
        for (int page = 1; page <= pages; page++)
        {
            using HttpResponseMessage response = await client.GetAsync(
                Url(request, key, request.SafeSearch && safeSearch(), country(), page), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Klipy answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ImageSearchResult> found = Parse(body, out bool hasNext, out string? error);
            if (error is not null) throw new InvalidOperationException($"Klipy: {error}");
            results.AddRange(found);
            if (!hasNext) break;
        }

        return results.DistinctBy(result => result.Url).Take(request.Count).ToArray();
    }

    /// <summary>Only request GIF media, never the MP4 or still-image alternatives.</summary>
    public static string Url(ImageSearchRequest request, string key, bool safe, string? country, int page = 1)
    {
        List<string> query =
        [
            $"q={Uri.EscapeDataString(request.Query)}",
            $"page={Math.Max(1, page).ToString(CultureInfo.InvariantCulture)}",
            $"per_page={Math.Clamp(request.Count, 8, 50).ToString(CultureInfo.InvariantCulture)}",
            $"content_filter={(safe ? "high" : "off")}",
            "format_filter=gif"
        ];
        if (country?.Trim() is { Length: 2 } locale)
        {
            query.Add($"locale={Uri.EscapeDataString(locale.ToLowerInvariant())}");
        }

        string kind = request.TransparentOnly ? "stickers" : "gifs";
        return $"https://api.klipy.com/api/v1/{Uri.EscapeDataString(key)}/{kind}/search?{string.Join('&', query)}";
    }

    /// <summary>Reads Klipy's data.data items, preferring the largest available GIF rendition.</summary>
    public static IReadOnlyList<ImageSearchResult> Parse(string json, out bool hasNext, out string? error)
    {
        hasNext = false;
        error = null;
        List<ImageSearchResult> results = [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (Property(root, "result").ValueKind != JsonValueKind.True)
            {
                error = "The search was unsuccessful. Check your API key and Klipy account limits.";
                return results;
            }

            JsonElement data = Property(root, "data");
            JsonElement items = Property(data, "data");
            if (items.ValueKind != JsonValueKind.Array)
            {
                error = "Its answer didn't contain a search results list.";
                return results;
            }

            hasNext = Property(data, "has_next").ValueKind == JsonValueKind.True;
            foreach (JsonElement item in items.EnumerateArray())
            {
                JsonElement file = Property(item, "file");
                foreach (string size in (string[])["hd", "md", "sm", "xs"])
                {
                    JsonElement gif = Property(Property(file, size), "gif");
                    string? url = Text(gif, "url");
                    if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https")) continue;
                    results.Add(new ImageSearchResult(url!)
                    {
                        Width = Number(gif, "width"),
                        Height = Number(gif, "height"),
                        Title = Text(item, "title"),
                        Thumbnail = Text(Property(Property(file, "sm"), "gif"), "url")
                    });
                    break;
                }
            }
        }
        catch (JsonException)
        {
            error = "Its answer couldn't be read as JSON.";
        }

        return results;
    }

    private static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) ? value : default;

    private static string? Text(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static int Number(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out int number) ? number : 0;
}
