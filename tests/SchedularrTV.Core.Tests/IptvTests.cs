using System.Text;
using System.Xml.Linq;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Iptv;
using SchedularrTV.Providers.ErsatzTv;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class IptvTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "iptv-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 3, 18, 20, 30, TimeSpan.FromHours(-4));

    // --- Playlists -------------------------------------------------------------------------------

    private const string PlaylistText = """
        #EXTM3U url-tvg="http://provider.test/guide.xml.gz, http://provider.test/backup.xml"
        #EXTINF:-1 tvg-id="cnn.us" tvg-name="CNN" tvg-logo="http://logo.test/cnn.png" group-title="News, US" tvg-chno="202",CNN HD
        http://provider.test/live/cnn.m3u8
        #EXTINF:-1 tvg-id="cnn.us" group-title="News, US",CNN SD
        #EXTVLCOPT:http-user-agent=VLC/3.0
        http://provider.test/live/cnn-sd.ts

        #EXTINF:0,Local Access
        #EXTGRP:Community
        http://provider.test/live/local.ts
        """;

    [Fact]
    public void Playlists_give_each_stream_its_details_and_a_stable_key()
    {
        Playlist playlist = M3uParser.Parse(PlaylistText);

        Assert.Equal(["http://provider.test/guide.xml.gz", "http://provider.test/backup.xml"], playlist.GuideUrls);
        Assert.Equal(3, playlist.Channels.Count);

        PlaylistChannel hd = playlist.Channels[0];
        Assert.Equal("cnn.us", hd.Key);
        Assert.Equal("CNN HD", hd.Name);
        Assert.Equal("CNN", hd.TvgName);
        Assert.Equal("News, US", hd.Group);
        Assert.Equal(202, hd.Number);
        Assert.Equal("http://logo.test/cnn.png", hd.Logo);
        Assert.Equal("http://provider.test/live/cnn.m3u8", hd.Url);

        // The same guide id for a second stream still gets a key of its own.
        Assert.Equal("cnn.us#2", playlist.Channels[1].Key);
        Assert.Equal("VLC/3.0", playlist.Channels[1].UserAgent);

        PlaylistChannel local = playlist.Channels[2];
        Assert.Equal("Local Access", local.Key);
        Assert.Equal("Community", local.Group);
        Assert.Null(local.TvgId);
    }

    // --- Guides ----------------------------------------------------------------------------------

    private const string Guide = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE tv SYSTEM "xmltv.dtd">
        <tv>
          <channel id="cnn.us"><display-name>CNN</display-name></channel>
          <channel id="local.1"><display-name>Local Access</display-name></channel>
          <channel id="other"><display-name>Other</display-name></channel>
          <programme start="20261003180000 -0400" stop="20261003190000 -0400" channel="cnn.us">
            <title lang="en">The Situation Room</title><desc>News.</desc><icon src="http://img.test/a.jpg" />
          </programme>
          <programme start="20261003200000 -0400" stop="20261003210000 -0400" channel="cnn.us"><title>Anderson Cooper 360</title></programme>
          <programme start="20261003190000 -0400" stop="20261003200000 -0400" channel="cnn.us"><title>Erin Burnett OutFront</title></programme>
          <programme start="20261001000000 -0400" stop="20261001010000 -0400" channel="cnn.us"><title>Long ago</title></programme>
          <programme start="20261003180000 -0400" stop="20261003190000 -0400" channel="other"><title>Not wanted</title></programme>
        </tv>
        """;

    private static XmltvGuide ReadGuide(params string[] wanted)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Guide));
        return XmltvReader.Read(stream, _ => new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase), Now.AddHours(-12), Now.AddDays(7));
    }

    [Fact]
    public void Guides_keep_only_the_wanted_channels_and_times_in_order()
    {
        XmltvGuide guide = ReadGuide("cnn.us");

        Assert.Equal(3, guide.Channels.Count);
        Assert.False(guide.Programmes.ContainsKey("other"));
        Assert.Equal(["The Situation Room", "Erin Burnett OutFront", "Anderson Cooper 360"], guide.Programmes["cnn.us"].Select(programme => programme.Title));
        GuideProgramme first = guide.Programmes["cnn.us"][0];
        Assert.Equal("News.", first.Description);
        Assert.Contains("http://img.test/a.jpg", first.Xml);
    }

    [Fact]
    public void Playlist_channels_match_the_guide_by_id_then_by_name()
    {
        XmltvGuide guide = ReadGuide("cnn.us", "local.1");
        Playlist playlist = M3uParser.Parse(PlaylistText);

        Assert.Equal("cnn.us", guide.Match(playlist.Channels[0]));
        Assert.Equal("local.1", guide.Match(playlist.Channels[2]));
        Assert.Null(guide.Match(new PlaylistChannel("x", "Nowhere", "http://x.test/")));
    }

    [Theory]
    [InlineData("20261003180000 -0400", "2026-10-03T18:00:00-04:00")]
    [InlineData("20261003180000 +05:30", "2026-10-03T18:00:00+05:30")]
    [InlineData("20261003180000", "2026-10-03T18:00:00+00:00")]
    [InlineData("202610031800 +0100", "2026-10-03T18:00:00+01:00")]
    public void Guide_times_read_in_their_usual_shapes(string value, string expected)
    {
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), XmltvReader.ParseTime(value));
    }

    [Fact]
    public void Nonsense_times_are_ignored()
    {
        Assert.Null(XmltvReader.ParseTime("soon"));
        Assert.Null(XmltvReader.ParseTime(null));
    }

    // --- Schedules -------------------------------------------------------------------------------

    private static IptvSource Source() => new() { Id = "acme", Name = "Acme TV", Passthrough = true };

    [Fact]
    public void A_channel_schedule_follows_the_guide_from_the_listing_on_now_with_the_name_over_gaps()
    {
        PlaylistChannel cnn = M3uParser.Parse(PlaylistText).Channels[0];
        IReadOnlyList<GuideProgramme> listings = ReadGuide("cnn.us").Programmes["cnn.us"];

        ChannelSchedule schedule = IptvChannels.Build(Source(), cnn, 1001, listings, "http://localhost:5252/plugins/iptv/live/acme/1001.ts", Now, Now.AddHours(4));

        Assert.Equal(1001, schedule.ChannelNumber);
        Assert.Equal("iptv.acme.1001", schedule.TvgId);
        Assert.Equal("CNN HD", schedule.ChannelName);
        Assert.Equal("News, US", schedule.ChannelGroup);
        Assert.Equal("true", schedule.Annotations![LineupPublisher.CopyAnnotation]);

        // On now (from 18:00), the two after it, then the channel's name to the end.
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 18, 0, 0, TimeSpan.FromHours(-4)), schedule.Start);
        Assert.Equal(["The Situation Room", "Erin Burnett OutFront", "Anderson Cooper 360", "CNN HD"], schedule.Entries.Select(entry => entry.Item!.Title));
        Assert.Equal(Now.AddHours(4), schedule.End);
        for (int index = 1; index < schedule.Entries.Count; index++)
        {
            Assert.Equal(schedule.Entries[index - 1].End, schedule.Entries[index].Start);
        }

        ScheduleEntry first = schedule.Entries[0];
        Assert.Equal("true", first.Item!.Attributes[PlayoutPublisher.LiveAttribute]);
        Assert.Equal("http://localhost:5252/plugins/iptv/live/acme/1001.ts", first.Item.Path);
        Assert.Contains("The Situation Room", first.Annotations![XmltvPublisher.SourceProgrammeAnnotation]);
        Assert.Null(schedule.Entries[^1].Annotations);
    }

    [Fact]
    public void A_channel_without_listings_is_one_entry_under_its_name()
    {
        PlaylistChannel local = M3uParser.Parse(PlaylistText).Channels[2];
        IptvSource source = Source();
        source.Passthrough = false;
        source.Group = "IPTV";

        ChannelSchedule schedule = IptvChannels.Build(source, local, 1003, [], "http://x.test/", Now, Now.AddDays(2));

        ScheduleEntry only = Assert.Single(schedule.Entries);
        Assert.Equal("Local Access", only.Item!.Title);
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 18, 20, 0, TimeSpan.FromHours(-4)), only.Start);
        Assert.Equal("IPTV", schedule.ChannelGroup);
        Assert.Null(schedule.Annotations);
    }

    [Fact]
    public void The_plugin_and_the_ErsatzTV_output_agree_on_annotation_names()
    {
        Assert.Equal(XmltvPublisher.SourceProgrammeAnnotation, IptvChannels.SourceProgrammeAnnotation);
        Assert.Equal(LineupPublisher.CopyAnnotation, IptvChannels.CopyAnnotation);
    }

    // --- Publishing ------------------------------------------------------------------------------

    [Fact]
    public void The_providers_listing_reaches_the_guide_as_written_on_this_channel()
    {
        PlaylistChannel cnn = M3uParser.Parse(PlaylistText).Channels[0];
        IReadOnlyList<GuideProgramme> listings = ReadGuide("cnn.us").Programmes["cnn.us"];
        ChannelSchedule schedule = IptvChannels.Build(Source(), cnn, 1001, listings, "http://x.test/", Now, Now.AddHours(4));

        XDocument fragment = XDocument.Parse(XmltvPublisher.BuildFragment(schedule, GuideMode.Programs));
        List<XElement> programmes = [.. fragment.Root!.Elements("programme")];

        Assert.Equal(4, programmes.Count);
        Assert.All(programmes, programme => Assert.Equal("iptv.acme.1001", (string?)programme.Attribute("channel")));
        Assert.Equal("20261003180000 -0400", (string?)programmes[0].Attribute("start"));
        Assert.Equal("http://img.test/a.jpg", (string?)programmes[0].Element("icon")?.Attribute("src"));
        Assert.Equal("CNN HD", programmes[3].Element("title")?.Value);
    }

    [Fact]
    public void Passed_through_channels_are_marked_for_copying()
    {
        PlaylistChannel cnn = M3uParser.Parse(PlaylistText).Channels[0];
        IptvSource source = Source();

        Assert.True(LineupPublisher.IsPassthrough(IptvChannels.Build(source, cnn, 1, [], "http://x.test/", Now, Now.AddHours(1))));
        source.Passthrough = false;
        Assert.False(LineupPublisher.IsPassthrough(IptvChannels.Build(source, cnn, 1, [], "http://x.test/", Now, Now.AddHours(1))));
        Assert.Null(ChannelConfigSchema.Upgrade((System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(LineupPublisher.CopyOverlay)!));
    }

    // --- Relay -----------------------------------------------------------------------------------

    [Fact]
    public void Streams_are_copied_to_MPEG_TS_with_the_providers_headers()
    {
        IReadOnlyList<string> web = RelayHub.Arguments(new RelayTarget("a/1", "CNN", "http://provider.test/live.m3u8", "VLC/3.0", "http://provider.test/"));
        IReadOnlyList<string> file = RelayHub.Arguments(new RelayTarget("a/2", "Local", "D:/streams/local.ts", null, null));

        Assert.Contains("-reconnect", web);
        Assert.Equal("VLC/3.0", web[web.ToList().IndexOf("-user_agent") + 1]);
        Assert.Equal("http://provider.test/", web[web.ToList().IndexOf("-referer") + 1]);
        Assert.Equal("copy", web[web.ToList().IndexOf("-c") + 1]);
        Assert.Equal("pipe:1", web[^1]);
        Assert.DoesNotContain("-reconnect", file);
    }

    // --- Sources ---------------------------------------------------------------------------------

    [Fact]
    public void Numbers_are_given_once_from_the_first_number_skipping_any_in_use()
    {
        var store = new SourceStore(_folder);
        store.Save([new IptvSource { Name = "Acme", FirstNumber = 1000, Numbers = new() { ["kept"] = 1001 } }, new IptvSource { Name = "Other", FirstNumber = 1000, Numbers = new() { ["x"] = 1002 } }]);

        Assert.True(store.AssignNumbers("acme", ["a", "kept", "b"]));
        Assert.False(store.AssignNumbers("acme", ["a", "kept", "b"]));

        IptvSource acme = new SourceStore(_folder).Find("acme")!;
        Assert.Equal(1000, acme.Numbers["a"]);
        Assert.Equal(1001, acme.Numbers["kept"]);
        Assert.Equal(1003, acme.Numbers["b"]);
    }

    [Fact]
    public void Saving_an_older_copy_keeps_numbers_given_since()
    {
        var store = new SourceStore(_folder);
        store.Save([new IptvSource { Name = "Acme" }]);
        IptvSource editorCopy = store.Sources[0].Copy();
        store.AssignNumbers("acme", ["a"]);

        store.Save([editorCopy]);

        Assert.Equal(1000, store.Find("acme")!.Numbers["a"]);
    }
}
