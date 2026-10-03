using SchedularrTV.Plugins.Playout;

namespace SchedularrTV.Service;

/// <summary>
/// Plugins' requests to publish again (their external channels changed, say). Requests within a couple of seconds of
/// each other become one republish, run in the background.
/// </summary>
public sealed class ScheduleRepublisher(ScheduleCoordinator coordinator, ILogger<ScheduleRepublisher> logger) : IScheduleRepublisher
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);
    private int _pending;

    public void RequestRepublish()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(Settle).ConfigureAwait(false);
            Interlocked.Exchange(ref _pending, 0);
            try
            {
                await coordinator.RepublishAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Republishing for a plugin failed.");
            }
        });
    }
}
