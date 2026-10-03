using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SchedularrTV.Providers.ErsatzTv;

namespace SchedularrTV.Service;

/// <summary>
/// Relays ErsatzTV Next's HLS stream for the channel preview on the Channels page, so the browser only ever talks to
/// the scheduler: same origin, nothing to open in a firewall for ErsatzTV's port, and it works over https or from
/// another computer. Only the playlist and segment paths a player needs are passed through.
/// </summary>
public static partial class ErsatzTvPreviewProxy
{
    public const string RoutePrefix = "/api/ersatztv";
    public const string ClientName = "ersatztv-preview";

    // channel/5.m3u8, and the session's playlists and segments (session/5/live.m3u8, session/5/live001.ts…).
    [GeneratedRegex(@"^(channel/\d+\.m3u8|session/[A-Za-z0-9_-]+(/[A-Za-z0-9_-][A-Za-z0-9._-]*)+)$")]
    private static partial Regex AllowedPath();

    public static void MapErsatzTvPreview(this WebApplication app) =>
        app.MapGet(RoutePrefix + "/{**path}", RelayAsync);

    /// <summary>Where ErsatzTV listens, from this computer: its bind address, or loopback when it listens on all of them.</summary>
    public static string Upstream(ErsatzTvOptions options)
    {
        string host = options.Lineup.BindAddress?.Trim() is { Length: > 0 } bind and not ("0.0.0.0" or "::" or "[::]" or "*" or "+")
            ? bind
            : "127.0.0.1";
        if (host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('['))
        {
            host = $"[{host}]";
        }

        return $"http://{host}:{options.Lineup.Port}";
    }

    /// <summary>
    /// ErsatzTV writes absolute addresses into its playlists, built from the Host it was asked with (its own address,
    /// here); they're pointed back through this relay.
    /// </summary>
    public static string RewritePlaylist(string playlist, string upstream) =>
        playlist.Replace(upstream + "/", RoutePrefix + "/", StringComparison.OrdinalIgnoreCase);

    public static bool IsAllowed(string path) => AllowedPath().IsMatch(path) && !path.Contains("..", StringComparison.Ordinal);

    private static async Task RelayAsync(string path, HttpContext http, IOptions<ErsatzTvOptions> options, IHttpClientFactory clients)
    {
        CancellationToken cancellationToken = http.RequestAborted;
        if (!IsAllowed(path))
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        string upstream = Upstream(options.Value);
        http.Response.Headers.CacheControl = "no-cache, no-store";
        try
        {
            using HttpClient client = clients.CreateClient(ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{upstream}/{path}{http.Request.QueryString}");
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            http.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is { } type)
            {
                http.Response.ContentType = type.ToString();
            }

            if (response.IsSuccessStatusCode && path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                string playlist = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                await http.Response.WriteAsync(RewritePlaylist(playlist, upstream), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await body.CopyToAsync(http.Response.Body, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (HttpRequestException ex) when (!http.Response.HasStarted)
        {
            http.Response.StatusCode = StatusCodes.Status502BadGateway;
            http.Response.ContentType = "text/plain; charset=utf-8";
            await http.Response.WriteAsync($"ErsatzTV isn't answering at {upstream}: {ex.Message}", CancellationToken.None).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && !http.Response.HasStarted)
        {
            http.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            http.Response.ContentType = "text/plain; charset=utf-8";
            await http.Response.WriteAsync($"ErsatzTV at {upstream} didn't answer in time.", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException)
        {
            // The player went away (or ErsatzTV did, part-way through a segment); nothing more to send.
        }
    }
}
