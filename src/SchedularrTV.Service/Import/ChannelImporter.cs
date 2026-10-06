using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;
using SchedularrTV.Service.Editor;

namespace SchedularrTV.Service.Import;

/// <summary>
/// Brings another app's channels into the schedule: reads ErsatzTV's or Tunarr's database, works out each channel
/// against the library, and adds the chosen ones (with their shared collections and logos) to the editor's working
/// copy, ready to save.
/// </summary>
public sealed class ChannelImporter(ConfigEditor editor, IMediaLibrary library, LogoStore logos, ILogger<ChannelImporter> logger)
{
    /// <summary>The first place the app keeps its data that exists on this computer, if any.</summary>
    public static string? Find(ImportSource source) =>
        (source == ImportSource.ErsatzTv ? ErsatzTvImporter.LikelyPaths() : TunarrImporter.LikelyPaths())
        .FirstOrDefault(path => File.Exists(source == ImportSource.ErsatzTv ? ErsatzTvImporter.ResolvePath(path) : TunarrImporter.ResolvePath(path)));

    /// <summary>Reads the app's channels. Throws when the file isn't there or isn't its database.</summary>
    public ImportedSchedule Read(ImportSource source, string path)
    {
        ImportedSchedule schedule = source == ImportSource.ErsatzTv ? ErsatzTvImporter.Read(path.Trim().Trim('"')) : TunarrImporter.Read(path.Trim().Trim('"'));
        logger.LogInformation("Read {Count} channel(s) from {Source} at {Path}.", schedule.Channels.Count, source, schedule.From);
        return schedule;
    }

    /// <summary>Each channel worked out against the library as it is now (which is read first, when it hasn't been).</summary>
    public async Task<ImportPlan> PlanAsync(ImportedSchedule schedule, IEnumerable<string>? onlyChannels = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MediaItem> items = await library.SearchAsync(QueryExpression.All, cancellationToken).ConfigureAwait(false);
        return new ImportTranslator(items).Plan(schedule, editor.Working, onlyChannels);
    }

    /// <summary>
    /// Adds the plan's channels (the ones with something to play) and the collections they use to the editor, copying
    /// logo files in with the app's own (served from <paramref name="baseUri"/>). Returns how many channels were added.
    /// </summary>
    public async Task<int> ApplyAsync(ImportPlan plan, string baseUri, CancellationToken cancellationToken = default)
    {
        SchedulerConfig working = editor.Working;
        HashSet<string> used = new(plan.Channels.Where(channel => channel.Channel is not null)
            .SelectMany(channel => Collections(channel.Channel!)), StringComparer.OrdinalIgnoreCase);
        foreach ((string key, ContentSelector collection) in plan.Collections)
        {
            if (used.Contains(key))
            {
                working.GlobalCollections[key] = collection;
            }
        }

        int added = 0;
        foreach (ImportedChannelPlan channel in plan.Channels)
        {
            if (channel.Channel is not { } config)
            {
                continue;
            }

            if (channel.LogoFile is { } file)
            {
                try
                {
                    await using FileStream content = File.OpenRead(file);
                    config.Logo = await logos.SaveAsync(config.Id, file + Extension(file), content, baseUri, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    logger.LogWarning("Couldn't bring over {Channel}'s logo from {File}: {Message}", config.Name, file, ex.Message);
                }
            }

            working.Channels.Add(config);
            added++;
        }

        if (added > 0)
        {
            editor.MarkDirty();
        }

        return added;
    }

    private static IEnumerable<string> Collections(ChannelConfig channel) =>
        new[] { channel.BaseRotation }.Concat(channel.Blocks.SelectMany(block => block.Shows)).Select(selector => selector?.Collection).OfType<string>();

    /// <summary>ErsatzTV caches logos under a hash with no extension; the picture's first bytes say which it is.</summary>
    private static string Extension(string file)
    {
        if (Path.GetExtension(file) is { Length: > 1 } known && known.Length <= 5)
        {
            return string.Empty;
        }

        Span<byte> head = stackalloc byte[12];
        using FileStream stream = File.OpenRead(file);
        int read = stream.Read(head);
        return read >= 4 && head[0] == 0x89 && head[1] == 0x50 ? ".png"
            : read >= 3 && head[0] == 0xFF && head[1] == 0xD8 ? ".jpg"
            : read >= 3 && head[0] == 0x47 && head[1] == 0x49 ? ".gif"
            : read >= 12 && head[8] == 0x57 && head[9] == 0x45 ? ".webp"
            : ".png";
    }
}
