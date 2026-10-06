using System.Globalization;
using System.Text.Json;

namespace SchedularrTV.Service.Import;

/// <summary>
/// Reads ErsatzTV's channels and schedules from its database (<c>ersatztv.sqlite3</c>): classic schedules (their
/// fixed-time items become timed slots, the rest a rotation), block schedules (each template's blocks at their times),
/// collections, filler presets and logos. Sequential and scripted (YAML) playouts are noted, not brought over.
/// </summary>
public static class ErsatzTvImporter
{
    public const string DatabaseName = "ersatztv.sqlite3";

    /// <summary>Where ErsatzTV keeps its database on this computer, when it's there.</summary>
    public static IEnumerable<string> LikelyPaths()
    {
        if (Environment.GetEnvironmentVariable("ETV_CONFIG_FOLDER") is { Length: > 0 } configured)
        {
            yield return Path.Combine(configured, DatabaseName);
        }

        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ersatztv", DatabaseName);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "ersatztv", DatabaseName);
        yield return Path.Combine("/root", ".local", "share", "ersatztv", DatabaseName);
        yield return Path.Combine("/config", DatabaseName);
    }

    /// <summary>A database file, or the folder holding one.</summary>
    public static string ResolvePath(string path) =>
        Directory.Exists(path) ? Path.Combine(path, DatabaseName) : path;

    // CollectionType values.
    private const int CollectionKind = 0, ShowKind = 1, SeasonKind = 2, MultiKind = 4, SmartKind = 5, PlaylistKind = 6, SearchKind = 9, MovieKind = 10, EpisodeKind = 20;

    // Playout.ScheduleKind values.
    private const int Classic = 1, BlockSchedule = 2, Sequential = 3, Scripted = 4;

    private const int LogoArtwork = 2;

    public static ImportedSchedule Read(string path)
    {
        string file = ResolvePath(path);
        using SqliteSnapshot db = SqliteSnapshot.Open(file);
        if (!db.HasTable("Channel"))
        {
            throw new InvalidOperationException($"{file} doesn't look like ErsatzTV's database (it has no Channel table).");
        }

        var schedule = new ImportedSchedule { Source = ImportSource.ErsatzTv, From = file };
        var reader = new Reader(db, Path.GetDirectoryName(Path.GetFullPath(file)) ?? ".");
        foreach (Dictionary<string, object?> row in db.Rows(
                     $"SELECT c.Id, c.Name, c.Number, {db.Select("Channel", "c", "Group")} AS ChannelGroup, {db.Select("Channel", "c", "FallbackFillerId")} AS FallbackFillerId FROM Channel c"))
        {
            long id = SqliteSnapshot.Long(row, "Id") ?? 0;
            var channel = new ImportedChannel
            {
                SourceId = id.ToString(CultureInfo.InvariantCulture),
                Name = SqliteSnapshot.Text(row, "Name") ?? $"Channel {id}",
                Number = Number(SqliteSnapshot.Text(row, "Number")),
                Group = SqliteSnapshot.Text(row, "ChannelGroup") is { } group && group != "ErsatzTV" ? group : null,
                Logo = reader.Logo(id)
            };

            if (SqliteSnapshot.Long(row, "FallbackFillerId") is { } filler)
            {
                channel.Filler.AddRange(reader.FillerMedia(filler));
            }

            reader.ReadPlayout(channel, id);
            schedule.Channels.Add(channel);
        }

        List<ImportedChannel> ordered = [.. schedule.Channels.OrderBy(channel => channel.Number)];
        schedule.Channels.Clear();
        schedule.Channels.AddRange(ordered);
        return schedule;
    }

