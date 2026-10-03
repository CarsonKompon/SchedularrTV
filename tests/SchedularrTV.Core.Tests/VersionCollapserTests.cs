using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class VersionCollapserTests
{
    private static MediaItem Version(string id, string group, string versionName) => new()
    {
        Id = id,
        Title = "The Beginning",
        Kind = MediaKind.Episode,
        Duration = TimeSpan.FromMinutes(22),
        SeriesId = "nirvana",
        SeriesName = "Nirvana the Band the Show",
        SeasonNumber = 1,
        EpisodeNumber = 1,
        PremiereDate = new DateTimeOffset(2007, 1, 1, 0, 0, 0, TimeSpan.Zero),
        VersionGroupId = group,
        VersionName = versionName
    };

    private static readonly MediaItem Web = Version("b-web", "a-dvd", "S01E01 The Beginning - Web Release");
    private static readonly MediaItem Dvd = Version("a-dvd", "a-dvd", "S01E01 The Beginning - DVD Release");

    [Fact]
    public void A_version_group_contributes_exactly_one_item()
    {
        IReadOnlyList<MediaItem> collapsed = VersionCollapser.Collapse([Web, Dvd], null);

        Assert.Single(collapsed);
    }

    [Fact]
    public void Items_without_alternates_are_untouched()
    {
        MediaItem single = TestMedia.Episode("Solo", 1, 1, TimeSpan.FromMinutes(11));

        IReadOnlyList<MediaItem> collapsed = VersionCollapser.Collapse([single, Web, Dvd], null);

        Assert.Equal(2, collapsed.Count);
        Assert.Contains(single, collapsed);
    }

    [Fact]
    public void Excluding_a_version_selects_the_remaining_one()
    {
        VersionSelection selection = new() { Exclude = ["Web Release"] };

        IReadOnlyList<MediaItem> collapsed = VersionCollapser.Collapse([Web, Dvd], selection);

        Assert.Equal(Dvd.Id, Assert.Single(collapsed).Id);
    }

    [Fact]
    public void Preferring_a_version_selects_it_over_the_default()
    {
        VersionSelection selection = new() { Prefer = ["Web Release"] };

        IReadOnlyList<MediaItem> collapsed = VersionCollapser.Collapse([Web, Dvd], selection);

        Assert.Equal(Web.Id, Assert.Single(collapsed).Id);
    }

    [Fact]
    public void Prefer_falls_back_through_the_list_in_order()
    {
        VersionSelection selection = new() { Prefer = ["Bluray", "Web Release"] };

        IReadOnlyList<MediaItem> collapsed = VersionCollapser.Collapse([Web, Dvd], selection);

        Assert.Equal(Web.Id, Assert.Single(collapsed).Id);
    }

    [Fact]
    public void Version_matching_is_case_insensitive()
    {
        VersionSelection selection = new() { Exclude = ["web release"] };

        IReadOnlyList<MediaItem> collapsed = VersionCollapser.Collapse([Web, Dvd], selection);

        Assert.Equal(Dvd.Id, Assert.Single(collapsed).Id);
    }

    [Fact]
    public void Excluding_every_version_keeps_the_episode_rather_than_dropping_it()
    {
        // The point of a block list is to choose between versions, not to delete content that only
        // exists in the blocked flavour.
        VersionSelection selection = new() { Exclude = ["Web Release", "DVD Release"] };

        IReadOnlyList<MediaItem> collapsed = VersionCollapser.Collapse([Web, Dvd], selection);

        Assert.Single(collapsed);
    }

    [Fact]
    public void An_episode_with_only_a_blocked_version_still_plays()
    {
        MediaItem onlyWeb = Version("solo-web", "solo-web", "S01E09 The Bday - Web Release");
        VersionSelection selection = new() { Exclude = ["Web Release"] };

        IReadOnlyList<MediaItem> collapsed = VersionCollapser.Collapse([onlyWeb], selection);

        Assert.Equal(onlyWeb.Id, Assert.Single(collapsed).Id);
    }

    [Fact]
    public void The_default_pick_does_not_depend_on_provider_order()
    {
        MediaItem first = Assert.Single(VersionCollapser.Collapse([Web, Dvd], null));
        MediaItem second = Assert.Single(VersionCollapser.Collapse([Dvd, Web], null));

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public void The_winner_keeps_the_groups_original_position()
    {
        MediaItem before = TestMedia.Episode("Before", 1, 1, TimeSpan.FromMinutes(5));
        MediaItem after = TestMedia.Episode("After", 1, 1, TimeSpan.FromMinutes(5));

        IReadOnlyList<MediaItem> collapsed = VersionCollapser.Collapse([before, Web, Dvd, after], null);

        Assert.Equal([before.Id, "a-dvd", after.Id], collapsed.Select(i => i.Id));
    }

    [Fact]
    public void Separate_groups_are_collapsed_independently()
    {
        MediaItem e2Web = Version("d-web", "c-dvd", "S01E02 The Buzz - Web Release");
        MediaItem e2Dvd = Version("c-dvd", "c-dvd", "S01E02 The Buzz - DVD Release");

        VersionSelection selection = new() { Exclude = ["Web Release"] };

        IReadOnlyList<MediaItem> collapsed = VersionCollapser.Collapse([Web, Dvd, e2Web, e2Dvd], selection);

        Assert.Equal(["a-dvd", "c-dvd"], collapsed.Select(i => i.Id));
    }

    [Fact]
    public async Task A_channel_plays_the_collections_preferred_version_and_never_both()
    {
        ChannelSchedule schedule = await BuildAsync(new VersionSelection { Exclude = ["Web Release"] });

        string[] played = [.. schedule.Entries
            .Where(e => e.Item?.Kind == MediaKind.Episode)
            .Select(e => e.Item!.Id)];

        Assert.NotEmpty(played);
        Assert.DoesNotContain("b-web", played);
        Assert.Contains("a-dvd", played);
    }

    [Theory]
    // Dated variants of one release share a label, so a rule naming the release catches them all.
    [InlineData("S01E01 The Beginning - DVD Release [2009-03-15]", "DVD Release")]
    [InlineData("S01E01 The Beginning - DVD Release [2010-01-02]", "DVD Release")]
    [InlineData("S01E01 Help Wanted - TV Premiere [1999-05-01]", "TV Premiere")]
    [InlineData("S01E01 Help Wanted - HQ Version", "HQ Version")]
    [InlineData("S01E01 The Beginning - Web Release", "Web Release")]
    [InlineData("S01E05 Something - DVD Release [a] [b]", "DVD Release")]
    // No release qualifier: merged files whose metadata collided, not alternate versions.
    [InlineData("053 Xmas 2", null)]
    [InlineData("Nutrition_Facts", null)]
    [InlineData("The Tom Green Show (Rogers 22) S01E00 (1995-11-02) [lxT7QtAIhKE]", null)]
    [InlineData("S01E01 The Beginning - [2009-03-15]", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_version_name_reduces_to_the_release_it_describes(string? versionName, string? expected) =>
        Assert.Equal(expected, VersionCollapser.ReleaseLabel(versionName));

    [Fact]
    public void Preferring_a_release_catches_its_dated_variants()
    {
        MediaItem dated = Version("a-dvd", "a-dvd", "S01E01 The Beginning - DVD Release [2009-03-15]");
        MediaItem web = Version("b-web", "a-dvd", "S01E01 The Beginning - Web Release");

        VersionSelection selection = new() { Prefer = ["DVD Release"] };

        Assert.Equal("a-dvd", Assert.Single(VersionCollapser.Collapse([web, dated], selection)).Id);
    }

    [Fact]
    public void Avoiding_a_release_catches_its_dated_variants()
    {
        MediaItem premiere = Version("a-tv", "a-tv", "S01E01 Help Wanted - TV Premiere [1999-05-01]");
        MediaItem hq = Version("b-hq", "a-tv", "S01E01 Help Wanted - HQ Version");

        VersionSelection selection = new() { Exclude = ["TV Premiere"] };

        Assert.Equal("b-hq", Assert.Single(VersionCollapser.Collapse([premiere, hq], selection)).Id);
    }

    [Fact]
    public async Task Alternate_versions_do_not_double_an_episodes_airtime()    {
        ChannelSchedule schedule = await BuildAsync(versions: null, length: TimeSpan.FromMinutes(44));

        // Two versions plus one plain episode is two distinct episodes, so a 44 minute window has to
        // reach the plain episode rather than airing the same one twice.
        string[] played = [.. schedule.Entries
            .Where(e => e.Item?.Kind == MediaKind.Episode)
            .Select(e => e.Item!.Id)];

        Assert.Contains("plain", played);
    }

    private static async Task<ChannelSchedule> BuildAsync(VersionSelection? versions, TimeSpan? length = null)
    {
        MediaItem plain = new()
        {
            Id = "plain",
            Title = "The Buzz",
            Kind = MediaKind.Episode,
            Duration = TimeSpan.FromMinutes(22),
            SeriesId = "nirvana",
            SeriesName = "Nirvana the Band the Show",
            SeasonNumber = 1,
            EpisodeNumber = 2,
            PremiereDate = new DateTimeOffset(2007, 1, 2, 0, 0, 0, TimeSpan.Zero)
        };

        FakeMediaLibrary library = new([Web, Dvd, plain]);
        SchedulingEngine engine = new(library, new InMemoryPlaybackStateStore());

        ChannelConfig channel = new()
        {
            Id = "test",
            Name = "Test",
            Number = 1,
            TimeZone = "UTC",
            AutoTemporalTags = false,
            BaseRotation = new ContentSelector
            {
                Query = "show:'Nirvana the Band the Show'",
                Order = PlaybackOrder.Chronological,
                Versions = versions
            }
        };

        SchedulerConfig config = new()
        {
            Settings = new SchedulerSettings { TimeZone = "UTC" },
            Channels = [channel]
        };

        DateTimeOffset day = new(2026, 1, 12, 0, 0, 0, TimeSpan.Zero);
        return await engine.BuildAsync(config, channel, day, day + (length ?? TimeSpan.FromMinutes(22)));
    }
}
