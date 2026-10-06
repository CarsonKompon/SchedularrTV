using System.Globalization;
using System.Text;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;

namespace SchedularrTV.Service.Import;

/// <summary>One imported channel as it will be set up here, with how much of it was found in the library.</summary>
public sealed class ImportedChannelPlan
{
    public required ImportedChannel Source { get; init; }

    /// <summary>The channel to add; null when nothing of it could be found in the library.</summary>
    public ChannelConfig? Channel { get; init; }

    /// <summary>Its logo file on this computer, still to be copied in with the app's own logos.</summary>
    public string? LogoFile { get; init; }

    public int Found { get; init; }

    public int Missing { get; init; }

    /// <summary>Shows, films or collections that couldn't be found (by name), for the summary.</summary>
    public IReadOnlyList<string> MissingNames { get; init; } = [];

    public List<string> Notes { get; } = [];
}

/// <summary>The import worked out: the channels, and the collections they share.</summary>
public sealed class ImportPlan
{
    public required ImportedSchedule Schedule { get; init; }

    public List<ImportedChannelPlan> Channels { get; } = [];

    public Dictionary<string, ContentSelector> Collections { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Turns another app's channels into SchedularrTV's: each programme found in the Jellyfin library (by Jellyfin's id, by
/// its file, by show, season and episode, or a film's title and year), shows as whole shows or some of their seasons or
/// episodes, timed slots as blocks, what goes round as the base rotation (each show weighted by how often it came up),
/// collections as shared collections, and filler as the channel's filler.
/// </summary>
public sealed class ImportTranslator
{
    private readonly Dictionary<string, MediaItem> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MediaItem> _byPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MediaItem> _byTail = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MediaItem> _byEpisode = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MediaItem> _byMovie = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Id, string Name)> _seriesByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Id, string Name)> _seriesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _episodesInSeries = new(StringComparer.Ordinal);

    public ImportTranslator(IEnumerable<MediaItem> library)
    {
        foreach (MediaItem item in library)
        {
            _byId.TryAdd(Id(item.Id), item);
            if (item.Path is { Length: > 0 } path)
            {
                _byPath.TryAdd(NormalPath(path), item);
                _byTail.TryAdd(Tail(path), item);
            }

            if (item.Kind == MediaKind.Movie)
            {
                _byMovie.TryAdd(MovieKey(item.Title, item.PremiereDate?.Year), item);
                _byMovie.TryAdd(MovieKey(item.Title, null), item);
            }
            else if (item.SeriesId is { Length: > 0 } seriesId && item.SeriesName is { Length: > 0 } series)
            {
                _seriesByName.TryAdd(Plain(series), (seriesId, series));
                _seriesById.TryAdd(Id(seriesId), (seriesId, series));
                _episodesInSeries[seriesId] = _episodesInSeries.GetValueOrDefault(seriesId) + 1;
                if (item.SeasonNumber is { } season && item.EpisodeNumber is { } episode)
                {
                    _byEpisode.TryAdd(EpisodeKey(series, season, episode), item);
                }
            }
        }
    }

    /// <summary>Works out every channel, numbered and named so none clashes with <paramref name="existing"/>'s (or each other's).</summary>
    public ImportPlan Plan(ImportedSchedule schedule, SchedulerConfig existing, IEnumerable<string>? onlyChannels = null)
    {
        var plan = new ImportPlan { Schedule = schedule };
        HashSet<string>? wanted = onlyChannels is null ? null : new(onlyChannels, StringComparer.Ordinal);
        HashSet<string> ids = new(existing.Channels.Select(channel => channel.Id), StringComparer.OrdinalIgnoreCase);
        HashSet<int> numbers = [.. existing.Channels.Select(channel => channel.Number)];
        foreach (string key in existing.GlobalCollections.Keys)
        {
            _collectionKeys.Add(key);
        }

        List<ImportedChannel> chosen = [.. schedule.Channels.Where(source => wanted is null || wanted.Contains(source.SourceId))];

        // Channels whose numbers are free here keep them; one that clashes moves to a number none of the others has either.
        _reserved.Clear();
        _reserved.UnionWith(chosen.Select(source => source.Number).Where(number => number > 0 && !numbers.Contains(number)));
        foreach (ImportedChannel source in chosen)
        {
            plan.Channels.Add(Channel(source, plan, ids, numbers));
        }

        return plan;
    }

    private readonly HashSet<string> _collectionKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _reserved = [];

    /// <summary>What was and wasn't found, as one channel is worked out.</summary>
    private sealed class Tally
    {
        public int Found;
        public int Missing;
        public readonly List<string> Names = [];

        public void Lost(string name)
        {
            Missing++;
            if (!Names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                Names.Add(name);
            }
        }
    }

    private ImportedChannelPlan Channel(ImportedChannel source, ImportPlan plan, HashSet<string> ids, HashSet<int> numbers)
    {
        var tally = new Tally();
        List<string> notes = [.. source.Notes];

        // Timed slots become blocks, each running to the next on the same days (or as long as the other app said).
        List<BlockConfig> blocks = [];
        HashSet<string> blockNames = new(StringComparer.OrdinalIgnoreCase);
        List<ImportedContent> allDay = [];
        foreach (IGrouping<string, ImportedSlot> days in source.Slots.GroupBy(slot => string.Join(',', slot.Days.Order())))
        {
            List<ImportedSlot> slots = [.. days.OrderBy(slot => slot.Start)];
            if (slots.Count == 1 && slots[0].Length is null)
            {
                // One thing all day long: that's a rotation, not a block.
                if (slots[0].Days.Count == 0)
                {
                    allDay.Add(slots[0].Content);
                    continue;
                }
            }

            for (int index = 0; index < slots.Count; index++)
            {
                ImportedSlot slot = slots[index];
                if (Unsupported(slot.Content, notes))
                {
                    continue;
                }

                ContentSelector? selector = Selector(slot.Content, plan, tally);
                if (selector is null)
                {
                    continue;
                }

                TimeSpan start = Day(slot.Start);
                TimeSpan end = slot.Length is { } length && length > TimeSpan.Zero
                    ? Day(start + (length >= TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) - TimeSpan.FromMinutes(1) : length))
                    : slots.Count > 1 ? Day(slots[(index + 1) % slots.Count].Start) : Day(start - TimeSpan.FromMinutes(1));
                if (end == start)
                {
                    end = Day(start - TimeSpan.FromMinutes(1));
                }

                string name = Unique(string.IsNullOrWhiteSpace(slot.Content.Name) ? "Block" : slot.Content.Name, blockNames);
                blocks.Add(new BlockConfig
                {
                    Name = name,
                    Time = start,
                    EndTime = end,
                    Recurrence = slot.Days.Count > 0 ? Recurrence.DaysOfWeek : Recurrence.Daily,
                    Days = [.. slot.Days],
                    Shows = [selector]
                });
            }
        }

        // What goes round: every include, weighted, in one rotation.
        List<ContentInclude> rotation = [];
        foreach (ImportedContent content in source.Rotation.Concat(allDay))
        {
            if (Unsupported(content, notes))
            {
                continue;
            }

            int weight = Math.Max(1, (int)Math.Round(content.Weight));
            foreach (ContentInclude include in Includes(content, tally))
            {
                if (rotation.FirstOrDefault(existing => Same(existing, include)) is { } same)
                {
                    same.Weight = (same.Weight ?? 1) + weight;
                    continue;
                }

                include.Weight = weight;
                rotation.Add(include);
            }
        }

        // Equal weights say nothing: left off, the YAML stays plain.
        if (rotation.Select(include => include.Weight).Distinct().Count() <= 1)
        {
            rotation.ForEach(include => include.Weight = null);
        }

        List<string> filler = [.. source.Filler.Select(media => Find(media, tally)).OfType<MediaItem>().Select(item => item.Id).Distinct()];
        string? logoFile = source.Logo is { } logo && !IsWeb(logo) && File.Exists(logo) ? logo : null;

        if (blocks.Count == 0 && rotation.Count == 0)
        {
            notes.Add("Nothing it played could be found in the Jellyfin library, so it isn't added.");
            return new ImportedChannelPlan { Source = source, Found = tally.Found, Missing = tally.Missing, MissingNames = tally.Names }.With(notes);
        }

        int number = source.Number > 0 && !numbers.Contains(source.Number) ? source.Number : Next(numbers, _reserved, source.Number);
        if (source.Number > 0 && number != source.Number)
        {
            notes.Add($"Channel {source.Number.ToString(CultureInfo.InvariantCulture)} is taken here, so it's channel {number.ToString(CultureInfo.InvariantCulture)}.");
        }

        numbers.Add(number);
        var channel = new ChannelConfig
        {
            Id = UniqueId(Slug(source.Name), ids),
            Name = source.Name,
            Number = number,
            Group = source.Group,
            Logo = source.Logo is { } address && IsWeb(address) ? address : null,
            BaseRotation = rotation.Count > 0 ? new ContentSelector { Include = rotation } : null,
            Blocks = blocks,
            Interstitials = filler.Count > 0
                ? new InterstitialsConfig { Filler = new FillerTierConfig { Query = $"id IN [{string.Join(", ", filler.Select(Quote))}]" } }
                : null
        };
        ids.Add(channel.Id);

        if (rotation.Count == 0)
        {
            // Between blocks there must be something: what the blocks play, shuffled.
            channel.BaseRotation = new ContentSelector
            {
                Include = [.. blocks.SelectMany(block => block.Shows).SelectMany(selector => selector.Collection is { } key && plan.Collections.TryGetValue(key, out ContentSelector? shared)
                    ? shared.Include
                    : selector.Include).DistinctBy(Describe)],
                Order = PlaybackOrder.Shuffle
            };
        }

        return new ImportedChannelPlan
        {
            Source = source, Channel = channel, LogoFile = logoFile, Found = tally.Found, Missing = tally.Missing, MissingNames = tally.Names
        }.With(notes);
    }