    /// <summary>"5", "5.1" (a sub-channel, rounded down) or junk (0).</summary>
    private static int Number(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? (int)Math.Floor(number) : 0;

    public static ImportedOrder Order(long? playbackOrder) => playbackOrder switch
    {
        1 => ImportedOrder.Chronological,
        2 or 7 => ImportedOrder.Random,
        3 or 4 or 5 => ImportedOrder.Shuffle,
        6 or 8 => ImportedOrder.SeasonEpisode,
        _ => ImportedOrder.Default
    };

    /// <summary>ErsatzTV's day lists: a JSON array of names (["Monday","Friday"]) or numbers, or a comma list.</summary>
    public static List<DayOfWeek> Days(string? text)
    {
        List<DayOfWeek> days = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return days;
        }

        IEnumerable<string> parts;
        try
        {
            using JsonDocument json = JsonDocument.Parse(text);
            parts = json.RootElement.ValueKind == JsonValueKind.Array
                ? [.. json.RootElement.EnumerateArray().Select(element => element.ValueKind == JsonValueKind.Number ? element.GetInt32().ToString(CultureInfo.InvariantCulture) : element.GetString() ?? string.Empty)]
                : [];
        }
        catch (JsonException)
        {
            parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        foreach (string part in parts)
        {
            if (Enum.TryParse(part, ignoreCase: true, out DayOfWeek day) && Enum.IsDefined(day) && !days.Contains(day))
            {
                days.Add(day);
            }
        }

        return days;
    }

    /// <summary>Looks things up in the database as the channels need them, each table read once.</summary>
    private sealed class Reader(SqliteSnapshot db, string folder)
    {
        private Dictionary<long, (string Title, string? JellyfinId, int? Year)>? _shows;
        private Dictionary<long, (long ShowId, int Number)>? _seasons;
        private Dictionary<long, ImportedMedia>? _episodes;
        private Dictionary<long, ImportedMedia>? _movies;
        private readonly Dictionary<long, ImportedContent> _collections = [];

        private Dictionary<long, (string Title, string? JellyfinId, int? Year)> Shows => _shows ??= LoadShows();

        private Dictionary<long, (long ShowId, int Number)> Seasons => _seasons ??= db.HasTable("Season")
            ? db.Rows($"SELECT s.Id, {db.Select("Season", "s", "SeasonNumber")} AS SeasonNumber, {db.Select("Season", "s", "ShowId")} AS ShowId FROM Season s")
                .Where(row => SqliteSnapshot.Long(row, "Id") is not null)
                .ToDictionary(row => SqliteSnapshot.Long(row, "Id")!.Value, row => (SqliteSnapshot.Long(row, "ShowId") ?? 0, SqliteSnapshot.Int(row, "SeasonNumber") ?? 0))
            : [];

        private Dictionary<long, ImportedMedia> Episodes => _episodes ??= LoadEpisodes();

        private Dictionary<long, ImportedMedia> Movies => _movies ??= LoadMovies();

        private Dictionary<long, (string, string?, int?)> LoadShows()
        {
            if (!db.HasTable("Show"))
            {
                return [];
            }

            string jellyfin = db.HasTable("JellyfinShow") ? "LEFT JOIN JellyfinShow js ON js.Id = s.Id" : string.Empty;
            string jellyfinId = db.HasTable("JellyfinShow") ? db.Select("JellyfinShow", "js", "ItemId") : "NULL";
            string metadata = db.HasTable("ShowMetadata") ? "LEFT JOIN ShowMetadata sm ON sm.ShowId = s.Id" : string.Empty;
            string title = db.HasTable("ShowMetadata") ? db.Select("ShowMetadata", "sm", "Title") : "NULL";
            string year = db.HasTable("ShowMetadata") ? db.Select("ShowMetadata", "sm", "Year") : "NULL";
            Dictionary<long, (string, string?, int?)> shows = [];
            foreach (Dictionary<string, object?> row in db.Rows($"SELECT s.Id, {title} AS Title, {year} AS Year, {jellyfinId} AS ItemId FROM Show s {metadata} {jellyfin}"))
            {
                if (SqliteSnapshot.Long(row, "Id") is { } id && !shows.ContainsKey(id))
                {
                    shows[id] = (SqliteSnapshot.Text(row, "Title") ?? $"Show {id}", SqliteSnapshot.Text(row, "ItemId"), SqliteSnapshot.Int(row, "Year"));
                }
            }

            return shows;
        }

        /// <summary>The file and running time of an item's first version, as subqueries on <paramref name="key"/> (EpisodeId, MovieId).</summary>
        private (string Path, string Duration) VersionColumns(string key, string alias)
        {
            if (!db.HasTable("MediaVersion") || db.Column("MediaVersion", key) is null)
            {
                return ("NULL", "NULL");
            }

            string path = db.HasTable("MediaFile")
                ? $"(SELECT mf.Path FROM MediaVersion mv JOIN MediaFile mf ON mf.MediaVersionId = mv.Id WHERE mv.{key} = {alias}.Id LIMIT 1)"
                : "NULL";
            string duration = db.Column("MediaVersion", "Duration") is not null
                ? $"(SELECT mv.Duration FROM MediaVersion mv WHERE mv.{key} = {alias}.Id LIMIT 1)"
                : "NULL";
            return (path, duration);
        }

        private Dictionary<long, ImportedMedia> LoadEpisodes()
        {
            if (!db.HasTable("Episode"))
            {
                return [];
            }

            (string path, string duration) = VersionColumns("EpisodeId", "e");
            bool metadata = db.HasTable("EpisodeMetadata");
            bool jellyfin = db.HasTable("JellyfinEpisode");
            string sql = $"SELECT e.Id, {db.Select("Episode", "e", "SeasonId")} AS SeasonId, "
                         + $"{(metadata ? db.Select("EpisodeMetadata", "em", "Title") : "NULL")} AS Title, "
                         + $"{(metadata ? db.Select("EpisodeMetadata", "em", "EpisodeNumber") : "NULL")} AS EpisodeNumber, "
                         + $"{(jellyfin ? db.Select("JellyfinEpisode", "je", "ItemId") : "NULL")} AS ItemId, {path} AS Path, {duration} AS Duration "
                         + "FROM Episode e "
                         + (metadata ? "LEFT JOIN EpisodeMetadata em ON em.EpisodeId = e.Id " : string.Empty)
                         + (jellyfin ? "LEFT JOIN JellyfinEpisode je ON je.Id = e.Id " : string.Empty);
            Dictionary<long, ImportedMedia> episodes = [];
            foreach (Dictionary<string, object?> row in db.Rows(sql))
            {
                if (SqliteSnapshot.Long(row, "Id") is not { } id || episodes.ContainsKey(id))
                {
                    continue;
                }

                (long showId, int? season) = SqliteSnapshot.Long(row, "SeasonId") is { } seasonId && Seasons.TryGetValue(seasonId, out var found)
                    ? (found.ShowId, found.Number)
                    : (0L, null);
                (string show, string? showJellyfin, _) = Shows.TryGetValue(showId, out var known) ? known : (string.Empty, null, null);
                episodes[id] = new ImportedMedia(SqliteSnapshot.Text(row, "ItemId"), SqliteSnapshot.Text(row, "Path"), SqliteSnapshot.Text(row, "Title") ?? $"Episode {id}",
                    show.Length == 0 ? null : show, showJellyfin, season, SqliteSnapshot.Int(row, "EpisodeNumber"), Duration: SqliteSnapshot.Span(row, "Duration") ?? TimeSpan.Zero);
            }

            return episodes;
        }

        private Dictionary<long, ImportedMedia> LoadMovies()
        {
            if (!db.HasTable("Movie"))
            {
                return [];
            }

            (string path, string duration) = VersionColumns("MovieId", "m");
            bool metadata = db.HasTable("MovieMetadata");
            bool jellyfin = db.HasTable("JellyfinMovie");
            string sql = $"SELECT m.Id, {(metadata ? db.Select("MovieMetadata", "mm", "Title") : "NULL")} AS Title, "
                         + $"{(metadata ? db.Select("MovieMetadata", "mm", "Year") : "NULL")} AS Year, "
                         + $"{(jellyfin ? db.Select("JellyfinMovie", "jm", "ItemId") : "NULL")} AS ItemId, {path} AS Path, {duration} AS Duration "
                         + "FROM Movie m "
                         + (metadata ? "LEFT JOIN MovieMetadata mm ON mm.MovieId = m.Id " : string.Empty)
                         + (jellyfin ? "LEFT JOIN JellyfinMovie jm ON jm.Id = m.Id " : string.Empty);
            Dictionary<long, ImportedMedia> movies = [];
            foreach (Dictionary<string, object?> row in db.Rows(sql))
            {
                if (SqliteSnapshot.Long(row, "Id") is { } id && !movies.ContainsKey(id))
                {
                    movies[id] = new ImportedMedia(SqliteSnapshot.Text(row, "ItemId"), SqliteSnapshot.Text(row, "Path"), SqliteSnapshot.Text(row, "Title") ?? $"Movie {id}",
                        IsMovie: true, Year: SqliteSnapshot.Int(row, "Year"), Duration: SqliteSnapshot.Span(row, "Duration") ?? TimeSpan.Zero);
                }
            }

            return movies;
        }

        /// <summary>One media item, whatever it is: a show or season (as content), or an episode or film (as an item).</summary>
        private ImportedContent? Item(long mediaItemId, ImportedOrder order)
        {
            if (Shows.TryGetValue(mediaItemId, out var show))
            {
                return ImportedContent.ForShow(show.Title, show.JellyfinId, order: order);
            }

            if (Seasons.TryGetValue(mediaItemId, out var season) && Shows.TryGetValue(season.ShowId, out var parent))
            {
                return ImportedContent.ForShow(parent.Title, parent.JellyfinId, [season.Number], order);
            }

            if (Episodes.TryGetValue(mediaItemId, out ImportedMedia? episode) || Movies.TryGetValue(mediaItemId, out episode))
            {
                return new ImportedContent { Kind = ImportedContentKind.Items, Name = episode.Show ?? episode.Title, Items = [episode], Order = order };
            }

            return null;
        }

        private ImportedContent Collection(long id)
        {
            if (_collections.TryGetValue(id, out ImportedContent? known))
            {
                return known;
            }

            string name = db.HasTable("Collection")
                ? db.Rows("SELECT Name FROM Collection WHERE Id = $id", ("$id", id)).Select(row => SqliteSnapshot.Text(row, "Name")).FirstOrDefault() ?? $"Collection {id}"
                : $"Collection {id}";
            List<ImportedMedia> items = [];
            List<ImportedContent> children = [];
            if (db.HasTable("CollectionItem"))
            {
                string index = db.Select("CollectionItem", "ci", "CustomIndex");
                foreach (Dictionary<string, object?> row in db.Rows($"SELECT ci.MediaItemId, {index} AS CustomIndex FROM CollectionItem ci WHERE ci.CollectionId = $id ORDER BY {index}, ci.MediaItemId", ("$id", id)))
                {
                    if (SqliteSnapshot.Long(row, "MediaItemId") is { } member && Item(member, ImportedOrder.Default) is { } content)
                    {
                        if (content.Kind == ImportedContentKind.Items)
                        {
                            items.AddRange(content.Items);
                        }
                        else
                        {
                            children.Add(content);
                        }
                    }
                }
            }

            return _collections[id] = new ImportedContent { Kind = ImportedContentKind.Group, Name = name, Items = items, Children = children, CollectionName = name };
        }

        private ImportedContent MultiCollection(long id, ImportedOrder order)
        {
            string name = db.HasTable("MultiCollection")
                ? db.Rows("SELECT Name FROM MultiCollection WHERE Id = $id", ("$id", id)).Select(row => SqliteSnapshot.Text(row, "Name")).FirstOrDefault() ?? $"Multi-collection {id}"
                : $"Multi-collection {id}";
            List<ImportedContent> children = [];
            if (db.HasTable("MultiCollectionItem"))
            {
                children.AddRange(db.Rows("SELECT CollectionId FROM MultiCollectionItem WHERE MultiCollectionId = $id", ("$id", id))
                    .Select(row => SqliteSnapshot.Long(row, "CollectionId")).OfType<long>().Select(Collection));
            }

            if (db.HasTable("MultiCollectionSmartItem"))
            {
                children.AddRange(db.Rows("SELECT SmartCollectionId FROM MultiCollectionSmartItem WHERE MultiCollectionId = $id", ("$id", id))
                    .Select(row => SqliteSnapshot.Long(row, "SmartCollectionId")).OfType<long>().Select(Smart));
            }

            return new ImportedContent { Kind = ImportedContentKind.Group, Name = name, Children = children, Order = order, CollectionName = name };
        }

        private ImportedContent Smart(long id)
        {
            Dictionary<string, object?>? row = db.HasTable("SmartCollection")
                ? db.Rows("SELECT Name, Query FROM SmartCollection WHERE Id = $id", ("$id", id)).FirstOrDefault()
                : null;
            string name = row is null ? $"Smart collection {id}" : SqliteSnapshot.Text(row, "Name") ?? $"Smart collection {id}";
            string query = row is null ? string.Empty : SqliteSnapshot.Text(row, "Query") ?? string.Empty;
            return ImportedContent.Unsupported(name, $"the smart collection \"{name}\" is a search in ErsatzTV's own syntax ({query}); make it again as a collection query");
        }

        /// <summary>What a schedule item, block item or filler preset plays, from its CollectionType and the id that goes with it.</summary>
        public ImportedContent? Content(IReadOnlyDictionary<string, object?> row)
        {
            ImportedOrder order = Order(SqliteSnapshot.Long(row, "PlaybackOrder"));
            long? collection = SqliteSnapshot.Long(row, "CollectionId");
            long? media = SqliteSnapshot.Long(row, "MediaItemId");
            long? multi = SqliteSnapshot.Long(row, "MultiCollectionId");
            long? smart = SqliteSnapshot.Long(row, "SmartCollectionId");
            ImportedContent? content = SqliteSnapshot.Long(row, "CollectionType") switch
            {
                CollectionKind when collection is { } id => Collection(id),
                ShowKind or SeasonKind or MovieKind or EpisodeKind when media is { } id => Item(id, order),
                MultiKind when multi is { } id => MultiCollection(id, order),
                SmartKind when smart is { } id => Smart(id),
                SearchKind => ImportedContent.Unsupported(SqliteSnapshot.Text(row, "SearchTitle") ?? "A search",
                    $"the search \"{SqliteSnapshot.Text(row, "SearchQuery")}\" is in ErsatzTV's own syntax; make it again as a collection query"),
                PlaylistKind => ImportedContent.Unsupported("A playlist", "ErsatzTV playlists aren't brought over; make one as a collection"),
                { } other => ImportedContent.Unsupported($"Content of kind {other}", "this kind of ErsatzTV content (music, other videos, images, streams…) isn't brought over"),
                _ => null
            };

            if (content is { Kind: ImportedContentKind.Group } && content.Order != order && order != ImportedOrder.Default)
            {
                // A shared collection keeps its members; this use of it plays them its own way.
                content = new ImportedContent
                {
                    Kind = content.Kind, Name = content.Name, Items = content.Items, Children = content.Children, CollectionName = content.CollectionName, Order = order
                };
            }

            return content;
        }

        /// <summary>The episodes and films a filler preset plays (shows in it are expanded to their episodes).</summary>
        public List<ImportedMedia> FillerMedia(long presetId)
        {
            if (!db.HasTable("FillerPreset"))
            {
                return [];
            }

            Dictionary<string, object?>? preset = db.Rows(
                $"SELECT {db.Select("FillerPreset", "f", "CollectionType")} AS CollectionType, {db.Select("FillerPreset", "f", "CollectionId")} AS CollectionId, "
                + $"{db.Select("FillerPreset", "f", "MediaItemId")} AS MediaItemId, {db.Select("FillerPreset", "f", "MultiCollectionId")} AS MultiCollectionId, "
                + $"{db.Select("FillerPreset", "f", "SmartCollectionId")} AS SmartCollectionId FROM FillerPreset f WHERE f.Id = $id", ("$id", presetId)).FirstOrDefault();
            return preset is not null && Content(preset) is { } content ? Expand(content) : [];
        }

        public List<ImportedMedia> Expand(ImportedContent content)
        {
            List<ImportedMedia> media = [.. content.Items];
            if (content.Kind == ImportedContentKind.Show)
            {
                media.AddRange(Episodes.Values.Where(episode => string.Equals(episode.Show, content.Name, StringComparison.OrdinalIgnoreCase)
                                                                && (content.Seasons.Count == 0 || content.Seasons.Contains(episode.Season ?? -1))));
            }

            foreach (ImportedContent child in content.Children)
            {
                media.AddRange(Expand(child));
            }

            return media;
        }

        /// <summary>The channel's logo: a web address as it is, else the cached file (ErsatzTV keeps a hash name under cache/artwork/logos).</summary>
        public string? Logo(long channelId)
        {
            if (db.Column("Artwork", "ChannelId") is null)
            {
                return null;
            }

            string? stored = db.Rows("SELECT Path FROM Artwork WHERE ChannelId = $id AND ArtworkKind = $kind", ("$id", channelId), ("$kind", LogoArtwork))
                .Select(row => SqliteSnapshot.Text(row, "Path")).FirstOrDefault(path => path is not null);
            if (stored is null)
            {
                return null;
            }

            if (Uri.TryCreate(stored, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https")
            {
                return stored;
            }

            string logos = Path.Combine(folder, "cache", "artwork", "logos");
            string[] candidates =
            [
                stored,
                Path.Combine(logos, stored),
                Path.Combine(logos, stored.Length > 2 ? stored[..2] : stored, stored),
                Path.Combine(folder, "cache", "artwork", stored)
            ];
            return candidates.FirstOrDefault(candidate => Path.IsPathRooted(candidate) && File.Exists(candidate));
        }

        /// <summary>The channel's playout: its classic or block schedule as slots and a rotation.</summary>
        public void ReadPlayout(ImportedChannel channel, long channelId)
        {
            if (!db.HasTable("Playout"))
            {
                channel.Notes.Add("It has no playout to bring over.");
                return;
            }

            Dictionary<string, object?>? playout = db.Rows(
                $"SELECT p.Id, {db.Select("Playout", "p", "ProgramScheduleId")} AS ProgramScheduleId, {db.Select("Playout", "p", "ScheduleKind")} AS ScheduleKind, "
                + $"{db.Select("Playout", "p", "ScheduleFile")} AS ScheduleFile FROM Playout p WHERE p.ChannelId = $id ORDER BY p.Id", ("$id", channelId)).FirstOrDefault();
            if (playout is null)
            {
                channel.Notes.Add("It has no playout in ErsatzTV, so there's nothing scheduled to bring over.");
                return;
            }

            long playoutId = SqliteSnapshot.Long(playout, "Id") ?? 0;
            long? kind = SqliteSnapshot.Long(playout, "ScheduleKind");
            long? scheduleId = SqliteSnapshot.Long(playout, "ProgramScheduleId");
            switch (kind)
            {
                case BlockSchedule:
                    ReadBlocks(channel, playoutId);
                    break;
                case Sequential or Scripted:
                    channel.Notes.Add($"Its {(kind == Sequential ? "sequential" : "scripted")} schedule ({SqliteSnapshot.Text(playout, "ScheduleFile")}) is a file of ErsatzTV's own and isn't brought over.");
                    break;
                default:
                    if (scheduleId is { } id)
                    {
                        ReadClassic(channel, id, []);
                        ReadAlternates(channel, playoutId);
                    }
                    else if (kind is not null and not Classic)
                    {
                        channel.Notes.Add("Its kind of playout isn't one that can be brought over.");
                    }

                    break;
            }
        }

        /// <summary>A classic schedule: items at a fixed time start a slot that runs to the next one; the rest rotate.</summary>
        private void ReadClassic(ImportedChannel channel, long scheduleId, IReadOnlyList<DayOfWeek> days)
        {
            if (!db.HasTable("ProgramScheduleItem"))
            {
                return;
            }

            string table = "ProgramScheduleItem";
            string Join(string sub) => db.HasTable(sub) ? $"LEFT JOIN {sub} ON {sub}.Id = i.Id" : string.Empty;
            string Is(string sub) => db.HasTable(sub) ? $"CASE WHEN {sub}.Id IS NULL THEN 0 ELSE 1 END" : "0";
            string sql = $"SELECT i.Id, {db.Select(table, "i", "StartTime")} AS StartTime, {db.Select(table, "i", "CollectionType")} AS CollectionType, "
                         + $"{db.Select(table, "i", "CollectionId")} AS CollectionId, {db.Select(table, "i", "MediaItemId")} AS MediaItemId, "
                         + $"{db.Select(table, "i", "MultiCollectionId")} AS MultiCollectionId, {db.Select(table, "i", "SmartCollectionId")} AS SmartCollectionId, "
                         + $"{db.Select(table, "i", "SearchTitle")} AS SearchTitle, {db.Select(table, "i", "SearchQuery")} AS SearchQuery, "
                         + $"{db.Select(table, "i", "PlaybackOrder")} AS PlaybackOrder, {db.Select(table, "i", "FallbackFillerId")} AS FallbackFillerId, "
                         + $"{db.Select(table, "i", "PostRollFillerId")} AS PostRollFillerId, "
                         + $"{Is("ProgramScheduleDurationItem")} AS IsDuration, "
                         + $"{(db.HasTable("ProgramScheduleDurationItem") ? db.Select("ProgramScheduleDurationItem", "ProgramScheduleDurationItem", "PlayoutDuration") : "NULL")} AS PlayoutDuration, "
                         + $"{Is("ProgramScheduleMultipleItem")} AS IsMultiple, "
                         + $"{(db.HasTable("ProgramScheduleMultipleItem") ? db.Select("ProgramScheduleMultipleItem", "ProgramScheduleMultipleItem", "Count") : "NULL")} AS MultipleCount "
                         + $"FROM {table} i {Join("ProgramScheduleDurationItem")} {Join("ProgramScheduleMultipleItem")} "
                         + $"WHERE i.ProgramScheduleId = $id ORDER BY {db.Select(table, "i", "Index")}, i.Id";

            List<(TimeSpan? Start, ImportedContent Content)> items = [];
            foreach (Dictionary<string, object?> row in db.Rows(sql, ("$id", scheduleId)))
            {
                if (Content(row) is not { } content)
                {
                    continue;
                }

                // How much each takes, for a rotation: a number of episodes, a length of time, or one.
                if (SqliteSnapshot.Int(row, "IsMultiple") == 1 && int.TryParse(SqliteSnapshot.Text(row, "MultipleCount"), out int count) && count > 0)
                {
                    content = content.WithWeight(count);
                }
                else if (SqliteSnapshot.Int(row, "IsDuration") == 1 && SqliteSnapshot.Span(row, "PlayoutDuration") is { TotalMinutes: > 0 } length)
                {
                    content = content.WithWeight(Math.Max(1, length.TotalMinutes / 30));
                }

                foreach (string column in new[] { "FallbackFillerId", "PostRollFillerId" })
                {
                    if (SqliteSnapshot.Long(row, column) is { } filler && channel.Filler.Count == 0)
                    {
                        channel.Filler.AddRange(FillerMedia(filler));
                    }
                }

                items.Add((SqliteSnapshot.Span(row, "StartTime"), content));
            }

            if (items.Count == 0)
            {
                return;
            }

            if (items.All(item => item.Start is null))
            {
                if (days.Count == 0)
                {
                    channel.Rotation.AddRange(items.Select(item => item.Content));
                }
                else
                {
                    // An alternate schedule for some days, with nothing at set times: all day on those days.
                    channel.Slots.Add(new ImportedSlot(TimeSpan.Zero, Together(items.Select(item => item.Content)), days, TimeSpan.FromDays(1)));
                }

                return;
            }

            // Items before the first fixed time belong to the last slot of the day (the schedule wraps round).
            int first = items.FindIndex(item => item.Start is not null);
            List<(TimeSpan? Start, ImportedContent Content)> ordered = [.. items.Skip(first), .. items.Take(first)];
            for (int index = 0; index < ordered.Count;)
            {
                TimeSpan start = ordered[index].Start!.Value;
                List<ImportedContent> contents = [ordered[index].Content];
                int next = index + 1;
                while (next < ordered.Count && ordered[next].Start is null)
                {
                    contents.Add(ordered[next].Content);
                    next++;
                }

                channel.Slots.Add(new ImportedSlot(start, Together(contents), days));
                index = next;
            }

            // Between the slots (and for anything a slot doesn't fill), the same things go round.
            if (days.Count == 0)
            {
                channel.Rotation.AddRange(items.Select(item => item.Content).Where(content => content.Kind != ImportedContentKind.Unsupported));
            }
        }

        /// <summary>Alternate schedules a playout uses on some days of the week: their slots on those days.</summary>
        private void ReadAlternates(ImportedChannel channel, long playoutId)
        {
            if (!db.HasTable("ProgramScheduleAlternate"))
            {
                return;
            }

            foreach (Dictionary<string, object?> row in db.Rows(
                         $"SELECT ProgramScheduleId, {db.Select("ProgramScheduleAlternate", "a", "DaysOfWeek")} AS DaysOfWeek, "
                         + $"{db.Select("ProgramScheduleAlternate", "a", "LimitToDateRange")} AS LimitToDateRange FROM ProgramScheduleAlternate a WHERE a.PlayoutId = $id",
                         ("$id", playoutId)))
            {
                List<DayOfWeek> days = Days(SqliteSnapshot.Text(row, "DaysOfWeek"));
                if (SqliteSnapshot.Long(row, "ProgramScheduleId") is not { } schedule || days.Count is 0 or 7 || SqliteSnapshot.Int(row, "LimitToDateRange") == 1)
                {
                    if (SqliteSnapshot.Int(row, "LimitToDateRange") == 1)
                    {
                        channel.Notes.Add("An alternate schedule limited to some dates of the year isn't brought over.");
                    }

                    continue;
                }

                ReadClassic(channel, schedule, days);
            }
        }

        /// <summary>A block schedule: each template's blocks at their start times, on the template's days.</summary>
        private void ReadBlocks(ImportedChannel channel, long playoutId)
        {
            if (!db.HasTable("PlayoutTemplate") || !db.HasTable("TemplateItem") || !db.HasTable("Block"))
            {
                channel.Notes.Add("Its block schedule couldn't be read.");
                return;
            }

            Dictionary<long, (string Name, int Minutes)> blocks = db.Rows($"SELECT b.Id, b.Name, {db.Select("Block", "b", "Minutes")} AS Minutes FROM Block b")
                .Where(row => SqliteSnapshot.Long(row, "Id") is not null)
                .ToDictionary(row => SqliteSnapshot.Long(row, "Id")!.Value, row => (SqliteSnapshot.Text(row, "Name") ?? "Block", SqliteSnapshot.Int(row, "Minutes") ?? 0));

            Dictionary<long, ImportedContent> contents = [];
            ImportedContent? BlockContent(long blockId)
            {
                if (contents.TryGetValue(blockId, out ImportedContent? known))
                {
                    return known;
                }

                if (!db.HasTable("BlockItem"))
                {
                    return null;
                }

                string table = "BlockItem";
                List<ImportedContent> items = [.. db.Rows(
                        $"SELECT {db.Select(table, "bi", "CollectionType")} AS CollectionType, {db.Select(table, "bi", "CollectionId")} AS CollectionId, "
                        + $"{db.Select(table, "bi", "MediaItemId")} AS MediaItemId, {db.Select(table, "bi", "MultiCollectionId")} AS MultiCollectionId, "
                        + $"{db.Select(table, "bi", "SmartCollectionId")} AS SmartCollectionId, {db.Select(table, "bi", "SearchTitle")} AS SearchTitle, "
                        + $"{db.Select(table, "bi", "SearchQuery")} AS SearchQuery, {db.Select(table, "bi", "PlaybackOrder")} AS PlaybackOrder "
                        + $"FROM BlockItem bi WHERE bi.BlockId = $id ORDER BY {db.Select(table, "bi", "Index")}", ("$id", blockId))
                    .Select(Content).OfType<ImportedContent>()];
                return contents[blockId] = Together(items, blocks.TryGetValue(blockId, out var block) ? block.Name : null);
            }

            string days = db.Select("PlayoutTemplate", "pt", "DaysOfWeek");
            foreach (Dictionary<string, object?> template in db.Rows(
                         $"SELECT pt.TemplateId, {days} AS DaysOfWeek, {db.Select("PlayoutTemplate", "pt", "LimitToDateRange")} AS LimitToDateRange "
                         + $"FROM PlayoutTemplate pt WHERE pt.PlayoutId = $id ORDER BY {db.Select("PlayoutTemplate", "pt", "Index")}", ("$id", playoutId)))
            {
                if (SqliteSnapshot.Int(template, "LimitToDateRange") == 1)
                {
                    channel.Notes.Add("A template limited to some dates of the year isn't brought over.");
                    continue;
                }

                List<DayOfWeek> on = Days(SqliteSnapshot.Text(template, "DaysOfWeek"));
                foreach (Dictionary<string, object?> item in db.Rows(
                             "SELECT BlockId, StartTime FROM TemplateItem WHERE TemplateId = $id ORDER BY StartTime", ("$id", SqliteSnapshot.Long(template, "TemplateId") ?? 0)))
                {
                    if (SqliteSnapshot.Long(item, "BlockId") is { } blockId && SqliteSnapshot.Span(item, "StartTime") is { } start && BlockContent(blockId) is { } content)
                    {
                        TimeSpan? length = blocks.TryGetValue(blockId, out var block) && block.Minutes > 0 ? TimeSpan.FromMinutes(block.Minutes) : null;
                        channel.Slots.Add(new ImportedSlot(start, content, on.Count == 7 ? [] : on, length));
                    }
                }
            }

            channel.Rotation.AddRange(contents.Values.Where(content => content.Kind != ImportedContentKind.Unsupported).DistinctBy(content => content.Name));
        }

        private static ImportedContent Together(IEnumerable<ImportedContent> contents, string? name = null)
        {
            List<ImportedContent> list = [.. contents];
            return list.Count == 1 && name is null
                ? list[0]
                : new ImportedContent { Kind = ImportedContentKind.Group, Name = name ?? string.Join(", ", list.Select(content => content.Name).Distinct()), Children = list };
        }
    }
}
