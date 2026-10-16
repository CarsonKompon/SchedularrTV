using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>
/// Golden-master tests of the whole engine. Each scenario builds two consecutive days for one channel (the second
/// resuming from the first's saved playback state) and compares everything that comes out (every entry field,
/// generated stand-ins, archive guide data, diagnostics and the persisted cursors) with a recorded snapshot.
/// </summary>
/// <remarks>
/// They exist to prove refactors of the engine change nothing. A missing snapshot is recorded on the first run
/// (except on CI, where it fails). A difference writes <c>&lt;name&gt;.received.txt</c> beside the snapshot and
/// fails; if the change is intended, set <c>UPDATE_SNAPSHOTS=1</c> and run again to accept it.
/// </remarks>
public sealed class EngineSnapshotTests : IDisposable
{
    private static readonly DateTimeOffset Monday = new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);

    private readonly string _archive = Path.Combine(Path.GetTempPath(), $"snapshot-archive-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_archive))
        {
            Directory.Delete(_archive, recursive: true);
        }
    }

    public static TheoryData<string> Scenarios => new()
    {
        "core-breaks-only",
        "promos-any-break",
        "promos-before-show",
        "generated-fallback",
        "generated-mixed",
        "archive-follow"
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Engine_output_matches_its_snapshot(string scenario)
    {
        SchedulerConfig config = Config(scenario);
        ChannelConfig channel = config.Channels[0];
        var state = new RecordingStateStore();

        // A fresh engine per day, as after a restart: only the saved state carries over.
        ChannelSchedule first = await new SchedulingEngine(new FakeMediaLibrary(Library()), state)
            .BuildAsync(config, channel, Monday, Monday.AddDays(1));
        ChannelSchedule second = await new SchedulingEngine(new FakeMediaLibrary(Library()), state)
            .BuildAsync(config, channel, Monday.AddDays(1), Monday.AddDays(2));

        string actual = Render(first, second, state).Replace(_archive, "<archive>", StringComparison.OrdinalIgnoreCase);
        Snapshot.Verify(scenario, actual);
    }

    private static readonly ScheduleEntryKind[] PromoKinds =
    [
        ScheduleEntryKind.Promo, ScheduleEntryKind.UpNextPromo, ScheduleEntryKind.NowPlayingPromo, ScheduleEntryKind.ThisHasBeenPromo,
        ScheduleEntryKind.YoureWatchingPromo, ScheduleEntryKind.BackToTheShowPromo, ScheduleEntryKind.EventuallyPromo,
        ScheduleEntryKind.NewEpisodePromo, ScheduleEntryKind.RightBackPromo
    ];

    [Theory]
    [InlineData("promos-any-break")]
    [InlineData("generated-mixed")]
    [InlineData("archive-follow")]
    public async Task With_every_feature_plugin_off_only_core_programming_airs(string scenario)
    {
        SchedulerConfig config = Config(scenario);
        ChannelSchedule schedule = await new SchedulingEngine(new FakeMediaLibrary(Library()), new InMemoryPlaybackStateStore())
            {
                Modules = EngineModules.For([])
            }
            .BuildAsync(config, config.Channels[0], Monday, Monday.AddDays(1));

        Assert.DoesNotContain(schedule.Entries, entry => PromoKinds.Contains(entry.Kind));
        Assert.All(schedule.Entries, entry => Assert.Null(entry.GeneratedTemplate));
        Assert.DoesNotContain(schedule.Entries, entry => entry.ProgramOrigin is ProgramOrigin.Archive or ProgramOrigin.ArchiveRandom);
        Assert.False(schedule.ArchiveFollowEnabled);
        Assert.Equal(scenario == "archive-follow", schedule.Diagnostics.Any(diagnostic => diagnostic.Message.Contains("Archive Follow plugin is off")));
    }

    [Fact]
    public async Task An_archive_slot_uses_its_mapped_blocks_own_generated_promos()
    {
        SchedulerConfig config = Config("archive-follow");
        ChannelConfig channel = config.Channels[0];
        BlockConfig mornings = channel.Blocks.Single(block => block.Name == "Archive mornings");
        mornings.GeneratedPromos = new GeneratedPromosConfig
        {
            Templates = [.. Enum.GetValues<GeneratedPromoType>().Select(type =>
            {
                GeneratedPromoTemplate template = GeneratedPromoTemplates.ForType(type);
                template.Name = $"Morning {template.Name}";
                template.Usage = GeneratedPromoUsage.Mixed;
                template.MixChance = 1;
                return template;
            })]
        };
        HashSet<string> morningTemplates = [.. mornings.GeneratedPromos.Templates.Select(template => template.Name)];

        ChannelSchedule schedule = await new SchedulingEngine(new FakeMediaLibrary(Library()), new InMemoryPlaybackStateStore())
            .BuildAsync(config, channel, Monday, Monday.AddDays(1));

        List<ScheduleEntry> generated = [.. schedule.Entries.Where(entry => entry.BlockName == mornings.Name && entry.GeneratedTemplate is not null)];
        Assert.NotEmpty(generated);
        foreach (ScheduleEntry entry in generated)
        {
            Assert.Contains(entry.GeneratedTemplate!, morningTemplates);

            // What the service does to render it: find the template again from the entry's block name.
            GeneratedPromoSource source = Assert.IsType<GeneratedPromoSource>(GeneratedPromoPlanner.Source(config, channel, entry.BlockName));
            Assert.Contains(source.Promos.Templates, template => template.Name == entry.GeneratedTemplate);
        }
    }

    [Fact]
    public async Task Promos_without_the_generated_promos_plugin_air_only_clips()
    {
        SchedulerConfig config = Config("generated-mixed");
        ChannelSchedule schedule = await new SchedulingEngine(new FakeMediaLibrary(Library()), new InMemoryPlaybackStateStore())
            {
                Modules = EngineModules.For([PlannerFeature.Promos])
            }
            .BuildAsync(config, config.Channels[0], Monday, Monday.AddDays(1));

        Assert.Contains(schedule.Entries, entry => PromoKinds.Contains(entry.Kind));
        Assert.All(schedule.Entries, entry => Assert.Null(entry.GeneratedTemplate));
        Assert.Contains(schedule.Diagnostics, diagnostic => diagnostic.Message.Contains("Generated promos plugin is off"));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Scenarios
    // ---------------------------------------------------------------------------------------------------------

    private SchedulerConfig Config(string scenario)
    {
        bool promos = scenario != "core-breaks-only";
        string placement = scenario == "promos-before-show" ? "before_show" : "any_break";
        bool clips = scenario != "generated-fallback";
        bool archive = scenario == "archive-follow";
        string follow = archive ? WriteArchive() : string.Empty;

        // Archive slots labelled "Morning" take this block's breaks and promos, whatever time they air.
        string archiveBlock = archive
            ? """
                - name: Archive mornings
                  follow_only: true
                  shows:
                  - include:
                    - show: SpongeBob
              """
            : string.Empty;

        string yaml = $$"""
            settings:
              time_zone: UTC
              seed: snapshot
            channels:
            - id: zone
              name: The Zone
              number: 7
              time_zone: UTC
              auto_temporal_tags: false
              pad_to_nearest: 30m
              max_break: 9m
              stack_to_fill: true
              mid_roll:
                mode: auto
                target_segment: 12m
                min_segment: 6m
                chapter_tolerance: 3m
                max_break_length: 3m
              base_rotation:
                include:
                - show: SpongeBob
                - show: Rugrats
                - show: Doug
                order: shuffle
              interstitials:
                max_shows_per_slot: 1
                show_chance: 100
                shows:
                  include:
                  - show: Oh Yeah
                  order: shuffle
                  max_duration: 8m
                commercials:
                  query: "tag:'Commercials'"
                  max_repeats_per_break: 1
                filler:
                  query: "tag:'Bumpers'"
                  trimmable: true
              promos:
                enabled: {{(promos ? "true" : "false")}}
                base_query: "tag:'Promo'"
                max_per_break: 2
                up_next:
                  placement: {{placement}}
                eventually:
                  chance: 1
              anchors:
              - name: Prime time
                time: "20:00"
                show:
                  include:
                  - show: Rocket Power
                  order: season_episode
              blocks:
              - name: Shorts hour
                time: "16:00"
                end_time: "17:00"
                shows:
                - include:
                  - show: Hey Arnold
                  order: season_episode
            {{archiveBlock}}
            {{follow}}
            """;

        SchedulerConfig config = SchedulerConfigLoader.Parse(yaml);
        ChannelConfig channel = config.Channels[0];
        if (scenario is "generated-fallback" or "generated-mixed" or "archive-follow")
        {
            // The snapshots predate We'll Be Right Back and New Episode, which have tests of their own.
            GeneratedPromoTemplate[] templates = [.. Enum.GetValues<GeneratedPromoType>()
                .Where(type => type is not (GeneratedPromoType.RightBack or GeneratedPromoType.NewEpisode))
                .Select(type => GeneratedPromoTemplates.ForType(type))];
            if (scenario == "generated-mixed")
            {
                foreach (GeneratedPromoTemplate template in templates)
                {
                    template.Usage = GeneratedPromoUsage.Mixed;
                    template.MixChance = 0.5;
                }
            }

            channel.GeneratedPromos = new GeneratedPromosConfig { Templates = [.. templates] };
        }

        if (!clips)
        {
            // Generated templates stand in wherever the library has nothing.
            channel.Promos!.BaseQuery = "tag:'No such promos'";
        }

        return config;
    }

    /// <summary>Two archived mornings, mapped onto a follow-only block, with one show the library doesn't have.</summary>
    private string WriteArchive()
    {
        foreach ((string date, int offset) in new[] { ("2002-09-28", 0), ("2002-09-29", 1) })
        {
            string folder = Path.Combine(_archive, "2002", "09");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, $"{date}.json"), $$"""
                {"schedule":[
                  {"time":"06:00","end_time":"06:30","show":"SpongeBob","episode":"Sponge {{3 + offset}}","blocks":["Morning"]},
                  {"time":"06:30","end_time":"07:00","show":"Rugrat","episode":"Rugrat {{5 + offset}}","blocks":["Morning"]},
                  {"time":"07:00","end_time":"07:30","show":"Hey Arnold","episode":"Arnold {{2 + offset}} / Arnold {{3 + offset}}","blocks":["Morning"]},
                  {"time":"07:30","end_time":"08:00","show":"Not In The Library","episode":"Lost","blocks":["Morning"]},
                  {"time":"08:00","end_time":"09:00","show":"Doug","episode":"Doug {{1 + offset}}","blocks":["Morning","Late"]}
                ]}
                """);
        }

        File.WriteAllText(Path.Combine(_archive, "_block_index.json"), """{"Morning":{"count":2},"Late":{"count":1}}""");

        string directory = _archive.Replace("'", "''");
        return $$"""
              follow:
                enabled: true
                directory: '{{directory}}'
                start_year: 2002
                end_year: 2002
                blocks:
                  Morning: Archive mornings
                shows:
                  Rugrat: Rugrats
            """;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Library
    // ---------------------------------------------------------------------------------------------------------

    private static List<MediaItem> Library()
    {
        List<MediaItem> items = [];
        for (int episode = 1; episode <= 12; episode++)
        {
            items.Add(Episode("SpongeBob", episode, TimeSpan.FromMinutes(22), $"Sponge {episode}", TimeSpan.FromMinutes(11)));
            items.Add(Episode("Rugrats", episode, TimeSpan.FromMinutes(23), $"Rugrat {episode}", TimeSpan.FromMinutes(12)));
            items.Add(Episode("Hey Arnold", episode, TimeSpan.FromMinutes(11), $"Arnold {episode}"));
            items.Add(Episode("Rocket Power", episode, TimeSpan.FromMinutes(22) + TimeSpan.FromSeconds(30), $"Rocket {episode}", TimeSpan.FromMinutes(10)));
            items.Add(Episode("Doug", episode, TimeSpan.FromMinutes(44), $"Doug {episode}", TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(29)));
            items.Add(Episode("Oh Yeah", episode, TimeSpan.FromMinutes(3 + episode % 5), $"Short {episode}"));
        }

        int[] adSeconds = [15, 30, 30, 60, 15, 45, 30, 20];
        for (int ad = 0; ad < 40; ad++)
        {
            items.Add(TestMedia.Filler($"ad-{ad:D2}", TimeSpan.FromSeconds(adSeconds[ad % adSeconds.Length]), "Commercials"));
        }

        for (int bumper = 0; bumper < 10; bumper++)
        {
            items.Add(TestMedia.Bumper($"bump-{bumper:D2}", TimeSpan.FromSeconds(5 + bumper)));
        }

        foreach (string show in new[] { "SpongeBob", "Rugrats", "Hey Arnold", "Rocket Power", "Doug" })
        {
            string slug = show.Replace(" ", "-").ToLowerInvariant();
            items.Add(TestMedia.Promo($"{slug}-up-next", TimeSpan.FromSeconds(10), show, "up_next"));
            items.Add(TestMedia.Promo($"{slug}-now-playing", TimeSpan.FromSeconds(5), show, "now_playing"));
            items.Add(TestMedia.Promo($"{slug}-this-has-been", TimeSpan.FromSeconds(5), show, "this_has_been"));
            items.Add(TestMedia.Promo($"{slug}-youre-watching", TimeSpan.FromSeconds(4), show, "youre_watching"));
            items.Add(TestMedia.Promo($"{slug}-back", TimeSpan.FromSeconds(3), show, "back_to_the_show"));
            items.Add(TestMedia.Promo($"{slug}-coming-up", TimeSpan.FromSeconds(15), show, "coming_up"));
            items.Add(TestMedia.Promo($"{slug}-later", TimeSpan.FromSeconds(20), show, "later_today"));
            items.Add(TestMedia.PromoAt($"{slug}-up-next-8pm", TimeSpan.FromSeconds(10), show, "up_next", "800pm"));
            items.Add(TestMedia.PromoOn($"{slug}-eventually", TimeSpan.FromSeconds(20), show, null, "800pm"));
        }

        return items;
    }

    private static MediaItem Episode(string show, int episode, TimeSpan duration, string title, params TimeSpan[] chapters) => new()
    {
        Id = $"{show.Replace(" ", "-").ToLowerInvariant()}-e{episode:D2}",
        Title = title,
        Kind = MediaKind.Episode,
        Duration = duration,
        SeriesId = show,
        SeriesName = show,
        SeasonNumber = 1,
        EpisodeNumber = episode,
        PremiereDate = new DateTimeOffset(1995, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(episode * 7),
        Chapters = chapters.Length == 0 ? [] : [TimeSpan.Zero, .. chapters]
    };

    // ---------------------------------------------------------------------------------------------------------
    // Rendering
    // ---------------------------------------------------------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string Render(ChannelSchedule first, ChannelSchedule second, RecordingStateStore state) =>
        JsonSerializer.Serialize(new
        {
            Days = new[] { Day(first), Day(second) },
            State = state.Snapshot()
        }, Json).Replace("\r\n", "\n");

    private static object Day(ChannelSchedule schedule) => new
    {
        Start = Time(schedule.Start),
        End = Time(schedule.End),
        schedule.ArchiveFollowEnabled,
        Padding = Span(schedule.TotalPadding),
        Entries = schedule.Entries.Select(Entry).ToArray(),
        ArchiveDays = schedule.ArchiveDays.Select(day => new
        {
            Date = day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Start = Time(day.Start),
            End = Time(day.End),
            Blocks = day.Blocks.Select(block => new
            {
                Start = Time(block.Start),
                End = Time(block.End),
                block.Labels,
                block.Show,
                block.Episode,
                OriginalStart = Span(block.OriginalStart),
                OriginalEnd = Span(block.OriginalEnd)
            })
        }),
        Diagnostics = schedule.Diagnostics.Select(diagnostic => $"{diagnostic.Level} {Time(diagnostic.At)} {diagnostic.Message}")
    };

    private static object Entry(ScheduleEntry entry) => new
    {
        Start = Time(entry.Start),
        Duration = Span(entry.Duration),
        Kind = entry.Kind.ToString(),
        Origin = entry.ProgramOrigin.ToString(),
        Item = entry.Item is { } item ? $"{item.Id} [{item.Kind}] {Span(item.Duration)} path={item.Path ?? "-"}" : null,
        entry.IsTrimmed,
        InPoint = Span(entry.InPoint),
        Segment = entry.SegmentCount == 0 ? null : $"{entry.SegmentNumber}/{entry.SegmentCount}",
        entry.StopBeforeEnd,
        SourceDuration = Span(entry.SourceDuration),
        entry.Source,
        entry.BlockName,
        entry.ContinuesSlot,
        entry.IsSlotIntro,
        entry.IsSlotOutro,
        entry.PromoTargetId,
        entry.GeneratedTemplate,
        GeneratedTargetAt = Time(entry.GeneratedTargetAt),
        entry.GeneratedSubject,
        Fallback = entry.Fallback?.Select(Entry).ToArray(),
        Annotations = entry.Annotations?.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}")
    };

    private static string? Time(DateTimeOffset? value) => value?.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    private static string? Span(TimeSpan? value) => value?.ToString("c", CultureInfo.InvariantCulture);

    /// <summary>Keeps every saved cursor so the snapshot also proves the same state is consumed in the same order.</summary>
    private sealed class RecordingStateStore : IPlaybackStateStore
    {
        private readonly Dictionary<string, PlaybackState> _states = new(StringComparer.Ordinal);

        public ValueTask<PlaybackState?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_states.TryGetValue(key, out PlaybackState? state) ? state.Clone() : null);

        public ValueTask SaveAsync(PlaybackState state, CancellationToken cancellationToken = default)
        {
            _states[state.Key] = state.Clone();
            return ValueTask.CompletedTask;
        }

        public ValueTask ResetAsync(string keyPrefix, CancellationToken cancellationToken = default)
        {
            foreach (string key in _states.Keys.Where(key => key.StartsWith(keyPrefix, StringComparison.Ordinal)).ToList())
            {
                _states.Remove(key);
            }

            return ValueTask.CompletedTask;
        }

        public object Snapshot() => _states.Values.OrderBy(state => state.Key, StringComparer.Ordinal).Select(Describe).ToArray();

        private static object Describe(PlaybackState state) => new
        {
            state.Key,
            state.Cursor,
            state.Bag,
            state.RandomState,
            state.PoolFingerprint,
            state.LastItemId,
            Titles = state.Titles?.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => new { Title = pair.Key, State = Describe(pair.Value) }),
            TitleCredits = state.TitleCredits?.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"{pair.Key}={pair.Value}")
        };
    }
}