    private static bool Unsupported(ImportedContent content, List<string> notes)
    {
        if (content.Kind != ImportedContentKind.Unsupported)
        {
            return false;
        }

        string note = $"Skipped {content.Name}: {content.Problem}.";
        if (!notes.Contains(note))
        {
            notes.Add(note);
        }

        return true;
    }

    /// <summary>The content as one selector (a shared collection, when it was one there); null when none of it was found.</summary>
    private ContentSelector? Selector(ImportedContent content, ImportPlan plan, Tally tally)
    {
        List<ContentInclude> includes = Includes(content, tally);
        if (includes.Count == 0)
        {
            return null;
        }

        PlaybackOrder? order = Order(content.Order);
        if (content.CollectionName is { Length: > 0 } name)
        {
            // Made once, and shared by every channel (and slot) that played it there.
            if (!_collectionsByName.TryGetValue(name, out string? key))
            {
                key = Unique(Slug(name).Replace('-', '_'), _collectionKeys);
                _collectionsByName[name] = key;
                plan.Collections[key] = new ContentSelector { Include = includes };
            }

            return new ContentSelector { Collection = key, Order = order };
        }

        return new ContentSelector { Include = includes, Order = order };
    }

    private readonly Dictionary<string, string> _collectionsByName = new(StringComparer.Ordinal);

