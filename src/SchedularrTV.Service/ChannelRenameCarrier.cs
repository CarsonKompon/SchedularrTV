using SchedularrTV.Core.Ordering;

namespace SchedularrTV.Service;

/// <summary>
/// When a channel's id is changed, where each of its rotations is up to (kept under the channel's id) moves to the new
/// id, so the channel carries on rather than starting its shows over.
/// </summary>
public sealed class ChannelRenameCarrier(SchedulerConfigProvider provider, IPlaybackStateStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        provider.ChannelIdsChanged += Carry;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        provider.ChannelIdsChanged -= Carry;
        return Task.CompletedTask;
    }

    // Done before the change is announced (so before the rebuild it triggers), hence waited on here.
    private void Carry(IReadOnlyDictionary<string, string> renamed) => CarryAsync(store, renamed).AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// Moves every state kept under each old channel id to its new one. All are read before any is moved, so two channels
    /// swapping ids keep their own.
    /// </summary>
    public static async ValueTask CarryAsync(IPlaybackStateStore store, IReadOnlyDictionary<string, string> renamed, CancellationToken cancellationToken = default)
    {
        List<PlaybackState> moved = [];
        foreach ((string from, string to) in renamed)
        {
            string prefix = from + "|";
            foreach (PlaybackState state in await store.FindAsync(prefix, cancellationToken).ConfigureAwait(false))
            {
                PlaybackState copy = state.Clone();
                copy.Key = to + "|" + state.Key[prefix.Length..];
                moved.Add(copy);
            }
        }

        if (moved.Count == 0)
        {
            return;
        }

        foreach (string from in renamed.Keys)
        {
            await store.ResetAsync(from + "|", cancellationToken).ConfigureAwait(false);
        }

        await store.SaveAllAsync(moved, cancellationToken).ConfigureAwait(false);
    }
}
