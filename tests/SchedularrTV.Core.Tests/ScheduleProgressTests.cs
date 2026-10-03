using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Publishing;
using SchedularrTV.Core.Query;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Service;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class ScheduleProgressTests
{
    [Fact]
    public async Task Automatic_config_save_trigger_uses_selective_rebuilding()
    {
        using var fixture = new Fixture(new ControlledLibrary(false));
        fixture.Save(config => config.Settings.RebuildInterval = TimeSpan.FromDays(1));
        using var service = fixture.CreateRebuildService();
        try
        {
            await service.StartAsync(CancellationToken.None);
            var initial = await fixture.Publisher.Published.Task.WaitAsync(TimeSpan.FromSeconds(10));
            fixture.Publisher.Published = new(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Save(config => config.Channels[0].MaxBreak = TimeSpan.FromMinutes(2));
            var changed = await fixture.Publisher.Published.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotSame(initial[0], changed[0]);
            Assert.Same(initial[1], changed[1]);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Saving_one_channel_only_builds_that_channel_and_publishes_the_complete_lineup()
    {
        var library = new ControlledLibrary(false);
        using var fixture = new Fixture(library);
        IReadOnlyList<ChannelSchedule> initial = await fixture.Coordinator.RebuildAsync(true, true, CancellationToken.None);
        library.Queries.Clear();
        var updates = new List<ScheduleRebuildProgress>();
        fixture.Coordinator.StateChanged += () =>
        {
            if (fixture.Coordinator.Progress is { } progress) updates.Add(progress);
        };
        fixture.Save(config => config.Channels[0].MaxBreak = TimeSpan.FromMinutes(2));

        IReadOnlyList<ChannelSchedule> changed = await fixture.Coordinator.RebuildAsync(true, true, CancellationToken.None, onlyChanged: true);

        Assert.NotSame(initial[0], changed[0]);
        Assert.Same(initial[1], changed[1]);
        Assert.NotEmpty(library.Queries);
        Assert.DoesNotContain(library.Queries, query => query.Contains("Second", StringComparison.Ordinal));
        Assert.All(updates, progress => Assert.Equal(1, progress.TotalChannels));
        Assert.Equal(2, fixture.Publisher.Calls);
        Assert.Equal(changed, fixture.Publisher.Latest);

        library.Queries.Clear();
        fixture.Save(_ => { });
        Assert.Same(changed, await fixture.Coordinator.RebuildAsync(true, true, CancellationToken.None, onlyChanged: true));
        Assert.Empty(library.Queries);
        Assert.Equal(2, fixture.Publisher.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Collection_changes_only_rebuild_dependent_channels_including_nested_references(bool inBlock)
    {
        var library = new ControlledLibrary(false);
        using var fixture = new Fixture(library);
        fixture.Save(config =>
        {
            config.GlobalCollections["shared"] = new ContentSelector { Include = [new ContentInclude { Show = "First" }] };
            config.GlobalCollections["alias"] = new ContentSelector { Collection = "shared" };
            if (inBlock)
            {
                config.Channels[0].Blocks.Add(new BlockConfig
                {
                    Name = "Archive only", FollowOnly = true,
                    InterstitialShows = new ContentSelector { Collection = "alias" }
                });
            }
            else config.Channels[0].BaseRotation = new ContentSelector { Collection = "alias" };
        });
        var initial = await fixture.Coordinator.RebuildAsync(true, true, CancellationToken.None, onlyChanged: true);
        fixture.Save(config => config.GlobalCollections["shared"].Order = PlaybackOrder.Random);
        var changed = await fixture.Coordinator.RebuildAsync(true, true, CancellationToken.None, onlyChanged: true);
        Assert.NotSame(initial[0], changed[0]);
        Assert.Same(initial[1], changed[1]);

        library.Queries.Clear();
        fixture.Save(config => config.GlobalCollections["unused"] = new ContentSelector { Query = "*" });
        Assert.Same(changed, await fixture.Coordinator.RebuildAsync(true, true, CancellationToken.None, onlyChanged: true));
        Assert.Empty(library.Queries);
        Assert.Equal(2, fixture.Publisher.Calls);
    }

    [Fact]
    public async Task Timer_that_observes_a_saved_edit_replans_it_before_recording_the_inputs()
    {
        var library = new ControlledLibrary(false);
        using var fixture = new Fixture(library);
        await fixture.Coordinator.RebuildAsync(false, true, CancellationToken.None);
        fixture.Save(config => config.Channels[0].BaseRotation = new ContentSelector { Include = [new ContentInclude { Show = "Second" }] });
        var updated = await fixture.Coordinator.RebuildAsync(false, false, CancellationToken.None);
        Assert.Contains(updated[0].Entries, entry => entry.Item?.SeriesName == "Second");
        Assert.Same(updated, await fixture.Coordinator.RebuildAsync(false, true, CancellationToken.None, onlyChanged: true));
    }

    [Fact]
    public async Task Shared_settings_rebuild_all_channels_and_removing_a_channel_only_republishes()
    {
        var library = new ControlledLibrary(false);
        using var fixture = new Fixture(library);
        var initial = await fixture.Coordinator.RebuildAsync(true, true, CancellationToken.None);
        fixture.Save(config => config.Settings.Seed = "new seed");
        var rebuilt = await fixture.Coordinator.RebuildAsync(true, true, CancellationToken.None, onlyChanged: true);
        Assert.NotSame(initial[0], rebuilt[0]);
        Assert.NotSame(initial[1], rebuilt[1]);

        library.Queries.Clear();
        fixture.Save(config => config.Channels.RemoveAt(1));
        var remaining = await fixture.Coordinator.RebuildAsync(true, true, CancellationToken.None, onlyChanged: true);
        Assert.Same(rebuilt[0], Assert.Single(remaining));
        Assert.Empty(library.Queries);
        Assert.Equal(3, fixture.Publisher.Calls);
        Assert.Equal(remaining, fixture.Publisher.Latest);
    }

    [Fact]
    public async Task Failed_build_keeps_its_old_plan_and_retries_without_rebuilding_successful_neighbors()
    {
        var library = new ControlledLibrary(false);
        using var fixture = new Fixture(library);
        var initial = await fixture.Coordinator.RebuildAsync(false, true, CancellationToken.None);
        fixture.Save(config => config.Channels[1].MaxBreak = TimeSpan.FromMinutes(2));
        library.FailSecond = true;
        var failed = await fixture.Coordinator.RebuildAsync(false, true, CancellationToken.None, onlyChanged: true);
        Assert.Same(initial[0], failed[0]);
        Assert.Same(initial[1], failed[1]);
        Assert.Single(fixture.Coordinator.Failures);

        library.FailSecond = false;
        library.Queries.Clear();
        var retried = await fixture.Coordinator.RebuildAsync(false, true, CancellationToken.None, onlyChanged: true);
        Assert.Same(initial[0], retried[0]);
        Assert.NotSame(initial[1], retried[1]);
        Assert.Empty(fixture.Coordinator.Failures);
        Assert.DoesNotContain(library.Queries, query => query.Contains("First", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Saves_during_a_build_are_compared_with_the_inputs_that_actually_built_each_channel()
    {
        var library = new ControlledLibrary(false);
        using var fixture = new Fixture(library);
        var initial = await fixture.Coordinator.RebuildAsync(false, true, CancellationToken.None);
        fixture.Save(config => config.Channels[0].MaxBreak = TimeSpan.FromMinutes(2));
        library.Pause();
        var active = fixture.Coordinator.RebuildAsync(false, true, CancellationToken.None, onlyChanged: true);
        await library.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Save(config => config.Channels[1].MaxBreak = TimeSpan.FromMinutes(3));
        var queued = fixture.Coordinator.RebuildAsync(false, true, CancellationToken.None, onlyChanged: true);
        library.Release.TrySetResult();
        var firstSave = await active;
        var secondSave = await queued;
        Assert.NotSame(initial[0], firstSave[0]);
        Assert.Same(initial[1], firstSave[1]);
        Assert.Same(firstSave[0], secondSave[0]);
        Assert.NotSame(firstSave[1], secondSave[1]);
        Assert.Same(secondSave, await fixture.Coordinator.RebuildAsync(false, true, CancellationToken.None, onlyChanged: true));
    }

    [Fact]
    public async Task New_channels_build_and_manual_rebuilds_still_force_all_channels()
    {
        var library = new ControlledLibrary(false);
        using var fixture = new Fixture(library);
        var initial = await fixture.Coordinator.RebuildAsync(false, true, CancellationToken.None, onlyChanged: true);
        fixture.Save(config => config.Channels.Add(new ChannelConfig
        {
            Id = "third", Name = "Third", Number = 3, BaseRotation = new ContentSelector { Query = "*" }
        }));
        var added = await fixture.Coordinator.RebuildAsync(false, true, CancellationToken.None, onlyChanged: true);
        Assert.Equal(3, added.Count);
        Assert.Same(initial[0], added[0]);
        Assert.Same(initial[1], added[1]);
        var forced = await fixture.Coordinator.RebuildAsync(false, true, CancellationToken.None);
        Assert.All(Enumerable.Range(0, 3), index => Assert.NotSame(added[index], forced[index]));
    }

    [Fact]
    public async Task Routine_runs_extend_unchanged_channels_and_selective_runs_recover_expired_plans()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 9, 24, 6, 0, 0, TimeSpan.Zero));
        using var fixture = new Fixture(new ControlledLibrary(false), clock: clock);
        var initial = await fixture.Coordinator.RebuildAsync(false, true, CancellationToken.None);
        clock.Now += TimeSpan.FromMinutes(30);
        var extended = await fixture.Coordinator.RebuildAsync(false, false, CancellationToken.None);
        Assert.All(Enumerable.Range(0, 2), index =>
        {
            Assert.True(extended[index].End > initial[index].End);
            Assert.Equal(initial[index].Entries.Where(entry => entry.End > clock.Now),
                extended[index].Entries.Where(entry => entry.Start < initial[index].End));
        });
        clock.Now += TimeSpan.FromHours(2);
        var recovered = await fixture.Coordinator.RebuildAsync(false, true, CancellationToken.None, onlyChanged: true);
        Assert.All(recovered, schedule => Assert.True(schedule.End > clock.Now));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Progress_tracks_channels_schedule_percentage_and_publication(bool failSecond)
    {
        using var fixture = new Fixture(new ControlledLibrary(failSecond));
        List<ScheduleRebuildProgress> updates = [];
        fixture.Coordinator.StateChanged += () =>
        {
            if (fixture.Coordinator.Progress is { } progress) updates.Add(progress);
        };

        await fixture.Coordinator.RebuildAsync(true, true, CancellationToken.None);

        Assert.Contains(updates, progress => progress.ChannelName == "First" && progress.CompletedChannels == 0);
        Assert.Contains(updates, progress => progress.ChannelName == "Second" && progress.CompletedChannels == 1);
        Assert.Contains(updates, progress => progress.Stage == "Scheduling programmes" && progress.ChannelPercent is > 0 and < 100);
        Assert.Contains(updates, progress => progress.Stage == "Publishing: Test publisher" && progress.CompletedChannels == 2);
        Assert.Equal(updates.Select(progress => progress.Percent).Order(), updates.Select(progress => progress.Percent));
        Assert.All(updates, progress => Assert.Equal(2, progress.TotalChannels));
        Assert.Equal(failSecond ? 1 : 0, fixture.Coordinator.Failures.Count);
        Assert.False(fixture.Coordinator.IsBuilding);
        Assert.Null(fixture.Coordinator.Progress);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelling_a_queued_build_preserves_active_progress_and_cleans_up(bool singleChannel)
    {
        var library = new ControlledLibrary(false, paused: true);
        using var fixture = new Fixture(library);
        Task active = fixture.Coordinator.RebuildAsync(false, true, CancellationToken.None);
        await library.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ScheduleRebuildProgress? progress = fixture.Coordinator.Progress;
        Assert.NotNull(progress);
        using var cancellation = new CancellationTokenSource();
        Task queued = singleChannel
            ? fixture.Coordinator.RebuildChannelAsync("first", cancellation.Token)
            : fixture.Coordinator.RebuildAsync(false, true, cancellation.Token);
        try
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.True(fixture.Coordinator.IsBuilding);
            Assert.Equal(progress, fixture.Coordinator.Progress);
        }
        finally
        {
            library.Release.TrySetResult();
            await active;
        }
        Assert.False(fixture.Coordinator.IsBuilding);
        Assert.Null(fixture.Coordinator.Progress);
    }

    [Fact]
    public async Task Single_channel_rebuild_reports_one_channel_and_clears_progress()
    {
        using var fixture = new Fixture(new ControlledLibrary(false));
        List<ScheduleRebuildProgress> updates = [];
        fixture.Coordinator.StateChanged += () =>
        {
            if (fixture.Coordinator.Progress is { } progress) updates.Add(progress);
        };
        Assert.Null(await fixture.Coordinator.RebuildChannelAsync("second", CancellationToken.None));
        Assert.All(updates, progress => Assert.Equal(1, progress.TotalChannels));
        Assert.Contains(updates, progress => progress.ChannelName == "Second" && progress.ChannelPercent > 0);
        Assert.Contains(updates, progress => progress.CompletedChannels == 1);
        Assert.False(fixture.Coordinator.IsBuilding);
        Assert.Null(fixture.Coordinator.Progress);
    }

    [Fact]
    public async Task Rolling_horizon_finishes_grid_slots_instead_of_scheduling_tiny_episodes_at_the_tail()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 9, 23, 6, 16, 17, TimeSpan.Zero));
        var config = new SchedulerConfig();
        config.Settings.Horizon = TimeSpan.FromHours(12);
        config.Settings.TimeZone = "UTC";
        config.Settings.Promos.Enabled = false;
        config.Channels = [new()
        {
            Id = "first", Name = "First", Number = 1, PadToNearest = TimeSpan.FromMinutes(30),
            StackToFill = true, MidRoll = new() { Mode = MidRollMode.Auto },
            BaseRotation = new() { Query = "kind == 'Episode'", Order = PlaybackOrder.Chronological }
        }];
        var library = new FakeMediaLibrary([
            TestMedia.Episode("First", 1, 1, TimeSpan.FromMinutes(22)),
            TestMedia.Episode("Tiny", 1, 1, TimeSpan.FromSeconds(46))]);
        ChannelSchedule truncated = await new ScheduleBuilder(library, new InMemoryPlaybackStateStore())
            .BuildChannelAsync(config, config.Channels[0], new DateTimeOffset(2026, 9, 23, 6, 0, 0, TimeSpan.Zero), clock.Now + config.Settings.Horizon);
        Assert.True(truncated.Entries.Count(entry => entry.Start >= new DateTimeOffset(2026, 9, 23, 18, 0, 0, TimeSpan.Zero)
            && entry.Kind == ScheduleEntryKind.Program && entry.Item?.SeriesName == "Tiny") > 2);
        using var fixture = new Fixture(library, config, clock);

        ChannelSchedule initial = Assert.Single(await fixture.Coordinator.RebuildAsync(false, true, CancellationToken.None));
        Assert.Equal(new DateTimeOffset(2026, 9, 23, 18, 30, 0, TimeSpan.Zero), initial.End);
        Assert.Equal(initial.End, initial.Entries[^1].End);
        Assert.All(initial.Entries.Where(entry => entry.Kind == ScheduleEntryKind.Program && entry.SegmentNumber <= 1),
            entry => Assert.Equal(0, entry.Start.TimeOfDay.Ticks % TimeSpan.FromMinutes(30).Ticks));

        clock.Now = clock.Now.AddMinutes(30);
        ChannelSchedule extended = Assert.Single(await fixture.Coordinator.RebuildAsync(false, false, CancellationToken.None));
        Assert.Equal(initial.Entries.Where(entry => entry.End > clock.Now),
            extended.Entries.Where(entry => entry.Start < initial.End));
        Assert.Equal(new DateTimeOffset(2026, 9, 23, 19, 0, 0, TimeSpan.Zero), extended.End);
        Assert.Equal(initial.End, extended.Entries.First(entry => entry.Start >= initial.End).Start);

        clock.Now = clock.Now.AddMinutes(1);
        ChannelSchedule replanned = Assert.Single(await fixture.Coordinator.RebuildAsync(false, true, CancellationToken.None));
        var boundary = new DateTimeOffset(2026, 9, 23, 7, 0, 0, TimeSpan.Zero);
        Assert.Equal(extended.Entries.Where(entry => entry.End > clock.Now && entry.Start < boundary),
            replanned.Entries.Where(entry => entry.Start < boundary));
        Assert.Equal(boundary, replanned.Entries.First(entry => entry.Start >= boundary).Start);
    }

    [Theory]
    [InlineData(0, true, 0, 0)]
    [InlineData(16, true, 0, 30)]
    [InlineData(16, true, 10, 40)]
    [InlineData(16, false, 0, 16)]
    public async Task Horizon_rounding_respects_exact_boundaries_custom_origin_and_ungridded_channels(
        int minute, bool grid, int originMinute, int expectedMinute)
    {
        var clock = new TestClock(new DateTimeOffset(2026, 9, 23, 6, minute, 0, TimeSpan.Zero));
        var config = new SchedulerConfig();
        config.Settings.Horizon = TimeSpan.FromHours(1);
        config.Settings.TimeZone = "UTC";
        config.Channels = [new()
        {
            Id = "first", Name = "First", Number = 1,
            PadToNearest = grid ? TimeSpan.FromMinutes(30) : null,
            ScheduleStart = new DateTimeOffset(2026, 9, 23, 0, originMinute, 0, TimeSpan.Zero),
            BaseRotation = new() { Query = "kind == 'Episode'" }
        }];
        using var fixture = new Fixture(new FakeMediaLibrary([
            TestMedia.Episode("First", 1, 1, TimeSpan.FromMinutes(22))]), config, clock);
        ChannelSchedule schedule = Assert.Single(await fixture.Coordinator.RebuildAsync(false, true, CancellationToken.None));
        Assert.Equal(new DateTimeOffset(2026, 9, 23, 7, expectedMinute, 0, TimeSpan.Zero), schedule.End);
        Assert.Equal(schedule.End, schedule.Entries[^1].End);
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"schedule-progress-{Guid.NewGuid():N}");
        private readonly SchedulerConfigProvider _provider;
        public ScheduleCoordinator Coordinator { get; }
        public TestPublisher Publisher { get; } = new();

        public ScheduleRebuildService CreateRebuildService() => new(Coordinator, _provider,
            Options.Create(new SchedulerServiceOptions { AutoRebuild = true }), NullLogger<ScheduleRebuildService>.Instance);

        public void Save(Action<SchedulerConfig> edit)
        {
            SchedulerConfig config = SchedulerConfigLoader.Parse(_provider.ReadRaw());
            edit(config);
            Assert.True(_provider.Save(SchedulerConfigLoader.Serialize(config)).IsValid);
        }

        public Fixture(IMediaLibrary library, SchedulerConfig? configuration = null, TimeProvider? clock = null)
        {
            _provider = new SchedulerConfigProvider(Options.Create(new SchedulerServiceOptions
            {
                ConfigPath = Path.Combine(_directory, "schedule.yaml"), WatchConfig = false, AutoRebuild = false
            }), NullLogger<SchedulerConfigProvider>.Instance);
            var config = new SchedulerConfig();
            config.Settings.Horizon = TimeSpan.FromHours(1);
            config.Settings.TimeZone = "UTC";
            config.Channels = [Channel("first", "First", 1), Channel("second", "Second", 2)];
            config = configuration ?? config;
            Assert.True(_provider.Save(SchedulerConfigLoader.Serialize(config)).IsValid);
            foreach (ChannelConfig channel in config.Channels)
            {
                _provider.Current.Channels.Single(saved => saved.Id == channel.Id).ScheduleStart = channel.ScheduleStart;
            }
            Coordinator = new ScheduleCoordinator(_provider, new ScheduleBuilder(library, new InMemoryPlaybackStateStore()),
                library, [Publisher], NullLogger<ScheduleCoordinator>.Instance,
                NullLoggerFactory.Instance, clock ?? TimeProvider.System);
        }

        private static ChannelConfig Channel(string id, string name, int number) => new()
        {
            Id = id, Name = name, Number = number,
            BaseRotation = new() { Include = [new ContentInclude { Show = name }] }
        };

        public void Dispose()
        {
            _provider.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    private sealed class ControlledLibrary(bool failSecond, bool paused = false) : IMediaLibrary
    {
        public List<string> Queries { get; } = [];
        public bool FailSecond { get; set; } = failSecond;
        private bool _paused = paused;
        public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Pause()
        {
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _paused = true;
        }

        public async ValueTask<IReadOnlyList<MediaItem>> SearchAsync(QueryExpression expression, CancellationToken cancellationToken = default)
        {
            Queries.Add(expression.ToQueryString());
            Entered.TrySetResult();
            if (_paused) await Release.Task.WaitAsync(cancellationToken);
            if (FailSecond && expression.ToQueryString().Contains("Second")) throw new IOException("Test library failure");
            return QueryEvaluator.Filter([
                TestMedia.Episode("First", 1, 1, TimeSpan.FromMinutes(1)),
                TestMedia.Episode("Second", 1, 1, TimeSpan.FromMinutes(1))], expression);
        }

        public ValueTask RefreshAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class TestPublisher : IPlayoutPublisher
    {
        public string Name => "Test publisher";
        public int Calls { get; private set; }
        public IReadOnlyList<ChannelSchedule> Latest { get; private set; } = [];
        public TaskCompletionSource<IReadOnlyList<ChannelSchedule>> Published { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task PublishAsync(IReadOnlyList<ChannelSchedule> schedules, CancellationToken cancellationToken = default)
        {
            Calls++;
            Latest = schedules;
            Published.TrySetResult(schedules);
            return Task.CompletedTask;
        }
    }
}