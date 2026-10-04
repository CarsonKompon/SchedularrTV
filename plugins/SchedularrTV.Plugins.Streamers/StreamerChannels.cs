using System.Collections.Frozen;
using System.Globalization;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.Streamers;

/// <summary>A stretch of the channel's guide: someone live, someone expected, or nobody.</summary>
/// <param name="Priority">Lower wins where two overlap (live always wins).</param>
public sealed record GuideSlot(DateTimeOffset Start, DateTimeOffset End, string Title, string? Overview, int Priority);

/// <summary>
/// The streamer channels as whole channels for the scheduler to publish. The picture is always the channel's one live
/// stream (it switches streamer, or shows the countdown, by itself); the guide shows who's live, and who's expected when.
/// </summary>
public sealed class StreamerChannels(StreamerStore store, StreamerMonitor monitor, StreamerSettings settings) : IExternalChannelSource
{
    private static readonly TimeSpan LongestBehind = TimeSpan.FromHours(12);

    private volatile IReadOnlySet<int> _taken = new HashSet<int>();

    public string Name => "Streamers";

    /// <summary>The channel opened for editing on the Streamers page.</summary>
    public string? EditPath(ChannelSchedule schedule) =>
        schedule.ChannelId.StartsWith(IdPrefix, StringComparison.Ordinal)
            ? $"plugins/streamers?edit={Uri.EscapeDataString(schedule.ChannelId[IdPrefix.Length..])}"
            : "plugins/streamers";

    private const string IdPrefix = "streamers-";

    /// <summary>True when another channel already has this one's number, so it's left out.</summary>
    public bool NumberTaken(StreamerChannel channel) => _taken.Contains(channel.Number);

    public ValueTask<IReadOnlyList<ChannelSchedule>> GetSchedulesAsync(ExternalChannelRequest request, CancellationToken cancellationToken)
    {
        _taken = new HashSet<int>(request.TakenNumbers);
        List<ChannelSchedule> schedules = [];
        foreach (StreamerChannel channel in store.Channels.Where(channel => channel.Enabled && channel.Streamers.Count > 0))
        {
            if (request.TakenNumbers.Contains(channel.Number))
            {
                continue;
            }

            schedules.Add(Build(channel, settings.LiveUrl(channel.Id), Slots(channel, request.Now, request.Until), request.Now, request.Until, Logo(channel)));
        }

        return ValueTask.FromResult<IReadOnlyList<ChannelSchedule>>(schedules);
    }

    /// <summary>The channel's own picture, else its top streamer's (as their platform shows it); null when neither is known yet.</summary>
    public string? Logo(StreamerChannel channel) =>
        channel.Logo ?? channel.Streamers.Select(streamer => monitor.State(streamer.Key)?.Latest?.Avatar).FirstOrDefault(avatar => avatar is not null);

    /// <summary>Who's live now and who's expected before <paramref name="until"/>, in the channel's priority order.</summary>
    public IReadOnlyList<GuideSlot> Slots(StreamerChannel channel, DateTimeOffset now, DateTimeOffset until)
    {
        List<GuideSlot> slots = [];
        for (int priority = 0; priority < channel.Streamers.Count; priority++)
        {
            StreamerEntry streamer = channel.Streamers[priority];
            StreamerState? state = monitor.State(streamer.Key);
            string name = state?.Name(streamer) ?? streamer.Handle;
            if (state is { Live: true, Latest: { } live })
            {
                DateTimeOffset start = live.StartedAt is { } started && started > now - LongestBehind ? started : now;
                TimeSpan usual = monitor.Prediction(streamer, now)?.Length ?? TimeSpan.FromHours(3);
                DateTimeOffset end = start + usual > now + TimeSpan.FromMinutes(30) ? start + usual : now + TimeSpan.FromMinutes(30);
                string overview = string.Join(" · ", new[] { live.Game, $"Live on {StreamerHandles.PlatformName(streamer.Platform)}" }.Where(part => part is { Length: > 0 }));
                slots.Add(new GuideSlot(start, end, live.Title is { Length: > 0 } title ? $"{name}: {title}" : $"{name} (live)", overview, -1));
                continue;
            }

            DateTimeOffset from = now;
            for (int count = 0; count < 20; count++)
            {
                if (monitor.Prediction(streamer, from) is not { } next || next.At >= until)
                {
                    break;
                }

                TimeSpan length = next.Length is { } usual && usual >= TimeSpan.FromMinutes(30) ? usual : TimeSpan.FromHours(2);
                DateTimeOffset start = next.At > now ? next.At : now;
                string overview = next.Kind == PredictionKind.Scheduled
                    ? $"Scheduled stream on {StreamerHandles.PlatformName(streamer.Platform)}."
                    : $"Expected on {StreamerHandles.PlatformName(streamer.Platform)}, judging by their past streams ({next.Confidence:P0} sure).";
                slots.Add(new GuideSlot(start, next.At + length, next.Kind == PredictionKind.Scheduled ? $"{name} (scheduled)" : $"{name} (expected)",
                    overview, priority));

                // The next one is looked for after this one (an announced stream is only known one at a time).
                from = next.At + length + TimeSpan.FromHours(1);
            }
        }

        return slots;
    }