/// <summary>Records a snapshot on first run and compares against it afterwards.</summary>
internal static class Snapshot
{
    public static void Verify(string name, string actual, [CallerFilePath] string caller = "")
    {
        string folder = Path.Combine(Path.GetDirectoryName(caller)!, "Snapshots");
        string expectedPath = Path.Combine(folder, $"{name}.txt");
        string receivedPath = Path.Combine(folder, $"{name}.received.txt");
        Directory.CreateDirectory(folder);
        bool update = Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") is "1" or "true";

        if (!File.Exists(expectedPath) || update)
        {
            Assert.False(!update && Environment.GetEnvironmentVariable("CI") is "true",
                $"Snapshot {name} is missing. Run the tests locally to record it and commit Snapshots/{name}.txt.");
            File.WriteAllText(expectedPath, actual, new UTF8Encoding(false));
            if (File.Exists(receivedPath))
            {
                File.Delete(receivedPath);
            }

            return;
        }

        string expected = File.ReadAllText(expectedPath).Replace("\r\n", "\n");
        if (expected == actual)
        {
            if (File.Exists(receivedPath))
            {
                File.Delete(receivedPath);
            }

            return;
        }

        File.WriteAllText(receivedPath, actual, new UTF8Encoding(false));
        string[] want = expected.Split('\n');
        string[] got = actual.Split('\n');
        int line = Enumerable.Range(0, Math.Min(want.Length, got.Length)).FirstOrDefault(index => want[index] != got[index], -1);
        if (line < 0)
        {
            line = Math.Min(want.Length, got.Length);
        }

        Assert.Fail($"""
            Engine output for '{name}' differs from Snapshots/{name}.txt at line {line + 1}.
              expected: {(line < want.Length ? want[line].Trim() : "<end>")}
              actual:   {(line < got.Length ? got[line].Trim() : "<end>")}
            The full output is in Snapshots/{name}.received.txt. If the change is intended, run with UPDATE_SNAPSHOTS=1.
            """);
    }
}
