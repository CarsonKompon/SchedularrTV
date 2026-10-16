using System.Globalization;
using System.Text.Json;

namespace SchedularrTV.Images.SerpApi;

/// <summary>
/// Google Images results through SerpApi's <c>google_images</c> engine: the full-size picture of each result, best
/// first, with Google's own transparent-background filter (<c>image_color=trans</c>) when asked for.
/// </summary>
public sealed class SerpApiImageSearch(IHttpClientFactory http, Func<string?> apiKey, Func<bool> safeSearch, Func<string?> country) : IImageSearchProvider
{
    public const string ProviderId = "serpapi";

    /// <summary>Read from the environment when the plugin's own setting is empty.</summary>
    public const string KeyVariable = "SERPAPI_API_KEY";

    private const string Endpoint = "https://serpapi.com/search.json";

    public string Id => ProviderId;

    public string DisplayName => "SerpApi (Google Images)";

    public string? SetupProblem => string.IsNullOrWhiteSpace(apiKey())
        ? "Enter your SerpApi API key in Plugins › SerpApi image search › Settings."
        : null;

    public async Task<IReadOnlyList<ImageSearchResult>> SearchAsync(ImageSearchRequest request, CancellationToken cancellationToken = default)
    {
        if (apiKey() is not { Length: > 0 } key)
        {
            throw new InvalidOperationException(SetupProblem);
        }

        string url = Url(request, key, request.SafeSearch && safeSearch(), country());
        using HttpResponseMessage response = await http.CreateClient(ProviderId).GetAsync(url, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ImageSearchResult> results = Parse(body, out string? error);
        if (error is not null && results.Count == 0)
        {
            // "Google hasn't returned any results for this query." isn't a failure, just nothing found.
            if (error.Contains("hasn't returned any results", StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            throw new InvalidOperationException($"SerpApi: {error}");
        }

        if (!response.IsSuccessStatusCode && results.Count == 0)
        {
            throw new InvalidOperationException($"SerpApi answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        return results;
    }

    /// <summary>The search's address: Google Images, the first page of results, filtered as asked.</summary>
    public static string Url(ImageSearchRequest request, string key, bool safe, string? country)
    {
        List<string> query =
        [
            "engine=google_images",
            $"q={Uri.EscapeDataString(request.Query)}",
            "ijn=0",
            $"safe={(safe ? "active" : "off")}",
            $"api_key={Uri.EscapeDataString(key)}"
        ];
        if (request.TransparentOnly)
        {
            query.Add("image_color=trans");
        }

        if (request.AnimatedOnly)
        {
            query.Add("image_type=animated");
        }

        if (country is { Length: 2 } gl)
        {
            query.Add($"gl={Uri.EscapeDataString(gl.ToLowerInvariant())}");
        }

        return $"{Endpoint}?{string.Join('&', query)}";
    }

    /// <summary>The results in SerpApi's answer (<c>images_results</c>, each's <c>original</c>), and its <c>error</c> if it gave one.</summary>
    public static IReadOnlyList<ImageSearchResult> Parse(string json, out string? error)
    {
        error = null;
        List<ImageSearchResult> results = [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("error", out JsonElement problem) && problem.ValueKind == JsonValueKind.String)
            {
                error = problem.GetString();
            }

            if (!root.TryGetProperty("images_results", out JsonElement images) || images.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (JsonElement image in images.EnumerateArray())
            {
                if (Text(image, "original") is not { Length: > 0 } original)
                {
                    continue;
                }

                results.Add(new ImageSearchResult(original)
                {
                    Width = Number(image, "original_width"),
                    Height = Number(image, "original_height"),
                    Title = Text(image, "title"),
                    Page = Text(image, "link"),
                    Thumbnail = Text(image, "thumbnail")
                });
            }
        }
        catch (JsonException ex)
        {
            error = $"its answer couldn't be read ({ex.Message}).";
        }

        return results;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value)
            ? value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number
            : value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed
            : 0
            : 0;
}
