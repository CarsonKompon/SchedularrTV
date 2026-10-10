using System.Collections.Frozen;

namespace SchedularrTV.Core.Publishing;

/// <summary>
/// The channels kept playing around the clock, by number, and whether each counts as watched. Kept up to date by
/// whatever keeps them playing, and read by the viewer sources: once something is holding a channel open on purpose,
/// "someone is pulling the stream" no longer means "someone is watching", so they answer from the channel's setting.
/// </summary>
public sealed class AlwaysOnChannels
{
    private FrozenDictionary<int, bool> _channels = FrozenDictionary<int, bool>.Empty;

    /// <summary>Every always-on channel's number, with whether it counts as watched.</summary>
    public IReadOnlyDictionary<int, bool> Channels => _channels;

    /// <summary>Replaces the set (number → counts as watched).</summary>
    public void Set(IEnumerable<KeyValuePair<int, bool>> channels) =>
        _channels = channels.GroupBy(pair => pair.Key).ToFrozenDictionary(group => group.Key, group => group.Last().Value);

    public bool IsAlwaysOn(int channelNumber) => _channels.ContainsKey(channelNumber);

    /// <summary>
    /// What to say about whether an always-on channel is watched, from its setting; null when the channel isn't kept
    /// on, so the caller answers as it otherwise would.
    /// </summary>
    public bool? Watched(int channelNumber) => _channels.TryGetValue(channelNumber, out bool watched) ? watched : null;
}
