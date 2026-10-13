#:property PublishAot=false
#:project ../src/SchedularrTV.Providers.Jellyfin/SchedularrTV.Providers.Jellyfin.csproj

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Query;
using SchedularrTV.Providers.Jellyfin;

// Repeatable full snapshot benchmark with streamed HTTP fixtures, no server or credentials.
// dotnet run -c Release --file tools/benchmark-jellyfin-scan.cs
using var handler = new ScanHandler();
using var http = new HttpClient(handler);
var options = Options.Create(new JellyfinOptions { BaseUrl = "http://jellyfin.test", ApiKey = "benchmark", UserId = "user" });
var client = new JellyfinClient(new Factory(http), options, NullLogger<JellyfinClient>.Instance);
var library = new JellyfinMediaLibrary(client, options, NullLogger<JellyfinMediaLibrary>.Instance);
for (int i = 0; i < 3; i++) await library.RefreshAsync();
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();
handler.Reset();
using var process = Process.GetCurrentProcess();
TimeSpan cpu = process.TotalProcessorTime;
long allocated = GC.GetTotalAllocatedBytes(true);
var timer = Stopwatch.StartNew();
const int iterations = 10;
for (int i = 0; i < iterations; i++) await library.RefreshAsync();
timer.Stop();
cpu = process.TotalProcessorTime - cpu;
allocated = GC.GetTotalAllocatedBytes(true) - allocated;
Console.WriteLine($"20k-item scan: {timer.Elapsed.TotalMilliseconds / iterations:F1} ms, {cpu.TotalMilliseconds / iterations:F1} CPU ms, {allocated / iterations:N0} B allocated");
Console.WriteLine($"Per scan: {handler.Requests / iterations} requests, {handler.CountRequests / iterations} count-enabled requests, {handler.Bytes / iterations:N0} response bytes");
var items = await library.SearchAsync(QueryExpression.All);
Console.WriteLine($"Snapshot: {items.Count} items, SHA256 {Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(items)))}");

sealed class Factory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}

sealed class ScanHandler : HttpMessageHandler
{
    private readonly Dictionary<string, byte[]> _responses = new();
    public int Requests { get; private set; }
    public int CountRequests { get; private set; }
    public long Bytes { get; private set; }
    public void Reset() { Requests = 0; CountRequests = 0; Bytes = 0; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
        Requests++;
        if (query["enableTotalRecordCount"] != "false") CountRequests++;
        string key = request.RequestUri.Query;
        if (!_responses.TryGetValue(key, out byte[]? bytes))
        {
            IEnumerable<object> items = [];
            int total = 0;
            if (query["isMissing"] == "false")
            {
                total = 20000;
                int start = int.Parse(query["startIndex"]!);
                int count = Math.Min(int.Parse(query["limit"]!), total - start);
                items = Enumerable.Range(start, Math.Max(0, count)).Select(i => (object)new
                {
                    Id = $"episode-{i}", Name = $"Episode {i}", Type = "Episode", SeriesId = "series", SeasonId = "season",
                    Path = $"/media/TV/Show/Season 1/episode-{i}.mkv", RunTimeTicks = TimeSpan.FromMinutes(22).Ticks,
                    Tags = new[] { "Drama" },
                    UserData = query["enableUserData"] == "false" ? null : new { Played = false, PlayCount = 0, IsFavorite = false, PlaybackPositionTicks = 0 }
                });
            }
            else if (query["includeItemTypes"] == "Series") items = [new { Id = "series", Name = "Show" }];
            else if (query["includeItemTypes"] == "Season") items = [new { Id = "season", IndexNumber = 1 }];
            else if (query["includeItemTypes"] == "BoxSet") items = Enumerable.Range(0, 50).Select(i => (object)new { Id = $"box-{i}", Name = $"Collection {i}" });
            else if (query["parentId"] is not null) items = Enumerable.Range(0, 500).Select(i => (object)new
            {
                Id = $"episode-{i}", Name = $"Episode {i}", Type = "Episode",
                ImageTags = query["enableImages"] == "false" ? null : new { Primary = "0123456789abcdef0123456789abcdef" },
                UserData = query["enableUserData"] == "false" ? null : new { Played = false, PlayCount = 0, IsFavorite = false, PlaybackPositionTicks = 0 }
            });
            bytes = JsonSerializer.SerializeToUtf8Bytes(new { Items = items, TotalRecordCount = query["enableTotalRecordCount"] == "false" ? 0 : total });
            _responses[key] = bytes;
        }
        Bytes += bytes.Length;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(bytes, writable: false))
        });
    }
}
