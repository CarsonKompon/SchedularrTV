namespace SchedularrTV.Core.Configuration;

/// <summary>How far viewer-aware scheduling may move a show away from its usual share of airtime.</summary>
public enum ViewerAwareStrength
{
    /// <summary>Up to 1.5× as often for favourites, down to two thirds for shows that get skipped.</summary>
    Gentle,

    /// <summary>Up to 2.5× as often, down to 40%.</summary>
    Balanced,

    /// <summary>Up to 4× as often, down to 20%.</summary>
    Strong
}

/// <summary>
/// Viewer-aware scheduling: shows people actually watch on this channel come round more often, and shows they
/// skip are rested. It works where the channel already chooses between shows: among a block's shows, and among
/// the titles of a pool that rotates titles. A show is never dropped entirely, and nothing changes until the
/// channel's viewing history passes every threshold.
/// </summary>
public sealed class ViewerAwareConfig
{
    public bool Enabled { get; set; }

    public ViewerAwareStrength Strength { get; set; } = ViewerAwareStrength.Balanced;

    /// <summary>How many days of viewing history to judge shows on.</summary>
    public int LookbackDays { get; set; } = 30;

    /// <summary>The channel needs at least this many days of recorded history.</summary>
    public int MinHistoryDays { get; set; } = 7;

    /// <summary>The channel needs at least this many different viewers a day, on average over the lookback.</summary>
    public double MinAverageDailyViewers { get; set; } = 1;

    /// <summary>The channel needs at least this many hours watched over the lookback.</summary>
    public double MinWatchedHours { get; set; } = 10;

    /// <summary>A show is only judged once it has aired at least this many minutes in the lookback.</summary>
    public int MinShowMinutes { get; set; } = 60;
}
