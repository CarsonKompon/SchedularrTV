using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.Service;

/// <summary>
/// A channel a plugin supplies whole (IPTV, a streamer channel), as last published: its schedule, which plugin's it is
/// (<paramref name="Source"/> is the source's name) and where it's set up (a page relative to the app), if anywhere.
/// </summary>
public sealed record ExternalChannel(ChannelSchedule Schedule, string Source, string? EditPath);
