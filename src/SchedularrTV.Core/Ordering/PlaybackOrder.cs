namespace SchedularrTV.Core.Ordering;

public enum PlaybackOrder
{
    /// <summary>Strictly by air/release date, then season/episode.</summary>
    Chronological,

    /// <summary>Pure random selection; repeats are allowed.</summary>
    Random,

    /// <summary>Weighted bag: no item repeats until every item has played once.</summary>
    Shuffle,

    /// <summary>Season number, then episode number.</summary>
    SeasonEpisode
}
