using SchedularrTV.Core.Publishing;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.CreditSqueeze;

/// <summary>
/// At each publish: finds the credits to squeeze (by each channel's and block's odds), puts in the ones already made,
/// and asks for the rest. Like generated promos, a squeeze not ready when its credits come round is simply not used: the
/// credits play as they are. Once one is made, the schedule is published again with it in.
/// </summary>
public sealed class CreditSqueezeProcessor(SqueezeRenderer renderer, CreditsMarkers credits, CreditSqueezeSettings settings, IChannelViewers? viewers = null)
    : IPlayoutProcessor
{
    public string Name => "Credit squeeze";

    // Before the overlays, which leave squeezes (marked as outros) clear of screen bugs.
    public int Order => 50;

    public ValueTask<ChannelSchedule> ProcessAsync(PlayoutContext context, CancellationToken cancellationToken)
    {
        ChannelSchedule schedule = context.Schedule;
        if (context.Channel is not { } channel)
        {
            return ValueTask.FromResult(schedule);
        }

        // Layouts set for particular shows on every channel.
        CreditSqueezeSection? everywhere = PluginSettings.Read<CreditSqueezeSection>(context.Config.Settings.Plugins, CreditSqueezeSection.PluginId);
        TimeZoneInfo zone = SchedulingEngine.ResolveTimeZone(string.IsNullOrWhiteSpace(channel.TimeZone) ? context.Config.Settings.TimeZone : channel.TimeZone);
        List<SqueezePlan> plans = SqueezePlanner.Plan(schedule.Entries, channel.Id, schedule.ChannelName, zone,
            entry => context.Settings<CreditSqueezeSection>(CreditSqueezeSection.PluginId, entry), credits.CreditsOf, everywhere, settings.AssumedCredits);
        if (plans.Count == 0)
        {
            return ValueTask.FromResult(schedule);
        }

        // Unknown counts as watched, so a playout target that can't report viewers still gets its squeezes.
        bool make = !settings.WatchedOnly || viewers?.IsWatched(schedule.ChannelNumber) != false;
        List<ScheduleEntry> entries = SqueezePlanner.Apply(schedule.Entries, plans, plan =>
        {
            if (renderer.Ready(plan.Job) is { } file)
            {
                return file;
            }

            if (make)
            {
                ScheduleEntry entry = schedule.Entries[plan.Index];
                renderer.Want(plan.Job, entry.Start + (plan.From - entry.InPoint), waitForPromo: plan.PromoPending);
            }

            return null;
        });

        return ValueTask.FromResult(schedule with { Entries = entries });
    }
}
