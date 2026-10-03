using System.Text.Json;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Providers.ErsatzTv;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class ErsatzTvPublishingTests
{
    private static readonly DateTimeOffset Start = new(2026, 4, 13, 0, 0, 0, TimeSpan.FromHours(-5));

    private static MediaItem Episode(string series, int season, int episode, TimeSpan duration, string path) => new()
    {
        Id = $"{series}-{season}-{episode}",
        Title = $"{series} S{season}E{episode}",
        Kind = MediaKind.Episode,
        Duration = duration,
        SeriesId = series,
        SeriesName = series,
        SeasonNumber = season,
        EpisodeNumber = episode,
        Overview = "A synopsis.",
        Path = path
    };

    private static ChannelSchedule Schedule(params ScheduleEntry[] entries) => new()
    {
        ChannelId = "test",
        ChannelName = "Test Channel",
        ChannelNumber = 1,
        ChannelTvgId = "schedularrtv.1",
        Start = Start,
        End = entries.Length > 0 ? entries[^1].End : Start.AddHours(1),
        Entries = entries
    };

    private static ChannelSchedule SampleSchedule()
    {
        var program = new ScheduleEntry
        {
            Start = Start,
            Duration = TimeSpan.FromMinutes(22),
            Kind = ScheduleEntryKind.Program,
            Item = Episode("Clone High", 1, 3, TimeSpan.FromMinutes(22), "/media/tv/clonehigh/s01e03.mkv"),
            Source = "base rotation"
        };

        var commercial = new ScheduleEntry
        {
            Start = program.End,
            Duration = TimeSpan.FromSeconds(30),
            Kind = ScheduleEntryKind.Commercial,
            Item = new MediaItem { Id = "ad", Title = "Ad", Duration = TimeSpan.FromSeconds(30), Path = "/media/ads/ad.mkv" },
            Source = "commercial"
        };

        var trimmed = new ScheduleEntry
        {
            Start = commercial.End,
            Duration = TimeSpan.FromSeconds(90),
            Kind = ScheduleEntryKind.Filler,
            Item = new MediaItem { Id = "bumper", Title = "Bumper", Duration = TimeSpan.FromMinutes(3), Path = "/media/bumpers/b.mkv" },
            IsTrimmed = true,
            SourceDuration = TimeSpan.FromMinutes(3),
            Source = "filler"
        };

        var next = new ScheduleEntry
        {
            Start = trimmed.End,
            Duration = TimeSpan.FromMinutes(22),
            Kind = ScheduleEntryKind.Program,
            Item = Episode("Aqua Teen", 2, 5, TimeSpan.FromMinutes(22), "/media/tv/aquateen/s02e05.mkv"),
            Source = "base rotation"
        };

        return Schedule(program, commercial, trimmed, next);
    }

    // --- Playout documents ----------------------------------------------------------------------

    [Fact]
    public void Playout_file_names_use_compact_iso_8601()
    {
        string stamp = PlayoutPublisher.FormatFileTimestamp(new DateTimeOffset(2026, 4, 13, 0, 0, 0, TimeSpan.FromHours(-5)));

        Assert.Equal("20260413T000000.000000000-0500", stamp);
    }

    [Fact]
    public void Playout_items_are_contiguous_and_carry_local_sources()
    {
        PlayoutDocumentAssertions.AssertContiguous(SampleSchedule());
    }

    [Fact]
    public void A_trimmed_filler_gets_an_out_point()
    {
        ChannelSchedule schedule = SampleSchedule();
        string json = Serialize(schedule);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement items = document.RootElement.GetProperty("items");

        JsonElement bumper = items.EnumerateArray()
            .Single(i => i.GetProperty("source").GetProperty("path").GetString()!.Contains("bumpers", StringComparison.Ordinal));

        Assert.Equal(90_000, bumper.GetProperty("source").GetProperty("out_point_ms").GetInt64());

        JsonElement episode = items.EnumerateArray()
            .First(i => i.GetProperty("source").GetProperty("path").GetString()!.Contains("clonehigh", StringComparison.Ordinal));

        Assert.False(episode.GetProperty("source").TryGetProperty("out_point_ms", out _));
    }

    [Fact]
    public void Padding_becomes_a_black_and_silent_lavfi_item()
    {
        ChannelSchedule schedule = Schedule(new ScheduleEntry
        {
            Start = Start,
            Duration = TimeSpan.FromMinutes(5),
            Kind = ScheduleEntryKind.Padding,
            Source = "padding"
        });

        using JsonDocument document = JsonDocument.Parse(Serialize(schedule));
        JsonElement item = document.RootElement.GetProperty("items")[0];

        Assert.False(item.TryGetProperty("source", out _));
        Assert.Equal("lavfi", item.GetProperty("tracks").GetProperty("video").GetProperty("source").GetProperty("source_type").GetString());
        Assert.Equal("lavfi", item.GetProperty("tracks").GetProperty("audio").GetProperty("source").GetProperty("source_type").GetString());
    }

    [Fact]
    public void The_document_declares_the_schema_version()
    {
        using JsonDocument document = JsonDocument.Parse(Serialize(SampleSchedule()));

        Assert.Equal("https://ersatztv.org/playout/version/0.0.3", document.RootElement.GetProperty("version").GetString());
    }

    [Theory]
    [InlineData("/media/tv/show.mkv", "/media", "/mnt/library", "/mnt/library/tv/show.mkv")]
    [InlineData("/media/tv/show.mkv", "/media/", "/mnt/library/", "/mnt/library/tv/show.mkv")]
    [InlineData("/other/tv/show.mkv", "/media", "/mnt/library", "/other/tv/show.mkv")]
    public void Paths_are_remapped_for_the_playout_host(string path, string from, string to, string expected) =>
        Assert.Equal(expected, PlayoutPublisher.MapPath(path, [new PathMapping { From = from, To = to }]));

    // --- XMLTV ----------------------------------------------------------------------------------

    [Fact]
    public void The_guide_absorbs_ad_breaks_into_the_preceding_programme()
    {
        ChannelSchedule schedule = SampleSchedule();

        IReadOnlyList<GuideEntry> guide = XmltvPublisher.BuildGuide(schedule, GuideMode.Programs);

        Assert.Equal(2, guide.Count);

        // The first programme runs until the second one starts, covering the ad and the bumper.
        Assert.Equal(guide[1].Start, guide[0].Stop);
        Assert.Equal(TimeSpan.FromMinutes(24), guide[0].Stop - guide[0].Start);
        Assert.Equal(schedule.End, guide[^1].Stop);
    }

    [Fact]
    public void Guide_mode_all_lists_every_non_padding_entry()
    {
        IReadOnlyList<GuideEntry> guide = XmltvPublisher.BuildGuide(SampleSchedule(), GuideMode.All);

        Assert.Equal(4, guide.Count);
    }

    /// <summary>Two eleven-minute episodes filling one half-hour slot are one listing, not two.</summary>
    private static ChannelSchedule StackedSchedule()
    {
        var first = new ScheduleEntry
        {
            Start = Start,
            Duration = TimeSpan.FromMinutes(11),
            Kind = ScheduleEntryKind.Program,
            Item = Episode("Adventure Time", 1, 1, TimeSpan.FromMinutes(11), "/media/at/s01e01.mkv"),
            Source = "base rotation"
        };

        var advert = new ScheduleEntry
        {
            Start = first.End,
            Duration = TimeSpan.FromMinutes(4),
            Kind = ScheduleEntryKind.Commercial,
            Item = new MediaItem { Id = "ad", Title = "Ad", Duration = TimeSpan.FromMinutes(4), Path = "/media/ads/ad.mkv" },
            Source = "commercial"
        };

        var second = new ScheduleEntry
        {
            Start = advert.End,
            Duration = TimeSpan.FromMinutes(11),
            Kind = ScheduleEntryKind.Program,
            Item = Episode("Adventure Time", 1, 2, TimeSpan.FromMinutes(11), "/media/at/s01e02.mkv"),
            Source = "base rotation",
            ContinuesSlot = true
        };

        return Schedule(first, advert, second);
    }

    [Fact]
    public void A_stacked_slot_is_one_guide_listing()
    {
        IReadOnlyList<GuideEntry> guide = XmltvPublisher.BuildGuide(StackedSchedule(), GuideMode.Programs);

        GuideEntry only = Assert.Single(guide);

        Assert.Equal(Start, only.Start);
        Assert.Equal(TimeSpan.FromMinutes(26), only.Stop - only.Start);
        Assert.Equal(2, only.Episodes);
    }

    [Fact]
    public void A_stacked_listing_names_every_episode_it_covers()
    {
        string xml = XmltvPublisher.BuildFragment(StackedSchedule(), GuideMode.Programs);

        Assert.Contains("<title lang=\"en\">Adventure Time</title>", xml, StringComparison.Ordinal);

        // Both episodes are named, so the listing is not silently blank.
        Assert.Contains("Adventure Time S1E1 / Adventure Time S1E2", xml, StringComparison.Ordinal);
        Assert.Contains("system=\"onscreen\">S01E01 / S01E02", xml, StringComparison.Ordinal);

        // A single episode number would claim the slot holds only one of them.
        Assert.DoesNotContain("xmltv_ns", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_episode_listing_still_names_the_episode()
    {
        string xml = XmltvPublisher.BuildFragment(SampleSchedule(), GuideMode.Programs);

        Assert.Contains("<sub-title lang=\"en\">", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_listing_carries_artwork_and_a_series_category_for_the_guide()
    {
        string xml = XmltvPublisher.BuildFragment(
            SampleSchedule(), GuideMode.Programs, "http://localhost:8096");

        Assert.Contains("<category lang=\"en\">Series</category>", xml, StringComparison.Ordinal);
        Assert.Contains("icon src=\"http://localhost:8096/Items/Clone%20High/Images/Primary", xml, StringComparison.Ordinal);
        Assert.Contains("type=\"still\"", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Artwork_is_omitted_when_no_media_server_address_is_known()
    {
        string xml = XmltvPublisher.BuildFragment(SampleSchedule(), GuideMode.Programs);

        Assert.DoesNotContain("<icon", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void An_intro_is_folded_into_the_listing_for_the_show_it_introduces()
    {
        var intro = new ScheduleEntry
        {
            Start = Start,
            Duration = TimeSpan.FromSeconds(45),
            Kind = ScheduleEntryKind.Program,
            Item = new MediaItem
            {
                Id = "intro",
                Title = "Intro",
                Duration = TimeSpan.FromSeconds(45),
                Path = "/media/tv/spongebob/Extras/Intro.avi"
            },
            Source = "intro",
            IsSlotIntro = true
        };

        var episode = new ScheduleEntry
        {
            Start = intro.End,
            Duration = TimeSpan.FromMinutes(11),
            Kind = ScheduleEntryKind.Program,
            Item = Episode("SpongeBob SquarePants", 1, 1, TimeSpan.FromMinutes(11), "/media/tv/spongebob/s01e01.mkv"),
            Source = "base rotation"
        };

        IReadOnlyList<GuideEntry> guide = XmltvPublisher.BuildGuide(Schedule(intro, episode), GuideMode.Programs);

        GuideEntry only = Assert.Single(guide);

        // The listing covers the intro but is named after the programme, not "Intro".
        Assert.Equal(Start, only.Start);
        Assert.Equal("SpongeBob SquarePants", only.Entry.Item!.SeriesName);
    }

    [Fact]
    public void The_fragment_binds_programmes_to_the_channel_tvg_id()
    {
        string xml = XmltvPublisher.BuildFragment(SampleSchedule(), GuideMode.Programs);

        Assert.Contains("channel=\"schedularrtv.1\"", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("<channel id=", xml, StringComparison.Ordinal);
        Assert.Contains("<title lang=\"en\">Clone High</title>", xml, StringComparison.Ordinal);
        Assert.Contains("<sub-title lang=\"en\">Clone High S1E3</sub-title>", xml, StringComparison.Ordinal);
        Assert.Contains("system=\"onscreen\">S01E03", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Xmltv_timestamps_use_the_expected_format()
    {
        string xml = XmltvPublisher.BuildFragment(SampleSchedule(), GuideMode.Programs);

        Assert.Contains("start=\"20260413000000 -0500\"", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tvg_id_falls_back_to_the_channel_id()
    {
        ChannelSchedule schedule = SampleSchedule() with { ChannelTvgId = null };

        Assert.Equal("test", schedule.TvgId);
    }

    // --- Lineup -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("hls")]
    [InlineData("./hls")]
    [InlineData(".\\hls")]
    public void Lineup_folders_resolve_to_absolute_paths(string configured)
    {
        // ErsatzTV Next resolves these against its working directory, not the lineup file.
        string resolved = LineupPublisher.Resolve(Path.GetFullPath("etv/config"), configured);

        Assert.True(Path.IsPathRooted(resolved));
        Assert.EndsWith("hls", resolved, StringComparison.Ordinal);
        Assert.Contains("config", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void An_absolute_lineup_folder_is_left_alone()
    {
        string absolute = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "etv-hls"));

        Assert.Equal(absolute, LineupPublisher.Resolve(Path.GetFullPath("etv/config"), absolute));
    }

    // --- Generated promo stand-ins ------------------------------------------------------------------

    private static ScheduleEntry Ad(DateTimeOffset start, int seconds) => new()
    {
        Start = start, Duration = TimeSpan.FromSeconds(seconds), Kind = ScheduleEntryKind.Commercial, Source = "commercial",
        Item = new MediaItem { Id = $"ad{seconds}", Title = "Ad", Duration = TimeSpan.FromSeconds(seconds), Path = $"/media/ads/{seconds}.mkv" }
    };

    private static ScheduleEntry Generated(DateTimeOffset start, int seconds, string? path, params ScheduleEntry[] fallback) => new()
    {
        Start = start, Duration = TimeSpan.FromSeconds(seconds), Kind = ScheduleEntryKind.UpNextPromo, GeneratedTemplate = "Classic",
        Item = new MediaItem { Id = "generated", Title = "Up next", Duration = TimeSpan.FromSeconds(seconds), Path = path },
        Fallback = fallback
    };

    [Fact]
    public void An_unrendered_generated_promo_airs_its_stand_in_shifted_and_cut_to_the_slot()
    {
        // Built for 10:00; the slot has since moved an hour later and the stand-in overruns by 5s.
        DateTimeOffset built = Start.AddHours(-1);
        ScheduleEntry[] playable = [.. PlayoutPublisher.Playable(Generated(Start, 20, null, Ad(built, 10), Ad(built.AddSeconds(10), 15)))];

        Assert.Equal(2, playable.Length);
        Assert.Equal((Start, TimeSpan.FromSeconds(10), false), (playable[0].Start, playable[0].Duration, playable[0].IsTrimmed));
        Assert.Equal((Start.AddSeconds(10), TimeSpan.FromSeconds(10), true), (playable[1].Start, playable[1].Duration, playable[1].IsTrimmed));
        Assert.Equal(TimeSpan.FromSeconds(15), playable[1].SourceDuration);
    }

    [Fact]
    public void A_short_stand_in_is_padded_and_a_rendered_promo_ignores_it()
    {
        ScheduleEntry[] padded = [.. PlayoutPublisher.Playable(Generated(Start, 20, null, Ad(Start, 15)))];
        Assert.Equal([ScheduleEntryKind.Commercial, ScheduleEntryKind.Padding], padded.Select(entry => entry.Kind));
        Assert.Equal(Start.AddSeconds(20), padded[^1].End);

        ScheduleEntry rendered = Generated(Start, 20, "/renders/promo.mp4", Ad(Start, 20));
        Assert.Same(rendered, Assert.Single(PlayoutPublisher.Playable(rendered)));
    }

    [Fact]
    public void Viewers_are_read_from_the_heartbeat_next_touches_for_each_watched_channel()
    {
        string root = Path.Combine(Path.GetTempPath(), $"etv-viewers-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "hls", "5"));
            Directory.CreateDirectory(Path.Combine(root, "hls", "8"));
            File.WriteAllText(Path.Combine(root, "lineup.json"), """{ "output": { "folder": "hls" } }""");
            File.WriteAllText(Path.Combine(root, "hls", "5", ".heartbeat"), "");
            File.WriteAllText(Path.Combine(root, "hls", "8", ".heartbeat"), "");
            File.SetLastWriteTimeUtc(Path.Combine(root, "hls", "8", ".heartbeat"), DateTime.UtcNow.AddMinutes(-5));

            var monitor = new ErsatzTvViewerMonitor(
                Microsoft.Extensions.Options.Options.Create(new ErsatzTvOptions { ConfigRoot = root }),
                TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<ErsatzTvViewerMonitor>.Instance);

            Assert.True(monitor.IsWatched(5));
            Assert.False(monitor.IsWatched(8));
            Assert.False(monitor.IsWatched(10));
            Assert.Null(new ErsatzTvViewerMonitor(
                Microsoft.Extensions.Options.Options.Create(new ErsatzTvOptions { ConfigRoot = Path.Combine(root, "missing") }),
                TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<ErsatzTvViewerMonitor>.Instance).IsWatched(5));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string Serialize(ChannelSchedule schedule) =>
        JsonSerializer.Serialize(PlayoutPublisher.Build(schedule, new ErsatzTvOptions()), PlayoutPublisher.JsonOptions);

    private static class PlayoutDocumentAssertions
    {
        public static void AssertContiguous(ChannelSchedule schedule)
        {
            using JsonDocument document = JsonDocument.Parse(Serialize(schedule));
            JsonElement items = document.RootElement.GetProperty("items");

            Assert.Equal(schedule.Entries.Count, items.GetArrayLength());

            DateTimeOffset cursor = schedule.Start;
            foreach (JsonElement item in items.EnumerateArray())
            {
                Assert.Equal(cursor, DateTimeOffset.Parse(item.GetProperty("start").GetString()!));
                cursor = DateTimeOffset.Parse(item.GetProperty("finish").GetString()!);
            }

            Assert.Equal(schedule.End, cursor);
        }
    }
}
