using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Overlays;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>Music video credits: {tag.name} tokens read an item's "name: value" tags, and overlays can be kept to some shows.</summary>
public sealed class OverlayTagTokenTests
{
    private static readonly DateTimeOffset Eight = new(2026, 11, 7, 20, 0, 0, TimeSpan.Zero);

    private static MediaItem Video(params string[] tags) => new()
    {
        Id = "mv-1",
        Title = "Smells Like Teen Spirit",
        Kind = MediaKind.Episode,
        SeriesId = "series-mv",
        SeriesName = "Music Videos",
        SeasonNumber = 1,
        EpisodeNumber = 1,
        Duration = TimeSpan.FromMinutes(5),
        Tags = tags
    };

    private static ScheduleEntry Interstitial(MediaItem item) => new()
    {
        Start = Eight, Duration = item.Duration, Kind = ScheduleEntryKind.Interstitial, Item = item
    };

    private static readonly ChannelConfig Channel = new() { Id = "mtv", Name = "MTV", Number = 1 };

    [Fact]
    public void Tag_tokens_read_name_value_tags_and_list_repeats_together()
    {
        MediaItem video = Video("artist: Nirvana", "Album:Nevermind", "label: DGC", "director: Jonas Åkerlund", "director: Dave Meyers", "Grunge");
        Dictionary<string, string> tokens = OverlayProcessor.Tokens(Interstitial(video), null, Channel);

        Assert.Equal("Nirvana · Smells Like Teen Spirit", OverlayRenderer.Fill("{tag.artist} · {episode}", tokens));
        Assert.Equal("Nevermind (DGC)", OverlayRenderer.Fill("{tag.album} ({tag.LABEL})", tokens));
        Assert.Equal("Directed by Jonas Åkerlund, Dave Meyers", OverlayRenderer.Fill("Directed by {tag.director}", tokens));
    }

    [Fact]
    public void A_missing_tag_reads_as_nothing_but_other_unknown_tokens_stay()
    {
        Dictionary<string, string> tokens = OverlayProcessor.Tokens(Interstitial(Video("artist: Nirvana")), null, Channel);

        Assert.Equal("Nirvana ", OverlayRenderer.Fill("{tag.artist} {tag.album}", tokens));
        Assert.Equal("{mystery}", OverlayRenderer.Fill("{mystery}", tokens));
    }

    [Fact]
    public void Attributes_lifted_out_of_tags_still_read()
    {
        MediaItem video = new()
        {
            Id = "promo", Title = "Promo", Kind = MediaKind.Episode, Duration = TimeSpan.FromSeconds(30),
            Attributes = new Dictionary<string, string> { ["brand"] = "Teletoon" }
        };

        Assert.Equal("Teletoon", OverlayRenderer.Fill("{tag.brand}", OverlayProcessor.Tokens(Interstitial(video), null, Channel)));
    }

    [Fact]
    public void The_editor_preview_shows_a_tag_tokens_name()
    {
        Assert.Equal("Artist – Album", OverlayRenderer.Fill("{tag.artist} – {tag.album}", OverlayProcessor.SampleTokens));
    }

    [Fact]
    public void An_overlay_kept_to_some_shows_shows_on_their_interstitials_only()
    {
        Overlay credits = new()
        {
            Conditions = new OverlayConditions { On = [OverlayOn.Interstitial], Shows = ["music videos"] }
        };
        ScheduleEntry video = Interstitial(Video("artist: Nirvana"));
        ScheduleEntry other = Interstitial(TestMedia.Episode("Cartoon Shorts", 1, 1, TimeSpan.FromMinutes(5)));
        ScheduleEntry programme = new()
        {
            Start = Eight, Duration = TimeSpan.FromMinutes(22), Kind = ScheduleEntryKind.Program,
            Item = TestMedia.Episode("Music Videos", 2, 1, TimeSpan.FromMinutes(22))
        };

        Assert.True(OverlayConditionMatcher.Matches(credits, video, programme, null));
        Assert.False(OverlayConditionMatcher.Matches(credits, other, programme, null));
        Assert.Contains("on music videos", credits.Conditions!.Describe(), StringComparison.Ordinal);

        // With nothing else set it's judged on the entry, programme or not.
        Overlay anywhere = new() { Conditions = new OverlayConditions { Shows = ["Music Videos"] } };
        Assert.True(OverlayConditionMatcher.Matches(anywhere, programme, programme, null));
        Assert.False(OverlayConditionMatcher.Matches(anywhere, other, programme, null));
    }
}