    /// <summary>The content as includes: shows (all of them, some seasons, or some episodes), and films by id.</summary>
    private List<ContentInclude> Includes(ImportedContent content, Tally tally)
    {
        List<ContentInclude> includes = [];
        PlaybackOrder? order = Order(content.Order);
        switch (content.Kind)
        {
            case ImportedContentKind.Show:
                if (Series(content) is { } series)
                {
                    tally.Found++;
                    includes.Add(new ContentInclude
                    {
                        Show = series.Name, ShowId = series.Id, Seasons = [.. content.Seasons.Distinct().Order()],
                        Order = order ?? PlaybackOrder.SeasonEpisode
                    });
                }
                else
                {
                    tally.Lost(content.Name);
                }

                break;
            case ImportedContentKind.AllMovies:
                includes.Add(new ContentInclude { Query = "kind == 'Movie'", Order = order ?? PlaybackOrder.Shuffle });
                break;
            case ImportedContentKind.Unsupported:
                break;
        }

        if (content.Kind is ImportedContentKind.Items or ImportedContentKind.Group && content.Items.Count > 0)
        {
            includes.AddRange(ItemIncludes(content.Items, order, tally));
        }

        foreach (ImportedContent child in content.Children.Where(child => child.Kind != ImportedContentKind.Unsupported))
        {
            foreach (ContentInclude include in Includes(child, tally))
            {
                include.Order ??= order;
                if (!includes.Any(existing => Same(existing, include)))
                {
                    includes.Add(include);
                }
            }
        }

        return includes;
    }

