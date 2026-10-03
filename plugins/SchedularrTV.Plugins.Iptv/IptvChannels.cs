using System.Collections.Frozen;
using System.Globalization;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.Iptv;

/// <summary>A carried channel as published, for the IPTV page.</summary>
public sealed record CarriedChannel(string SourceId, PlaylistChannel Channel, int Number, string? GuideId, int Listings, bool NumberTaken);

/// <summary>
/// The carried IPTV channels as whole channels for the scheduler to publish: each one's playout follows its guide,
/// one live item per listing, so ErsatzTV's guide is the provider's and the picture is the provider's stream, relayed.
/// </summary>
public sealed class IptvChannels(SourceStore store, IptvCatalog catalog, IptvSettings settings) : IExternalChannelSource
{
    /// <summary>Entry annotation for a listing's original guide element (the ErsatzTV output's XMLTV writer reads it).</summary>
    public const string SourceProgrammeAnnotation = "xmltv.programme";

    /// <summary>Channel annotation asking ErsatzTV to copy rather than re-encode.</summary>
    public const string CopyAnnotation = "ersatztv.copy";

    private static readonly TimeSpan LongestBehind = TimeSpan.FromHours(12);

    private volatile IReadOnlySet<int> _taken = new HashSet<int>();

    public string Name => "IPTV";

    /// <summary>Every carried channel of every enabled source, with its number and guide match.</summary>
    public IReadOnlyList<CarriedChannel> Carried()
    {
        IReadOnlySet<int> taken = _taken;
        List<CarriedChannel> carried = [];
        foreach (IptvSource source in store.Sources.Where(source => source.Enabled))
        {
            if (catalog.Get(source.Id) is not { } loaded)
            {
                continue;
            }

            foreach (PlaylistChannel channel in loaded.Playlist.Channels.Where(channel => source.Includes(channel.Key)))
            {
                if (!source.Numbers.TryGetValue(channel.Key, out int number))
                {
                    continue;
                }

                string? guideId = loaded.Guide?.Match(channel);
                int listings = guideId is not null && loaded.Guide!.Programmes.TryGetValue(guideId, out List<GuideProgramme>? programmes) ? programmes.Count : 0;
                carried.Add(new CarriedChannel(source.Id, channel, number, guideId, listings, taken.Contains(number)));
            }
        }

        return carried;
    }

    /// <summary>What to relay for a source's channel number, or null when it isn't carried.</summary>
    public RelayTarget? Target(string sourceId, int number)
    {
        if (store.Find(sourceId) is not { Enabled: true } source || catalog.Get(source.Id) is not { } loaded)
        {
            return null;
        }

        string? key = source.Numbers.FirstOrDefault(pair => pair.Value == number).Key;
        PlaylistChannel? channel = key is null ? null : loaded.Playlist.Channels.FirstOrDefault(candidate => candidate.Key == key);
        return channel is null || !source.Includes(channel.Key)
            ? null
            : new RelayTarget($"{source.Id}/{number.ToString(CultureInfo.InvariantCulture)}", channel.Name, channel.Url,
                channel.UserAgent ?? source.UserAgent, channel.Referrer);
    }

    public ValueTask<IReadOnlyList<ChannelSchedule>> GetSchedulesAsync(ExternalChannelRequest request, CancellationToken cancellationToken)
    {
        _taken = new HashSet<int>(request.TakenNumbers);
        List<ChannelSchedule> schedules = [];
        foreach (CarriedChannel carried in Carried())
        {
            if (carried.NumberTaken || store.Find(carried.SourceId) is not { } source || catalog.Get(source.Id) is not { } loaded)
            {
                continue;
            }

            IReadOnlyList<GuideProgramme> programmes = carried.GuideId is not null && loaded.Guide!.Programmes.TryGetValue(carried.GuideId, out List<GuideProgramme>? listed)
                ? listed
                : [];
            schedules.Add(Build(source, carried.Channel, carried.Number, programmes, settings.LiveUrl(source.Id, carried.Number), request.Now, request.Until));
        }

        return ValueTask.FromResult<IReadOnlyList<ChannelSchedule>>(schedules);
    }

    /// <summary>
    /// A channel's schedule from now (or the start of the listing on now) to <paramref name="until"/>: one entry per
    /// listing, and the channel's own name over any stretch the guide doesn't cover, all playing the same live stream.
    /// </summary>
    public static ChannelSchedule Build(IptvSource source, PlaylistChannel channel, int number, IReadOnlyList<GuideProgramme> programmes,
        string streamUrl, DateTimeOffset now, DateTimeOffset until)
    {
        DateTimeOffset floor = new(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, now.Offset);
        DateTimeOffset start = floor;

        // The listing on air now starts the schedule, so the guide shows it from its real start.
        if (programmes.FirstOrDefault(programme => programme.Start <= floor && programme.Stop > floor) is { } onNow && floor - onNow.Start <= LongestBehind)
        {
            start = onNow.Start;
        }

        DateTimeOffset end = until > start ? until : start.AddHours(1);
        List<ScheduleEntry> entries = [];
        DateTimeOffset cursor = start;
        foreach (GuideProgramme programme in programmes)
        {
            if (programme.Stop <= cursor)
            {
                continue;
            }

            if (programme.Start >= end)
            {
                break;
            }

            if (programme.Start > cursor)
            {
                entries.Add(Entry(source, channel, number, streamUrl, cursor, programme.Start, null));
                cursor = programme.Start;
            }

            // A listing overlapping the one before it (it happens) starts where that one ends.
            DateTimeOffset stop = programme.Stop < end ? programme.Stop : end;
            if (stop <= cursor)
            {
                continue;
            }

            entries.Add(Entry(source, channel, number, streamUrl, cursor, stop, programme));
            cursor = stop;
        }

        if (cursor < end)
        {
            entries.Add(Entry(source, channel, number, streamUrl, cursor, end, null));
        }

        return new ChannelSchedule
        {
            ChannelId = $"iptv-{source.Id}-{number.ToString(CultureInfo.InvariantCulture)}",
            ChannelName = channel.Name,
            ChannelNumber = number,
            ChannelTvgId = $"iptv.{source.Id}.{number.ToString(CultureInfo.InvariantCulture)}",
            ChannelGroup = source.Group ?? channel.Group ?? source.Name,
            ChannelLogo = channel.Logo,
            Start = start,
            End = end,
            Entries = entries,
            Annotations = source.Passthrough ? new Dictionary<string, string> { [CopyAnnotation] = "true" } : null
        };
    }

    private static ScheduleEntry Entry(IptvSource source, PlaylistChannel channel, int number, string streamUrl,
        DateTimeOffset start, DateTimeOffset stop, GuideProgramme? programme) => new()
    {
        Start = start,
        Duration = stop - start,
        Kind = ScheduleEntryKind.Program,
        Source = "iptv",
        Item = new MediaItem
        {
            Id = $"iptv:{source.Id}:{number.ToString(CultureInfo.InvariantCulture)}:{start.UtcTicks.ToString(CultureInfo.InvariantCulture)}",
            Title = programme?.Title ?? channel.Name,
            Overview = programme?.Description,
            Duration = stop - start,
            Path = streamUrl,
            Tags = ["IPTV"],
            Attributes = new Dictionary<string, string>
            {
                // The ErsatzTV output's own keys: joined where it is, never seeked into.
                ["ersatztv.live"] = "true",
                ["iptv.source"] = source.Id
            }.ToFrozenDictionary()
        },
        Annotations = programme is null ? null : new Dictionary<string, string> { [SourceProgrammeAnnotation] = programme.Xml }
    };
}
