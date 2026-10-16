using SchedularrTV.Core.Ordering;
using YamlDotNet.Serialization;

namespace SchedularrTV.Core.Configuration;

/// <summary>Which episode of a show drops next.</summary>
public enum DropReleaseOrder
{
    /// <summary>Season number, then episode number.</summary>
    EpisodeOrder,

    /// <summary>Original air (premiere) date, then season/episode.</summary>
    AirDate
}

/// <summary>
/// Settings for <see cref="PlaybackOrder.Drop"/>: a show releases new episodes on a cadence, and in
/// between drops only episodes that have already dropped play (as repeats).
/// </summary>
/// <remarks>
/// Applies per show. On a collection it gives every show in it the same cadence; on an include it
/// gives that one show its own. New episodes take the show's normal airings from the drop day on, unless
/// the show sets an air <see cref="Time"/>. Whether an episode has dropped is worked out from the airing's
/// time, so rebuilding the schedule never releases episodes early.
/// </remarks>
public sealed class DropConfig
{
    /// <summary>
    /// Weekdays episodes drop on, in the channel's time zone. When empty, <see cref="Every"/> is used instead; with
    /// neither, Fridays.
    /// </summary>
    public List<DayOfWeek> Days { get; set; } = [];

    /// <summary>Interval between drops when no <see cref="Days"/> are given, e.g. <c>3d</c>.</summary>
    public TimeSpan? Every { get; set; }

    /// <summary>
    /// For one show: the time of day its new episodes air, in the channel's time zone (e.g. <c>20:00</c>); the first
    /// chance at or after it goes to the new episode, ahead of whatever else was due. Null (the usual) leaves the show
    /// to its normal airings: on a drop day, its first airings are the new episodes.
    /// </summary>
    public TimeSpan? Time { get; set; }

    /// <summary>How many episodes drop at a time.</summary>
    public int Count { get; set; } = 1;

    /// <summary>
    /// First day drops count from, as <c>yyyy-MM-dd</c>. When omitted, drops start at the first build that
    /// uses them (the most recent drop time at or before it counts, so something is out straight away).
    /// </summary>
    public string? Start { get; set; }

    /// <summary>Episodes treated as already out (repeats) before the first drop.</summary>
    public int AlreadyOut { get; set; }

    /// <summary>Which episode drops next: episode order or air-date order.</summary>
    public DropReleaseOrder ReleaseOrder { get; set; } = DropReleaseOrder.EpisodeOrder;

    /// <summary>
    /// How episodes that already dropped repeat between drops: <c>shuffle</c> (default), <c>random</c>, or in
    /// release order (<c>season_episode</c> / <c>chronological</c>).
    /// </summary>
    public PlaybackOrder Repeats { get; set; } = PlaybackOrder.Shuffle;

    [YamlIgnore]
    public TimeSpan Interval => Every is { } every && every > TimeSpan.Zero ? every : TimeSpan.FromDays(7);

    [YamlIgnore]
    public TimeSpan TimeOfDay => Time is { } time && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1) ? time : TimeSpan.Zero;

    /// <summary>The weekdays drops happen on: <see cref="Days"/>, else none when <see cref="Every"/> is set, else Fridays.</summary>
    [YamlIgnore]
    public IReadOnlyList<DayOfWeek> DropDays => Days.Count > 0 ? Days : Every is null ? DefaultDays : [];

    /// <summary>Drops happen on Fridays unless days or an interval are set.</summary>
    public static readonly IReadOnlyList<DayOfWeek> DefaultDays = [DayOfWeek.Friday];

    /// <summary>New episodes air at a set time, ahead of whatever else is due, rather than at the show's normal airings.</summary>
    [YamlIgnore]
    public bool HasAirTime => Time is not null;

    public DropConfig Copy() => new()
    {
        Days = [.. Days],
        Every = Every,
        Time = Time,
        Count = Count,
        Start = Start,
        AlreadyOut = AlreadyOut,
        ReleaseOrder = ReleaseOrder,
        Repeats = Repeats
    };

    /// <summary>Short summary for editors, e.g. "1 episode Mondays at 20:00".</summary>
    public string Describe()
    {
        int count = Math.Max(1, Count);
        string what = count == 1 ? "1 episode" : $"{count} episodes";
        string when = DropDays.Count > 0
            ? string.Join(", ", DropDays.Distinct().OrderBy(d => ((int)d + 6) % 7).Select(d => d + "s"))
            : EveryText(Interval);
        string at = HasAirTime ? $" at {TimeOfDay:hh\\:mm}" : string.Empty;
        return $"{what} {when}{at}";
    }

    private static string EveryText(TimeSpan interval)
    {
        double days = interval.TotalDays;
        if (days < 1 || Math.Abs(days - Math.Round(days)) > 1e-9)
        {
            return $"every {interval.TotalHours:0.#} hours";
        }

        return days switch
        {
            1 => "every day",
            7 => "every week",
            _ => $"every {days:0} days"
        };
    }
}
