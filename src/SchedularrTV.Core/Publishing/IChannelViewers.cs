namespace SchedularrTV.Core.Publishing;

/// <summary>Whether anyone is currently tuned to a channel on the playout target.</summary>
public interface IChannelViewers
{
    /// <summary>Null when the playout target cannot tell, so callers should assume someone might be.</summary>
    bool? IsWatched(int channelNumber);
}
