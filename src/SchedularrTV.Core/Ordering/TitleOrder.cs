namespace SchedularrTV.Core.Ordering;

/// <summary>
/// How whole titles take turns within a collection, as opposed to how the episodes of one title are
/// ordered (<see cref="PlaybackOrder"/>).
/// </summary>
public enum TitleOrder
{
    /// <summary>Round-robin in the order the titles were added to the collection.</summary>
    Ordered,

    /// <summary>Round-robin in a deterministically shuffled title order.</summary>
    Shuffle
}
