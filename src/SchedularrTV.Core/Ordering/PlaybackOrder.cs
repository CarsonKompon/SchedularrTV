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
    SeasonEpisode,

    /// <summary>
    /// New episodes drop on a cadence (e.g. one every Monday) and air as soon as they're out; between
    /// drops only episodes that already dropped play, as repeats. Applies per show; see <c>DropConfig</c>.
    /// </summary>
    Drop
}