    /// <summary>Particular programmes: a show's episodes as that show (whole, when it's all of them), films by their ids.</summary>
    private IEnumerable<ContentInclude> ItemIncludes(IReadOnlyList<ImportedMedia> media, PlaybackOrder? order, Tally tally)
    {
        List<MediaItem> found = [];
        foreach (ImportedMedia item in media)
        {
            if (Find(item, tally) is { } match)
            {
                found.Add(match);
            }
        }

        List<string> loose = [];
        foreach (IGrouping<string?, MediaItem> show in found.DistinctBy(item => item.Id).GroupBy(item => item.Kind == MediaKind.Movie ? null : item.SeriesId))
        {
            List<MediaItem> items = [.. show];
            if (show.Key is not { } seriesId || items.Any(item => item.SeasonNumber is null || item.EpisodeNumber is null))
            {
                loose.AddRange(items.Select(item => item.Id));
                continue;
            }

            string name = items[0].SeriesName ?? seriesId;
            yield return items.Count >= _episodesInSeries.GetValueOrDefault(seriesId)
                ? new ContentInclude { Show = name, ShowId = seriesId, Order = order ?? PlaybackOrder.SeasonEpisode }
                : new ContentInclude
                {
                    Show = name, ShowId = seriesId, Order = order ?? PlaybackOrder.SeasonEpisode,
                    Episodes = [.. items.OrderBy(item => item.SeasonNumber).ThenBy(item => item.EpisodeNumber)
                        .Select(item => $"S{item.SeasonNumber!.Value:D2}E{item.EpisodeNumber!.Value:D2}")]
                };
        }

        if (loose.Count > 0)
        {
            yield return new ContentInclude { Query = $"id IN [{string.Join(", ", loose.Select(Quote))}]", Order = order ?? PlaybackOrder.Shuffle };
        }
    }

    /// <summary>The library's series for a show: by Jellyfin's id, by its episodes found here, else by name.</summary>
    private (string Id, string Name)? Series(ImportedContent content)
    {
        if (content.ShowJellyfinId is { Length: > 0 } id && _seriesById.TryGetValue(Id(id), out var byId))
        {
            return byId;
        }

        foreach (ImportedMedia item in content.Items)
        {
            if (Find(item, null) is { SeriesId: { Length: > 0 } seriesId, SeriesName: { Length: > 0 } name })
            {
                return (seriesId, name);
            }
        }

        return _seriesByName.TryGetValue(Plain(content.Name), out var byName) ? byName : null;
    }

    /// <summary>One programme in the library: by Jellyfin's id, its file (or the same file under another folder), show and episode, or film.</summary>
    public MediaItem? Find(ImportedMedia media) => Find(media, null);

