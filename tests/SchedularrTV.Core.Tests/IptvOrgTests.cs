using System.Text.Json;
using SchedularrTV.Iptv;
using SchedularrTV.Iptv.IptvOrg;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class IptvOrgTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "iptv-org-tests-" + Guid.NewGuid().ToString("N"));

    public IptvOrgTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static PlaylistChannel Channel(string name, string? tvgId = null, string? tvgName = null) =>
        new(tvgId ?? name, name, $"https://streams.example/{name}.m3u8", tvgId, tvgName);

    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CNN"] = "CNN.us",
        ["Cable News Network"] = "CNN.us",
        ["BBC One"] = "BBCOne.uk"
    };

    private static readonly IptvOrgGuideEntry[] Guides =
    [
        new("CNN.us", null, "tvtv.us", "cnn-1", "CNN", "en"),
        new("CNN.us", "East", "directv.com", "cnn-east", "CNN East", "en"),
        new("CNN.us", "SD", "tvguide.com", "cnn-sd", "CNN SD", "en"),
        new("BBCOne.uk", null, "sky.com", "2002", "BBC One", "en")
    ];

    [Theory]
    [InlineData("CNN.us", null, "CNN.us", null, false)]
    [InlineData("CNN.us@East", null, "CNN.us", "East", false)]
    [InlineData(null, "CNN", "CNN.us", null, true)]
    [InlineData(null, "CNN HD", "CNN.us", null, true)]
    [InlineData(null, "CNN (720p) [Geo-blocked]", "CNN.us", null, true)]
    public void A_playlist_channel_is_known_by_its_tvg_id_or_else_its_name(string? tvgId, string? name, string channel, string? feed, bool byName)
    {
        var identity = IptvOrgPlan.Identify(Channel(name ?? "x", tvgId), Names);

        Assert.Equal((channel, feed, byName), identity!.Value);
    }

    [Fact]
    public void A_channel_iptv_org_doesnt_know_is_left_out()
    {
        Assert.Null(IptvOrgPlan.Identify(Channel("Local Access 7"), Names));
        Assert.Empty(IptvOrgPlan.Plan([Channel("Local Access 7", "LocalAccess7.us")], Guides, Names));
    }

    [Fact]
    public void Each_channel_gets_the_site_for_its_own_feed_first_then_one_for_the_whole_channel()
    {
        List<GrabChannel> plan = IptvOrgPlan.Plan(
            [Channel("CNN East", "CNN.us@East"), Channel("CNN", "CNN.us"), Channel("CNN again", "CNN.us"), Channel("BBC One")],
            Guides, Names);

        Assert.Equal(new[] { "CNN.us@East", "CNN.us", "BBCOne.uk" }, plan.Select(channel => channel.XmltvId));
        Assert.Equal(new[] { "directv.com", "tvtv.us", "sky.com" }, plan.Select(channel => channel.Entry.Site));
        Assert.Equal(new[] { false, false, true }, plan.Select(channel => channel.ByName));
        Assert.Equal(3, plan[0].Sites);
    }

    [Fact]
    public void A_providers_own_tvg_id_falls_back_to_the_channel_name_but_keeps_its_id()
    {
        GrabChannel planned = Assert.Single(IptvOrgPlan.Plan([Channel("BBC One", "bbc1.provider")], Guides, Names));

        Assert.Equal(("bbc1.provider", "sky.com"), (planned.XmltvId, planned.Entry.Site));
    }

    [Fact]
    public void A_site_that_gave_nothing_is_skipped_until_every_site_has_been_tried()
    {
        var failed = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        PlaylistChannel cnn = Channel("CNN", "CNN.us");
        List<string> sites = [];
        for (int round = 0; round < 4; round++)
        {
            GrabChannel planned = Assert.Single(IptvOrgPlan.Plan([cnn], Guides, Names, failed));
            sites.Add(planned.Entry.Site);
            IptvOrgPlan.Remember(failed, planned, listed: false);
        }

        // Its own site first, then the feeds' sites, then round again.
        Assert.Equal(new[] { "tvtv.us", "directv.com", "tvguide.com", "tvtv.us" }, sites);

        GrabChannel working = Assert.Single(IptvOrgPlan.Plan([cnn], Guides, Names, failed));
        IptvOrgPlan.Remember(failed, working, listed: true);
        Assert.Empty(failed);
    }

    [Fact]
    public void The_channels_file_is_what_iptv_org_epg_reads_with_names_made_safe()
    {
        string xml = IptvOrgPlan.ChannelsXml([new GrabChannel("A&E.us", new IptvOrgGuideEntry("AandE.us", null, "tvtv.us", "a&e", "A&E <East>", "en"), "A&E", false)]);

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", xml, StringComparison.Ordinal);
        Assert.Contains("<channel site=\"tvtv.us\" lang=\"en\" xmltv_id=\"A&amp;E.us\" site_id=\"a&amp;e\">A&amp;E &lt;East&gt;</channel>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Iptv_orgs_lists_are_read_from_its_api()
    {
        using JsonDocument guides = JsonDocument.Parse("""
            [{"channel":"BBCOne.uk","feed":"EastMidlandsHD","site":"sky.co.uk","site_id":"bbcone","site_name":"BBC One","lang":"en","sources":[]},
             {"channel":null,"feed":null,"site":"x.com","site_id":"1","site_name":"Unlinked","lang":"en"}]
            """);
        using JsonDocument channels = JsonDocument.Parse("""
            [{"id":"CNN.us","name":"CNN","alt_names":["Cable News Network"],"is_nsfw":false},
             {"id":"Adult.us","name":"Adult","alt_names":[],"is_nsfw":true}]
            """);

        IptvOrgGuideEntry entry = Assert.Single(IptvOrgApi.ParseGuides(guides.RootElement));
        Assert.Equal(new IptvOrgGuideEntry("BBCOne.uk", "EastMidlandsHD", "sky.co.uk", "bbcone", "BBC One", "en"), entry);
        Dictionary<string, string> names = IptvOrgApi.ParseChannelNames(channels.RootElement);
        Assert.Equal("CNN.us", names["cable news network"]);
        Assert.False(names.ContainsKey("Adult"));
        Assert.Equal("https://iptv-org.github.io/iptv/countries/us.m3u", IptvOrgApi.PlaylistUrl(IptvOrgPlaylistKind.Country, "US"));
        Assert.True(IptvOrgApi.IsIptvOrgPlaylist("https://iptv-org.github.io/iptv/categories/news.m3u"));
    }

    [Fact]
    public void The_newest_long_term_support_node_is_picked()
    {
        using JsonDocument index = JsonDocument.Parse("""
            [{"version":"v25.1.0","lts":false},{"version":"v24.11.1","lts":"Krypton"},{"version":"v22.21.0","lts":"Jod"}]
            """);

        Assert.Equal("v24.11.1", IptvOrgTools.LatestLts(index.RootElement, 20));
        Assert.Null(IptvOrgTools.LatestLts(index.RootElement, 26));
        Assert.Equal(22, IptvOrgTools.Major("v22.21.0"));
    }

    [Fact]
    public void The_grab_command_comes_from_iptv_org_epgs_own_package_files()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "node_modules", "tsx"));
        File.WriteAllText(Path.Combine(_folder, "package.json"), """{"scripts":{"grab":"tsx scripts/commands/epg/grab2.ts"}}""");
        File.WriteAllText(Path.Combine(_folder, "node_modules", "tsx", "package.json"), """{"bin":{"tsx":"./dist/cli2.mjs"}}""");

        (string tsx, string script) = IptvOrgGuides.GrabCommand(_folder);

        Assert.Equal("scripts/commands/epg/grab2.ts", script);
        Assert.Equal(Path.GetFullPath(Path.Combine(_folder, "node_modules", "tsx", "dist", "cli2.mjs")), tsx);
    }

    [Fact]
    public async Task The_grabbed_guide_comes_first_and_the_providers_fills_in()
    {
        string file = Path.Combine(_folder, "guide.xml");
        File.WriteAllText(file, """
            <?xml version="1.0" encoding="UTF-8"?>
            <tv>
              <channel id="CNN.us"><display-name>CNN</display-name></channel>
              <channel id="BBCOne.uk"><display-name>BBC One</display-name></channel>
              <channel id="Empty.us"><display-name>Empty</display-name></channel>
              <programme start="20261003120000 +0000" stop="20261003130000 +0000" channel="CNN.us"><title>CNN Newsroom</title></programme>
              <programme start="20261003120000 +0000" stop="20261003140000 +0000" channel="BBCOne.uk"><title>Bargain Hunt</title></programme>
            </tv>
            """);

        XmltvGuide grabbed = await IptvFetcher.LoadIptvOrgGuideAsync(file, new Dictionary<string, string> { ["BBCOne.uk"] = "BBC 1 HD" }, Now, CancellationToken.None);
        var provider = new XmltvGuide();
        provider.Channels["p-cnn"] = ["CNN"];
        provider.Channels["p-local"] = ["Local 7"];
        provider.Programmes["p-cnn"] = [new GuideProgramme(Now, Now.AddHours(1), "Provider CNN", null, null, "<programme/>")];
        provider.Programmes["p-local"] = [new GuideProgramme(Now, Now.AddHours(1), "Local news", null, null, "<programme/>")];

        XmltvGuide merged = XmltvGuide.Merge(grabbed, provider)!;

        Assert.False(grabbed.Channels.ContainsKey("Empty.us"));
        Assert.Equal("CNN.us", merged.Match(Channel("CNN", "CNN.us")));
        Assert.Equal("CNN Newsroom", merged.Programmes[merged.Match(Channel("CNN", "CNN.us"))!][0].Title);
        Assert.Equal("BBCOne.uk", merged.Match(Channel("BBC 1 HD")));
        Assert.Equal("p-local", merged.Match(Channel("Local 7")));
        Assert.Same(provider, XmltvGuide.Merge(null, provider));
    }
}
