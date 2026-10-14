using System.Collections.Concurrent;
using System.Text.Json;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Publishing;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Plugins.Playout;

namespace SchedularrTV.Service;

/// <summary>Why a channel's last build failed, so the UI can say so and offer a retry.</summary>
public sealed record ChannelBuildFailure(string ChannelId, string ChannelName, string Message, DateTimeOffset At);

public sealed record ScheduleRebuildProgress(int CompletedChannels, int TotalChannels, string? ChannelName, string Stage, int ChannelPercent)
{
    public double Percent => TotalChannels == 0 ? 0 : Math.Clamp((CompletedChannels + ChannelPercent / 100.0) / TotalChannels * 100, 0, 100);
}

/// <summary>
/// Coordinates schedule builds and publication, and caches the most recent result so the API can
/// serve the exact playout that was handed to ErsatzTV rather than rebuilding on every request.
/// </summary>
public sealed class ScheduleCoordinator(
    SchedulerConfigProvider configProvider,
    ScheduleBuilder builder,
    IMediaLibrary library,
    IEnumerable<IPlayoutPublisher> publishers,
    ILogger<ScheduleCoordinator> logger,
    ILoggerFactory loggerFactory,
    TimeProvider timeProvider,
    IEnumerable<IScheduleFinisher>? finishers = null,
    IEnumerable<IPlayoutProcessor>? processors = null,
    IEnumerable<IExternalChannelSource>? externalSources = null)
{
    // Read on each use, not kept from the constructor: the lists leave out plugins switched off while running.
    private IReadOnlyList<IScheduleFinisher> _finishers => [.. finishers ?? []];

    // Each finisher sees the previous one's result; unchanged schedules keep their identity throughout.
    private IReadOnlyList<ChannelSchedule> Finish(SchedulerConfig config, IReadOnlyList<ChannelSchedule> schedules, DateTimeOffset now) =>
        _finishers.Aggregate(schedules, (current, finisher) => finisher.Prepare(config, current, now));

    private IReadOnlyList<IPlayoutProcessor> _processors =>
        [.. (processors ?? []).OrderBy(processor => processor.Order).ThenBy(processor => processor.Name, StringComparer.Ordinal)];

    private readonly SemaphoreSlim _buildLock = new(1, 1);

    // Builds run for minutes; renders landing meanwhile must still reach the playout before they air,
    // so only replacing and publishing the latest schedules is serialised against them.
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private readonly ConcurrentDictionary<string, ChannelBuildFailure> _failures = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ChannelSchedule> _latest = [];
    private Dictionary<string, string> _builtInputs = new(StringComparer.OrdinalIgnoreCase);
    private int _building;
    private ScheduleRebuildProgress? _progress;

    public ScheduleRebuildProgress? Progress => Volatile.Read(ref _progress);

    private void ReportProgress(ScheduleRebuildProgress? progress)
    {
        Volatile.Write(ref _progress, progress);
        StateChanged?.Invoke();
    }

    /// <summary>Raised when a build starts or finishes, so a UI can follow along.</summary>
    public event Action? StateChanged;

    /// <summary>True while any build is in flight, including one waiting its turn behind another.</summary>
    public bool IsBuilding => Volatile.Read(ref _building) > 0;

    public DateTimeOffset? LastBuiltAt { get; private set; }

    public IReadOnlyList<ChannelSchedule> Latest => _latest;

    private IReadOnlyList<ExternalChannel> _external = [];

    /// <summary>The plugins' whole channels (IPTV and the like) as last published, for the Channels page and dashboard.</summary>
    public IReadOnlyList<ExternalChannel> External => _external;

    /// <summary>Channels whose last build failed, keyed by channel id.</summary>
    public IReadOnlyDictionary<string, ChannelBuildFailure> Failures => _failures;

    /// <summary>
    /// Rebuilds a single channel, leaving every other channel's plan alone. Used to retry a channel
    /// that failed once whatever broke it has been fixed.
    /// </summary>
    public async Task<ChannelBuildFailure?> RebuildChannelAsync(string channelId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _building);
        StateChanged?.Invoke();

        bool lockTaken = false;
        try
        {
            await _buildLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            lockTaken = true;
            SchedulerConfig config = configProvider.Current;

            ChannelConfig? channel = config.Channels
                .FirstOrDefault(c => string.Equals(c.Id, channelId, StringComparison.OrdinalIgnoreCase));

            if (channel is null)
            {
                return new ChannelBuildFailure(channelId, channelId, "No such channel.", timeProvider.GetUtcNow());
            }

            DateTimeOffset now = timeProvider.GetUtcNow();
            string inputs = BuildInputs(config, channel);

            ReportProgress(new ScheduleRebuildProgress(0, 1, channel.Name, "Preparing channel", 0));

            try
            {
                ChannelSchedule rebuilt = await BuildChannelAsync(config, channel, now, replan: true, cancellationToken)
                    .ConfigureAwait(false);

                await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    _latest =
                    [
                        .. _latest.Where(s => !string.Equals(s.ChannelId, channel.Id, StringComparison.OrdinalIgnoreCase)),
                        rebuilt
                    ];
                    _latest = Finish(config, _latest, now);

                    _failures.TryRemove(channel.Id, out _);
                    _builtInputs[channel.Id] = inputs;
                    LastBuiltAt = now;

                    ReportProgress(new ScheduleRebuildProgress(1, 1, null, "Publishing", 0));
                    await PublishAsync(_latest, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _publishLock.Release();
                }

                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Channel {ChannelId} failed to rebuild: {Message}", channel.Id, ex.Message);

                var failure = new ChannelBuildFailure(channel.Id, channel.Name, Describe(ex), now);
                _failures[channel.Id] = failure;
                return failure;
            }
        }
        finally
        {
            if (lockTaken)
            {
                ReportProgress(null);
                _buildLock.Release();
            }
            Interlocked.Decrement(ref _building);
            StateChanged?.Invoke();
        }
    }

    // Inner exceptions carry the useful part; the outer one is usually just "an error occurred".
    private static string Describe(Exception ex) =>
        ex.InnerException is { } inner ? $"{ex.Message} ({inner.Message})" : ex.Message;

    /// <param name="replan">
    /// Discards the future half of the previous plan and schedules it again. Off, only the stretch
    /// past the old horizon is added, which is what keeps a published guide from being rewritten.
    /// </param>
    public async Task<IReadOnlyList<ChannelSchedule>> RebuildAsync(
        bool publish,
        bool replan,
        CancellationToken cancellationToken,
        bool onlyChanged = false)
    {
        // Counted rather than a bool: builds queue on the lock, and the first to finish must not
        // report the whole run as complete.
        Interlocked.Increment(ref _building);
        StateChanged?.Invoke();

        try
        {
            return await BuildAsync(publish, replan, cancellationToken, onlyChanged).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _building);
            StateChanged?.Invoke();
        }
    }

    private async Task<IReadOnlyList<ChannelSchedule>> BuildAsync(
        bool publish,
        bool replan,
        CancellationToken cancellationToken,
        bool onlyChanged)
    {
        await _buildLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SchedulerConfig config = configProvider.Current;
            ConfigValidationResult validation = configProvider.Validation;

            if (!validation.IsValid)
            {
                logger.LogError("Refusing to build: configuration is invalid ({Count} error(s)).", validation.Errors.Count);
                return _latest;
            }

            DateTimeOffset now = timeProvider.GetUtcNow();
            List<ChannelSchedule> schedules = [];
            var previousById = _latest.ToDictionary(schedule => schedule.ChannelId, StringComparer.OrdinalIgnoreCase);
            var inputs = config.Channels.ToDictionary(channel => channel.Id, channel => BuildInputs(config, channel), StringComparer.OrdinalIgnoreCase);
            var affected = config.Channels.Where(channel => !onlyChanged
                || !previousById.TryGetValue(channel.Id, out ChannelSchedule? previous) || previous.End <= now
                || _failures.ContainsKey(channel.Id)
                || !_builtInputs.TryGetValue(channel.Id, out string? built) || built != inputs[channel.Id])
                .Select(channel => channel.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var builtInputs = new Dictionary<string, string>(_builtInputs, StringComparer.OrdinalIgnoreCase);
            bool lineupChanged = !previousById.Keys.SequenceEqual(inputs.Keys, StringComparer.OrdinalIgnoreCase);
            string pluginInputs = PluginInputs(config);
            if (onlyChanged && affected.Count == 0 && !lineupChanged)
            {
                // Plugin sections only change what processors send to the outputs, so they republish without replanning.
                if (publish && pluginInputs != _pluginInputs)
                {
                    await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        _pluginInputs = pluginInputs;
                        logger.LogInformation("Plugin settings changed; republishing without rebuilding.");
                        await PublishAsync(_latest, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        _publishLock.Release();
                    }

                    return _latest;
                }

                logger.LogInformation("Schedule inputs unchanged; skipping rebuild and publication.");
                return _latest;
            }

            _pluginInputs = pluginInputs;
            int completed = 0;

            foreach (ChannelConfig channel in config.Channels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!affected.Contains(channel.Id))
                {
                    schedules.Add(previousById[channel.Id]);
                    continue;
                }
                ReportProgress(new ScheduleRebuildProgress(completed, affected.Count, channel.Name, "Preparing channel", 0));

                try
                {
                    bool channelReplan = replan || !_builtInputs.TryGetValue(channel.Id, out string? previousInputs)
                        || previousInputs != inputs[channel.Id];
                    schedules.Add(await BuildChannelAsync(config, channel, now, channelReplan, cancellationToken).ConfigureAwait(false));
                    builtInputs[channel.Id] = inputs[channel.Id];
                    _failures.TryRemove(channel.Id, out _);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One channel must not cost every other channel its playout: a renamed file or a
                    // collection that no longer resolves would otherwise take the whole build down.
                    logger.LogError(ex, "Channel {ChannelId} failed to build: {Message}", channel.Id, ex.Message);

                    _failures[channel.Id] = new ChannelBuildFailure(channel.Id, channel.Name, Describe(ex), now);

                    if (_latest.FirstOrDefault(s => string.Equals(s.ChannelId, channel.Id, StringComparison.OrdinalIgnoreCase)) is { } previous)
                    {
                        logger.LogWarning("Keeping the previous playout for {ChannelId}.", channel.Id);
                        schedules.Add(previous);
                    }
                }
                completed++;
                ReportProgress(new ScheduleRebuildProgress(completed, affected.Count, null, "Channel complete", 0));
            }

            await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                IReadOnlyList<ChannelSchedule> prepared = Finish(config, schedules, now);
                _latest = prepared;
                _builtInputs = builtInputs.Where(pair => inputs.ContainsKey(pair.Key)).ToDictionary(StringComparer.OrdinalIgnoreCase);
                foreach (string removed in _failures.Keys.Where(id => !inputs.ContainsKey(id))) _failures.TryRemove(removed, out _);
                LastBuiltAt = now;

                foreach (ChannelSchedule schedule in prepared.Where(schedule => affected.Contains(schedule.ChannelId)))
                {
                    logger.LogInformation(
                        "Built {Entries} entries for {ChannelName} ({Padding:g} padding, {Warnings} warning(s)).",
                        schedule.Entries.Count,
                        schedule.ChannelName,
                        schedule.TotalPadding,
                        schedule.Diagnostics.Count(d => d.Level >= DiagnosticLevel.Warning));
                }

                if (publish)
                {
                    await PublishAsync(prepared, cancellationToken).ConfigureAwait(false);
                }

                return prepared;
            }
            finally
            {
                _publishLock.Release();
            }
        }
        finally
        {
            ReportProgress(null);
            _buildLock.Release();
        }
    }

    /// <summary>Runs the finishers again (e.g. to attach newly rendered promo files) and republishes what changed.</summary>
    public async Task RefreshFinishedSchedulesAsync(CancellationToken cancellationToken)
    {
        if (_finishers.Count == 0)
        {
            return;
        }

        await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<ChannelSchedule> prepared = Finish(configProvider.Current, _latest, timeProvider.GetUtcNow());
            if (prepared.SequenceEqual(_latest, ReferenceEqualityComparer.Instance))
            {
                return;
            }

            _latest = prepared;
            await PublishAsync(prepared, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _publishLock.Release();
        }

        StateChanged?.Invoke();
    }

    /// <summary>
    /// Plugin sections are left out of a channel's build fingerprint: the planner never reads them, so changing
    /// one must not replan (and rewrite) the guide. They're fingerprinted separately to trigger a republish.
    /// </summary>
    private static readonly JsonSerializerOptions InputsJson = new()
    {
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                info =>
                {
                    if (info.Type == typeof(SchedulerSettings) || info.Type == typeof(ChannelConfig) || info.Type == typeof(BlockConfig))
                    {
                        for (int index = info.Properties.Count - 1; index >= 0; index--)
                        {
                            if (info.Properties[index].Name == nameof(ChannelConfig.Plugins))
                            {
                                info.Properties.RemoveAt(index);
                            }
                        }
                    }
                }
            }
        }
    };

    private string? _pluginInputs;

    private static string PluginInputs(SchedulerConfig config) => JsonSerializer.Serialize(new
    {
        Global = config.Settings.Plugins,
        Channels = config.Channels.Select(channel => new { channel.Id, channel.Plugins, Blocks = channel.Blocks.Select(block => new { block.Name, block.Plugins }) })
    });

    private static string BuildInputs(SchedulerConfig config, ChannelConfig channel)
    {
        // The ad breaks the channel and its blocks use: editing one rebuilds just the channels that use it.
        SortedDictionary<string, AdBreakConfig> adBreaks = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in (channel.AdBreaks ?? []).Concat(channel.Blocks.SelectMany(block => block.AdBreaks ?? [])))
        {
            if (config.AdBreaks.TryGetValue(name, out AdBreakConfig? adBreak))
            {
                adBreaks[name] = adBreak;
            }
        }

        JsonElement root = JsonSerializer.SerializeToElement(new { config.Settings, config.Titles, AdBreaks = adBreaks, Channel = channel }, InputsJson);
        var collections = new SortedDictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(root);
        return JsonSerializer.Serialize(new { Configuration = root, Collections = collections });

        void Collect(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray()) Collect(item);
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (property.NameEquals(nameof(ContentSelector.Collection)) && property.Value.ValueKind == JsonValueKind.String
                        && property.Value.GetString() is { Length: > 0 } name && visited.Add(name)
                        && config.GlobalCollections.TryGetValue(name, out ContentSelector? selector))
                    {
                        JsonElement definition = JsonSerializer.SerializeToElement(selector);
                        collections[name] = definition;
                        Collect(definition);
                    }
                    Collect(property.Value);
                }
            }
        }
    }

    /// <summary>
    /// Publishes the latest schedules again without rebuilding, for plugins whose output-side contributions (such as
    /// external channels) changed. Does nothing before the first build: publishing then would hand the outputs a
    /// lineup without the scheduler's own channels.
    /// </summary>
    public async Task RepublishAsync(CancellationToken cancellationToken)
    {
        await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (LastBuiltAt is null)
            {
                return;
            }

            logger.LogInformation("Republishing without rebuilding.");
            await PublishAsync(_latest, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _publishLock.Release();
        }
    }

    private async Task PublishAsync(IReadOnlyList<ChannelSchedule> schedules, CancellationToken cancellationToken)
    {
        // From the schedule as it is now, so a republish (not only a rebuild) carries a channel's latest encoding.
        schedules = WithEncoding(configProvider.Current, schedules);
        schedules = await ProcessAsync(schedules, cancellationToken).ConfigureAwait(false);

        // After the processors, which work on the scheduler's own channels and their configuration.
        IReadOnlyList<ChannelSchedule> external = await ExternalAsync(schedules, cancellationToken).ConfigureAwait(false);
        if (external.Count > 0)
        {
            schedules = [.. schedules, .. external];
        }

        foreach (IPlayoutPublisher publisher in publishers)
        {
            if (Progress is { } progress)
            {
                ReportProgress(progress with { ChannelName = null, Stage = $"Publishing: {publisher.Name}" });
            }
            try
            {
                await publisher.PublishAsync(schedules, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Publisher {Publisher} failed.", publisher.Name);
            }
        }
    }

    /// <summary>
    /// Each channel's own ErsatzTV encoding (<see cref="ChannelConfig.Encoding"/>), as an annotation on its schedule for
    /// the lineup to apply; channels without one are left as they are, and inherit the app-wide setting.
    /// </summary>
    public static IReadOnlyList<ChannelSchedule> WithEncoding(SchedulerConfig config, IReadOnlyList<ChannelSchedule> schedules)
    {
        Dictionary<string, string> own = new(StringComparer.OrdinalIgnoreCase);
        foreach (ChannelConfig channel in config.Channels)
        {
            if (SchedularrTV.Providers.ErsatzTv.ErsatzTvEncoding.ToAnnotation(channel.Encoding) is { } annotation)
            {
                own[channel.Id] = annotation;
            }
        }

        const string key = SchedularrTV.Providers.ErsatzTv.ErsatzTvEncoding.Annotation;
        return [.. schedules.Select(schedule =>
        {
            bool had = schedule.Annotations?.ContainsKey(key) == true;
            if (!own.TryGetValue(schedule.ChannelId, out string? annotation) && !had)
            {
                return schedule;
            }

            Dictionary<string, string> annotations = new(schedule.Annotations ?? new Dictionary<string, string>(), StringComparer.Ordinal);
            if (annotation is null)
            {
                annotations.Remove(key);
            }
            else
            {
                annotations[key] = annotation;
            }

            return schedule with { Annotations = annotations };
        })];
    }

    /// <summary>
    /// The plugins' whole channels (IPTV and the like), asked for at publish time. One whose number, id or guide id
    /// is already used is left out with a warning, so it can never take over one of the scheduler's own channels.
    /// </summary>
    private async Task<IReadOnlyList<ChannelSchedule>> ExternalAsync(IReadOnlyList<ChannelSchedule> own, CancellationToken cancellationToken)
    {
        List<IExternalChannelSource> sources = [.. externalSources ?? []];
        if (sources.Count == 0)
        {
            if (_external.Count > 0)
            {
                _external = [];
                StateChanged?.Invoke();
            }

            return [];
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset until = now + configProvider.Current.Settings.Horizon;
        HashSet<int> numbers = [.. own.Select(schedule => schedule.ChannelNumber)];
        HashSet<string> ids = new(own.Select(schedule => schedule.ChannelId), StringComparer.OrdinalIgnoreCase);
        HashSet<string> guideIds = new(own.Select(schedule => schedule.TvgId), StringComparer.OrdinalIgnoreCase);
        List<ChannelSchedule> added = [];
        List<ExternalChannel> listed = [];

        foreach (IExternalChannelSource source in sources)
        {
            IReadOnlyList<ChannelSchedule> offered;
            try
            {
                offered = await source.GetSchedulesAsync(new ExternalChannelRequest(now, until, numbers), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "External channels from {Source} couldn't be read; they're left out of this publish.", source.Name);
                continue;
            }

            foreach (ChannelSchedule schedule in offered)
            {
                if (numbers.Contains(schedule.ChannelNumber) || ids.Contains(schedule.ChannelId) || guideIds.Contains(schedule.TvgId))
                {
                    logger.LogWarning("{Source}'s channel {Name} ({Number}) uses a channel number or id that's already taken; it's left out.",
                        source.Name, schedule.ChannelName, schedule.ChannelNumber);
                    continue;
                }

                numbers.Add(schedule.ChannelNumber);
                ids.Add(schedule.ChannelId);
                guideIds.Add(schedule.TvgId);
                added.Add(schedule);
                listed.Add(new ExternalChannel(schedule, source.Name, EditPathOf(source, schedule)));
            }
        }

        _external = listed;
        StateChanged?.Invoke();
        return added;
    }

    // A plugin's answer, made relative to the app; one that throws (or a plugin built before 1.6) gives no Edit button.
    private string? EditPathOf(IExternalChannelSource source, ChannelSchedule schedule)
    {
        try
        {
            return source.EditPath(schedule)?.Trim().TrimStart('/') is { Length: > 0 } path ? path : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "{Source} couldn't say where {Channel} is set up.", source.Name, schedule.ChannelName);
            return null;
        }
    }

    /// <summary>
    /// Runs the plugins' playout processors over a copy of each schedule for the outputs. The planned schedules
    /// (served by the API and resumed by the next build) are left as they are, so a processor never sees its
    /// own earlier output. One that fails is skipped for that channel rather than holding back the publish.
    /// </summary>
    private async Task<IReadOnlyList<ChannelSchedule>> ProcessAsync(IReadOnlyList<ChannelSchedule> schedules, CancellationToken cancellationToken)
    {
        IReadOnlyList<IPlayoutProcessor> running = _processors;
        if (running.Count == 0)
        {
            return schedules;
        }

        SchedulerConfig config = configProvider.Current;
        List<ChannelSchedule> processed = new(schedules.Count);
        foreach (ChannelSchedule schedule in schedules)
        {
            ChannelConfig? channel = config.Channels.FirstOrDefault(candidate => string.Equals(candidate.Id, schedule.ChannelId, StringComparison.OrdinalIgnoreCase));
            ChannelSchedule current = schedule;
            foreach (IPlayoutProcessor processor in running)
            {
                try
                {
                    current = await processor.ProcessAsync(new PlayoutContext(config, channel, current), cancellationToken).ConfigureAwait(false) ?? current;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Playout processor {Processor} failed on {Channel}; its changes were skipped.", processor.Name, schedule.ChannelName);
                }
            }

            processed.Add(current);
        }

        return processed;
    }

    /// <summary>
    /// Rebuilds one channel, resuming from the end of whatever has already been planned.
    /// </summary>
    /// <remarks>
    /// A routine rebuild keeps the whole unplayed plan and only tops it back up to the horizon. Planning
    /// the future again on every pass would be wasted work even if the result were identical, and it is
    /// not: sequences resume from a cursor the last pass advanced across the full horizon, so the same
    /// clock times come back with different episodes and any guide a client has cached becomes fiction.
    /// Re-planning is therefore reserved for the cases that mean it, and even then the entry on air is
    /// carried over verbatim rather than cut off mid-scene.
    /// </remarks>
    private async Task<ChannelSchedule> BuildChannelAsync(
        SchedulerConfig config,
        ChannelConfig channel,
        DateTimeOffset now,
        bool replan,
        CancellationToken cancellationToken)
    {
        ChannelSchedule? previous = _latest
            .FirstOrDefault(s => string.Equals(s.ChannelId, channel.Id, StringComparison.OrdinalIgnoreCase));

        // When re-planning, keep what is on air up to the next grid boundary. Resuming at the end of just
        // the entry covering "now" lands mid-slot whenever that entry is a break or a mid-roll segment,
        // which pushes the next programme off the grid until it happens to re-align.
        DateTimeOffset boundary = replan
            ? SchedulingEngine.SlotBoundaryAfter(config, channel, now)
            : DateTimeOffset.MaxValue;

        IReadOnlyList<ScheduleEntry> carryOver = previous?.Entries
            .Where(e => e.End > now && e.Start < boundary)
            .OrderBy(e => e.Start)
            .ToList() ?? [];

        // A cold start aligns to the grid too, otherwise the very first programme lands wherever the
        // service happened to start and every later slot inherits that offset. Starting slightly in
        // the past is fine: ErsatzTV seeks into whichever item covers the current instant.
        DateTimeOffset start = carryOver.Count > 0
            ? carryOver[^1].End
            : SchedulingEngine.AlignToGrid(config, channel, Floor(now, TimeSpan.FromSeconds(1)));

        // Measured from "now" rather than from the resume point, so topping up adds the interval that
        // has elapsed instead of another full horizon on top of the plan that is already there.
        DateTimeOffset end = now + config.Settings.Horizon;
        DateTimeOffset alignedEnd = SchedulingEngine.AlignToGrid(config, channel, end);
        if (alignedEnd < end)
        {
            end = SchedulingEngine.SlotBoundaryAfter(config, channel, end);
        }

        if (end <= start && previous is not null && carryOver.Count > 0)
        {
            // Already planned past the horizon, which a shortened horizon can also cause. Nothing to add.
            return previous with
            {
                Entries = [.. carryOver], Start = carryOver[0].Start,
                ArchiveDays = ArchiveGuideDay.Clip(previous.ArchiveDays, carryOver[0].Start, previous.End)
            };
        }

        ChannelSchedule built = await builder
            .BuildChannelAsync(config, channel, start, end, cancellationToken, progress =>
            {
                if (Progress is { } current)
                {
                    ReportProgress(current with { Stage = progress.Stage, ChannelPercent = progress.Percent });
                }
            })
            .ConfigureAwait(false);

        if (carryOver.Count == 0)
        {
            return built;
        }

        return built with
        {
            Start = carryOver[0].Start,
            Entries = [.. carryOver, .. built.Entries],
            ArchiveFollowEnabled = built.ArchiveFollowEnabled || previous!.ArchiveFollowEnabled,
            ArchiveDays = [.. ArchiveGuideDay.Clip(previous!.ArchiveDays, carryOver[0].Start, start), .. built.ArchiveDays]
        };
    }

    /// <summary>
    /// Dry-run build for a single channel. Uses a throwaway playback-state store so previewing from
    /// the editor cannot advance the live rotation cursors, and never publishes.
    /// </summary>
    public Task<ChannelSchedule> PreviewAsync(
        SchedulerConfig config,
        ChannelConfig channel,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var previewBuilder = new ScheduleBuilder(library, new InMemoryPlaybackStateStore(), loggerFactory, builder.Features);
        return previewBuilder.BuildChannelAsync(config, channel, start, end, cancellationToken);
    }

    private static DateTimeOffset Floor(DateTimeOffset value, TimeSpan interval) =>
        new(value.Ticks - (value.Ticks % interval.Ticks), value.Offset);
}