    private MediaItem? Find(ImportedMedia media, Tally? tally)
    {
        MediaItem? match = null;
        if (media.JellyfinId is { Length: > 0 } id)
        {
            _byId.TryGetValue(Id(id), out match);
        }

        if (match is null && media.Path is { Length: > 0 } path)
        {
            match = _byPath.GetValueOrDefault(NormalPath(path)) ?? _byTail.GetValueOrDefault(Tail(path));
        }

        if (match is null && !media.IsMovie && media.Show is { Length: > 0 } show && media.Season is { } season && media.Episode is { } episode)
        {
            match = _byEpisode.GetValueOrDefault(EpisodeKey(show, season, episode));
        }

        if (match is null && media.IsMovie)
        {
            match = _byMovie.GetValueOrDefault(MovieKey(media.Title, media.Year)) ?? _byMovie.GetValueOrDefault(MovieKey(media.Title, null));
        }

        if (tally is not null)
        {
            if (match is null)
            {
                tally.Lost(media.Show ?? media.Title);
            }
            else
            {
                tally.Found++;
            }
        }

        return match;
    }

    public static PlaybackOrder? Order(ImportedOrder order) => order switch
    {
        ImportedOrder.Chronological => PlaybackOrder.Chronological,
        ImportedOrder.SeasonEpisode => PlaybackOrder.SeasonEpisode,
        ImportedOrder.Shuffle => PlaybackOrder.Shuffle,
        ImportedOrder.Random => PlaybackOrder.Random,
        _ => null
    };

    private static bool Same(ContentInclude a, ContentInclude b) => Describe(a) == Describe(b);

    private static string Describe(ContentInclude include) =>
        $"{include.ShowId}|{include.Show}|{include.Movie}|{include.Query}|{string.Join(',', include.Seasons)}|{string.Join(',', include.Episodes)}";

    private static TimeSpan Day(TimeSpan time)
    {
        long ticks = time.Ticks % TimeSpan.TicksPerDay;
        return TimeSpan.FromTicks(ticks < 0 ? ticks + TimeSpan.TicksPerDay : ticks);
    }

    private static int Next(HashSet<int> numbers, HashSet<int> reserved, int near)
    {
        int candidate = Math.Max(1, near);
        while (numbers.Contains(candidate) || reserved.Contains(candidate))
        {
            candidate++;
        }

        return candidate;
    }

    private static bool IsWeb(string text) => Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https";

    private static string Quote(string value) => "'" + value.Replace("'", "\\'", StringComparison.Ordinal) + "'";

    /// <summary>Jellyfin ids with or without their dashes, in either case.</summary>
    public static string Id(string id) => id.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    private static string NormalPath(string path) => path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    /// <summary>The file's folder and name: the same file seen under another root (a Docker mount, a network share).</summary>
    private static string Tail(string path)
    {
        // Three parts (show, season, file), as "Season 1/Episode 1.mkv" alone is in every show.
        string[] parts = NormalPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('/', parts.Skip(Math.Max(0, parts.Length - 3)));
    }

    /// <summary>Letters and digits only, lower case: "Kenan & Kel" and "kenan and kel"… nearly; "The Office (US)" is "theofficeus".</summary>
    public static string Plain(string text)
    {
        var plain = new StringBuilder(text.Length);
        foreach (char character in text.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(character))
            {
                plain.Append(char.ToLowerInvariant(character));
            }
        }

        return plain.ToString();
    }

    private static string EpisodeKey(string show, int season, int episode) => $"{Plain(show)}|{season}|{episode}";

    private static string MovieKey(string title, int? year) => $"{Plain(title)}|{year}";

    public static string Slug(string name)
    {
        var slug = new StringBuilder();
        foreach (char character in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                slug.Append(character);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        string text = slug.ToString().Trim('-');
        return text.Length == 0 ? "channel" : text.Length > 40 ? text[..40].TrimEnd('-') : text;
    }

    private static string UniqueId(string id, HashSet<string> taken) => Unique(id, taken, "-");

    private static string Unique(string name, HashSet<string> taken, string separator = " ")
    {
        string candidate = name;
        for (int suffix = 2; taken.Contains(candidate); suffix++)
        {
            candidate = $"{name}{separator}{suffix.ToString(CultureInfo.InvariantCulture)}";
        }

        taken.Add(candidate);
        return candidate;
    }
}

internal static class ImportedChannelPlanExtensions
{
    public static ImportedChannelPlan With(this ImportedChannelPlan plan, IEnumerable<string> notes)
    {
        plan.Notes.AddRange(notes);
        return plan;
    }
}
