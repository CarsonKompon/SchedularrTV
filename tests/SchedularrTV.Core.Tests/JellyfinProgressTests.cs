using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SchedularrTV.Providers.Jellyfin;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class JellyfinProgressTests
{
    [Fact]
    public async Task Library_read_reports_items_page_by_page_then_clears()
    {
        var handler = new FakeJellyfin(request =>
        {
            string query = request.RequestUri!.Query;
            if (!query.Contains("isMissing=false", StringComparison.Ordinal))
            {
                return """{"Items":[],"TotalRecordCount":0}""";
            }

            int start = int.Parse(System.Web.HttpUtility.ParseQueryString(query)["startIndex"]!);
            string items = string.Join(',', Enumerable.Range(start, Math.Min(2, 5 - start))
                .Select(index => $$"""{"Id":"item{{index}}","Name":"Item {{index}}","Type":"Movie","RunTimeTicks":6000000000,"Path":"M:\\m{{index}}.mkv"}"""));
            return $$"""{"Items":[{{items}}],"TotalRecordCount":5}""";
        });
        JellyfinMediaLibrary library = Library(handler, pageSize: 2);
        List<JellyfinLoadProgress?> seen = [];
        library.LoadingChanged += () => seen.Add(library.Loading);

        await library.RefreshAsync();

        Assert.Null(library.Loading);
        Assert.Equal("Connecting to Jellyfin", seen[0]!.Stage);
        Assert.Equal([2, 4, 5], seen.Where(progress => progress?.Stage == "Reading items").Select(progress => progress!.Items));
        Assert.All(seen.Where(progress => progress?.Stage == "Reading items"), progress => Assert.Equal(5, progress!.Total));
        Assert.Equal(100, seen.Last(progress => progress?.Stage == "Reading items")!.Percent);
        Assert.Contains(seen, progress => progress?.Stage == "Reading collections");
        Assert.Null(seen[^1]);
    }

    [Theory]
    [InlineData("Running", 42.5, true)]
    [InlineData("Idle", null, false)]
    public async Task Jellyfins_own_library_scan_is_read_from_its_scheduled_tasks(string state, double? percent, bool running)
    {
        string progress = percent is { } value ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null";
        var handler = new FakeJellyfin(request => request.RequestUri!.AbsolutePath.EndsWith("/ScheduledTasks", StringComparison.Ordinal)
            ? $$"""[{"Name":"Clean Transcode Directory","Key":"DeleteTranscodeFiles","State":"Running","CurrentProgressPercentage":10},{"Name":"Scan Media Library","Key":"RefreshLibrary","State":"{{state}}","CurrentProgressPercentage":{{progress}}}]"""
            : "[]");

        JellyfinServerScan? scan = await Client(handler).GetLibraryScanAsync(CancellationToken.None);

        Assert.Equal(running, scan is not null);
        if (running)
        {
            Assert.Equal(("Scan Media Library", 42.5), (scan!.Name, scan.Percent));
        }
    }

    private static JellyfinClient Client(FakeJellyfin handler, int pageSize = 500) => new(
        new Factory(handler),
        Options.Create(new JellyfinOptions { BaseUrl = "http://jellyfin.test", ApiKey = "key", UserId = "user", PageSize = pageSize }),
        NullLogger<JellyfinClient>.Instance);

    private static JellyfinMediaLibrary Library(FakeJellyfin handler, int pageSize) => new(
        Client(handler, pageSize),
        Options.Create(new JellyfinOptions { BaseUrl = "http://jellyfin.test", ApiKey = "key", UserId = "user", PageSize = pageSize }),
        NullLogger<JellyfinMediaLibrary>.Instance);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeJellyfin(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request), Encoding.UTF8, "application/json")
            });
    }
}