    /// <summary>
    /// The channel's schedule from now to <paramref name="until"/>: the slots placed by priority (a lower-priority one only
    /// fills what's left around the others), and "Offline" in the gaps, all playing the channel's one live stream.
    /// </summary>
    public static ChannelSchedule Build(StreamerChannel channel, string streamUrl, IReadOnlyList<GuideSlot> slots, DateTimeOffset now, DateTimeOffset until,
        string? logo = null)
    {
        DateTimeOffset floor = new(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, now.Offset);
        DateTimeOffset start = slots.Where(slot => slot.Priority < 0 && slot.Start < floor).Select(slot => Minute(slot.Start)).DefaultIfEmpty(floor).Min();
        DateTimeOffset end = until > start ? until : start.AddHours(1);

        List<GuideSlot> placed = [];
        foreach (GuideSlot slot in slots.OrderBy(slot => slot.Priority).ThenBy(slot => slot.Start))
        {
            DateTimeOffset from = Minute(slot.Start < start ? start : slot.Start);
            DateTimeOffset to = Minute(slot.End > end ? end : slot.End);
            foreach ((DateTimeOffset pieceStart, DateTimeOffset pieceEnd) in Free(placed, from, to).ToList())
            {
                // Scraps left around a higher-priority stream aren't worth a guide entry.
                if (pieceEnd - pieceStart >= TimeSpan.FromMinutes(15) || slot.Priority < 0)
                {
                    placed.Add(slot with { Start = pieceStart, End = pieceEnd });
                }
            }
        }

        placed.Sort((a, b) => a.Start.CompareTo(b.Start));
        List<ScheduleEntry> entries = [];
        DateTimeOffset cursor = start;
        foreach (GuideSlot slot in placed)
        {
            if (slot.Start > cursor)
            {
                entries.Add(Entry(channel, streamUrl, cursor, slot.Start, "Offline", Waiting(placed, cursor)));
            }

            entries.Add(Entry(channel, streamUrl, slot.Start, slot.End, slot.Title, slot.Overview));
            cursor = slot.End;
        }

        if (cursor < end)
        {
            entries.Add(Entry(channel, streamUrl, cursor, end, "Offline", Waiting(placed, cursor)));
        }

        return new ChannelSchedule
        {
            ChannelId = IdPrefix + channel.Id,
            ChannelName = channel.Name,
            ChannelNumber = channel.Number,
            ChannelTvgId = $"streamers.{channel.Id}",
            ChannelGroup = channel.Group ?? "Streamers",
            ChannelLogo = logo ?? channel.Logo,
            Start = start,
            End = end,
            Entries = entries
        };
    }

    /// <summary>The parts of [from, to) not already taken.</summary>
    private static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> Free(List<GuideSlot> placed, DateTimeOffset from, DateTimeOffset to)
    {
        DateTimeOffset cursor = from;
        foreach (GuideSlot taken in placed.Where(slot => slot.End > from && slot.Start < to).OrderBy(slot => slot.Start))
        {
            if (taken.Start > cursor)
            {
                yield return (cursor, taken.Start);
            }

            if (taken.End > cursor)
            {
                cursor = taken.End;
            }
        }

        if (cursor < to)
        {
            yield return (cursor, to);
        }
    }

    private static string? Waiting(List<GuideSlot> placed, DateTimeOffset after) =>
        placed.FirstOrDefault(slot => slot.Start >= after) is { } next ? $"Up next: {next.Title}." : null;

    private static DateTimeOffset Minute(DateTimeOffset at) => new(at.Ticks - at.Ticks % TimeSpan.TicksPerMinute, at.Offset);

    private static ScheduleEntry Entry(StreamerChannel channel, string streamUrl, DateTimeOffset start, DateTimeOffset stop, string title, string? overview) => new()
    {
        Start = start,
        Duration = stop - start,
        Kind = ScheduleEntryKind.Program,
        Source = "streamers",
        Item = new MediaItem
        {
            Id = $"streamers:{channel.Id}:{start.UtcTicks.ToString(CultureInfo.InvariantCulture)}",
            Title = title,
            Overview = overview,
            Duration = stop - start,
            Path = streamUrl,
            Tags = ["Streamers"],
            Attributes = new Dictionary<string, string>
            {
                // The ErsatzTV output's own keys: joined where it is, never seeked into.
                ["ersatztv.live"] = "true",
                ["ersatztv.probe_hint"] = LiveStream.ProbeHint,
                ["streamers.channel"] = channel.Id
            }.ToFrozenDictionary()
        }
    };
}
