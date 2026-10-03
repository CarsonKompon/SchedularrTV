using SchedularrTV.Providers.ErsatzTv;
using SchedularrTV.Service;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class ErsatzTvPreviewProxyTests
{
    [Theory]
    [InlineData("0.0.0.0", "http://127.0.0.1:8409")]
    [InlineData("", "http://127.0.0.1:8409")]
    [InlineData("::", "http://127.0.0.1:8409")]
    [InlineData("192.168.1.20", "http://192.168.1.20:8409")]
    [InlineData("::1", "http://[::1]:8409")]
    public void ErsatzTV_is_reached_on_loopback_unless_it_listens_on_one_address(string bind, string expected)
    {
        var options = new ErsatzTvOptions { Lineup = new LineupServerOptions { BindAddress = bind, Port = 8409 } };

        Assert.Equal(expected, ErsatzTvPreviewProxy.Upstream(options));
    }

    [Fact]
    public void Playlist_addresses_are_pointed_back_through_the_relay()
    {
        const string playlist = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="subs",URI="http://127.0.0.1:8409/session/5/live_sub.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=6811200,SUBTITLES="subs"
            http://127.0.0.1:8409/session/5/live.m3u8
            """;

        string rewritten = ErsatzTvPreviewProxy.RewritePlaylist(playlist, "http://127.0.0.1:8409");

        Assert.Contains("URI=\"/api/ersatztv/session/5/live_sub.m3u8\"", rewritten);
        Assert.Contains("\n/api/ersatztv/session/5/live.m3u8", rewritten);
        Assert.DoesNotContain("8409", rewritten);
    }

    [Theory]
    [InlineData("channel/5.m3u8", true)]
    [InlineData("session/5/live.m3u8", true)]
    [InlineData("session/5/live000123.ts", true)]
    [InlineData("channels.m3u", false)]
    [InlineData("xmltv.xml", false)]
    [InlineData("session/5/../../lineup.json", false)]
    [InlineData("channel/five.m3u8", false)]
    public void Only_what_a_player_needs_is_relayed(string path, bool allowed)
    {
        Assert.Equal(allowed, ErsatzTvPreviewProxy.IsAllowed(path));
    }
}
