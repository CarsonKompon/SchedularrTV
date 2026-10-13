using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Query;
using SchedularrTV.Providers.Jellyfin;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class JellyfinScanTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Paged_scan_counts_once_when_available_and_handles_servers_without_totals(bool suppliesTotal)
    {
        List<int> starts = [];
        List<string?> counts = [];
        using var handler = new Handler(request =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            Assert.Equal("false", query["enableImages"]);
            Assert.Equal("false", query["enableUserData"]);
            Assert.Equal("user", query["userId"]);
            Assert.NotNull(request.Headers.Authorization);
            int start = int.Parse(query["startIndex"]!);
            starts.Add(start);
            counts.Add(query["enableTotalRecordCount"]);
            // Return less than the requested limit, as servers can cap page sizes themselves.
            string items = start < 3 ? $$"""{"Id":"item-{{start}}"}""" : "";
            int total = !suppliesTotal ? 0 : query["enableTotalRecordCount"] == "true" ? 3 : 1;
            return Json($$"""{"Items":[{{items}}],"TotalRecordCount":{{total}}}""");
        });
        using var http = new HttpClient(handler);
        List<int> progress = [];
        var items = await Client(http).GetAllItemsAsync(default, (_, total) => progress.Add(total));
        Assert.Equal(["item-0", "item-1", "item-2"], items.Select(item => item.Id));
        Assert.Equal(suppliesTotal ? [0, 1, 2] : new[] { 0, 1, 2, 3 }, starts);
        Assert.Equal(suppliesTotal ? ["true", "false", "false"] : new[] { "true", "true", "true", "true" }, counts);
        Assert.All(progress, total => Assert.Equal(suppliesTotal ? 3 : 0, total));
    }

    [Fact]
    public async Task Lean_scan_preserves_collections_series_versions_chapters_and_attributes()
    {
        using var handler = new Handler(request =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            Assert.Equal("false", query["enableImages"]);
            Assert.Equal("false", query["enableUserData"]);
            Assert.Equal(query["isMissing"] == "false" ? "true" : "false", query["enableTotalRecordCount"]);
            if (query["isMissing"] == "false") return Json("""
                {"Items":[{"Id":"episode","Name":"Episode","Type":"Episode","SeriesId":"series","SeasonId":"season",
                "SeriesName":"Old name","ParentIndexNumber":9,"MediaSourceCount":2,"RunTimeTicks":1000000000,
                "Path":"/TV/Show/Season 1/episode.mkv","Tags":["Drama","BRAND: Example"],
                "Chapters":[{"StartPositionTicks":0},{"StartPositionTicks":900000000,"Name":"End Credits"}]}],"TotalRecordCount":1}
                """);
            if (query["ids"] is not null) return Json("""{"Items":[{"Id":"episode","MediaSources":[{"Id":"other"},{"Id":"episode","Name":"DVD"}]}]}""");
            if (query["parentId"] == "box") return Json("""{"Items":[{"Id":"series"},{"Id":"episode"}]}""");
            return Json(query["includeItemTypes"] switch
            {
                "BoxSet" => """{"Items":[{"Id":"box","Name":"Collection"},{"Id":"unnamed"}]}""",
                "Series" => """{"Items":[{"Id":"series","Name":"Show","OfficialRating":"TV-PG","Genres":["Drama"],"Studios":[{"Name":"Network"}]}]}""",
                "Season" => """{"Items":[{"Id":"season","IndexNumber":1}]}""",
                "Video" => """{"Items":[{"Id":"extra","Name":"Intro","Type":"Video","RunTimeTicks":10000000}]}""",
                _ => throw new InvalidOperationException(request.RequestUri.ToString())
            });
        });
        using var http = new HttpClient(handler);
        var library = new JellyfinMediaLibrary(Client(http), Options(), NullLogger<JellyfinMediaLibrary>.Instance);
        var items = await library.SearchAsync(QueryExpression.All);
        Assert.Equal(2, items.Count);
        var episode = Assert.Single(items, item => item.Id == "episode");
        Assert.Equal("Show", episode.SeriesName);
        Assert.Equal(1, episode.SeasonNumber);
        Assert.Equal(["Collection"], episode.Collections);
        Assert.Equal(["Drama"], episode.Tags);
        Assert.Equal(["Drama"], episode.Genres);
        Assert.Equal(["Network"], episode.Networks);
        Assert.Equal("TV-PG", episode.Rating);
        Assert.Equal("Example", episode.Attributes["brand"]);
        Assert.Equal(TimeSpan.FromSeconds(90), episode.CreditsStart);
        Assert.Equal([TimeSpan.Zero, TimeSpan.FromSeconds(90)], episode.Chapters);
        Assert.Equal("episode", episode.VersionGroupId);
        Assert.Equal("DVD", episode.VersionName);
        Assert.Empty(Assert.Single(items, item => item.Id == "extra").Attributes);
    }

    [Fact]
    public async Task Json_is_read_directly_from_the_response_stream_and_disposed()
    {
        var content = new UnbufferedContent(new MemoryStream(Encoding.UTF8.GetBytes("""{"Items":[{"Id":"one"}],"TotalRecordCount":1}""")));
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var http = new HttpClient(handler);
        Assert.Single(await Client(http).GetAllItemsAsync(default));
        Assert.True(content.Disposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Streamed_body_honours_request_timeout_and_caller_cancellation(bool timeout)
    {
        using var cancellation = new CancellationTokenSource();
        var stream = new WaitingStream();
        var content = new UnbufferedContent(stream);
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var http = new HttpClient(handler) { Timeout = timeout ? TimeSpan.FromMilliseconds(200) : Timeout.InfiniteTimeSpan };
        Task scan = Client(http).GetAllItemsAsync(cancellation.Token);
        if (!timeout)
        {
            await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(content.Disposed);
    }

    private static IOptions<JellyfinOptions> Options() => Microsoft.Extensions.Options.Options.Create(new JellyfinOptions
    {
        BaseUrl = "http://jellyfin.test", ApiKey = "key", UserId = "user", PageSize = 2
    });

    private static JellyfinClient Client(HttpClient http) => new(new Factory(http), Options(), NullLogger<JellyfinClient>.Instance);
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class UnbufferedContent(Stream stream) : HttpContent
    {
        public bool Disposed { get; private set; }
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => throw new InvalidOperationException("Response must not be buffered.");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult(stream);
        protected override void Dispose(bool disposing) { Disposed = true; if (disposing) stream.Dispose(); base.Dispose(disposing); }
    }

    private sealed class WaitingStream : MemoryStream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
