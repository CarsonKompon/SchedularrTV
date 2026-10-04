using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.Plugins.Promos;

/// <summary>
/// A promo made on request: its video file, how long it runs, and the programme it promotes (its show, episode and
/// start), when it promotes one.
/// </summary>
public sealed record MadePromo(string Path, TimeSpan Length, string? Subject = null, string? SubjectEpisode = null, DateTimeOffset? SubjectStart = null);

/// <summary>
/// Makes a promo on request from a channel's own generated promo templates (the Generated Promos plugin provides it),
/// for another plugin that wants one where the schedule has none, such as the panel of a credit squeeze. Ask for it
/// with <c>GetService</c>: it's missing when no plugin makes promos.
/// </summary>
public interface IPromoMaker
{
    /// <summary>Whether the channel (in the block named, if any) has an enabled template of this type.</summary>
    bool CanMake(SchedulerConfig config, ChannelConfig channel, string? blockName, GeneratedPromoType type);

    /// <summary>
    /// A promo of this type as it would air at <paramref name="airsAt"/> in <paramref name="schedule"/> (an Up Next for the
    /// programme starting then, a Coming Up for one later today, and so on), made now or reused if it was made before.
    /// Null when the channel can't make one.
    /// </summary>
    Task<MadePromo?> MakeAsync(SchedulerConfig config, ChannelConfig channel, ChannelSchedule schedule, string? blockName, GeneratedPromoType type,
        DateTimeOffset airsAt, CancellationToken cancellationToken = default);
}
