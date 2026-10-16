using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SchedularrTV.Plugins.Images;
using SchedularrTV.Service.Components.Shared;
using SchedularrTV.Service.Images;
using SkiaSharp;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class AutomaticImageCacheTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "image-cache-tests-" + Guid.NewGuid().ToString("N"));
    private const string First = "https://images.test/first.png";
    private const string Second = "https://images.test/second.png";
    private const string Third = "https://images.test/third.png";
    private readonly Search _search = new();
    private readonly Http _http = new();
    private AutomaticImages Create() => new([_search], _http, _folder);
    private static AutomaticImageRequest Request(string query = "Rugrats character", int result = 1) => new(query, result, false, "fake");
    public void Dispose() => TestFolders.Delete(_folder);

    [Fact]
    public async Task Browsing_is_read_only_and_previewing_does_not_search_or_change_the_pick()
    {
        AutomaticImages images = Create();
        string? picked = await images.GetAsync(Request());
        Assert.NotNull(picked);
        AutomaticImageCacheSearch cache = Assert.Single(await images.CachedSearchesAsync());
        Assert.Equal("Rugrats character", cache.Query);
        Assert.Equal(3, cache.Results.Count);
        Assert.True(cache.Results[0].Picked);
        Assert.NotNull(cache.Results[0].Picture);
        Assert.Null(cache.Results[1].Picture);
        Assert.Equal(1, _search.Calls);
        Assert.Equal(1, _http.Calls);
        Assert.NotNull(await images.PreviewCachedAsync(cache.Id, Second));
        Assert.Equal(picked, images.Peek(Request(), queue: false));
        Assert.Equal(1, _search.Calls);
        Assert.Equal(2, _http.Calls);
    }

    [Fact]
    public async Task Blocking_advances_every_rank_for_this_query_but_leaves_other_shows_alone()
    {
        AutomaticImages images = Create();
        string? first = await images.GetAsync(Request());
        string? second = await images.GetAsync(Request(result: 2));
        Assert.Equal(first, await images.GetAsync(Request("Doug character")));
        var cache = (await images.CachedSearchesAsync()).Single(search => search.Query == "Rugrats character");
        List<AutomaticImageRequest> notifications = [];
        images.Fetched += notifications.Add;
        await images.SetBlockedAsync(cache.Id, First, true);
        Assert.Equal(2, notifications.Count);
        Assert.All(notifications, request => Assert.Equal("Rugrats character", request.Query));
        Assert.Null(images.Peek(Request(), queue: false));
        Assert.Equal(first, images.Peek(Request("Doug character"), queue: false));
        Assert.Equal(second, await images.GetAsync(Request()));
        string? third = await images.GetAsync(Request(result: 2));
        Assert.NotNull(third);
        Assert.NotEqual(second, third);
        Assert.Equal(2, _search.Calls); // One search per show, no extra API call after blocking.
        Assert.True(File.Exists(first));
    }

    [Fact]
    public async Task Blocks_and_request_metadata_survive_restarts_and_unblocking_reselects()
    {
        AutomaticImages images = Create();
        string? original = await images.GetAsync(Request());
        string id = Assert.Single(await images.CachedSearchesAsync()).Id;
        await images.SetBlockedAsync(id, First, true);
        images = Create();
        string? replacement = await images.GetAsync(Request());
        Assert.NotNull(replacement);
        Assert.NotEqual(original, replacement);
        Assert.True(Assert.Single(await images.CachedSearchesAsync()).Results[0].Blocked);
        images = Create(); // No in-memory request registrations: unblocking still invalidates its saved pick.
        await images.SetBlockedAsync(id, First, false);
        Assert.Equal(original, await images.GetAsync(Request()));
        Assert.Equal(1, _search.Calls);
    }

    [Fact]
    public async Task All_blocked_results_return_no_image_and_unblocking_clears_the_failure_cooldown()
    {
        AutomaticImages images = Create();
        await images.GetAsync(Request());
        string id = Assert.Single(await images.CachedSearchesAsync()).Id;
        foreach (string url in new[] { First, Second, Third }) await images.SetBlockedAsync(id, url, true);
        Assert.Null(await images.GetAsync(Request()));
        Assert.NotNull(images.Problem(Request()));
        await images.SetBlockedAsync(id, Second, false);
        Assert.NotNull(await images.GetAsync(Request()));
        Assert.Equal(1, _search.Calls);
    }

    [Fact]
    public async Task Cache_cleanup_keeps_blocks_manageable_and_they_apply_to_future_searches()
    {
        AutomaticImages images = Create();
        await images.GetAsync(Request());
        string id = Assert.Single(await images.CachedSearchesAsync()).Id;
        await images.SetBlockedAsync(id, First, true);
        images.Prune(TimeSpan.Zero);
        images = Create();
        var retained = Assert.Single(await images.CachedSearchesAsync());
        Assert.Equal("Rugrats character", retained.Query);
        Assert.True(retained.Results[0].Blocked);
        Assert.NotNull(await images.GetAsync(Request()));
        var cached = Assert.Single(await images.CachedSearchesAsync());
        Assert.False(cached.Results[0].Picked);
        Assert.True(cached.Results[1].Picked);
        Assert.Equal(2, _search.Calls);
    }

    [Fact]
    public async Task Existing_cache_files_without_metadata_are_labeled_when_the_query_is_used_again()
    {
        AutomaticImages images = Create();
        await images.GetAsync(Request());
        string id = Assert.Single(await images.CachedSearchesAsync()).Id;
        string file = Path.Combine(_folder, "searches", id + ".json");
        JsonObject old = JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
        foreach (string name in new[] { "Query", "Provider", "Transparent", "Animated" }) old.Remove(name);
        await File.WriteAllTextAsync(file, old.ToJsonString());
        Directory.Delete(Path.Combine(_folder, "requests"), recursive: true);
        images = Create();
        await images.GetAsync(Request());
        Assert.Equal("Rugrats character", Assert.Single(await images.CachedSearchesAsync()).Query);
        await images.SetBlockedAsync(id, First, true);
        Assert.NotNull(await images.GetAsync(Request()));
        Assert.Equal(1, _search.Calls);
    }

    [Fact]
    public async Task Concurrent_ranks_and_identical_requests_share_a_single_provider_search()
    {
        _search.Delay = true;
        AutomaticImages images = Create();
        string?[] pictures = await Task.WhenAll(images.GetAsync(Request()), images.GetAsync(Request()), images.GetAsync(Request(result: 2)));
        Assert.All(pictures, picture => Assert.NotNull(picture));
        Assert.Equal(pictures[0], pictures[1]);
        Assert.NotEqual(pictures[0], pictures[2]);
        Assert.Equal(1, _search.Calls);
        Assert.Equal(2, _http.Calls);
    }

    [Fact]
    public async Task Browser_renders_cached_query_list_without_making_network_requests()
    {
        AutomaticImages images = Create();
        await images.GetAsync(Request());
        using var services = new ServiceCollection().AddLogging().AddSingleton(images).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<AutomaticImageCacheBrowser>(ParameterView.Empty)).ToHtmlString());
        Assert.Contains("Rugrats character", html);
        Assert.Contains("Only searches with blocked results", html);
        Assert.Equal(1, _search.Calls);
        Assert.Equal(1, _http.Calls);
    }

    [Fact]
    public async Task Blocking_an_inflight_candidate_invalidates_the_pick_written_by_that_lookup()
    {
        AutomaticImages images = Create();
        await images.GetAsync(Request());
        string id = Assert.Single(await images.CachedSearchesAsync()).Id;
        _http.PausedUrl = Second;
        Task<string?> finding = images.GetAsync(Request(result: 2));
        await _http.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task blocking = images.SetBlockedAsync(id, Second, true);
        _http.Release.TrySetResult();
        Assert.NotNull(await finding);
        await blocking;
        Assert.Null(images.Peek(Request(result: 2), queue: false));
        Assert.NotNull(await images.GetAsync(Request(result: 2)));
        var cache = Assert.Single(await images.CachedSearchesAsync());
        Assert.True(cache.Results[1].Blocked);
        Assert.True(cache.Results[2].Picked);
        Assert.Equal(1, _search.Calls);
    }

    [Fact]
    public async Task Cache_actions_reject_unknown_candidates_and_path_traversal()
    {
        AutomaticImages images = Create();
        await images.GetAsync(Request());
        string id = Assert.Single(await images.CachedSearchesAsync()).Id;
        await Assert.ThrowsAsync<ArgumentException>(() => images.SetBlockedAsync("../outside", First, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => images.PreviewCachedAsync(id, "https://other.test/file.png"));
    }

    private sealed class Search : IImageSearchProvider
    {
        public string Id => "fake";
        public string DisplayName => "Fake search";
        public int Calls;
        public bool Delay;
        public async Task<IReadOnlyList<ImageSearchResult>> SearchAsync(ImageSearchRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            if (Delay) await Task.Delay(50, cancellationToken);
            return [new(First), new(Second), new(Third)];
        }
    }

    private sealed class Http : IHttpClientFactory
    {
        public int Calls;
        public string? PausedUrl;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpClient CreateClient(string name) => new(new Handler(this));
        private sealed class Handler(Http owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner.Calls);
                if (request.RequestUri!.AbsoluteUri == owner.PausedUrl)
                {
                    owner.Started.TrySetResult();
                    await owner.Release.Task.WaitAsync(cancellationToken);
                }
                using var bitmap = new SKBitmap(16, 16);
                bitmap.Erase(request.RequestUri!.AbsoluteUri == First ? SKColors.Red : SKColors.Blue);
                using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data.ToArray()) };
            }
        }
    }
}
