using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.Plugins.Playout;

/// <summary>
/// Channels a plugin supplies whole, with their schedule already made (an IPTV playlist and its guide, say), published
/// to the outputs alongside the scheduler's own channels: the same lineup, playouts and guide. They aren't planned
/// and aren't in the schedule file; the Channels page and dashboard list them, with an Edit button that goes to
/// <see cref="EditPath"/>. Register one with
/// <see cref="PluginServiceCollectionExtensions.AddExternalChannels{T}"/>.
/// </summary>
/// <remarks>
/// Asked on every publish (every rebuild, and whenever <see cref="IScheduleRepublisher.RequestRepublish"/> is called),
/// so it should answer from what it already has rather than fetch anything. Schedules must be gap-free from their
/// <see cref="ChannelSchedule.Start"/> to <see cref="ChannelSchedule.End"/>, and cover at least up to
/// <see cref="ExternalChannelRequest.Until"/>.
/// </remarks>
public interface IExternalChannelSource
{
    string Name { get; }

    ValueTask<IReadOnlyList<ChannelSchedule>> GetSchedulesAsync(ExternalChannelRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Where one of these channels is set up: a page of the plugin's, relative to the app (<c>plugins/iptv</c>, say). The
    /// channel is listed on the Channels page and the dashboard with an Edit button that goes there. Null for none
    /// (API 1.6; older plugins have none).
    /// </summary>
    string? EditPath(ChannelSchedule schedule) => null;
}

/// <param name="Now">The time of the publish.</param>
/// <param name="Until">How far ahead schedules should reach (now plus the scheduler's horizon).</param>
/// <param name="TakenNumbers">
/// Channel numbers already used by the scheduler's own channels (and by sources asked earlier). A channel given one of
/// these is left out.
/// </param>
public sealed record ExternalChannelRequest(DateTimeOffset Now, DateTimeOffset Until, IReadOnlySet<int> TakenNumbers);

/// <summary>
/// Asks the scheduler to publish again soon without rebuilding anything, for a plugin whose
/// <see cref="IExternalChannelSource"/> or other output-side contribution has changed. Requests close together are
/// combined. Provided by the host; inject it.
/// </summary>
public interface IScheduleRepublisher
{
    void RequestRepublish();
}
