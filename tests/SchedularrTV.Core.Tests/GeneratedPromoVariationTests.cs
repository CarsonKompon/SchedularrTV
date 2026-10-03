using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Service;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class GeneratedPromoVariationTests : IDisposable
{
    private static readonly DateTimeOffset Day = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"generated-promo-variation-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public void Up_next_read_outs_vary_how_later_shows_are_linked()
    {
        UpNextListing[] listings = [new("SpongeBob", Day), new("Rugrats", Day.AddMinutes(30)), new("Hey Arnold!", Day.AddHours(1))];

        string[] scripts = [.. Enumerable.Range(0, 40).Select(seed => UpNextPromo.Script(listings, TimeZoneInfo.Utc, variety: $"seed-{seed}"))];

        Assert.True(scripts.Distinct().Count() >= 4, string.Join("\n", scripts.Distinct()));
        Assert.All(scripts, script =>
        {
            Assert.StartsWith("Coming up next... SpongeBob. ", script, StringComparison.Ordinal);
            Assert.Contains("Rugrats at 12:30, ", script, StringComparison.Ordinal);
            Assert.EndsWith(" Hey Arnold! at 1!", script, StringComparison.Ordinal);
        });
        Assert.Contains(scripts, script => script.Contains("Followed by Rugrats", StringComparison.Ordinal));
        Assert.Equal(scripts[0], UpNextPromo.Script(listings, TimeZoneInfo.Utc, variety: "seed-0"));
        Assert.Equal("Coming up next... SpongeBob. Then Rugrats at 12:30! Then Hey Arnold! at 1:00!",
            UpNextPromo.Script(listings, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Openers_and_closers_are_picked_per_promo_and_an_empty_one_says_nothing()
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.ForType(GeneratedPromoType.NowPlaying);
        template.Voice!.PreMessages = ["Right now on {channel},", ""];
        template.Voice.PostMessages = ["", "Only on {channel}!"];

        string[] scripts = [.. Enumerable.Range(0, 30).Select(minute =>
            GeneratedPromoResolver.Resolve(template, "The Zone", [new UpNextListing("SpongeBob", Day.AddMinutes(minute))], TimeZoneInfo.Utc)!.Script!)];

        Assert.Contains("Right now on The Zone, SpongeBob!", scripts);
        Assert.Contains("SpongeBob!", scripts);
        Assert.Contains("SpongeBob! Only on The Zone!", scripts);
        Assert.Contains("Right now on The Zone, SpongeBob! Only on The Zone!", scripts);

        template.Voice.PreMessages = [];
        template.Voice.PostMessages = [];
        Assert.Equal("You're watching SpongeBob!",
            GeneratedPromoResolver.Resolve(template, "The Zone", [new UpNextListing("SpongeBob", Day)], TimeZoneInfo.Utc)!.Script);
    }

    [Fact]
    public void Single_phrases_from_older_files_become_lists()
    {
        const string yaml = """
            channels:
            - id: zone
              name: The Zone
              generated_promos:
                templates:
                - name: Classic
                  voice:
                    provider: tiktok
                    voice: en_us_006
                    pre_message: Coming up next...
                    post_message: Only on {channel}!
            """;

        GeneratedVoiceConfig voice = SchedulerConfigLoader.Parse(yaml).Channels[0].GeneratedPromos!.Templates[0].Voice!;

        Assert.Equal(["Coming up next..."], voice.PreMessages);
        Assert.Equal(["Only on {channel}!"], voice.PostMessages);
        Assert.Null(voice.PreMessage);
        var config = new SchedulerConfig { Channels = [new ChannelConfig { Id = "z", Name = "Z", GeneratedPromos = new() { Templates = [new() { Name = "T", Voice = voice }] } }] };
        voice.PostMessages.Add("");
        string saved = SchedulerConfigLoader.Serialize(config);
        Assert.DoesNotContain("pre_message:", saved);
        Assert.Equal(["Only on {channel}!", ""], SchedulerConfigLoader.Parse(saved).Channels[0].GeneratedPromos!.Templates[0].Voice!.PostMessages);
    }

    [Fact]
    public async Task Up_next_lands_in_mid_rolls_or_the_last_break_but_only_once_per_show()
    {
        List<MediaItem> library = [];
        for (int episode = 1; episode <= 12; episode++)
        {
            foreach (string show in (string[])["SpongeBob", "Rugrats"])
            {
                library.Add(new MediaItem
                {
                    Id = $"{show}-{episode}", Title = $"{show} {episode}", SeriesName = show, SeriesId = show, Kind = MediaKind.Episode,
                    SeasonNumber = 1, EpisodeNumber = episode, Duration = TimeSpan.FromMinutes(24),
                    Chapters = [TimeSpan.Zero, TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(16)], Path = $@"M:\{show}\{episode}.mkv"
                });
            }
        }

        for (int ad = 0; ad < 20; ad++)
        {
            library.Add(TestMedia.Filler($"ad-{ad}", TimeSpan.FromSeconds(30), "Commercials"));
        }

        var channel = new ChannelConfig
        {
            Id = "zone", Name = "The Zone", Number = 1, TimeZone = "UTC", AutoTemporalTags = false, PadToNearest = TimeSpan.FromMinutes(30),
            MidRoll = new MidRollConfig { Mode = MidRollMode.Chapters, TargetSegment = TimeSpan.FromMinutes(8), MinSegment = TimeSpan.FromMinutes(5),
                ChapterTolerance = TimeSpan.FromMinutes(2), MinBreak = TimeSpan.FromSeconds(30) },
            BaseRotation = new ContentSelector { Query = "kind == 'Episode'", Order = PlaybackOrder.Random },
            Promos = new PromoConfig { BaseQuery = "tag:'Promo'", UpNext = new PromoSpotConfig { SkipWhenSameSeries = false } },
            GeneratedPromos = new GeneratedPromosConfig { Templates = [GeneratedPromoTemplates.UpNext("Classic")] }
        };
        var config = new SchedulerConfig
        {
            Settings = new SchedulerSettings
            {
                TimeZone = "UTC",
                Interstitials = new InterstitialsConfig
                {
                    Commercials = new CommercialsConfig { BaseQuery = "tag:\"Commercials\"", MaxRepeatsPerBreak = 12 },
                    Filler = new FillerTierConfig { Fallback = false }
                }
            },
            Channels = [channel]
        };

        ChannelSchedule schedule = await new SchedulingEngine(new FakeMediaLibrary(library), new InMemoryPlaybackStateStore())
            .BuildAsync(config, channel, Day, Day.AddHours(12));

        List<ScheduleEntry> entries = [.. schedule.Entries];
        ScheduleEntry[] upNext = [.. entries.Where(entry => entry.Kind == ScheduleEntryKind.UpNextPromo)];
        bool InMidRoll(ScheduleEntry promo) =>
            entries.FirstOrDefault(entry => entry.Kind == ScheduleEntryKind.Program && entry.Start >= promo.End) is { SegmentNumber: > 1 };

        Assert.Contains(upNext, InMidRoll);
        Assert.Contains(upNext, promo => !InMidRoll(promo));
        Assert.Contains(upNext, promo => entries.First(entry => entry.Start >= promo.End).Kind != ScheduleEntryKind.Program);
        List<DateTimeOffset> slotStarts = [.. entries.Where(entry => entry.Kind == ScheduleEntryKind.Program && entry.SegmentNumber <= 1).Select(entry => entry.Start)];
        Assert.All(slotStarts.Zip(slotStarts.Skip(1)), slot =>
            Assert.True(upNext.Count(promo => promo.Start >= slot.First && promo.Start < slot.Second) <= 1, $"more than one Up Next in the slot at {slot.First}"));
        Assert.All(entries.Zip(entries.Skip(1)), pair => Assert.Equal(pair.First.End, pair.Second.Start));
    }

    [Fact]
    public void Cache_settings_expire_unused_videos_and_voice_overs_or_keep_them_when_zero()
    {
        using var provider = new SchedulerConfigProvider(Options.Create(new SchedulerServiceOptions
        {
            ConfigPath = Path.Combine(_directory, "schedule.yaml"), WatchConfig = false, AutoRebuild = false
        }), NullLogger<SchedulerConfigProvider>.Instance);
        using var service = new GeneratedPromoService(
            Options.Create(new GeneratedPromoOptions { OutputPath = Path.Combine(_directory, "renders") }),
            new TextToSpeechCatalog([]), TimeProvider.System, NullLogger<GeneratedPromoService>.Instance, provider);
        string video = Old(Path.Combine(service.Root, "The Zone", "Classic", $"{new string('a', 32)}.mp4"), days: 3);
        string voice = Old(Path.Combine(service.VoiceDirectory, $"{new string('b', 32)}.mp3"), days: 3);

        Save(provider, videos: TimeSpan.Zero, audio: TimeSpan.Zero);
        service.Prepare(provider.Current, [], DateTimeOffset.UtcNow);
        Assert.True(File.Exists(video) && File.Exists(voice));

        Save(provider, videos: TimeSpan.FromDays(7), audio: TimeSpan.FromDays(1));
        service.Prepare(provider.Current, [], DateTimeOffset.UtcNow.AddHours(2));
        Assert.True(File.Exists(video));
        Assert.False(File.Exists(voice));

        Save(provider, videos: TimeSpan.FromDays(1), audio: TimeSpan.FromDays(1));
        service.Prepare(provider.Current, [], DateTimeOffset.UtcNow.AddHours(4));
        Assert.False(File.Exists(video));
        Assert.False(Directory.Exists(Path.GetDirectoryName(video)));
    }

    private static string Old(string path, int days)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-days));
        return path;
    }

    private static void Save(SchedulerConfigProvider provider, TimeSpan videos, TimeSpan audio)
    {
        var config = new SchedulerConfig();
        config.Settings.GeneratedPromosKeepVideos = videos;
        config.Settings.GeneratedPromosKeepAudio = audio;
        Assert.True(provider.Save(SchedulerConfigLoader.Serialize(config)).IsValid);
    }
}
