using SchedularrTV.Core.Ordering;

namespace SchedularrTV.Service.Editor;

/// <summary>
/// Clears the saved rotation position for a channel and rebuilds it.
/// </summary>
/// <remarks>
/// The UI is hosted in the same process, so it resets through the store directly rather than calling
/// its own API, which would need the API key the user never has to configure for local use.
/// </remarks>
public sealed class RotationResetter(IPlaybackStateStore store, ScheduleCoordinator coordinator)
{
    public async Task ResetAsync(string channelId, CancellationToken cancellationToken = default)
    {
        // Keys are "{channelId}|{scope}|{identity}", so the channel prefix covers every rule on it.
        await store.ResetAsync($"{channelId}|", cancellationToken).ConfigureAwait(false);
        await coordinator.RebuildAsync(publish: true, replan: true, cancellationToken).ConfigureAwait(false);
    }
}
