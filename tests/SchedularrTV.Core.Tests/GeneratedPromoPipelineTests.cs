using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Publishing;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Service;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class GeneratedPromoPipelineTests : IDisposable
{
    private static readonly DateTimeOffset Day = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"generated-promo-pipeline-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Theory]
    [InlineData(false, false, false, 1.0)]
    [InlineData(true, false, false, 1.0)]
    [InlineData(true, true, false, 1.0)]
    [InlineData(true, true, true, 1.2)]
    [InlineData(true, false, false, 1.2)]
    public async Task Background_music_loops_under_the_promo_and_ducks_under_the_voice(bool voiced, bool duck, bool videoBackground, double tempo)
    {
        string song = Path.Combine(_directory, "song.wav");
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(song, FakeVoice.Wav(TimeSpan.FromSeconds(2), 220, 16_000));
        // Matches TikTok's output: 24 kHz mono MP3.
        string voice = Path.Combine(_directory, "voice.mp3");
        await RunTool("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=24000",
            "-t", "4", "-ac", "1", "-c:a", "libmp3lame", voice);
        string? background = null;
        if (videoBackground)
        {
            background = Path.Combine(_directory, "background.mp4");
            await RunTool("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=64x36:rate=10", "-t", "1", "-pix_fmt", "yuv420p", background);
        }

        var composition = new PromoComposition
        {
            Width = 64, Height = 36, Fps = 10, Duration = TimeSpan.FromSeconds(6),
            AudioPath = voiced ? voice : null, BackgroundPath = background, AudioTempo = tempo,
            Music = new PromoMusic(song, 0.5, TimeSpan.FromSeconds(0.5), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500), duck)
        };
        string output = Path.Combine(_directory, $"music-{voiced}-{duck}-{videoBackground}-{tempo}.mp4");

        await new PromoRenderer().RenderAsync(composition, output);

        Assert.InRange(await ProbeSeconds(output), 5.95, 6.1);
        double musicTail = await MeanVolume(output, 4.5, 0.8, "bandpass=f=220:width_type=q:w=4,");
        Assert.InRange(musicTail, -45, -5);
        double voiceWhileSpeaking = await MeanVolume(output, 1, 2, "bandpass=f=880:width_type=q:w=4,");
        if (voiced)
        {
            Assert.True(voiceWhileSpeaking > -35, $"the voice-over should be audible, measured {voiceWhileSpeaking} dB");
            double musicWhileSpeaking = await MeanVolume(output, 1, 2, "bandpass=f=220:width_type=q:w=4,");
            Assert.True(duck ? musicWhileSpeaking < musicTail - 6 : Math.Abs(musicWhileSpeaking - musicTail) < 3,
                $"music {musicWhileSpeaking} dB while speaking vs {musicTail} dB alone (duck: {duck})");
        }
        else
        {
            Assert.True(voiceWhileSpeaking < -45, $"no voice expected, measured {voiceWhileSpeaking} dB");
        }
    }

    [Fact]
    public void Templates_round_trip_through_yaml_and_validate()
    {
        var config = new SchedulerConfig { Channels = [Channel(GeneratedPromoUsage.Mixed)] };
        config.Channels[0].GeneratedPromos!.Templates[0].Layers[0].Color = "#80FFC400";

        SchedulerConfig parsed = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config));
        GeneratedPromoTemplate template = Assert.Single(parsed.Channels[0].GeneratedPromos!.Templates);

        Assert.True(SchedulerConfigLoader.Validate(parsed).IsValid);
        Assert.Equal(GeneratedPromoUsage.Mixed, template.Usage);
        Assert.Equal(TimeSpan.FromSeconds(8), template.Duration);
        Assert.Equal("fake", template.Voice!.Provider);
        Assert.Equal("#80FFC400", template.Layers[0].Color);
        Assert.Equal(1, template.Layers.Single(layer => layer.Kind == GeneratedLayerKind.UpNext && layer.UpNext!.Offset == 1).UpNext!.Offset);

        template.Layers.RemoveAll(layer => layer.Kind == GeneratedLayerKind.UpNext);
        template.Layers[0].Color = "yellow";
        parsed.Channels[0].GeneratedPromos!.Templates.Add(new GeneratedPromoTemplate { Name = template.Name, Duration = TimeSpan.Zero });
        IReadOnlyList<string> errors = SchedulerConfigLoader.Validate(parsed).Errors;
        Assert.Contains(errors, error => error.Contains("needs its 'up_next' text layer"));
        Assert.Contains(errors, error => error.Contains("hex colour"));
        Assert.Contains(errors, error => error.Contains("used more than once"));
        Assert.Contains(errors, error => error.Contains("duration must be"));
    }

    [Theory]
    [InlineData(GeneratedPromoUsage.Fallback, false, 0.0, true)]
    [InlineData(GeneratedPromoUsage.Fallback, true, 1.0, false)]
    [InlineData(GeneratedPromoUsage.Mixed, true, 1.0, true)]
    [InlineData(GeneratedPromoUsage.Mixed, true, 0.0, false)]
    public async Task Engine_reserves_fixed_length_templates_as_fallbacks_or_mixed_with_clips(
        GeneratedPromoUsage usage, bool clips, double chance, bool expected)
    {
        ChannelConfig channel = Channel(usage, chance);
        List<MediaItem> library = Library(clips);
        var engine = new SchedulingEngine(new FakeMediaLibrary(library), new InMemoryPlaybackStateStore());

        ChannelSchedule schedule = await engine.BuildAsync(new SchedulerConfig { Channels = [channel] }, channel, Day, Day.AddHours(2));

        ScheduleEntry[] upNext = [.. schedule.Entries.Where(entry => entry.Kind == ScheduleEntryKind.UpNextPromo)];
        Assert.NotEmpty(upNext);
        Assert.All(upNext, entry =>
        {
            Assert.Equal(expected, entry.GeneratedTemplate == "Classic");
            if (expected)
            {
                Assert.Equal(TimeSpan.FromSeconds(8), entry.Duration);
                Assert.Null(entry.Item!.Path);
                ScheduleEntry? next = schedule.Entries.FirstOrDefault(program => program.Start >= entry.End && program.Kind == ScheduleEntryKind.Program);
                Assert.True(entry.End <= (next?.Start ?? schedule.End));
            }
        });
        Assert.All(schedule.Entries.Zip(schedule.Entries.Skip(1)), pair => Assert.Equal(pair.First.End, pair.Second.Start));
    }

    [Fact]
    public async Task Templates_longer_than_the_break_or_disabled_are_not_used()
    {
        ChannelConfig channel = Channel(GeneratedPromoUsage.Fallback);
        channel.GeneratedPromos!.Templates[0].Duration = TimeSpan.FromMinutes(9);
        var engine = new SchedulingEngine(new FakeMediaLibrary(Library(false)), new InMemoryPlaybackStateStore());
        Assert.DoesNotContain((await engine.BuildAsync(new SchedulerConfig(), channel, Day, Day.AddHours(1))).Entries,
            entry => entry.GeneratedTemplate is not null);

        channel.GeneratedPromos.Templates[0].Duration = TimeSpan.FromSeconds(8);
        channel.GeneratedPromos.Enabled = false;
        Assert.DoesNotContain((await engine.BuildAsync(new SchedulerConfig(), channel, Day, Day.AddHours(1))).Entries,
            entry => entry.GeneratedTemplate is not null);
    }

    [Fact]
    public void Resolver_fills_layers_script_and_content_key_from_the_schedule()
    {
        GeneratedPromoTemplate template = Template();
        UpNextListing[] listings =
        [
            new("SpongeBob SquarePants", Day),
            new("Jimmy Neutron", Day.AddMinutes(30)),
            new("Rocket Power", Day.AddHours(1))
        ];

        ResolvedGeneratedPromo promo = GeneratedPromoResolver.Resolve(template, "The Zone", listings, TimeZoneInfo.Utc)!;

        Assert.Equal(["UP NEXT"], promo.LayerLines[1]);
        Assert.Equal(["SpongeBob SquarePants"], promo.LayerLines[2]);
        Assert.Equal(["12:30  Jimmy Neutron"], promo.LayerLines[3]);
        Assert.Equal(["Only on The Zone"], promo.LayerLines[4]);
        Assert.StartsWith("Coming up next... SpongeBob SquarePants. ", promo.Script, StringComparison.Ordinal);
        Assert.Contains("Jimmy Neutron at 12:30!", promo.Script, StringComparison.Ordinal);
        Assert.EndsWith(" Only on The Zone!", promo.Script, StringComparison.Ordinal);
        Assert.Equal(promo.Key, GeneratedPromoResolver.Resolve(template, "The Zone", listings[..2], TimeZoneInfo.Utc)!.Key);
        Assert.NotEqual(promo.Key, GeneratedPromoResolver.Resolve(template, "The Zone", [listings[0], listings[2]], TimeZoneInfo.Utc)!.Key);
        Assert.Null(GeneratedPromoResolver.Resolve(template, "The Zone", [], TimeZoneInfo.Utc));

        PromoComposition composition = GeneratedPromoResolver.Compose(promo, "voice.wav", 1.2);
        Assert.Equal(TimeSpan.FromSeconds(8), composition.Duration);
        Assert.Equal(1.2, composition.AudioTempo);
        Assert.Equal(5, composition.Layers.Count);
        PromoTextLayer later = Assert.IsType<PromoTextLayer>(composition.Layers[3]);
        Assert.Equal(PromoTransition.SlideUp, later.In);
        Assert.Equal(["12:30  Jimmy Neutron"], later.Lines);
    }

    [Fact]
    public async Task Rendering_caches_voice_fits_long_reads_and_falls_back_to_silent_video()
    {
        var voice = new FakeVoice(TimeSpan.FromSeconds(9));
        using GeneratedPromoService service = Service(voice);
        ResolvedGeneratedPromo promo = GeneratedPromoResolver.Resolve(
            Template(), "The Zone", [new("SpongeBob", Day), new("Rugrats", Day.AddMinutes(30))], TimeZoneInfo.Utc)!;

        GeneratedPromoRender first = await service.RenderAsync(promo);
        GeneratedPromoRender second = await service.RenderAsync(promo);

        Assert.Null(first.VoiceError);
        Assert.Equal(first.Path, second.Path);
        Assert.Equal(1, voice.Calls);
        Assert.Equal(promo.Script, voice.LastText);
        Assert.Equal("classic-voice", voice.LastVoice);
        Assert.InRange(await ProbeSeconds(first.Path), 7.95, 8.1);

        voice.Fail = true;
        ResolvedGeneratedPromo other = GeneratedPromoResolver.Resolve(Template(), "The Zone", [new("Hey Arnold!", Day)], TimeZoneInfo.Utc)!;
        GeneratedPromoRender silent = await service.RenderAsync(other);
        Assert.NotNull(silent.VoiceError);
        Assert.EndsWith(".silent.mp4", silent.Path, StringComparison.Ordinal);
        Assert.True(File.Exists(silent.Path));
    }

    [Fact]
    public async Task Voice_cache_shares_concurrent_calls_survives_cleanup_and_tracks_provider_settings()
    {
        var voice = new FakeVoice(TimeSpan.FromSeconds(2)) { Delay = TimeSpan.FromMilliseconds(300) };
        using GeneratedPromoService service = Service(voice);
        ResolvedGeneratedPromo promo = GeneratedPromoResolver.Resolve(Template(), "The Zone", [new("SpongeBob", Day)], TimeZoneInfo.Utc)!;

        GeneratedPromoRender[] renders = await Task.WhenAll(service.RenderAsync(promo), service.RenderAsync(promo));

        Assert.All(renders, render => Assert.Null(render.VoiceError));
        Assert.Equal(1, voice.Calls);
        string cached = Assert.Single(Directory.GetFiles(service.VoiceDirectory));

        service.Prepare(new SchedulerConfig(), [], DateTimeOffset.UtcNow.AddYears(1));
        Assert.True(File.Exists(cached));
        Assert.False(File.Exists(renders[0].Path));

        await service.RenderAsync(promo);
        Assert.Equal(1, voice.Calls);

        voice.Settings = "other-model";
        await service.RenderAsync(promo);
        Assert.Equal(2, voice.Calls);
        Assert.Equal(2, Directory.GetFiles(service.VoiceDirectory).Length);
    }

    [Fact]
    public void Font_list_holds_installed_families_once_and_sorted()
    {
        IReadOnlyList<string> fonts = PromoFonts.Installed();

        Assert.Contains("Arial", fonts);
        Assert.DoesNotContain(fonts, font => font.StartsWith('@'));
        Assert.Equal(fonts.Order(StringComparer.OrdinalIgnoreCase), fonts);
        Assert.Equal(fonts.Count, fonts.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Save_folder_comes_from_schedule_settings_and_must_be_a_full_path()
    {
        using var provider = new SchedulerConfigProvider(Options.Create(new SchedulerServiceOptions
        {
            ConfigPath = Path.Combine(_directory, "schedule.yaml"), WatchConfig = false, AutoRebuild = false
        }), NullLogger<SchedulerConfigProvider>.Instance);
        using var service = new GeneratedPromoService(
            Options.Create(new GeneratedPromoOptions { OutputPath = Path.Combine(_directory, "default") }),
            new TextToSpeechCatalog([]), TimeProvider.System, NullLogger<GeneratedPromoService>.Instance, provider);
        Assert.Equal(Path.Combine(_directory, "default"), service.Root);

        string custom = Path.Combine(_directory, "custom promos");
        var config = new SchedulerConfig();
        config.Settings.GeneratedPromosPath = custom;
        Assert.Contains("generated-promos:", SchedulerConfigLoader.Serialize(config));
        Assert.True(provider.Save(SchedulerConfigLoader.Serialize(config)).IsValid);
        Assert.Equal(custom, service.Root);
        Assert.Equal(Path.Combine(custom, "voice"), service.VoiceDirectory);
        Assert.DoesNotContain("generated-promos", SchedulerConfigLoader.Serialize(new SchedulerConfig()));

        config.Settings.GeneratedPromosPath = "state/promos";
        Assert.Contains(SchedulerConfigLoader.Validate(config).Errors, error => error.StartsWith("settings.plugins.generated-promos.path"));
    }

    [Fact]
    public async Task Background_clip_plays_the_promoted_episode_with_its_sound_ducked_under_the_voice()
    {
        Directory.CreateDirectory(_directory);
        string episode = Path.Combine(_directory, "episode.mp4");
        await RunTool("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=red:size=160x90:rate=10",
            "-f", "lavfi", "-i", "sine=frequency=110:sample_rate=48000", "-t", "30", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", episode);
        using GeneratedPromoService service = Service(new FakeVoice(TimeSpan.FromSeconds(2.5)));
        GeneratedPromoTemplate template = Template();
        template.BackgroundClip = new GeneratedClipConfig { IncludeAudio = true, Volume = 0.8 };
        UpNextListing[] listings = [new("SpongeBob", Day, null, episode, TimeSpan.FromSeconds(30)), new("Rugrats", Day.AddMinutes(30))];

        ResolvedGeneratedPromo promo = GeneratedPromoResolver.Resolve(template, "The Zone", listings, TimeZoneInfo.Utc)!;
        Assert.Equal(episode, promo.ClipPath);
        Assert.InRange(promo.ClipStart.TotalSeconds, 4.5, 22);
        GeneratedPromoRender render = await service.RenderAsync(promo);

        Assert.Null(render.VoiceError);
        Assert.InRange(await ProbeSeconds(render.Path), 7.95, 8.1);
        double tail = await MeanVolume(render.Path, 4.5, 2, "bandpass=f=110:width_type=q:w=4,");
        double speaking = await MeanVolume(render.Path, 1, 1.5, "bandpass=f=110:width_type=q:w=4,");
        Assert.InRange(tail, -40, -3);
        Assert.True(speaking < tail - 6, $"episode sound {speaking} dB while speaking vs {tail} dB after");
        Assert.True(await MeanVolume(render.Path, 1, 1.5, "bandpass=f=440:width_type=q:w=4,") > -35, "the voice-over should stay audible");

        string frame = Path.Combine(_directory, "frame.png");
        await RunTool("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-ss", "4", "-i", render.Path, "-frames:v", "1", frame);
        using SkiaSharp.SKBitmap still = SkiaSharp.SKBitmap.Decode(frame);
        SkiaSharp.SKColor corner = still.GetPixel(3, 3);
        Assert.True(corner.Red > 180 && corner.Green < 80, $"episode frame {corner}");

        string song = Path.Combine(_directory, "song.wav");
        await File.WriteAllBytesAsync(song, FakeVoice.Wav(TimeSpan.FromSeconds(2), 220, 16_000));
        template.Music = new GeneratedMusicConfig { Paths = [song], Volume = 0.5 };
        GeneratedPromoRender both = await service.RenderAsync(GeneratedPromoResolver.Resolve(template, "The Zone", listings, TimeZoneInfo.Utc)!);
        Assert.InRange(await MeanVolume(both.Path, 4.5, 2, "bandpass=f=110:width_type=q:w=4,"), -40, -3);
        Assert.InRange(await MeanVolume(both.Path, 4.5, 2, "bandpass=f=220:width_type=q:w=4,"), -45, -3);
    }

    [Fact]
    public void Clip_start_is_fixed_when_set_varies_per_airing_otherwise_and_falls_back_without_a_file()
    {
        Directory.CreateDirectory(_directory);
        string episode = Path.Combine(_directory, "episode.mkv");
        File.WriteAllText(episode, "");
        GeneratedPromoTemplate template = Template();
        var show = new UpNextListing("SpongeBob", Day, null, episode, TimeSpan.FromMinutes(22));

        Assert.Null(GeneratedPromoResolver.Clip(template, show));
        template.BackgroundClip = new GeneratedClipConfig();
        Assert.Null(GeneratedPromoResolver.Clip(template, show with { Path = Path.Combine(_directory, "gone.mkv") }));
        Assert.Null(GeneratedPromoResolver.Clip(template, show with { Path = null }));

        TimeSpan[] starts = [.. Enumerable.Range(0, 20).Select(day => GeneratedPromoResolver.Clip(template, show with { Start = Day.AddDays(day) })!.Value.Start)];
        Assert.All(starts, start => Assert.InRange(start.TotalMinutes, 22 * 0.15 - 0.02, 22 * 0.75 + 0.02));
        Assert.True(starts.Distinct().Count() > 10);
        Assert.Equal(starts[0], GeneratedPromoResolver.Clip(template, show)!.Value.Start);

        template.BackgroundClip.StartAt = TimeSpan.FromMinutes(3);
        Assert.Equal(TimeSpan.FromMinutes(3), GeneratedPromoResolver.Clip(template, show)!.Value.Start);
        template.BackgroundClip.StartAt = TimeSpan.FromMinutes(40);
        Assert.Equal(TimeSpan.FromMinutes(22) - template.Duration, GeneratedPromoResolver.Clip(template, show)!.Value.Start);

        ResolvedGeneratedPromo withClip = GeneratedPromoResolver.Resolve(template, "The Zone", [show], TimeZoneInfo.Utc)!;
        template.BackgroundClip = null;
        Assert.NotEqual(withClip.Key, GeneratedPromoResolver.Resolve(template, "The Zone", [show], TimeZoneInfo.Utc)!.Key);
    }

    [Fact]
    public async Task Coordinator_publishes_placeholders_then_attaches_background_renders()
    {
        var clock = new FixedClock(Day.AddMinutes(-1));
        using var provider = new SchedulerConfigProvider(Options.Create(new SchedulerServiceOptions
        {
            ConfigPath = Path.Combine(_directory, "schedule.yaml"), WatchConfig = false, AutoRebuild = false
        }), NullLogger<SchedulerConfigProvider>.Instance);
        var config = new SchedulerConfig { Channels = [Channel(GeneratedPromoUsage.Fallback)] };
        config.Settings.TimeZone = "UTC";
        config.Settings.Horizon = TimeSpan.FromHours(2);
        Assert.True(provider.Save(SchedulerConfigLoader.Serialize(config)).IsValid);
        var library = new FakeMediaLibrary(Library(false));
        using GeneratedPromoService promos = Service(new FakeVoice(TimeSpan.FromSeconds(3)), clock);
        var publisher = new RecordingPublisher();
        var coordinator = new ScheduleCoordinator(provider, new ScheduleBuilder(library, new InMemoryPlaybackStateStore()), library,
            [publisher], NullLogger<ScheduleCoordinator>.Instance, NullLoggerFactory.Instance, clock, [promos]);

        ScheduleEntry placeholder = (await coordinator.RebuildAsync(true, true, CancellationToken.None))[0].Entries
            .First(entry => entry.GeneratedTemplate is not null && entry.End > clock.GetUtcNow());
        Assert.Null(placeholder.Item!.Path);
        Assert.True(promos.Pending > 0);
        GeneratedPromoQueueStatus queued = promos.Queue!;
        Assert.Equal((0, 0, promos.Pending, 0), (queued.Done, queued.Failed, queued.Total, queued.Rendering.Count));
        Assert.Equal(queued.Total, queued.Waiting);
        Assert.NotNull(queued.NextAirsAt);
        int changes = 0;
        List<GeneratedPromoJob> started = [];
        promos.QueueChanged += () =>
        {
            Interlocked.Increment(ref changes);
            if (promos.Queue is { } status) lock (started) started.AddRange(status.Rendering);
        };

        await promos.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (promos.Pending > 0)
            {
                await Task.Delay(50, timeout.Token);
            }

            await coordinator.RefreshFinishedSchedulesAsync(CancellationToken.None);
        }
        finally
        {
            await promos.StopAsync(CancellationToken.None);
        }

        ScheduleEntry attached = publisher.Latest[0].Entries.Single(entry => entry.Start == placeholder.Start);
        Assert.Null(promos.Queue);
        Assert.True(changes >= queued.Total * 2);
        Assert.Contains(started, job => job.Label == "The Zone › Classic");
        Assert.Equal(2, publisher.Calls);
        Assert.StartsWith(promos.Root, attached.Item!.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(promos.Root, "The Zone", "Classic"), Path.GetDirectoryName(attached.Item.Path));
        Assert.True(File.Exists(attached.Item.Path));
        Assert.Equal(placeholder.Duration, attached.Duration);

        int calls = publisher.Calls;
        await coordinator.RefreshFinishedSchedulesAsync(CancellationToken.None);
        Assert.Equal(calls, publisher.Calls);
    }

    [Fact]
    public async Task Renders_reach_the_playout_while_a_long_rebuild_is_still_running()
    {
        var clock = new FixedClock(Day.AddMinutes(-1));
        using var provider = new SchedulerConfigProvider(Options.Create(new SchedulerServiceOptions
        {
            ConfigPath = Path.Combine(_directory, "schedule.yaml"), WatchConfig = false, AutoRebuild = false
        }), NullLogger<SchedulerConfigProvider>.Instance);
        var config = new SchedulerConfig { Channels = [Channel(GeneratedPromoUsage.Fallback)] };
        config.Settings.TimeZone = "UTC";
        config.Settings.Horizon = TimeSpan.FromHours(2);
        Assert.True(provider.Save(SchedulerConfigLoader.Serialize(config)).IsValid);
        var library = new GatedLibrary(new FakeMediaLibrary(Library(false)));
        using GeneratedPromoService promos = Service(new FakeVoice(TimeSpan.FromSeconds(3)), clock);
        var publisher = new RecordingPublisher();
        var coordinator = new ScheduleCoordinator(provider, new ScheduleBuilder(library, new InMemoryPlaybackStateStore()), library,
            [publisher], NullLogger<ScheduleCoordinator>.Instance, NullLoggerFactory.Instance, clock, [promos]);

        await coordinator.RebuildAsync(true, true, CancellationToken.None);
        Assert.True(promos.Pending > 0);

        library.Close();
        Task building = coordinator.RebuildAsync(true, true, CancellationToken.None);
        await promos.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (promos.Pending > 0)
            {
                await Task.Delay(50, timeout.Token);
            }

            int calls = publisher.Calls;
            await coordinator.RefreshFinishedSchedulesAsync(timeout.Token);

            Assert.False(building.IsCompleted);
            Assert.Equal(calls + 1, publisher.Calls);
            Assert.Contains(publisher.Latest[0].Entries, entry => entry.GeneratedTemplate is not null && entry.Item!.Path is not null);
        }
        finally
        {
            library.Open();
            await building;
            await promos.StopAsync(CancellationToken.None);
        }
    }

    private sealed class GatedLibrary(IMediaLibrary inner) : IMediaLibrary
    {
        private TaskCompletionSource _gate = CompletedGate();

        private static TaskCompletionSource CompletedGate()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gate.SetResult();
            return gate;
        }

        public void Close() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open() => _gate.TrySetResult();

        public async ValueTask<IReadOnlyList<MediaItem>> SearchAsync(SchedularrTV.Core.Query.QueryExpression expression, CancellationToken cancellationToken = default)
        {
            await _gate.Task.WaitAsync(cancellationToken);
            return await inner.SearchAsync(expression, cancellationToken);
        }

        public ValueTask RefreshAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    [Fact]
    public void Prepare_leaves_aired_and_unreachable_slots_alone()
    {
        using GeneratedPromoService service = Service(new FakeVoice(TimeSpan.FromSeconds(1)));
        var config = new SchedulerConfig { Channels = [Channel(GeneratedPromoUsage.Fallback)] };
        ScheduleEntry Generated(DateTimeOffset start) => new()
        {
            Start = start, Duration = TimeSpan.FromSeconds(8), Kind = ScheduleEntryKind.UpNextPromo, GeneratedTemplate = "Classic",
            Item = GeneratedPromoPlanner.Placeholder(config.Channels[0].GeneratedPromos!.Templates[0])
        };
        var schedule = new ChannelSchedule
        {
            ChannelId = "zone", ChannelName = "The Zone", Start = Day, End = Day.AddHours(5),
            Entries =
            [
                Generated(Day.AddMinutes(-10)),
                Generated(Day.AddHours(4)),
                new() { Start = Day.AddHours(4).AddSeconds(8), Duration = TimeSpan.FromMinutes(22), Kind = ScheduleEntryKind.Program,
                    Item = TestMedia.Episode("Only one", 1, 1, TimeSpan.FromMinutes(22)) }
            ]
        };

        IReadOnlyList<ChannelSchedule> prepared = service.Prepare(config, [schedule], Day);

        Assert.Same(schedule, prepared[0]);
        Assert.Equal(0, service.Pending);
    }

    [Theory]
    [InlineData(60, 1)]
    [InlineData(0, 2)]
    public void Prepare_only_queues_promos_airing_within_the_lead_time(int leadMinutes, int queuedNow)
    {
        using GeneratedPromoService service = Service(new FakeVoice(TimeSpan.FromSeconds(1)));
        var config = new SchedulerConfig { Channels = [Channel(GeneratedPromoUsage.Fallback)] };
        config.Settings.GeneratedPromosLeadTime = TimeSpan.FromMinutes(leadMinutes);
        ChannelSchedule schedule = TwoPromoSchedule(config);

        service.Prepare(config, [schedule], Day);
        Assert.Equal(queuedNow, service.Pending);

        // The first promo aired while still waiting, so it is dropped rather than rendered late.
        service.Prepare(config, [schedule], Day.AddHours(2.5));
        Assert.Equal(1, service.Pending);
    }

    [Fact]
    public void Prepare_renders_only_for_watched_channels_and_drops_renders_once_viewers_leave()
    {
        var viewers = new FakeViewers();
        using var service = new GeneratedPromoService(
            Options.Create(new GeneratedPromoOptions { OutputPath = Path.Combine(_directory, "renders") }),
            new TextToSpeechCatalog([new FakeVoice(TimeSpan.FromSeconds(1))]),
            TimeProvider.System,
            NullLogger<GeneratedPromoService>.Instance,
            viewers: viewers);
        var config = new SchedulerConfig { Channels = [Channel(GeneratedPromoUsage.Fallback)] };
        config.Settings.GeneratedPromosLeadTime = TimeSpan.Zero;
        ChannelSchedule schedule = TwoPromoSchedule(config);

        viewers.Watched = false;
        service.Prepare(config, [schedule], Day);
        Assert.Equal(0, service.Pending);

        viewers.Watched = true;
        service.Prepare(config, [schedule], Day);
        Assert.Equal(2, service.Pending);

        viewers.Watched = false;
        service.Prepare(config, [schedule], Day);
        Assert.Null(service.Queue);

        // A target that can't report viewers is treated as watched, as is turning the setting off.
        viewers.Watched = null;
        service.Prepare(config, [schedule], Day);
        Assert.Equal(2, service.Pending);
        viewers.Watched = false;
        config.Settings.GeneratedPromosWatchedOnly = false;
        service.Prepare(config, [schedule], Day);
        Assert.Equal(2, service.Pending);
    }

    private sealed class FakeViewers : SchedularrTV.Core.Publishing.IChannelViewers
    {
        public bool? Watched { get; set; }

        public bool? IsWatched(int channelNumber) => Watched;
    }

    private static ChannelSchedule TwoPromoSchedule(SchedulerConfig config)
    {
        List<ScheduleEntry> entries = [];
        foreach (DateTimeOffset at in (DateTimeOffset[])[Day.AddMinutes(10), Day.AddHours(3)])
        {
            entries.Add(new()
            {
                Start = at, Duration = TimeSpan.FromSeconds(8), Kind = ScheduleEntryKind.UpNextPromo, GeneratedTemplate = "Classic",
                Item = GeneratedPromoPlanner.Placeholder(config.Channels[0].GeneratedPromos!.Templates[0])
            });
            entries.AddRange(Enumerable.Range(0, 4).Select(index => new ScheduleEntry
            {
                Start = at.AddSeconds(8).AddMinutes(22 * index), Duration = TimeSpan.FromMinutes(22), Kind = ScheduleEntryKind.Program,
                Item = TestMedia.Episode(index % 2 == 0 ? "SpongeBob" : "Rugrats", 1, index + 1, TimeSpan.FromMinutes(22))
            }));
        }

        return new ChannelSchedule
        {
            ChannelId = "zone", ChannelName = "The Zone", ChannelNumber = 1, Start = Day, End = Day.AddHours(5), Entries = entries
        };
    }

    private GeneratedPromoService Service(ITextToSpeechProvider voice, TimeProvider? clock = null) => new(
        Options.Create(new GeneratedPromoOptions { OutputPath = Path.Combine(_directory, "renders"), Concurrency = 2 }),
        new TextToSpeechCatalog([voice]),
        clock ?? TimeProvider.System,
        NullLogger<GeneratedPromoService>.Instance);

    private static ChannelConfig Channel(GeneratedPromoUsage usage, double chance = 1) => new()
    {
        Id = "zone",
        Name = "The Zone",
        Number = 1,
        TimeZone = "UTC",
        AutoTemporalTags = false,
        PadToNearest = TimeSpan.FromMinutes(30),
        BaseRotation = new ContentSelector { Include = [new ContentInclude { Show = "SpongeBob" }, new ContentInclude { Show = "Rugrats" }] },
        Promos = new PromoConfig { BaseQuery = "tag:'Promo'", UpNext = new PromoSpotConfig { SkipWhenSameSeries = false } },
        GeneratedPromos = new GeneratedPromosConfig
        {
            Templates = [Template(usage, chance)]
        }
    };

    private static GeneratedPromoTemplate Template(GeneratedPromoUsage usage = GeneratedPromoUsage.Fallback, double chance = 1) => new()
    {
        Name = "Classic",
        Usage = usage,
        MixChance = chance,
        Duration = TimeSpan.FromSeconds(8),
        Width = 320,
        Height = 180,
        Fps = 10,
        Voice = new GeneratedVoiceConfig { Provider = "fake", Voice = "classic-voice", PostMessage = "Only on {channel}!" },
        Layers =
        [
            new() { Name = "Panel", Kind = GeneratedLayerKind.Rect, X = 10, Y = 10, Width = 300, Height = 160, Color = "#96000000" },
            new() { Name = "Heading", Kind = GeneratedLayerKind.Text, Text = "UP NEXT", X = 20, Y = 20, Width = 280, Height = 30 },
            new() { Name = "Next", Kind = GeneratedLayerKind.UpNext, X = 20, Y = 60, Width = 280, Height = 40,
                UpNext = new GeneratedUpNextOptions() },
            new() { Name = "Later", Kind = GeneratedLayerKind.UpNext, X = 20, Y = 110, Width = 280, Height = 25,
                TransitionIn = GeneratedTransition.SlideUp, UpNext = new GeneratedUpNextOptions { Offset = 1, IncludeTime = true } },
            new() { Name = "Channel", Kind = GeneratedLayerKind.Text, Text = "Only on {channel}", X = 20, Y = 140, Width = 280, Height = 25 }
        ]
    };

    private static List<MediaItem> Library(bool clips)
    {
        List<MediaItem> items = [];
        for (int episode = 1; episode <= 6; episode++)
        {
            items.Add(TestMedia.Episode("SpongeBob", 1, episode, TimeSpan.FromMinutes(22)));
            items.Add(TestMedia.Episode("Rugrats", 1, episode, TimeSpan.FromMinutes(22)));
        }

        if (clips)
        {
            items.Add(TestMedia.Promo("clip-spongebob", TimeSpan.FromSeconds(10), "SpongeBob", "up_next"));
            items.Add(TestMedia.Promo("clip-rugrats", TimeSpan.FromSeconds(10), "Rugrats", "up_next"));
        }

        return items;
    }

    private static async Task<double> ProbeSeconds(string path)
    {
        var info = new System.Diagnostics.ProcessStartInfo("ffprobe") { RedirectStandardOutput = true };
        foreach (string argument in (string[])["-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", path])
            info.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(info)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return double.Parse(output, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task RunTool(string file, params string[] arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo(file) { RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(info)!;
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }

    private static async Task<double> MeanVolume(string path, double start, double length, string filter = "")
    {
        var info = new System.Diagnostics.ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (string argument in (string[])["-hide_banner", "-nostats", "-ss", start.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "-t", length.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", path, "-vn", "-af", $"{filter}volumedetect", "-f", "null", "-"])
            info.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(info)!;
        string log = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(log, @"mean_volume: (-?[\d.]+|-inf) dB");
        Assert.True(match.Success, log);
        return match.Groups[1].Value == "-inf" ? double.NegativeInfinity
            : double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class FakeVoice(TimeSpan length) : ITextToSpeechProvider
    {
        private int _calls;

        public int Calls => _calls;

        public bool Fail { get; set; }

        public TimeSpan Delay { get; set; }

        public string Settings { get; set; } = string.Empty;

        public string? LastText { get; private set; }

        public string? LastVoice { get; private set; }

        public string Id => "fake";

        public string DisplayName => "Fake";

        public string CacheIdentity => Settings;

        public Task<IReadOnlyList<TtsVoice>> GetVoicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TtsVoice>>([new("classic-voice", "Classic")]);

        public async Task<TtsAudio> SynthesizeAsync(string text, string voice, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            LastText = text;
            LastVoice = voice;
            await Task.Delay(Delay, cancellationToken);
            return Fail ? throw new HttpRequestException("offline") : new TtsAudio(Wav(length, 440, 16_000), "wav");
        }

        public static byte[] Wav(TimeSpan length, double frequency, int rate)
        {
            int samples = (int)(length.TotalSeconds * rate);
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.ASCII);
            writer.Write("RIFF"u8);
            writer.Write(36 + samples * 2);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(rate);
            writer.Write(rate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(samples * 2);
            for (int index = 0; index < samples; index++)
                writer.Write((short)(Math.Sin(index * 2 * Math.PI * frequency / rate) * 8_000));
            writer.Flush();
            return stream.ToArray();
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingPublisher : IPlayoutPublisher
    {
        public string Name => "Recording";

        public int Calls { get; private set; }

        public IReadOnlyList<ChannelSchedule> Latest { get; private set; } = [];

        public Task PublishAsync(IReadOnlyList<ChannelSchedule> schedules, CancellationToken cancellationToken = default)
        {
            Calls++;
            Latest = schedules;
            return Task.CompletedTask;
        }
    }
}
