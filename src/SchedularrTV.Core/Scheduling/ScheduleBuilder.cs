using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;

namespace SchedularrTV.Core.Scheduling;

public sealed record ScheduleBuildProgress(string Stage, int Percent);

/// <summary>
/// Builds schedules for every configured channel. Channels are built sequentially so that shared
/// playback state (a collection referenced by two channels) is updated deterministically.
/// </summary>
public sealed class ScheduleBuilder
{
    private readonly SchedulingEngine _engine;
    private readonly ILogger _logger;
    private readonly Func<IReadOnlyCollection<PlannerFeature>>? _liveFeatures;
    private IReadOnlyList<PlannerFeature> _features;

    /// <param name="features">
    /// The planner features to run; null runs all of them. (Not an <c>IEnumerable</c>, which dependency injection
    /// would fill with an empty list and so switch everything off.)
    /// </param>
    public ScheduleBuilder(
        IMediaLibrary library,
        IPlaybackStateStore stateStore,
        ILoggerFactory? loggerFactory = null,
        IReadOnlyCollection<PlannerFeature>? features = null)
    {
        ILoggerFactory loggers = loggerFactory ?? NullLoggerFactory.Instance;
        _features = features is null ? PlannerFeature.All : [.. features.Distinct()];
        _engine = new SchedulingEngine(library, stateStore, loggers.CreateLogger<SchedulingEngine>())
        {
            Modules = features is null ? EngineModules.Default : EngineModules.For(_features)
        };
        _logger = loggers.CreateLogger<ScheduleBuilder>();
    }

    /// <summary>
    /// A builder whose features are asked for before every build, so plugins switched on or off while running take
    /// part (or stop) from the next build on.
    /// </summary>
    public ScheduleBuilder(
        IMediaLibrary library,
        IPlaybackStateStore stateStore,
        ILoggerFactory? loggerFactory,
        Func<IReadOnlyCollection<PlannerFeature>> features)
        : this(library, stateStore, loggerFactory, features())
    {
        _liveFeatures = features;
    }

    /// <summary>The planner features this builder runs.</summary>
    public IReadOnlyList<PlannerFeature> Features
    {
        get
        {
            Refresh();
            return _features;
        }
    }

    private void Refresh()
    {
        if (_liveFeatures is null)
        {
            return;
        }

        PlannerFeature[] now = [.. _liveFeatures().Distinct()];
        if (now.Length == _features.Count && now.All(_features.Contains))
        {
            return;
        }

        _features = now;
        _engine.Modules = EngineModules.For(now);
    }

    public async Task<IReadOnlyList<ChannelSchedule>> BuildAllAsync(
        SchedulerConfig config,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken = default)
    {
        Refresh();
        List<ChannelSchedule> schedules = [];

        foreach (ChannelConfig channel in config.Channels)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                ChannelSchedule schedule = await _engine
                    .BuildAsync(config, channel, start, end, cancellationToken)
                    .ConfigureAwait(false);

                schedules.Add(schedule);
            }
            catch (SchedulerConfigurationException ex)
            {
                // One misconfigured channel must not take down the whole run.
                _logger.LogError(ex, "Skipping channel {ChannelId}: {Message}", channel.Id, ex.Message);
            }
        }

        return schedules;
    }

    public Task<ChannelSchedule> BuildChannelAsync(
        SchedulerConfig config,
        ChannelConfig channel,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken = default,
        Action<ScheduleBuildProgress>? progress = null)
    {
        Refresh();
        return _engine.BuildAsync(config, channel, start, end, cancellationToken, progress);
    }
}
