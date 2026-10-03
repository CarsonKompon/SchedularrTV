using System.Net;
using System.Text;
using SchedularrTV.Providers.ErsatzTv;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class ErsatzTvSetupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"etv-setup-{Guid.NewGuid():N}");

    public ErsatzTvSetupTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_lineup_beside_the_program_is_found_nearest_first()
    {
        string program = Path.Combine(_root, "ersatztv", "ersatztv.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(program)!);
        File.WriteAllText(program, string.Empty);
        string config = Path.Combine(_root, "ersatztv", "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "lineup.json"), "{}");
        string other = Path.Combine(_root, "ersatztv", "mine");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "lineup.json"), "{}");

        IReadOnlyList<string> found = ErsatzTvLineupFinder.FoldersNear(program);

        Assert.Equal(Path.GetFullPath(config), found[0]);
        Assert.Contains(Path.GetFullPath(other), found);
        Assert.Empty(ErsatzTvLineupFinder.FoldersNear(null));
    }

    [Theory]
    [InlineData("20261003120000 -0400", 2026, 10, 3, 16)]
    [InlineData("20261003120000 +0000", 2026, 10, 3, 12)]
    public void Xmltv_times_are_read_with_their_offset(string text, int year, int month, int day, int utcHour)
    {
        DateTimeOffset parsed = ErsatzTvGuideCheck.Parse(text)!.Value;

        Assert.Equal(new DateTime(year, month, day, utcHour, 0, 0, DateTimeKind.Utc), parsed.UtcDateTime);
    }

    private sealed class FakeErsatz(string playlist, string guide) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith(".m3u", StringComparison.Ordinal) ? playlist : guide, Encoding.UTF8)
            });
    }

    private const string Playlist = """
        #EXTM3U
        #EXTINF:0 tvg-id="schedularrtv.9" tvg-name="Weather", Weather
        http://localhost:8409/channel/9.m3u8
        """;

    [Fact]
    public async Task An_empty_guide_while_guide_files_exist_points_at_ErsatzTV_reading_another_lineup()
    {
        string folder = Path.Combine(_root, "xmltv");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "schedularrtv.9.xml"), "<tv/>");
        const string guide = """<?xml version="1.0"?><tv><channel id="schedularrtv.9"><display-name>Weather</display-name></channel></tv>""";
        using var http = new HttpClient(new FakeErsatz(Playlist, guide));

        GuideCheckResult result = await ErsatzTvGuideCheck.RunAsync(http, "http://localhost:8409", folder, DateTimeOffset.Now, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(0, Assert.Single(result.Channels).Listings);
        Assert.Contains(result.Advice, advice => advice.Contains("different lineup", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_guide_with_listings_now_is_reported_with_how_to_refresh_jellyfin()
    {
        DateTimeOffset now = new(2026, 10, 3, 12, 0, 0, TimeSpan.FromHours(-4));
        const string guide = """
            <?xml version="1.0"?>
            <tv>
              <channel id="schedularrtv.9"><display-name>Weather</display-name></channel>
              <programme start="20261003113000 -0400" stop="20261003120000 -0400" channel="schedularrtv.9"><title>A</title></programme>
              <programme start="20261003120000 -0400" stop="20261003123000 -0400" channel="schedularrtv.9"><title>B</title></programme>
            </tv>
            """;
        using var http = new HttpClient(new FakeErsatz(Playlist, guide));

        GuideCheckResult result = await ErsatzTvGuideCheck.RunAsync(http, "http://localhost:8409", Path.Combine(_root, "none"), now, CancellationToken.None);

        Assert.True(result.Ok);
        GuideChannelCheck channel = Assert.Single(result.Channels);
        Assert.Equal(2, channel.Listings);
        Assert.True(channel.OnNow);
        Assert.Contains(result.Advice, advice => advice.Contains("Refresh Guide", StringComparison.Ordinal));
    }
}
