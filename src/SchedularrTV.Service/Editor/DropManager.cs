using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.Service.Editor;

/// <summary>
/// The channel page's view of its drop shows: where each one is, and putting one back to a chosen episode.
/// </summary>
/// <remarks>
/// Reads the saved configuration, not the editor's working copy: the saved state belongs to what was last built.
/// </remarks>
public sealed class DropManager(
    SchedulerConfigProvider configs,
    IMediaLibrary library,
    IPlaybackStateStore store,
    ScheduleCoordinator coordinator,
    TimeProvider time)
{
    public async Task<IReadOnlyList<DropShowStatus>> ListAsync(string channelId, CancellationToken cancellationToken = default)
    {
        SchedulerConfig config = configs.Current;
        ChannelConfig? channel = config.Channels.FirstOrDefault(c => string.Equals(c.Id, channelId, StringComparison.OrdinalIgnoreCase));
        return channel is null
            ? []
            : await DropStatusReader.ReadAsync(config, channel, library, store, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts <paramref name="show"/> back so <paramref name="episodesOut"/> episodes are out, then rebuilds the channel
    /// from the next slot so the guide follows. Done while no other build runs, so none can write over it.
    /// </summary>
    public Task<ChannelBuildFailure?> ResetAsync(string channelId, DropShowStatus show, int episodesOut, bool dropNow,
        CancellationToken cancellationToken = default) =>
        coordinator.RebuildChannelAsync(channelId, cancellationToken,
            ct => DropStatusReader.ResetAsync(store, show, episodesOut, dropNow, time.GetUtcNow(), ct));

    /// <summary>True when any of the channel's programming (in <paramref name="config"/>) puts a show on drops.</summary>
    public static bool UsesDrops(SchedulerConfig config, ChannelConfig channel) =>
        ContentSelectorQuery.Of(channel).Any(selector => UsesDrops(config, selector, 0));

    private static bool UsesDrops(SchedulerConfig config, ContentSelector selector, int depth)
    {
        if (selector.Order == PlaybackOrder.Drop || selector.Include.Any(include => include.Order == PlaybackOrder.Drop))
        {
            return true;
        }

        return depth < ContentSelectorQuery.MaxCollectionDepth
            && !string.IsNullOrWhiteSpace(selector.Collection)
            && config.GlobalCollections.TryGetValue(selector.Collection, out ContentSelector? global)
            && UsesDrops(config, global, depth + 1);
    }
}
