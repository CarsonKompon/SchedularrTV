namespace SchedularrTV.Core.Scheduling;

/// <summary>
/// One future fixed point on a channel — a block window opening or an anchor firing — and the titles
/// that could fill it.
/// </summary>
/// <param name="Day">Lower-case weekday of <paramref name="At"/> in the channel's own time zone.</param>
public sealed record UpcomingAiring(DateTimeOffset At, string Day, IReadOnlySet<string> Titles);

/// <summary>
/// What a channel is going to play beyond the window currently being built, as far as its recurrence
/// rules can say. Only the fixed points carry a time: unanchored rotation time is listed in
/// <paramref name="Rotation"/> without one, because when a title surfaces there is up to the rotation.
/// </summary>
public sealed record ChannelOutlook(IReadOnlyList<UpcomingAiring> Airings, IReadOnlySet<string> Rotation)
{
    public static ChannelOutlook Empty { get; } =
        new([], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}
