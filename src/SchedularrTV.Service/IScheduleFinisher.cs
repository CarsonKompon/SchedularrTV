using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.Service;

/// <summary>
/// Finishes built schedules before they're kept and published, such as the Generated promos plugin pointing
/// promo slots at their rendered files. Runs after every build and again when <see cref="ScheduleCoordinator.RefreshFinishedSchedulesAsync"/>
/// is called (when renders land).
/// </summary>
/// <remarks>
/// A host extension for plugins built alongside the scheduler, not part of the stable plugin API: it changes the
/// schedules the dashboard and API show, not just what outputs receive (that's <c>IPlayoutProcessor</c>).
/// </remarks>
public interface IScheduleFinisher
{
    /// <summary>Returns the same schedule instances when nothing needs to change.</summary>
    IReadOnlyList<ChannelSchedule> Prepare(SchedulerConfig config, IReadOnlyList<ChannelSchedule> schedules, DateTimeOffset now);
}

/// <summary>A card a plugin adds to the dashboard, such as Generated promos' render queue. Host extension, like <see cref="IScheduleFinisher"/>.</summary>
/// <param name="Component">A component with no required parameters.</param>
public sealed record DashboardCard(Type Component, int Order = 100);
