using System.Globalization;
using System.Text;
using System.Xml;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Publishing;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.Providers.ErsatzTv;

/// <summary>
/// Writes one XMLTV fragment per channel, named <c>{tvgId}.xml</c>.
/// </summary>
/// <remarks>
/// ErsatzTV Next builds the <c>&lt;channel&gt;</c> elements itself from lineup.json and copies only
/// the <c>&lt;programme&gt;</c> elements out of these files, so the file name must match the
/// channel's <c>tvg_id</c> exactly or the guide comes back empty.
/// </remarks>
public sealed class XmltvPublisher(IOptions<ErsatzTvOptions> options, ILogger<XmltvPublisher> logger) : IPlayoutPublisher
{
    // Read on each use: settings saved in the app apply straight away.
    private ErsatzTvOptions _options => options.Value;

    public string Name => "xmltv";

    public async Task PublishAsync(IReadOnlyList<ChannelSchedule> schedules, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ConfigRoot) || string.IsNullOrWhiteSpace(_options.XmltvFolder))
        {
            return;
        }

        string folder = Path.GetFullPath(Path.Combine(_options.ConfigRoot, _options.XmltvFolder));
        Directory.CreateDirectory(folder);

        HashSet<string> current = new(StringComparer.OrdinalIgnoreCase);

        foreach (ChannelSchedule schedule in schedules)
        {
            string fileName = string.Concat(schedule.TvgId.Split(Path.GetInvalidFileNameChars())) + ".xml";
            current.Add(fileName);

            string path = Path.Combine(folder, fileName);
            string temp = path + ".tmp";

            await File.WriteAllTextAsync(temp, BuildFragment(schedule, _options.GuideMode, _options.ArtworkBaseUrl), new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);

            File.Move(temp, path, overwrite: true);
        }

        RemoveOrphans(folder, current);

        logger.LogInformation("Wrote XMLTV fragments for {Count} channel(s) to {Folder}.", schedules.Count, folder);
    }

    /// <summary>
    /// Drops fragments for channels that no longer exist. Renumbering a channel changes its guide id,
    /// and a stale file left behind is indistinguishable from a live one when reading the folder.
    /// </summary>
    private void RemoveOrphans(string folder, HashSet<string> current)
    {
        foreach (string path in Directory.EnumerateFiles(folder, "*.xml"))
        {
            if (current.Contains(Path.GetFileName(path)))
            {
                continue;
            }

            try
            {
                File.Delete(path);
                logger.LogInformation("Removed stale XMLTV fragment {File}.", Path.GetFileName(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not remove stale XMLTV fragment {File}.", Path.GetFileName(path));
            }
        }
    }

    internal static string BuildFragment(ChannelSchedule schedule, GuideMode mode, string? artworkBaseUrl = null)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        var buffer = new StringWriterWithEncoding(Encoding.UTF8);

        using (XmlWriter writer = XmlWriter.Create(buffer, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("tv");
            writer.WriteAttributeString("generator-info-name", "SchedularrTV");

            foreach (GuideEntry guide in BuildGuide(schedule, mode))
            {
                WriteProgramme(writer, schedule.TvgId, guide, artworkBaseUrl);
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return buffer.ToString();
    }

    /// <summary>
    /// Collapses the timeline into what a viewer should see in a guide: one entry per programme,
    /// stretched over the ad break that follows it. Nobody wants 40 thirty-second listings per hour.
    /// </summary>
    public static IReadOnlyList<GuideEntry> BuildGuide(ChannelSchedule schedule, GuideMode mode)
    {
        List<ScheduleEntry> ordered = schedule.Entries.OrderBy(e => e.Start).ToList();

        if (mode == GuideMode.All)
        {
            return ordered
                .Where(e => e.Kind != ScheduleEntryKind.Padding)
                .Select(e => new GuideEntry(e.Start, e.End, e))
                .ToList();
        }

        List<int> programIndexes = [];
        List<DateTimeOffset?> listingStarts = [];
        DateTimeOffset? introStart = null;

        for (int i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Kind != ScheduleEntryKind.Program)
            {
                continue;
            }

            // A title sequence belongs to the programme it introduces, so the listing starts at the
            // intro but is named after the show. An end card belongs to the programme it closes.
            if (ordered[i].IsSlotIntro)
            {
                introStart ??= ordered[i].Start;
                continue;
            }

            if (ordered[i].IsSlotOutro)
            {
                continue;
            }

            // A programme split for mid-roll breaks is several entries but one listing, and so is a
            // slot filled with several episodes of the same show.
            if (programIndexes.Count > 0
                && (ordered[i].ContinuesSlot
                    || (ordered[programIndexes[^1]].Item?.Id is { } previousId
                        && previousId == ordered[i].Item?.Id
                        && ordered[i].SegmentNumber > 1)))
            {
                continue;
            }

            programIndexes.Add(i);
            listingStarts.Add(introStart);
            introStart = null;
        }

        if (programIndexes.Count == 0)
        {
            return BuildGuide(schedule, GuideMode.All);
        }

        List<GuideEntry> guide = [];
        for (int i = 0; i < programIndexes.Count; i++)
        {
            ScheduleEntry entry = ordered[programIndexes[i]];

            int next = i + 1 < programIndexes.Count ? programIndexes[i + 1] : ordered.Count;

            DateTimeOffset start = listingStarts[i] ?? entry.Start;

            DateTimeOffset stop = i + 1 < programIndexes.Count
                ? ordered[next].Start
                : schedule.End;

            // Every distinct episode in this listing: a stacked slot is one block in the guide, but
            // it still has to say which episodes it holds or the listing looks empty.
            List<MediaItem> episodes = ordered
                .Take(next)
                .Skip(programIndexes[i])
                .Where(e => e.Kind == ScheduleEntryKind.Program && e.Item is not null && !e.IsSlotIntro && !e.IsSlotOutro)
                .Select(e => e.Item!)
                .DistinctBy(m => m.Id, StringComparer.Ordinal)
                .ToList();

            guide.Add(new GuideEntry(start, stop, entry, episodes));
        }

        return guide;
    }

    private static void WriteProgramme(XmlWriter writer, string tvgId, GuideEntry guide, string? artworkBaseUrl)
    {
        ScheduleEntry entry = guide.Entry;
        MediaItem? item = entry.Item;

        writer.WriteStartElement("programme");
        writer.WriteAttributeString("start", FormatXmltvTime(guide.Start));
        writer.WriteAttributeString("stop", FormatXmltvTime(guide.Stop));
        writer.WriteAttributeString("channel", tvgId);

        // Series get the show name as the title and the episode name as the sub-title, which is how
        // Jellyfin's guide expects to group episodes.
        bool isSeries = item?.SeriesName is { Length: > 0 };

        if (isSeries)
        {
            WriteText(writer, "title", item!.SeriesName!);

            // A stacked slot is one listing covering several episodes, so it is named after all of
            // them; picking the first would claim the slot is a single episode it is not.
            string subTitle = string.Join(" / ", guide.Titles);
            if (subTitle.Length > 0)
            {
                WriteText(writer, "sub-title", subTitle);
            }
        }
        else
        {
            WriteText(writer, "title", item?.Title ?? entry.Source);
        }

        if (!string.IsNullOrWhiteSpace(item?.Overview) && guide.Episodes == 1)
        {
            WriteText(writer, "desc", item.Overview);
        }

        // Jellyfin reads these to decide a listing is a show rather than a one-off, which is what
        // gives it a series page instead of a bare guide entry.
        WriteText(writer, "category", isSeries ? "Series" : "Movie");

        if (entry.BlockName is { Length: > 0 } block)
        {
            WriteText(writer, "category", block);
        }

        if (item?.PremiereDate is { } premiere)
        {
            writer.WriteElementString("date", premiere.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        }

        WriteArtwork(writer, item, artworkBaseUrl);

        if (guide.EpisodeNumbers.Count > 0)
        {
            writer.WriteStartElement("episode-num");
            writer.WriteAttributeString("system", "onscreen");
            writer.WriteString(string.Join(" / ", guide.EpisodeNumbers));
            writer.WriteEndElement();
        }

        // xmltv_ns addresses exactly one episode, so it is only honest for an unstacked listing.
        if (guide.Episodes == 1 && item is { SeasonNumber: > 0, EpisodeNumber: > 0 })
        {
            writer.WriteStartElement("episode-num");
            writer.WriteAttributeString("system", "xmltv_ns");
            writer.WriteString($"{item.SeasonNumber - 1}.{item.EpisodeNumber - 1}.0/1");
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    /// <summary>
    /// Points the guide at the media server's own images: the series poster for the listing, and the
    /// episode still where a client can use one.
    /// </summary>
    private static void WriteArtwork(XmlWriter writer, MediaItem? item, string? artworkBaseUrl)
    {
        if (item is null || string.IsNullOrWhiteSpace(artworkBaseUrl))
        {
            return;
        }

        string root = artworkBaseUrl.TrimEnd('/');
        string? posterId = item.SeriesId ?? item.Id;

        if (posterId is null || item.Id.StartsWith("fs:", StringComparison.Ordinal))
        {
            return;
        }

        string poster = $"{root}/Items/{Uri.EscapeDataString(posterId)}/Images/Primary?fillHeight=440";

        writer.WriteStartElement("icon");
        writer.WriteAttributeString("src", poster);
        writer.WriteEndElement();

        writer.WriteStartElement("image");
        writer.WriteAttributeString("type", "poster");
        writer.WriteString(poster);
        writer.WriteEndElement();

        if (item.SeriesId is not null)
        {
            writer.WriteStartElement("image");
            writer.WriteAttributeString("type", "still");
            writer.WriteAttributeString("orient", "L");
            writer.WriteString($"{root}/Items/{Uri.EscapeDataString(item.Id)}/Images/Primary?fillHeight=220");
            writer.WriteEndElement();
        }
    }

    private static void WriteText(XmlWriter writer, string name, string value)
    {
        writer.WriteStartElement(name);
        writer.WriteAttributeString("lang", "en");
        writer.WriteString(value);
        writer.WriteEndElement();
    }

    private static string FormatXmltvTime(DateTimeOffset value) =>
        value.ToString("yyyyMMddHHmmss zzz", CultureInfo.InvariantCulture)
            .Replace(":", string.Empty, StringComparison.Ordinal);

    private sealed class StringWriterWithEncoding(Encoding encoding) : StringWriter
    {
        public override Encoding Encoding { get; } = encoding;
    }
}

/// <summary>
/// One guide listing. <paramref name="Episodes"/> holds every distinct episode it covers, which is
/// more than one when a slot was filled with several episodes of the same show.
/// </summary>
public sealed record GuideEntry(
    DateTimeOffset Start,
    DateTimeOffset Stop,
    ScheduleEntry Entry,
    IReadOnlyList<MediaItem> Items)
{
    public GuideEntry(DateTimeOffset start, DateTimeOffset stop, ScheduleEntry entry)
        : this(start, stop, entry, entry.Item is null ? [] : [entry.Item])
    {
    }

    public int Episodes => Math.Max(Items.Count, 1);

    /// <summary>Episode names in running order, so a stacked slot lists all of them.</summary>
    public IReadOnlyList<string> Titles => [.. Items.Select(i => i.Title).Where(t => !string.IsNullOrWhiteSpace(t))];

    public IReadOnlyList<string> EpisodeNumbers =>
    [
        .. Items
            .Where(i => i is { SeasonNumber: > 0, EpisodeNumber: > 0 })
            .Select(i => $"S{i.SeasonNumber:D2}E{i.EpisodeNumber:D2}")
    ];
}
