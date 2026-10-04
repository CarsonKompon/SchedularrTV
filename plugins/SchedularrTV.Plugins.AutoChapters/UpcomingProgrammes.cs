using SchedularrTV.Core.Scheduling;
using SchedularrTV.Plugins.Playout;

namespace SchedularrTV.AutoChapters;

/// <summary>
/// Sees each schedule as it's published and puts the programmes airing in the next two days at the front of the
/// queue, so they have markers by the time later builds plan their breaks. Changes nothing in the schedule.
/// </summary>
public sealed class UpcomingProgrammes(ChapterScanner scanner, TimeProvider? clock = null) : IPlayoutProcessor
{
    public static readonly TimeSpan Horizon = TimeSpan.FromDays(2);

    public string Name => "auto-chapters";

    public int Order => 1000;

    public ValueTask<ChannelSchedule> ProcessAsync(PlayoutContext context, CancellationToken cancellationToken)
    {
        DateTimeOffset now = (clock ?? TimeProvider.System).GetUtcNow();
        scanner.QueueSoon(context.Schedule.Entries
            .Where(entry => entry.Kind == ScheduleEntryKind.Program && entry.Item is not null && !entry.IsSlotIntro && !entry.IsSlotOutro
                && entry.End > now && entry.Start < now + Horizon)
            .Select(entry => (entry.Item!, entry.Start)));
        return ValueTask.FromResult(context.Schedule);
    }
}
