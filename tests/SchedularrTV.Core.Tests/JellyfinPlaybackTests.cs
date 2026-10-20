using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Providers.ErsatzTv;
using SchedularrTV.Providers.Jellyfin;
using SchedularrTV.Service;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class JellyfinPlaybackTests
{
    private const string Id = "00112233445566778899aabbccddeeff";
    private const string RemotePath = "/not-mounted-on-this-host/tv/episode.mkv";
    private readonly JellyfinOptions _settings = new() { BaseUrl = "http://jellyfin:8096/jellyfin/", ApiKey = "private-key" };

    private MediaItem Item(string? path = RemotePath) => new()
    {
        Id = Id, Title = "Remote episode", Path = path, Duration = TimeSpan.FromMinutes(22),
        Attributes = new Dictionary<string, string>
        {
            [JellyfinPlaybackIdentity.ItemId] = Id,
            [JellyfinPlaybackIdentity.OriginalPath] = RemotePath,
            [JellyfinPlaybackIdentity.Server] = _settings.BaseUrl.TrimEnd('/')
        }
    };

    private JellyfinPlayback Playback(Handler handler, Dictionary<string, string?>? config = null) => new(
        Options.Create(_settings), new ConfigurationBuilder().AddInMemoryCollection(config ?? []).Build(),
        new EphemeralDataProtectionProvider(), new Factory(handler));

    [Fact]
    public void Remote_files_become_seekable_http_playout_without_credentials_or_path_changes()
    {
        var playback = Playback(new Handler());
        var options = new ErsatzTvOptions { ManageProcess = true };
        MediaItem item = Item();
        var start = DateTimeOffset.UtcNow;
        var schedule = new ChannelSchedule
        {
            ChannelId = "test", ChannelName = "Test", ChannelNumber = 1, Start = start, End = start.AddMinutes(2),
            Entries = [new ScheduleEntry
            {
                Item = item, Start = start, Duration = TimeSpan.FromMinutes(2), InPoint = TimeSpan.FromMinutes(5),
                IsTrimmed = true, Kind = ScheduleEntryKind.Program
            }]
        };
        string json = JsonSerializer.Serialize(PlayoutPublisher.Build(schedule, options, playbackUrls: playback), PlayoutPublisher.JsonOptions);
        Assert.DoesNotContain(_settings.ApiKey, json);
        Assert.DoesNotContain(RemotePath, json);
        Assert.Contains("http://127.0.0.1:5252/api/jellyfin/playback/", json);
        Assert.Contains("300000", json);
        Assert.Contains("420000", json);
        Assert.Equal(RemotePath, item.Path);
    }

    [Fact]
    public void Mapped_local_and_rendered_media_are_not_replaced_with_the_original_video()
    {
        var playback = Playback(new Handler());
        var options = new ErsatzTvOptions { ManageProcess = true };
        Assert.Null(playback.Resolve(Item("/rendered/credits.mp4"), options));
        Assert.Null(playback.Resolve(new MediaItem { Id = Id, Title = "Local", Path = RemotePath }, options));
        options.PathMappings.Add(new PathMapping { From = "/not-mounted-on-this-host", To = "/media" });
        Assert.Null(playback.Resolve(Item(), options));

        string file = Path.GetTempFileName();
        try
        {
            var local = new MediaItem
            {
                Id = Id, Title = "Local", Path = file,
                Attributes = new Dictionary<string, string>(Item().Attributes) { [JellyfinPlaybackIdentity.OriginalPath] = file }
            };
            Assert.Null(playback.Resolve(local, new ErsatzTvOptions { ManageProcess = true }));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void External_playout_needs_an_explicit_reachable_scheduler_address()
    {
        Assert.Null(Playback(new Handler()).Resolve(Item(), new ErsatzTvOptions()));
        var playback = Playback(new Handler(), new() { ["ErsatzTv:JellyfinStreamBaseUrl"] = "http://scheduler:5252/" });
        Assert.StartsWith("http://scheduler:5252/api/jellyfin/playback/", playback.Resolve(Item(), new ErsatzTvOptions()));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task Relay_authenticates_and_preserves_byte_ranges_for_seeking(string method)
    {
        var handler = new Handler();
        var playback = Playback(handler);
        string url = playback.Resolve(Item(), new ErsatzTvOptions { ManageProcess = true })!;
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Headers.Range = "bytes=10-13";
        context.Request.Headers.IfRange = "\"version\"";
        context.Response.Body = new MemoryStream();

        await playback.RelayAsync(url.Split('/')[^1], context);

        Assert.Equal(206, context.Response.StatusCode);
        Assert.Equal("bytes 10-13/100", context.Response.Headers.ContentRange);
        Assert.Equal(4, context.Response.ContentLength);
        Assert.Equal("bytes", context.Response.Headers.AcceptRanges);
        Assert.Equal("http://jellyfin:8096/jellyfin/Items/" + Id + "/Download", handler.Uri);
        Assert.Equal("private-key", handler.Key);
        Assert.Equal("bytes=10-13", handler.Range);
        Assert.Equal("\"version\"", handler.IfRange);
        Assert.Equal("GET", handler.Method);
        Assert.Equal(method == "HEAD" ? 0 : 4, context.Response.Body.Length);
    }

    [Fact]
    public async Task Invalid_tokens_and_changed_servers_cannot_fetch_media()
    {
        var handler = new Handler();
        var playback = Playback(handler);
        var context = new DefaultHttpContext();
        await playback.RelayAsync("not-a-ticket", context);
        Assert.Equal(404, context.Response.StatusCode);
        Assert.Null(handler.Uri);

        string url = playback.Resolve(Item(), new ErsatzTvOptions { ManageProcess = true })!;
        _settings.BaseUrl = "http://another-server:8096";
        context = new DefaultHttpContext();
        await playback.RelayAsync(url.Split('/')[^1], context);
        Assert.Equal(404, context.Response.StatusCode);
        Assert.Null(handler.Uri);
    }

    [Theory]
    [InlineData(401, 401)]
    [InlineData(302, 502)]
    [InlineData(416, 416)]
    public async Task Upstream_failures_keep_status_without_leaking_error_bodies(int status, int expected)
    {
        var handler = new Handler { Status = (HttpStatusCode)status };
        var playback = Playback(handler);
        string url = playback.Resolve(Item(), new ErsatzTvOptions { ManageProcess = true })!;
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();
        await playback.RelayAsync(url.Split('/')[^1], context);
        Assert.Equal(expected, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
    }

    private sealed class Factory(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.PartialContent;
        public string? Uri, Key, Range, IfRange, Method;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri!.AbsoluteUri;
            Key = request.Headers.GetValues("X-Emby-Token").Single();
            Range = request.Headers.Range?.ToString();
            IfRange = request.Headers.IfRange?.ToString();
            Method = request.Method.Method;
            var response = new HttpResponseMessage(Status) { Content = new ByteArrayContent([1, 2, 3, 4]) };
            response.Content.Headers.ContentLength = 4;
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(10, 13, 100);
            response.Headers.AcceptRanges.Add("bytes");
            return Task.FromResult(response);
        }
    }
}
