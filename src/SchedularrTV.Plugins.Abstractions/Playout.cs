using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.Plugins.Playout;

/// <summary>
/// Changes each channel's schedule on its way to the outputs: replace or insert entries in breaks, or attach
/// <see cref="ScheduleEntry.Annotations"/> (for example an overlay to burn in) for an output that understands them.
/// Register one with <see cref="PluginServiceCollectionExtensions.AddPlayoutProcessor{T}"/>.
/// </summary>
/// <remarks>
/// Processors run every time schedules are published, always on the schedule as planned, never on a previous
/// processor run's output; nothing they return is fed back into planning. A processor must therefore be a pure
/// function of its input (no randomness that isn't seeded from it), or the same airing would differ between
/// publishes. Keep entries' start times and durations intact: outputs expect a gap-free timeline.
/// </remarks>
public interface IPlayoutProcessor
{
    string Name { get; }

    /// <summary>Lower runs first; ties run in name order.</summary>
    int Order => 0;

    ValueTask<ChannelSchedule> ProcessAsync(PlayoutContext context, CancellationToken cancellationToken);
}

/// <summary>One channel's schedule and the configuration it was built from.</summary>
public sealed class PlayoutContext(SchedulerConfig config, ChannelConfig? channel, ChannelSchedule schedule)
{
    public SchedulerConfig Config { get; } = config;

    /// <summary>Null when the channel has been removed from the configuration since it was built.</summary>
    public ChannelConfig? Channel { get; } = channel;

    public ChannelSchedule Schedule { get; } = schedule;

    /// <summary>
    /// A plugin's per-channel settings: the channel's <c>plugins.&lt;id&gt;</c> YAML section, falling back to
    /// <c>settings.plugins.&lt;id&gt;</c>. Null when neither is set.
    /// </summary>
    public T? Settings<T>(string pluginId) where T : class =>
        PluginSettings.Read<T>(Channel?.Plugins, pluginId) ?? PluginSettings.Read<T>(Config.Settings.Plugins, pluginId);

    /// <summary>The settings of the block an entry belongs to, then the channel's, then the global ones.</summary>
    public T? Settings<T>(string pluginId, ScheduleEntry entry) where T : class =>
        (entry.BlockName is { } name ? PluginSettings.Read<T>(Channel?.Blocks.FirstOrDefault(block => block.Name == name)?.Plugins, pluginId) : null)
        ?? Settings<T>(pluginId);
}
