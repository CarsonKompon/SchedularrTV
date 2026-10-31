using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace SchedularrTV.Providers.Jellyfin;

/// <summary>Reading and updating one item's metadata, for the library pages' metadata editor.</summary>
public sealed partial class JellyfinClient
{
    /// <summary>The server's address, as configured (for saying where a change goes).</summary>
    public string ServerAddress => _options.BaseUrl.TrimEnd('/');

    /// <summary>
    /// One item exactly as the server describes it, every field included, as JSON; null when it isn't found. Kept as
    /// JSON rather than a typed object so updating it sends back everything, including fields this app doesn't know.
    /// </summary>
    internal async Task<JsonObject?> GetItemDocumentAsync(string itemId, CancellationToken cancellationToken)
    {
        RequireConfigured();
        string? userId = _options.UserId ?? await FirstUserIdAsync(cancellationToken).ConfigureAwait(false);

        // Jellyfin 10.9+ answers /Items/{id}; older servers only the per-user route. The single-item routes return
        // every field, which updating needs.
        string current = BuildUrl($"/Items/{Uri.EscapeDataString(itemId)}", new Dictionary<string, string?> { ["userId"] = userId });
        if (await SendForDocumentAsync(current, cancellationToken).ConfigureAwait(false) is { } found)
        {
            return found;
        }

        return string.IsNullOrWhiteSpace(userId)
            ? null
            : await SendForDocumentAsync(BuildUrl($"/Users/{Uri.EscapeDataString(userId)}/Items/{Uri.EscapeDataString(itemId)}", NoQuery()), cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces the item's metadata with <paramref name="document"/>. Jellyfin's update takes the whole item: anything
    /// left out is cleared, so <paramref name="document"/> must be the full item from <see cref="GetItemDocumentAsync"/>
    /// with only the edited fields changed.
    /// </summary>
    internal async Task UpdateItemDocumentAsync(string itemId, JsonObject document, CancellationToken cancellationToken)
    {
        RequireConfigured();
        string url = BuildUrl($"/Items/{Uri.EscapeDataString(itemId)}", NoQuery());
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(document.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = Authorization();

        HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        logger.LogError("Jellyfin refused the metadata update for {Item}: {Status} {Detail}", itemId, response.StatusCode, detail);
        throw response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? (Exception)new JellyfinConfigurationException(
                $"Jellyfin refused the change ({(int)response.StatusCode}). Editing metadata needs an administrator's API key (Dashboard > API Keys).")
            : new HttpRequestException($"Jellyfin refused the change ({(int)response.StatusCode} {response.ReasonPhrase}).", null, response.StatusCode);
    }

    private async Task<JsonObject?> SendForDocumentAsync(string url, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(url);
        HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.MethodNotAllowed)
        {
            return null;
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new JellyfinConfigurationException(
                $"Jellyfin rejected the API key ({(int)response.StatusCode}). Create one under Dashboard > API Keys and set Jellyfin:ApiKey.");
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, string?> NoQuery() => new();

    private void RequireConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl) || string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new JellyfinConfigurationException(
                "Jellyfin is not configured. Set Jellyfin:BaseUrl and Jellyfin:ApiKey (Dashboard > API Keys).");
        }
    }
}
