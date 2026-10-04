using Microsoft.Extensions.Hosting;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Publishing;
using SchedularrTV.Service;

namespace SchedularrTV.CreditSqueeze;

/// <summary>
/// With squeezes only made for watched channels: notices someone tuning in to a squeezing channel and has the schedule
/// published again, so its squeezes start being made then rather than at the next rebuild.
/// </summary>
public sealed class TuningInWatcher(SchedulerConfigProvider config, CreditSqueezeSettings settings, IServiceProvider services) : BackgroundService
{
    private readonly HashSet<int> _watched = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                if (!settings.WatchedOnly || services.GetService<IChannelViewers>() is not { } viewers)
                {
                    continue;
                }

                bool tunedIn = false;
                foreach (ChannelConfig channel in config.Current.Channels.Where(Squeezes))
                {
                    bool watched = viewers.IsWatched(channel.Number) == true;
                    tunedIn |= watched && !_watched.Contains(channel.Number);
                    if (watched)
                    {
                        _watched.Add(channel.Number);
                    }
                    else
                    {
                        _watched.Remove(channel.Number);
                    }
                }

                if (tunedIn)
                {
                    services.GetService<IScheduleRepublisher>()?.RequestRepublish();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Whether a channel (or any of its blocks) squeezes credits.</summary>
    private static bool Squeezes(ChannelConfig channel) =>
        PluginSettings.Read<CreditSqueezeSection>(channel.Plugins, CreditSqueezeSection.PluginId) is { Enabled: true }
        || channel.Blocks.Any(block => PluginSettings.Read<CreditSqueezeSection>(block.Plugins, CreditSqueezeSection.PluginId) is { Enabled: true });
}
