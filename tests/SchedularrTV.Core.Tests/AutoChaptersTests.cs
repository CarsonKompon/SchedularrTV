using Microsoft.Extensions.Configuration;
using SchedularrTV.AutoChapters;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Plugins;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class AutoChaptersTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "auto-chapters-" + Guid.NewGuid().ToString("N"));

    public AutoChaptersTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    // What ffmpeg prints for a 22-minute episode: a fade at the cold open's end (too close to the start), two act
    // breaks (the second a fade out and back in moments apart), a dark scene with dialogue, and the end credits.
    private static readonly string[] Report =
    [
        "  Duration: 00:22:00.00, start: 0.000000, bitrate: 2000 kb/s",
        "[blackdetect @ 0x1] black_start:20.5 black_end:21.5 black_duration:1",
        "[silencedetect @ 0x2] silence_start: 20.4",
        "[silencedetect @ 0x2] silence_end: 21.7 | silence_duration: 1.3",
        "[blackdetect @ 0x1] black_start:420 black_end:421 black_duration:1",
        "[silencedetect @ 0x2] silence_start: 419.8",
        "[silencedetect @ 0x2] silence_end: 421.2 | silence_duration: 1.4",
        "[blackdetect @ 0x1] black_start:600 black_end:601 black_duration:1",
        "[blackdetect @ 0x1] black_start:840 black_end:841 black_duration:1",
        "[silencedetect @ 0x2] silence_start: 840",
        "[silencedetect @ 0x2] silence_end: 841 | silence_duration: 1",
        "[blackdetect @ 0x1] black_start:850 black_end:851 black_duration:1",
        "[silencedetect @ 0x2] silence_start: 850",
        "[silencedetect @ 0x2] silence_end: 851 | silence_duration: 1",
        "[blackdetect @ 0x1] black_start:1300 black_end:1301 black_duration:1",
        "[silencedetect @ 0x2] silence_start: 1299.5"
    ];

    [Fact]
    public void Fades_to_black_with_silence_become_markers_away_from_the_titles_and_credits()
    {
        Detection found = BlackDetector.Parse(Report, null, DetectionOptions.Default);

        Assert.Equal(TimeSpan.FromMinutes(22), found.Duration);
        Assert.Equal([TimeSpan.FromSeconds(420.5), TimeSpan.FromSeconds(840.5)], found.Markers);
    }

    [Fact]
    public void Ffmpeg_failures_are_explained_in_words()
    {
        string[] badOption =
        [
            "[vist#0:0/hevc @ 000001cbc479c100] [dec:hevc @ 000001cbc48f3500] Error applying decoder options: Invalid argument",
            "Error opening output file -.",
            "Error opening output files: Invalid argument"
        ];

        Assert.Contains("couldn't decode", BlackDetector.Explain(-22, badOption));
        Assert.Contains("damaged", BlackDetector.Explain(1, ["file.mp4: moov atom not found"]));
        Assert.Contains("exit code 5", BlackDetector.Explain(5, ["something else"]));
    }

    [Fact]
    public void Probing_reads_length_frame_rate_codecs_and_chapters()
    {
        string[] described =
        [
            "Input #0, matroska,webm, from 'Corner Gas S03E04.mkv':",
            "  Duration: 00:22:01.50, start: 0.000000, bitrate: 2100 kb/s",
            "  Chapters:",
            "    Chapter #0:0: start 0.000000, end 600.000000",
            "    Chapter #0:1: start 600.000000, end 1321.500000",
            "  Stream #0:0(eng): Video: h264 (High), yuv420p(progressive), 1920x1080 [SAR 1:1 DAR 16:9], 23.98 fps, 23.98 tbr, 1k tbn (default)",
            "  Stream #0:1(eng): Audio: ac3, 48000 Hz, 5.1(side), fltp, 384 kb/s (default)",
            "  Stream #0:2: Video: mjpeg (Baseline), yuvj420p(pc), 600x900, 90k tbr, 90k tbn (attached pic)"
        ];

        MediaInfo info = ReviewMedia.ParseProbe(described)!;

        Assert.Equal(1321.5, info.Duration, 3);
        Assert.Equal(23.98, info.Fps, 3);
        Assert.Equal("h264", info.VideoCodec);
        Assert.Equal("ac3", info.AudioCodec);
        Assert.Equal((1920, 1080), (info.Width, info.Height));
        Assert.Equal(2, info.Chapters);

        // AC-3 sound: browsers can't play it, so the review shows a converted preview.
        Assert.False(info.MaybePlayable("episode.mkv"));
        Assert.True((info with { AudioCodec = "aac" }).MaybePlayable("episode.mkv"));
        Assert.False((info with { AudioCodec = "aac" }).MaybePlayable("episode.avi"));
        Assert.Null(ReviewMedia.ParseProbe(["nothing.mkv: No such file or directory"]));
    }

    [Fact]
    public void Approved_markers_become_chapters_from_the_start()
    {
        IReadOnlyList<double> starts = ChapterWriter.Starts([840.5, 420.25, 420.5, 1320.9], 1321.5);

        // Sorted, too-close and end-of-file markers dropped, a chapter from 0.
        Assert.Equal([0d, 420.25, 840.5], starts);
        Assert.Equal(
            ";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=420250\ntitle=Chapter 1\n"
            + "[CHAPTER]\nTIMEBASE=1/1000\nSTART=420250\nEND=840500\ntitle=Chapter 2\n"
            + "[CHAPTER]\nTIMEBASE=1/1000\nSTART=840500\nEND=1321500\ntitle=Chapter 3\n",
            ChapterWriter.FfMetadata(starts, 1321.5));
        Assert.StartsWith("CHAPTER01=00:00:00.000\nCHAPTER01NAME=Chapter 1\nCHAPTER02=00:07:00.250\n", ChapterWriter.SimpleChapters(starts));

        Assert.Null(ChapterWriter.Unsupported("show.mkv"));
        Assert.Null(ChapterWriter.Unsupported("show.MP4"));
        Assert.Contains("AVI files can't hold chapter markers", ChapterWriter.Unsupported("show.avi"));
    }

    [Fact]
    public void Reviewed_programmes_leave_the_queue_and_are_found_by_key()
    {
        var store = new ChapterStore(_folder);
        store.Save(new ChapterRecord { Path = "/tv/a.mkv", Markers = [420], AnalysedAt = DateTimeOffset.UtcNow.AddMinutes(-2) });
        store.Save(new ChapterRecord { Path = "/tv/b.mkv", AnalysedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        store.Save(new ChapterRecord { Path = "/tv/c.mkv", Error = "broken" });

        Assert.Equal(["/tv/a.mkv", "/tv/b.mkv"], store.ReviewQueue().Select(record => record.Path));
        Assert.Equal(["/tv/a.mkv"], store.ReviewQueue(withMarkersOnly: true).Select(record => record.Path));

        ChapterRecord approved = store.Find("/tv/a.mkv")!.Copy();
        approved.Review = ReviewState.Applied;
        approved.ReviewedAt = DateTimeOffset.UtcNow;
        store.Save(approved);

        Assert.Equal(["/tv/b.mkv"], store.ReviewQueue().Select(record => record.Path));
        Assert.Equal("/tv/a.mkv", store.FindByKey(approved.Key)?.Path);

        // Kept across a restart.
        var reloaded = new ChapterStore(_folder);
        Assert.Equal(ReviewState.Applied, reloaded.Find("/tv/a.mkv")?.Review);
    }

    [Fact]
    public void Without_requiring_silence_dark_scenes_count_too()
    {
        Detection found = BlackDetector.Parse(Report, TimeSpan.FromMinutes(22), DetectionOptions.Default with { RequireSilence = false });

        Assert.Equal([TimeSpan.FromSeconds(420.5), TimeSpan.FromSeconds(600.5), TimeSpan.FromSeconds(840.5)], found.Markers);
    }

    [Fact]
    public void Ffmpeg_reads_the_sound_only_when_silence_is_required()
    {
        IReadOnlyList<string> withSound = BlackDetector.Arguments("show.mkv", DetectionOptions.Default);
        IReadOnlyList<string> pictureOnly = BlackDetector.Arguments("show.mkv", DetectionOptions.Default with { RequireSilence = false });

        Assert.Contains(withSound, argument => argument.StartsWith("silencedetect", StringComparison.Ordinal));
        Assert.Contains("0:a:0?", withSound);
        Assert.Contains("-an", pictureOnly);
        Assert.Contains(pictureOnly, argument => argument.Contains("blackdetect=d=0.4", StringComparison.Ordinal));
    }

    private AutoChaptersSettings Settings(params (string Key, string Value)[] values) =>
        new(new FakeContext(new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(pair => KeyValuePair.Create(pair.Key, (string?)pair.Value)))
            .Build(), _folder));

    private static MediaItem Episode(string path, params TimeSpan[] chapters) => new()
    {
        Id = path, Title = "Episode", Kind = MediaKind.Episode, Duration = TimeSpan.FromMinutes(22), SeriesName = "Show", Path = path,
        Chapters = chapters
    };

    [Fact]
    public void Programmes_without_chapters_get_the_markers_found_and_keep_their_own_otherwise()
    {
        var store = new ChapterStore(_folder);
        store.Save(new ChapterRecord { Path = "/tv/a.mkv", Markers = [420.5, 840.5] });
        store.Save(new ChapterRecord { Path = "/tv/b.mkv", Markers = [300] });
        var enricher = new ChapterEnricher(store, Settings());

        MediaItem plain = Episode("/tv/a.mkv");
        MediaItem enriched = enricher.Enrich(plain);
        MediaItem ownChapters = Episode("/tv/b.mkv", TimeSpan.FromMinutes(11));

        Assert.Equal([TimeSpan.FromSeconds(420.5), TimeSpan.FromSeconds(840.5)], enriched.Chapters);
        Assert.Equal(ChapterEnricher.SourceValue, enriched.Attributes[ChapterEnricher.SourceAttribute]);
        Assert.Same(enriched, enricher.Enrich(plain));
        Assert.Same(ownChapters, enricher.Enrich(ownChapters));
        Assert.True(ChapterEnricher.Wants(enriched));
        Assert.False(ChapterEnricher.Wants(ownChapters));

        // Found, but not used for breaks while that's switched off.
        Assert.Same(plain, new ChapterEnricher(store, Settings(("UseForMidRolls", "false"))).Enrich(plain));

        Assert.Equal(2, new ChapterStore(_folder).Count);
    }

    [Fact]
    public async Task The_combined_library_hands_out_enriched_items_and_mid_rolls_land_on_the_markers()
    {
        var store = new ChapterStore(_folder);
        store.Save(new ChapterRecord { Path = "/tv/a.mkv", Markers = [420.5, 840.5] });
        IMediaEnricher[] enrichers = [new ChapterEnricher(store, Settings())];
        var library = new CompositeMediaLibrary(() => [new FakeMediaLibrary([Episode("/tv/a.mkv")])], () => enrichers);

        MediaItem item = Assert.Single(await library.SearchAsync(new AllExpression()));
        IReadOnlyList<MediaSegment> parts = MidRollPlanner.Split(item, new MidRollConfig
        {
            Mode = MidRollMode.Chapters,
            TargetSegment = TimeSpan.FromMinutes(7),
            MinSegment = TimeSpan.FromMinutes(3)
        }, TimeSpan.FromMinutes(8));

        Assert.Equal([TimeSpan.Zero, TimeSpan.FromSeconds(420.5), TimeSpan.FromSeconds(840.5)], parts.Select(part => part.InPoint));
    }

    [Fact]
    public async Task Breaks_planned_on_the_fixed_interval_slide_onto_the_fades_found_later_and_nothing_else_moves()
    {
        var store = new ChapterStore(_folder);
        var clock = new SteppedClock(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
        var aligner = new MidRollAligner(store, Settings(), clock: clock);
        DateTimeOffset start = clock.GetUtcNow().AddHours(1);
        MediaItem episode = Episode("/tv/a.mkv");
        ScheduleEntry Part(int number, DateTimeOffset at, TimeSpan inPoint, TimeSpan length) => new()
        {
            Start = at, Duration = length, Kind = ScheduleEntryKind.Program, Item = episode, InPoint = inPoint, SegmentNumber = number, SegmentCount = 2
        };

        // Split at 13:00 with a two-minute break, then the next programme at 0:24:00.
        ScheduleEntry first = Part(1, start, TimeSpan.Zero, TimeSpan.FromMinutes(13));
        ScheduleEntry advert = new() { Start = start.AddMinutes(13), Duration = TimeSpan.FromMinutes(2), Kind = ScheduleEntryKind.Commercial };
        ScheduleEntry second = Part(2, start.AddMinutes(15), TimeSpan.FromMinutes(13), TimeSpan.FromMinutes(9));
        ScheduleEntry next = new() { Start = start.AddMinutes(24), Duration = TimeSpan.FromMinutes(30), Kind = ScheduleEntryKind.Program,
            Item = Episode("/tv/b.mkv") };
        var schedule = new ChannelSchedule
        {
            ChannelId = "ch", ChannelName = "Ch", Start = start, End = start.AddMinutes(54), Entries = [first, advert, second, next]
        };
        var context = new SchedularrTV.Plugins.Playout.PlayoutContext(new SchedulerConfig(), null, schedule);

        // Not analysed yet: left on the interval, and counted as waiting.
        Assert.Same(schedule, await aligner.ProcessAsync(context, CancellationToken.None));
        Assert.Equal(1, aligner.Summary.WaitingForAnalysis);

        store.Save(new ChapterRecord { Path = "/tv/a.mkv", Markers = [700, 1250] });
        ChannelSchedule aligned = await aligner.ProcessAsync(context, CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(700), aligned.Entries[0].Duration);
        Assert.Equal(start.AddSeconds(700), aligned.Entries[1].Start);
        Assert.Equal(start.AddSeconds(820), aligned.Entries[2].Start);
        Assert.Equal(TimeSpan.FromSeconds(700), aligned.Entries[2].InPoint);
        Assert.Equal(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(80), aligned.Entries[2].Duration);
        Assert.Equal(aligned.Entries[3].Start, aligned.Entries[2].End);
        Assert.Same(next, aligned.Entries[3]);
        Assert.Equal(1, aligner.Summary.AtBreakPoints);

        // Once it's on air it stays as sent; one that went out unaligned isn't changed under ErsatzTV mid-programme.
        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.FromSeconds(700), (await aligner.ProcessAsync(context, CancellationToken.None)).Entries[0].Duration);
        var fresh = new MidRollAligner(store, Settings(), clock: clock);
        Assert.Same(schedule, await fresh.ProcessAsync(context, CancellationToken.None));
    }

    private sealed class SteppedClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeContext(IConfiguration configuration, string folder) : IPluginContext
    {
        public PluginManifest Manifest { get; } = new() { Id = "auto-chapters", Name = "Auto Chapters" };
        public string? PluginDirectory => null;
        public string DataDirectory => folder;
        public string AppDataDirectory => folder;
        public IConfiguration Configuration => configuration;
        public IConfiguration AppConfiguration => new ConfigurationBuilder().Build();
        public bool IsLoaded(string pluginId) => false;
        public string StaticFilesPath => "/plugins/auto-chapters/";
    }
}
