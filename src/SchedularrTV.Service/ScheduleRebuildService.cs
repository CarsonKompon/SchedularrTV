using Microsoft.Extensions.Options;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Providers.Jellyfin;

namespace SchedularrTV.Service;

/// <summary>
/// Rebuilds and republishes playouts on a timer, and immediately whenever the schedule file changes.
/// </summary>
public sealed class ScheduleRebuildService(
    ScheduleCoordinator coordinator,
    SchedulerConfigProvider configProvider,
    IOptions<SchedulerServiceOptions> options,
    ILogger<ScheduleRebuildService> logger) : BackgroundService
{
    private readonly SchedulerServiceOptions _options = options.Value;
    private readonly SemaphoreSlim _configChanged = new(0, 1);

    private enum Trigger
    {
        Interval,
        ConfigChanged,
        Everything,
        Stop
    }

    private volatile bool _everything;

    /// <summary>
    /// Replans every channel soon, as for a schedule edit but without skipping unchanged channels: what the schedule is
    /// built from changed (a plugin switched on or off, a media server or folder changed in App settings).
    /// </summary>
    public void RequestFullRebuild()
    {
        _everything = true;
        if (_configChanged.CurrentCount == 0)
        {
            try
            {
                _configChanged.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.AutoRebuild)
        {
            logger.LogInformation("Automatic rebuilds are disabled; use POST /api/rebuild.");
            return;
        }

        configProvider.ConfigChanged += OnConfigChanged;

        // There is nothing to preserve on the first pass, and an edit made while the service was down
        // has to reach the schedule, so a cold start plans in full.
        Trigger trigger = Trigger.ConfigChanged;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await coordinator
                        .RebuildAsync(publish: true, replan: trigger is Trigger.ConfigChanged or Trigger.Everything, stoppingToken,
                            onlyChanged: trigger == Trigger.ConfigChanged)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (JellyfinConfigurationException ex)
                {
                    logger.LogError("{Message}", ex.Message);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Scheduled rebuild failed; retrying at the next interval.");
                }

                trigger = await WaitForNextRunAsync(stoppingToken).ConfigureAwait(false);

                if (trigger == Trigger.Stop)
                {
                    break;
                }
            }
        }
        finally
        {
            configProvider.ConfigChanged -= OnConfigChanged;
        }
    }

    /// <summary>Waits for the rebuild interval, or returns early when the schedule file changes.</summary>
    private async Task<Trigger> WaitForNextRunAsync(CancellationToken stoppingToken)
    {
        TimeSpan interval = configProvider.Current.Settings.RebuildInterval;
        if (interval <= TimeSpan.Zero)
        {
            interval = TimeSpan.FromHours(1);
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(interval);

        Trigger trigger = Trigger.Interval;

        try
        {
            await _configChanged.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (_everything)
            {
                _everything = false;
                logger.LogInformation("Settings or plugins changed; rebuilding every channel.");
                trigger = Trigger.Everything;
            }
            else
            {
                logger.LogInformation("Schedule changed; rebuilding.");
                trigger = Trigger.ConfigChanged;
            }
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // The interval elapsed rather than the config changing.
        }
        catch (OperationCanceledException)
        {
            return Trigger.Stop;
        }

        return stoppingToken.IsCancellationRequested ? Trigger.Stop : trigger;
    }

    private void OnConfigChanged(SchedulerConfig config)
    {
        // Capacity is one, so a burst of edits collapses into a single rebuild.
        if (_configChanged.CurrentCount == 0)
        {
            try
            {
                _configChanged.Release();
            }
            catch (SemaphoreFullException)
            {
                // RequestFullRebuild released it in between; one wake-up is enough.
            }
        }
    }

    public override void Dispose()
    {
        _configChanged.Dispose();
        base.Dispose();
    }
}
