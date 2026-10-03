using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.Core.Publishing;

/// <summary>
/// Destination for a finished schedule. ErsatzTV Next is one implementation; XMLTV/M3U output and
/// on-disk JSON are others. Keeping this abstract means the engine never depends on a specific
/// playout target and new ones can be added without touching scheduling code.
/// </summary>
public interface IPlayoutPublisher
{
    string Name { get; }

    Task PublishAsync(IReadOnlyList<ChannelSchedule> schedules, CancellationToken cancellationToken = default);
}
