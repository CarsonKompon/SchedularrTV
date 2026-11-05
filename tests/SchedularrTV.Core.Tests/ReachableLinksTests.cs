using System.Net;
using Microsoft.AspNetCore.Http;
using SchedularrTV.Core.Platform;
using SchedularrTV.Service;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>Channel logos and guide pictures have to load on other devices, which "localhost" can never reach.</summary>
public sealed class ReachableLinksTests
{
    [Theory]
    [InlineData("http://localhost:5252/logos/ch8-1a2b3c4d.png", "http://192.168.1.20:5252/logos/ch8-1a2b3c4d.png")]
    [InlineData("http://127.0.0.1:5252/logos/a.png?v=2", "http://192.168.1.20:5252/logos/a.png?v=2")]
    [InlineData("http://[::1]:5252/logos/a.png", "http://192.168.1.20:5252/logos/a.png")]
    [InlineData("https://example.com/logo.png", "https://example.com/logo.png")]
    [InlineData("http://192.168.1.5:5252/logos/a.png", "http://192.168.1.5:5252/logos/a.png")]
    public void Loopback_logo_links_move_to_the_network_address(string logo, string expected)
    {
        Assert.Equal(expected, NetworkAddress.Reachable(logo, "http://192.168.1.20:5252"));
    }

    [Fact]
    public void Without_a_network_address_links_are_left_alone()
    {
        Assert.Equal("http://localhost:5252/logos/a.png", NetworkAddress.Reachable("http://localhost:5252/logos/a.png", "http://localhost:5252"));
        Assert.Null(NetworkAddress.Reachable(null, "http://192.168.1.20:5252"));
    }

    [Theory]
    [InlineData("GET", "/logos/ch8.png", true)]
    [InlineData("HEAD", "/api/library/artwork/local-1?type=Primary", true)]
    [InlineData("GET", "/", false)]
    [InlineData("GET", "/api/schedules", false)]
    [InlineData("POST", "/logos/ch8.png", false)]
    [InlineData("GET", "/logos/../settings.json", false)]
    public void Other_devices_may_only_fetch_the_pictures(string method, string path, bool allowed)
    {
        // TEST-NET-3: never one of this computer's own addresses.
        IPAddress tv = IPAddress.Parse("203.0.113.7");
        string pathOnly = path.Split('?')[0];
        Assert.Equal(allowed, RemoteGuard.Allows(tv, method, new PathString(pathOnly)));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void This_computer_may_fetch_anything(string address)
    {
        Assert.True(RemoteGuard.Allows(IPAddress.Parse(address), "POST", new PathString("/api/schedules")));
    }
}
