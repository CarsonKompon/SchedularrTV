namespace SchedularrTV.Core.Configuration;

public enum MidRollMode
{
    /// <summary>All break time goes after the programme.</summary>
    None,

    /// <summary>Break only where chapter markers allow. Programmes without chapters play whole.</summary>
    Chapters,

    /// <summary>Prefer chapter markers, but split on an even cadence when none is close enough.</summary>
    Auto
}

/// <summary>
/// Places advert breaks inside a programme instead of only after it, the way broadcast TV does.
/// </summary>
/// <remarks>
/// Cadence is driven by <see cref="TargetSegment"/>, not by a break count: a 90-minute film should
/// get a break roughly every quarter hour, not two breaks with half-hour gaps between them.
/// <see cref="MaxBreaks"/> is only a safety cap.
/// </remarks>
public sealed class MidRollConfig
{
    public MidRollMode Mode { get; set; } = MidRollMode.None;

    /// <summary>Aim for a break roughly this often.</summary>
    public TimeSpan TargetSegment { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// No part of a programme may be shorter than this. Chapter markers are frequently seconds
    /// apart (title cards, logos), and cutting to advert six seconds in would be absurd.
    /// </summary>
    public TimeSpan MinSegment { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How far from the ideal cadence point a chapter marker may sit and still be used. Beyond this
    /// the marker is ignored, and <see cref="MidRollMode.Auto"/> splits on the cadence instead.
    /// </summary>
    public TimeSpan ChapterTolerance { get; set; } = TimeSpan.FromMinutes(4);

    /// <summary>Safety cap on breaks in one programme.</summary>
    public int MaxBreaks { get; set; } = 8;

    /// <summary>
    /// Cap on a single mid-roll break. Whatever the slot budget cannot spend inside the programme
    /// is pushed to the break after it, where interstitial shows can absorb it.
    /// </summary>
    public TimeSpan MaxBreakLength { get; set; } = TimeSpan.FromMinutes(4);

    /// <summary>Skip mid-rolls entirely when a break would be shorter than this.</summary>
    public TimeSpan MinBreak { get; set; } = TimeSpan.FromSeconds(30);
}
