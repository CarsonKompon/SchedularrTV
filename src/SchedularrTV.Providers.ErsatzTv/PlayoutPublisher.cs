using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Publishing;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.Providers.ErsatzTv;

/// <summary>
/// Writes one playout document per channel into the folder ErsatzTV Next reads.
/// </summary>
/// <remarks>
/// Next locates the playout for the current time from the file name, which must be
/// <c>{start}_{finish}.json</c> in compact ISO 8601. Because the scheduler is authoritative and
/// regenerates the whole horizon each run, previous files are removed after the new one lands —
/// leaving them would give Next two files claiming the same instant.
/// </remarks>
public sealed class PlayoutPublisher(
    IOptions<ErsatzTvOptions> options,
    ILogger<PlayoutPublisher> logger,
    MediaTrackProbe? trackProbe = null) : IPlayoutPublisher
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Read on each use: settings saved in the app apply straight away.
    private ErsatzTvOptions _options => options.Value;

    public string Name => "ersatztv-playout";

    public async Task PublishAsync(IReadOnlyList<ChannelSchedule> schedules, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ConfigRoot))
        {
            return;
        }

        IReadOnlyDictionary<string, IReadOnlyList<MediaStream>> streams = await TrackStreamsAsync(schedules, cancellationToken).ConfigureAwait(false);

        foreach (ChannelSchedule schedule in schedules)
        {
            string folder = ResolvePlayoutFolder(_options, schedule.ChannelNumber);
            Directory.CreateDirectory(folder);

            PlayoutDocument document = Build(schedule, _options, streams);
            string fileName = $"{FormatFileTimestamp(schedule.Start)}_{FormatFileTimestamp(schedule.End)}.json";
            string path = Path.Combine(folder, fileName);
            string temp = path + ".tmp";

            await using (FileStream stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
            RemoveSupersededPlayouts(folder, fileName);

            logger.LogInformation(
                "Wrote {Count} playout items for channel {ChannelNumber} to {Path}.",
                document.Items.Count,
                schedule.ChannelNumber,
                path);
        }
    }

    internal static string ResolvePlayoutFolder(ErsatzTvOptions options, int channelNumber) =>
        Path.GetFullPath(Path.Combine(
            options.ConfigRoot,
            options.PlayoutFolderTemplate.Replace(
                "{channelNumber}",
                channelNumber.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal)));

    /// <summary>
    /// The streams of every local file the channels with track preferences will play, read once for all of them.
    /// Channels without preferences need nothing read.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<MediaStream>>> TrackStreamsAsync(
        IReadOnlyList<ChannelSchedule> schedules, CancellationToken cancellationToken)
    {
        if (trackProbe is null)
        {
            return EmptyStreams;
        }

        List<string> paths = [.. schedules
            .Where(schedule => ErsatzTvTracks.For(schedule) is not null)
            .SelectMany(schedule => schedule.Entries.SelectMany(Playable))
            .Select(entry => entry.Item?.Path)
            .OfType<string>()
            .Where(path => path.Length > 0 && !IsHttp(path))];
        if (paths.Count == 0)
        {
            return EmptyStreams;
        }

        try
        {
            return await trackProbe.ProbeAsync(paths, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Programmes then play their default tracks, as they would without preferences.
            logger.LogWarning(ex, "Couldn't read the programmes' audio and subtitle tracks.");
            return EmptyStreams;
        }
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<MediaStream>> EmptyStreams =
        new Dictionary<string, IReadOnlyList<MediaStream>>();

    internal static PlayoutDocument Build(ChannelSchedule schedule, ErsatzTvOptions options,
        IReadOnlyDictionary<string, IReadOnlyList<MediaStream>>? streams = null)
    {
        SchedularrTV.Core.Configuration.ChannelTracksConfig? tracks = ErsatzTvTracks.For(schedule);
        var document = new PlayoutDocument
        {
            GeneratedAt = schedule.Start.ToString("O", CultureInfo.InvariantCulture),
            Items = new List<PlayoutItem>(schedule.Entries.Count)
        };

        int index = 0;
        foreach (ScheduleEntry entry in schedule.Entries.OrderBy(e => e.Start).SelectMany(Playable))
        {
            if (entry.Duration <= TimeSpan.Zero)
            {
                continue;
            }

            var item = new PlayoutItem
            {
                Id = (++index).ToString(CultureInfo.InvariantCulture),
                Start = entry.Start.ToString("O", CultureInfo.InvariantCulture),
                Finish = entry.End.ToString("O", CultureInfo.InvariantCulture)
            };

            if (entry.Item?.Path is { Length: > 0 } url && IsHttp(url))
            {
                item.Source = IsLive(entry.Item) ? LiveSourceFor(entry.Item, url, options) : HttpSourceFor(entry, url);
            }
            else if (entry.Item?.Path is { Length: > 0 } path)
            {
                // A mid-roll segment or trimmed filler plays only part of the file; a whole
                // programme needs no points at all.
                bool partial = entry.InPoint > TimeSpan.Zero || entry.IsTrimmed;

                var local = new LocalSource
                {
                    Path = MapPath(path, options.PathMappings),
                    InPointMs = entry.InPoint > TimeSpan.Zero ? (long)entry.InPoint.TotalMilliseconds : null,
                    OutPointMs = partial ? (long)(entry.InPoint + entry.Duration).TotalMilliseconds : null
                };
                item.Source = local;

                if (tracks is not null && streams is not null && streams.TryGetValue(path, out IReadOnlyList<MediaStream>? found))
                {
                    item.Tracks = TracksFor(TrackChooser.Choose(found, tracks), local, options);
                }
            }
            else
            {
                // Padding and unresolved entries become black + silence so the timeline has no hole.
                item.Tracks = new PlayoutTracks
                {
                    Video = new PlayoutTrack { Source = new LavfiSource { Params = options.PaddingVideoLavfi } },
                    Audio = new PlayoutTrack { Source = new LavfiSource { Params = options.PaddingAudioLavfi } }
                };
            }

            item.Graphics = Graphics(entry);
            document.Items.Add(item);
        }

        return document;
    }

    /// <summary>
    /// The chosen streams as the item's tracks; null when nothing was chosen, which leaves ErsatzTV's defaults (the first
    /// video and audio, no subtitles). A subtitle file beside the video is its own source, cut to the same part.
    /// </summary>
    internal static PlayoutTracks? TracksFor(TrackChoice choice, LocalSource video, ErsatzTvOptions options)
    {
        if (choice.Audio is null && choice.Subtitle is null)
        {
            return null;
        }

        PlayoutTrack? subtitle = choice.Subtitle switch
        {
            null => null,
            { ExternalPath: { } sidecar } => new PlayoutTrack
            {
                Source = new LocalSource
                {
                    Path = MapPath(sidecar, options.PathMappings),
                    InPointMs = video.InPointMs,
                    OutPointMs = video.OutPointMs
                }
            },
            { Index: var index } => new PlayoutTrack { StreamIndex = index }
        };

        return new PlayoutTracks
        {
            Audio = choice.Audio is { } audio ? new PlayoutTrack { StreamIndex = audio.Index } : null,
            Subtitle = subtitle
        };
    }

    /// <summary>
    /// Entry annotation holding graphics layers for the item (a JSON array in ErsatzTV's <c>graphics</c> format), such
    /// as the Overlays plugin's screen bugs and lower thirds.
    /// </summary>
    public const string GraphicsAnnotation = "ersatztv.graphics";

    private static System.Text.Json.Nodes.JsonArray? Graphics(ScheduleEntry entry)
    {
        if (entry.Annotations?.TryGetValue(GraphicsAnnotation, out string? json) != true || string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(json) is System.Text.Json.Nodes.JsonArray { Count: > 0 } layers ? layers : null;
        }
        catch (JsonException)
        {
            // A broken annotation costs the item its graphics, not its place in the playout.
            return null;
        }
    }

    /// <summary>Item attribute marking a stream as live: it's joined where it is, never seeked into.</summary>
    public const string LiveAttribute = "ersatztv.live";

    /// <summary>Item attribute holding ErsatzTV probe-hint JSON for the item's source.</summary>
    public const string ProbeHintAttribute = "ersatztv.probe_hint";

    private static bool IsHttp(string path) =>
        path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static bool IsLive(MediaItem item) =>
        item.Attributes.TryGetValue(LiveAttribute, out string? flag) && bool.TryParse(flag, out bool live) && live;

    private static System.Text.Json.Nodes.JsonNode? ProbeHint(MediaItem item)
    {
        if (!item.Attributes.TryGetValue(ProbeHintAttribute, out string? json))
        {
            return null;
        }

        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            // Without a valid hint ErsatzTV simply probes the stream itself.
            return null;
        }
    }

    /// <summary>A remote file is fetched by ErsatzTV over HTTP rather than read from disk.</summary>
    private static HttpSource HttpSourceFor(ScheduleEntry entry, string url)
    {
        bool partial = entry.InPoint > TimeSpan.Zero || entry.IsTrimmed;
        return new HttpSource
        {
            Uri = url,
            InPointMs = entry.InPoint > TimeSpan.Zero ? (long)entry.InPoint.TotalMilliseconds : null,
            OutPointMs = partial ? (long)(entry.InPoint + entry.Duration).TotalMilliseconds : null,
            ProbeHint = ProbeHint(entry.Item!)
        };
    }

    /// <summary>
    /// A live feed (such as a web page channel) is read through a small program marked live, so ErsatzTV joins it
    /// where it is. Given as an http source, ErsatzTV would read through it to the scheduled offset first, which for
    /// an endless real-time stream never arrives (a viewer tuning in mid-forecast would wait forever).
    /// </summary>
    private static ScriptSource LiveSourceFor(MediaItem item, string url, ErsatzTvOptions options) => new()
    {
        Command = options.LiveStreamCommand,
        Args = [.. options.LiveStreamArguments, url],
        IsLive = true,
        ProbeHint = ProbeHint(item)
    };

    /// <summary>A generated promo with no render yet airs its stand-in break, cut to the slot, instead of black.</summary>
    internal static IEnumerable<ScheduleEntry> Playable(ScheduleEntry entry)
    {
        if (entry.GeneratedTemplate is null || entry.Item?.Path is { Length: > 0 } || entry.Fallback is not { Count: > 0 } fallback)
        {
            yield return entry;
            yield break;
        }

        TimeSpan shift = entry.Start - fallback[0].Start;
        DateTimeOffset cursor = entry.Start;
        foreach (ScheduleEntry standIn in fallback)
        {
            DateTimeOffset start = standIn.Start + shift;
            if (start >= entry.End)
            {
                break;
            }

            TimeSpan duration = start + standIn.Duration > entry.End ? entry.End - start : standIn.Duration;
            bool cut = duration < standIn.Duration;
            yield return standIn with
            {
                Start = start,
                Duration = duration,
                IsTrimmed = standIn.IsTrimmed || cut,
                SourceDuration = cut ? standIn.SourceDuration ?? standIn.Item?.Duration : standIn.SourceDuration
            };
            cursor = start + duration;
        }

        if (entry.End > cursor)
        {
            yield return new ScheduleEntry { Start = cursor, Duration = entry.End - cursor, Kind = ScheduleEntryKind.Padding, Source = "padding" };
        }
    }

    internal static string MapPath(string path, IReadOnlyList<PathMapping> mappings)
    {
        foreach (PathMapping mapping in mappings)
        {
            if (path.StartsWith(mapping.From, StringComparison.OrdinalIgnoreCase))
            {
                return mapping.To.TrimEnd('/', '\\') + path[mapping.From.TrimEnd('/', '\\').Length..];
            }
        }

        return path;
    }

    /// <summary>Compact ISO 8601 with nanosecond precision, e.g. <c>20260413T000000.000000000-0500</c>.</summary>
    internal static string FormatFileTimestamp(DateTimeOffset value)
    {
        string stamp = value.ToString("yyyyMMdd'T'HHmmss.fffffff", CultureInfo.InvariantCulture);
        string offset = value.ToString("zzz", CultureInfo.InvariantCulture).Replace(":", string.Empty, StringComparison.Ordinal);

        // .NET tops out at 100ns precision; pad to the nine digits the file name convention uses.
        return $"{stamp}00{offset}";
    }

    private void RemoveSupersededPlayouts(string folder, string keepFileName)
    {
        foreach (string existing in Directory.EnumerateFiles(folder, "*.json"))
        {
            if (string.Equals(Path.GetFileName(existing), keepFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(existing);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not remove superseded playout {Path}.", existing);
            }
        }
    }
}
