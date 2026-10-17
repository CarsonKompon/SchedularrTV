using System.Text.Json;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Providers.ErsatzTv;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class ChannelTracksTests
{
    private static readonly DateTimeOffset Start = new(2026, 4, 13, 0, 0, 0, TimeSpan.FromHours(-5));

    // A typical anime release: Japanese default audio, an English dub, full English subtitles and signs-only ones.
    private static readonly MediaStream[] Anime =
    [
        new(1, MediaStreamKind.Audio, "aac", "jpn", IsDefault: true),
        new(2, MediaStreamKind.Audio, "aac", "eng", "English Dub"),
        new(3, MediaStreamKind.Subtitle, "ass", "eng", "Signs & Songs", IsForced: true),
        new(4, MediaStreamKind.Subtitle, "ass", "eng", "Full Subtitles"),
        new(5, MediaStreamKind.Subtitle, "hdmv_pgs_subtitle", "spa")
    ];

    [Theory]
    [InlineData("en", "eng")]
    [InlineData("English", "eng")]
    [InlineData("eng", "eng")]
    [InlineData("en-US", "eng")]
    [InlineData("fre", "fra")]
    [InlineData("ger", "deu")]
    [InlineData("JA", "jpn")]
    [InlineData("tlh", "tlh")]
    [InlineData("und", null)]
    [InlineData(" ", null)]
    public void Languages_compare_however_they_are_written(string written, string? expected) =>
        Assert.Equal(expected, LanguageCodes.Normalize(written));

    [Fact]
    public void No_preferences_leave_everything_to_ErsatzTV() =>
        Assert.Equal(TrackChoice.None, TrackChooser.Choose(Anime, new ChannelTracksConfig()));

    [Fact]
    public void The_first_preferred_audio_language_the_file_has_plays()
    {
        TrackChoice choice = TrackChooser.Choose(Anime, new ChannelTracksConfig { AudioLanguages = ["fr", "en", "ja"] });
        Assert.Equal(2, choice.Audio?.Index);
        Assert.Null(choice.Subtitle);
    }

    [Fact]
    public void Without_a_preferred_language_the_files_default_audio_plays()
    {
        MediaStream[] streams =
        [
            new(1, MediaStreamKind.Audio, "ac3", "eng", "Commentary"),
            new(2, MediaStreamKind.Audio, "ac3", "eng", IsDefault: true)
        ];
        Assert.Equal(2, TrackChooser.Choose(streams, new ChannelTracksConfig { AudioLanguages = ["de"] }).Audio?.Index);

        // Within a language, the commentary isn't the one picked.
        MediaStream[] noDefault =
        [
            new(1, MediaStreamKind.Audio, "ac3", "eng", "Director's Commentary"),
            new(2, MediaStreamKind.Audio, "ac3", "eng")
        ];
        Assert.Equal(2, TrackChooser.Choose(noDefault, new ChannelTracksConfig { AudioLanguages = ["en"] }).Audio?.Index);
    }

    [Fact]
    public void Full_subtitles_are_preferred_over_signs_only()
    {
        TrackChoice choice = TrackChooser.Choose(Anime, new ChannelTracksConfig
        {
            AudioLanguages = ["ja"], Subtitles = SubtitleMode.Selectable, SubtitleLanguages = ["en"]
        });
        Assert.Equal(1, choice.Audio?.Index);
        Assert.Equal(4, choice.Subtitle?.Index);
    }

    [Fact]
    public void Subtitle_languages_default_to_the_audio_languages()
    {
        TrackChoice choice = TrackChooser.Choose(Anime, new ChannelTracksConfig { AudioLanguages = ["en"], Subtitles = SubtitleMode.BurnIn });
        Assert.Equal(4, choice.Subtitle?.Index);
    }

    [Fact]
    public void Only_for_foreign_audio_gives_understood_programmes_just_their_forced_track()
    {
        var dubbed = new ChannelTracksConfig { AudioLanguages = ["en"], Subtitles = SubtitleMode.BurnIn, OnlyForForeignAudio = true };
        Assert.Equal(3, TrackChooser.Choose(Anime, dubbed).Subtitle?.Index);

        var subbed = new ChannelTracksConfig { AudioLanguages = ["ja"], SubtitleLanguages = ["en"], Subtitles = SubtitleMode.BurnIn, OnlyForForeignAudio = true };
        Assert.Equal(4, TrackChooser.Choose(Anime, subbed).Subtitle?.Index);

        // In the language already, and no forced track: no subtitles.
        MediaStream[] plain = [new(1, MediaStreamKind.Audio, "aac", "eng"), new(2, MediaStreamKind.Subtitle, "subrip", "eng")];
        Assert.Null(TrackChooser.Choose(plain, dubbed).Subtitle);
    }

    [Fact]
    public void Picture_subtitles_can_only_be_burnt_in()
    {
        var spanish = new ChannelTracksConfig { SubtitleLanguages = ["es"], Subtitles = SubtitleMode.Selectable };
        Assert.Null(TrackChooser.Choose(Anime, spanish).Subtitle);

        spanish.Subtitles = SubtitleMode.BurnIn;
        Assert.Equal(5, TrackChooser.Choose(Anime, spanish).Subtitle?.Index);
    }

    [Fact]
    public void Sdh_is_passed_over_when_asked()
    {
        MediaStream[] streams =
        [
            new(1, MediaStreamKind.Audio, "aac", "eng"),
            new(2, MediaStreamKind.Subtitle, "subrip", "eng", "English SDH", IsHearingImpaired: true),
            new(3, MediaStreamKind.Subtitle, "subrip", "eng")
        ];
        var tracks = new ChannelTracksConfig { SubtitleLanguages = ["en"], Subtitles = SubtitleMode.Selectable };
        Assert.Equal(2, TrackChooser.Choose(streams, tracks).Subtitle?.Index);
        tracks.AvoidHearingImpaired = true;
        Assert.Equal(3, TrackChooser.Choose(streams, tracks).Subtitle?.Index);
    }

    [Fact]
    public void Ffprobe_streams_are_read_with_their_languages_and_flags()
    {
        const string json = """
            { "streams": [
              { "index": 0, "codec_name": "h264", "codec_type": "video" },
              { "index": 1, "codec_name": "aac", "codec_type": "audio", "disposition": { "default": 1, "forced": 0 }, "tags": { "language": "jpn" } },
              { "index": 2, "codec_name": "subrip", "codec_type": "subtitle", "disposition": { "default": 0, "forced": 1, "hearing_impaired": 0 }, "tags": { "LANGUAGE": "eng", "title": "Signs" } },
              { "index": 3, "codec_name": "subrip", "codec_type": "subtitle", "tags": { "language": "eng", "title": "English (SDH)" } }
            ] }
            """;
        List<MediaStream> streams = MediaTrackProbe.Parse(json)!;

        Assert.Equal(3, streams.Count);
        Assert.Equal(new MediaStream(1, MediaStreamKind.Audio, "aac", "jpn", null, IsDefault: true), streams[0]);
        Assert.True(streams[1].IsForced);
        Assert.Equal("eng", streams[1].Language);
        Assert.True(streams[2].IsHearingImpaired);
        Assert.Null(MediaTrackProbe.Parse("not json"));
    }

    [Fact]
    public void Subtitle_files_beside_a_video_are_found_with_their_language_and_flags()
    {
        string folder = Path.Combine(Path.GetTempPath(), "schedularrtv-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string video = Path.Combine(folder, "Show S01E01.mkv");
            foreach (string name in new[] { "Show S01E01.mkv", "Show S01E01.en.srt", "Show S01E01.eng.forced.srt", "Show S01E01.fr.sdh.ass", "Show S01E01.nfo", "Other.en.srt" })
            {
                File.WriteAllText(Path.Combine(folder, name), "");
            }

            List<MediaStream> sidecars = [.. MediaTrackProbe.Sidecars(new FileInfo(video))];

            Assert.Equal(3, sidecars.Count);
            Assert.All(sidecars, sidecar => Assert.NotNull(sidecar.ExternalPath));
            Assert.Contains(sidecars, s => s.Language == "eng" && !s.IsForced && s.Codec == "subrip");
            Assert.Contains(sidecars, s => s.Language == "eng" && s.IsForced);
            Assert.Contains(sidecars, s => s.Language == "fra" && s.IsHearingImpaired && s.Codec == "ass");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Chosen_tracks_are_written_into_the_playout_item()
    {
        var tracks = new ChannelTracksConfig { AudioLanguages = ["ja"], Subtitles = SubtitleMode.Selectable, SubtitleLanguages = ["en"] };
        ChannelSchedule schedule = Schedule(tracks, "/media/anime/ep1.mkv", TimeSpan.Zero);
        var streams = new Dictionary<string, IReadOnlyList<MediaStream>> { ["/media/anime/ep1.mkv"] = Anime };

        using JsonDocument document = Serialize(schedule, streams);
        JsonElement item = document.RootElement.GetProperty("items")[0];
        JsonElement written = item.GetProperty("tracks");

        Assert.Equal("local", item.GetProperty("source").GetProperty("source_type").GetString());
        Assert.Equal(1, written.GetProperty("audio").GetProperty("stream_index").GetInt32());
        Assert.Equal(4, written.GetProperty("subtitle").GetProperty("stream_index").GetInt32());
        Assert.False(written.TryGetProperty("video", out _));
    }

    [Fact]
    public void A_subtitle_file_is_its_own_source_cut_like_the_video()
    {
        var tracks = new ChannelTracksConfig { Subtitles = SubtitleMode.BurnIn, SubtitleLanguages = ["en"] };
        ChannelSchedule schedule = Schedule(tracks, "/media/movies/film.mkv", TimeSpan.FromMinutes(10));
        var streams = new Dictionary<string, IReadOnlyList<MediaStream>>
        {
            ["/media/movies/film.mkv"] =
            [
                new(1, MediaStreamKind.Audio, "ac3", "eng"),
                new(0, MediaStreamKind.Subtitle, "subrip", "eng", ExternalPath: "/media/movies/film.en.srt")
            ]
        };

        using JsonDocument document = Serialize(schedule, streams, [new PathMapping { From = "/media", To = "/mnt/media" }]);
        JsonElement subtitle = document.RootElement.GetProperty("items")[0].GetProperty("tracks").GetProperty("subtitle");

        Assert.Equal("/mnt/media/movies/film.en.srt", subtitle.GetProperty("source").GetProperty("path").GetString());
        Assert.Equal(600_000, subtitle.GetProperty("source").GetProperty("in_point_ms").GetInt64());
        Assert.False(subtitle.TryGetProperty("stream_index", out _));
    }

    [Fact]
    public void Channels_without_preferences_or_unread_files_get_no_tracks()
    {
        var streams = new Dictionary<string, IReadOnlyList<MediaStream>> { ["/media/anime/ep1.mkv"] = Anime };
        using JsonDocument none = Serialize(Schedule(null, "/media/anime/ep1.mkv", TimeSpan.Zero), streams);
        Assert.False(none.RootElement.GetProperty("items")[0].TryGetProperty("tracks", out _));

        var tracks = new ChannelTracksConfig { AudioLanguages = ["en"] };
        using JsonDocument unread = Serialize(Schedule(tracks, "/media/other.mkv", TimeSpan.Zero), streams);
        Assert.False(unread.RootElement.GetProperty("items")[0].TryGetProperty("tracks", out _));
    }

    [Fact]
    public void The_annotation_round_trips_and_sets_the_channels_subtitle_mode()
    {
        var tracks = new ChannelTracksConfig { AudioLanguages = ["jpn"], Subtitles = SubtitleMode.BurnIn, OnlyForForeignAudio = true };
        ChannelTracksConfig read = ErsatzTvTracks.FromAnnotation(ErsatzTvTracks.ToAnnotation(tracks))!;

        Assert.Equal(new[] { "jpn" }, read.AudioLanguages);
        Assert.Equal(SubtitleMode.BurnIn, read.Subtitles);
        Assert.True(read.OnlyForForeignAudio);
        Assert.Null(ErsatzTvTracks.ToAnnotation(new ChannelTracksConfig()));

        Assert.Equal("burn", ErsatzTvTracks.SubtitleMode(read));
        Assert.Equal("convert", ErsatzTvTracks.SubtitleMode(new ChannelTracksConfig { Subtitles = SubtitleMode.Selectable }));
        Assert.Null(ErsatzTvTracks.SubtitleMode(new ChannelTracksConfig { AudioLanguages = ["en"] }));

        using JsonDocument overlay = JsonDocument.Parse(ErsatzTvEncoding.Overlay(new EffectiveEncoding("none", 1080, 6000, null, null), subtitleMode: "convert"));
        Assert.Equal("convert", overlay.RootElement.GetProperty("normalization").GetProperty("subtitle").GetProperty("mode").GetString());
        using JsonDocument without = JsonDocument.Parse(ErsatzTvEncoding.Overlay(new EffectiveEncoding("none", 1080, 6000, null, null)));
        Assert.False(without.RootElement.GetProperty("normalization").TryGetProperty("subtitle", out _));
    }

    [Fact]
    public void The_channels_tracks_section_reads_from_yaml()
    {
        SchedulerConfig config = SchedulerConfigLoader.Parse("""
            channels:
              - id: anime
                name: Anime
                number: 7
                tracks:
                  audio_languages: [ja, en]
                  subtitles: burn_in
                  subtitle_languages: [en]
                  only_for_foreign_audio: true
            """);

        ChannelTracksConfig tracks = config.Channels[0].Tracks!;
        Assert.Equal(new[] { "ja", "en" }, tracks.AudioLanguages);
        Assert.Equal(SubtitleMode.BurnIn, tracks.Subtitles);
        Assert.Equal(new[] { "en" }, tracks.SubtitleLanguages);
        Assert.True(tracks.OnlyForForeignAudio);
    }

    private static ChannelSchedule Schedule(ChannelTracksConfig? tracks, string path, TimeSpan inPoint)
    {
        var entry = new ScheduleEntry
        {
            Start = Start,
            Duration = TimeSpan.FromMinutes(24),
            InPoint = inPoint,
            Kind = ScheduleEntryKind.Program,
            Item = new MediaItem { Id = "ep", Title = "Episode", Duration = TimeSpan.FromMinutes(24) + inPoint, Path = path },
            Source = "base rotation"
        };

        return new ChannelSchedule
        {
            ChannelId = "anime",
            ChannelName = "Anime",
            ChannelNumber = 7,
            ChannelTvgId = "schedularrtv.7",
            Start = Start,
            End = entry.End,
            Entries = [entry],
            Annotations = ErsatzTvTracks.ToAnnotation(tracks) is { } annotation
                ? new Dictionary<string, string> { [ErsatzTvTracks.Annotation] = annotation }
                : null
        };
    }

    private static JsonDocument Serialize(ChannelSchedule schedule, IReadOnlyDictionary<string, IReadOnlyList<MediaStream>> streams,
        List<PathMapping>? mappings = null) =>
        JsonDocument.Parse(JsonSerializer.Serialize(
            PlayoutPublisher.Build(schedule, new ErsatzTvOptions { PathMappings = mappings ?? [] }, streams), PlayoutPublisher.JsonOptions));
}
