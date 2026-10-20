using SchedularrTV.Core.Publishing;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.CreditSqueeze;

/// <summary>
/// At each publish: finds the credits to squeeze (by each channel's and block's odds), puts in the ones already made,
/// and asks for the rest. Like generated promos, a squeeze not ready when its credits come round is simply not used: the
/// credits play as they are. Once one is made, the schedule is published again with it in.
/// </summary>
public sealed class CreditSqueezeProcessor(SqueezeRenderer renderer, CreditsMarkers credits, CreditSqueezeSettings settings, IChannelViewers? viewers = null,
    CreditsDetection? detection = null, TimeProvider? clock = null)
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
        bool make = !settings.WatchedOnly || viewers?.IsWatched(schedule.ChannelNumber) != false;
        DateTimeOffset now = (clock ?? TimeProvider.System).GetUtcNow();
        if (detection is not null && make)
        {
            for (int index = 0; index < schedule.Entries.Count; index++)
            {
                var entry = schedule.Entries[index];
                if (entry.Kind != ScheduleEntryKind.Program || entry.IsSlotIntro || entry.IsSlotOutro || entry.GeneratedTemplate is not null
                    || entry.Item is not { Path.Length: > 0 } item || entry.End < now || entry.Start > now + settings.LeadTime
                    || context.Settings<CreditSqueezeSection>(CreditSqueezeSection.PluginId, entry) is not { Enabled: true, AutoDetectCredits: true } section
                    || section.Looks().Count == 0) continue;
                if (index + 1 < schedule.Entries.Count && schedule.Entries[index + 1] is { IsSlotOutro: true } outro
                    && context.Settings<CreditSqueezeSection>(CreditSqueezeSection.PluginId, outro) is { Enabled: true } outroSection
                    && outroSection.Looks().Count > 0 && SqueezePlanner.Rolls(channel.Id, outro, "outro", outroSection.OutroChanceFor(SqueezePlanner.ShowOf(item)))) continue;
                detection.Want(item.Path!, item.Duration, section.LookFor(channel.Id, entry.Start).Recognition);
            }
        }
        List<SqueezePlan> plans = SqueezePlanner.Plan(schedule.Entries, channel.Id, schedule.ChannelName, zone,
            entry => context.Settings<CreditSqueezeSection>(CreditSqueezeSection.PluginId, entry), credits.CreditsOf, everywhere, settings.AssumedCredits,
            detection is null ? null : (entry, section) => entry.Start > now + settings.LeadTime ? null
                : detection.Known(entry.Item!.Path!, entry.Item.Duration, section.LookFor(channel.Id, entry.Start).Recognition)?.Interval);
        if (plans.Count == 0)
        {
            return ValueTask.FromResult(schedule);
        }

        plans = plans.Select(plan => plan with { Job = renderer.ForChannel(plan.Job, channel) }).ToList();

        // Unknown counts as watched, so a playout target that can't report viewers still gets its squeezes.
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
        }, plan => renderer.ExtraOf(plan.Job));

        return ValueTask.FromResult(schedule with { Entries = entries });
    }
}
