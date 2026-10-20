using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Media;
using SchedularrTV.Providers.ErsatzTv;
using SchedularrTV.Providers.Jellyfin;

namespace SchedularrTV.Service;

/// <summary>
/// Seekable, authenticated original-file playback without a mounted share. Only signed item URLs
/// are accepted; Jellyfin credentials stay in the outgoing header, never the playout or browser URL.
/// </summary>
public sealed class JellyfinPlayback(
    IOptions<JellyfinOptions> jellyfin,
    IConfiguration configuration,
    IDataProtectionProvider protection,
    IHttpClientFactory clients) : IPlaybackUrlResolver
{
    public const string ClientName = "jellyfin-playback";
    public const string Route = "/api/jellyfin/playback/{token}";
    private readonly IDataProtector _protector = protection.CreateProtector("SchedularrTV.JellyfinPlayback.v1");

    public string? Resolve(MediaItem item, ErsatzTvOptions options)
    {
        JellyfinOptions settings = jellyfin.Value;
        if (!settings.IsConfigured
            || !item.Attributes.TryGetValue(JellyfinPlaybackIdentity.ItemId, out string? id)
            || !Guid.TryParse(id, out _)
            || !item.Attributes.TryGetValue(JellyfinPlaybackIdentity.Server, out string? server)
            || server != settings.BaseUrl.TrimEnd('/')
            || !item.Attributes.TryGetValue(JellyfinPlaybackIdentity.OriginalPath, out string? original)
            || (item.Path ?? string.Empty) != original)
        {
            return null;
        }

        // Explicit mappings describe the playout machine, which may not be this computer.
        // Respect them even when this host cannot see the mapped path.
        if (!string.IsNullOrEmpty(item.Path)
            && (PlayoutPublisher.MapPath(item.Path, options.PathMappings) != item.Path
                || File.Exists(item.Path)
                || Uri.TryCreate(item.Path, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https"))
        {
            return null;
        }

        string? address = configuration["ErsatzTv:JellyfinStreamBaseUrl"];
        if (string.IsNullOrWhiteSpace(address))
        {
            // No assumption that a separate/containerized ErsatzTV can reach our loopback.
            if (!options.ManageProcess) return null;
            string port = int.TryParse(configuration["Server:Port"], out int configured) ? configured.ToString() : "5252";
            address = $"http://127.0.0.1:{port}";
        }

        string token = _protector.Protect(JsonSerializer.Serialize(new Ticket(server, id)));
        return $"{address.TrimEnd('/')}/api/jellyfin/playback/{token}";
    }

    public async Task RelayAsync(string token, HttpContext context)
    {
        Ticket? ticket;
        try
        {
            ticket = JsonSerializer.Deserialize<Ticket>(_protector.Unprotect(token));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        JellyfinOptions settings = jellyfin.Value;
        if (ticket is null || !settings.IsConfigured || ticket.Server != settings.BaseUrl.TrimEnd('/')
            || !Guid.TryParse(ticket.ItemId, out _))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Download serves the exact item's original file (including alternate-version item IDs),
        // with byte ranges. ErsatzTV handles transcoding and scheduled in/out points itself.
        // Some Jellyfin versions expose Download only as GET. For a downstream HEAD, fetch
        // headers with GET and dispose the upstream response without downloading its body.
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{settings.BaseUrl.TrimEnd('/')}/Items/{Uri.EscapeDataString(ticket.ItemId)}/Download");
        request.Headers.TryAddWithoutValidation("X-Emby-Token", settings.ApiKey);
        foreach (string header in new[] { "Range", "If-Range" })
        {
            if (context.Request.Headers.TryGetValue(header, out var value))
                request.Headers.TryAddWithoutValidation(header, value.ToArray());
        }

        context.Response.Headers.CacheControl = "no-store";
        try
        {
            using HttpClient client = clients.CreateClient(ClientName);
            using HttpResponseMessage response = await client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, context.RequestAborted).ConfigureAwait(false);
            context.Response.StatusCode = (int)response.StatusCode;
            // Preserve range failures as well as 200/206, but never relay redirects or error bodies
            // containing upstream addresses, authentication details, or internal file paths.
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                if ((int)response.StatusCode is >= 300 and < 400) context.Response.StatusCode = 502;
                return;
            }

            foreach (string header in new[] { "Content-Type", "Content-Length", "Content-Range", "Last-Modified" })
            {
                if (response.Content.Headers.TryGetValues(header, out var values))
                    context.Response.Headers[header] = values.ToArray();
            }
            foreach (string header in new[] { "Accept-Ranges", "ETag" })
            {
                if (response.Headers.TryGetValues(header, out var values))
                    context.Response.Headers[header] = values.ToArray();
            }
            if (!response.IsSuccessStatusCode)
            {
                context.Response.ContentLength = 0;
                return;
            }
            if (!HttpMethods.IsHead(context.Request.Method))
                await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            if (context.RequestAborted.IsCancellationRequested) return;
            if (context.Response.HasStarted) context.Abort();
            else
            {
                context.Response.Headers.Clear();
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
            }
        }
    }

    private sealed record Ticket(string Server, string ItemId);
}
