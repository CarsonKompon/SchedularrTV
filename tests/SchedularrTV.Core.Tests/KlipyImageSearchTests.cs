using System.Net;
using System.Text.Json;
using SchedularrTV.Images.Klipy;
using SchedularrTV.Plugins.Images;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class KlipyImageSearchTests
{
    [Fact]
    public void Search_encodes_queries_and_keys_and_uses_stickers_for_transparency()
    {
        string url = KlipyImageSearch.Url(new ImageSearchRequest("Tom & Jerry") { Count = 70 }, "a/b?", true, "US");
        Assert.StartsWith("https://api.klipy.com/api/v1/a%2Fb%3F/gifs/search?", url);
        Assert.Contains("q=Tom%20%26%20Jerry", url);
        Assert.Contains("per_page=50", url);
        Assert.Contains("content_filter=high", url);
        Assert.Contains("locale=us", url);
        Assert.Contains("format_filter=gif", url);

        url = KlipyImageSearch.Url(new ImageSearchRequest("dance") { TransparentOnly = true, Count = 1 }, "key", false, null, 2);
        Assert.Contains("/stickers/search?", url);
        Assert.Contains("per_page=8", url);
        Assert.Contains("page=2", url);
        Assert.Contains("content_filter=off", url);
        Assert.DoesNotContain("locale=", url);
    }

    [Fact]
    public void Results_prefer_HD_gifs_fall_back_to_smaller_gifs_and_skip_other_media()
    {
        var results = KlipyImageSearch.Parse("""
            {"result":true,"data":{"has_next":true,"data":[
              {"title":"Dance","file":{"hd":{"gif":{"url":"https://example.com/hd.gif","width":498,"height":280}},"sm":{"gif":{"url":"https://example.com/sm.gif"}}}},
              {"file":{"hd":{"mp4":{"url":"https://example.com/video.mp4"}},"md":{"gif":{"url":"https://example.com/md.gif"}}}},
              {"file":{"hd":{"jpg":{"url":"https://example.com/still.jpg"}}}},
              {"file":{"hd":{"gif":{"url":"file:///tmp/image.gif"}}}},
              null, {"file":null}
            ]}}
            """, out bool hasNext, out string? error);
        Assert.Null(error);
        Assert.True(hasNext);
        Assert.Equal(2, results.Count);
        Assert.Equal("https://example.com/hd.gif", results[0].Url);
        Assert.Equal((498, 280), (results[0].Width, results[0].Height));
        Assert.Equal("Dance", results[0].Title);
        Assert.Equal("https://example.com/sm.gif", results[0].Thumbnail);
        Assert.Equal("https://example.com/md.gif", results[1].Url);
    }

    [Theory]
    [InlineData("<html>Unavailable</html>")]
    [InlineData("null")]
    [InlineData("{\"result\":false}")]
    [InlineData("{\"result\":true,\"data\":{}}")]
    public void Invalid_answers_are_reported_as_errors(string json)
    {
        Assert.Empty(KlipyImageSearch.Parse(json, out _, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Search_pages_beyond_fifty_results_and_honors_the_requested_count()
    {
        using var handler = new Handler((_, call) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                result = true,
                data = new
                {
                    has_next = call == 1,
                    data = Enumerable.Range((call - 1) * 50, 50).Select(i => new
                    {
                        file = new { hd = new { gif = new { url = $"https://example.com/{i}.gif" } } }
                    })
                }
            }))
        });
        using var client = new HttpClient(handler);
        var search = new KlipyImageSearch(new Factory(client), () => "key", () => true, () => null);
        var results = await search.SearchAsync(new ImageSearchRequest("dance") { Count = 70 });
        Assert.Equal(70, results.Count);
        Assert.Equal(2, handler.Urls.Count);
        Assert.Contains("page=2", handler.Urls[1]);
        Assert.Equal("https://example.com/69.gif", results[^1].Url);
    }

    [Fact]
    public async Task Missing_keys_and_HTTP_failures_are_actionable()
    {
        using var handler = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        using var client = new HttpClient(handler);
        string key = " ";
        var search = new KlipyImageSearch(new Factory(client), () => key, () => true, () => null);
        Assert.NotNull(search.SetupProblem);
        await Assert.ThrowsAsync<InvalidOperationException>(() => search.SearchAsync(new ImageSearchRequest("dance")));
        Assert.Empty(handler.Urls);
        key = "secret-key";
        Assert.Null(search.SetupProblem);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => search.SearchAsync(new ImageSearchRequest("dance")));
        Assert.Contains("429", error.Message);
        Assert.DoesNotContain(key, error.Message);
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Urls.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(respond(request, Urls.Count));
        }
    }
}
