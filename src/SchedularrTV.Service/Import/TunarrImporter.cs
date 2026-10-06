using System.Globalization;
using System.Text.Json;

namespace SchedularrTV.Service.Import;

/// <summary>
/// Reads Tunarr's channels from its data folder: <c>db.db</c> (channels, programmes, shows, custom shows, filler lists)
/// and <c>channel-lineups/&lt;channel&gt;.json</c> (each channel's programmes in order, and its time-slot or random-slot
/// schedule when it was made with one). A slot schedule becomes timed slots or a weighted rotation; a plain list of
/// programmes becomes a rotation of its shows and films, each as often as it came up.
/// </summary>
public static class TunarrImporter
{
    public const string DatabaseName = "db.db";
    public const string LineupFolder = "channel-lineups";

    /// <summary>Where Tunarr keeps its data on this computer, when it's there.</summary>
    public static IEnumerable<string> LikelyPaths()
    {
        if (Environment.GetEnvironmentVariable("TUNARR_DATABASE_PATH") is { Length: > 0 } configured)
        {
            yield return configured;
        }

        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "tunarr");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "tunarr");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Preferences", "tunarr");
        yield return Path.Combine("/config", "tunarr");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tunarr");
    }

    /// <summary>The database in a data folder, or the file itself.</summary>
    public static string ResolvePath(string path) =>
        Directory.Exists(path) ? Path.Combine(path, DatabaseName) : path;

    // Named apart from the app's own Program class.
    private sealed record TunarrProgram(string Id, ImportedMedia Media, string? ShowId);

    public static ImportedSchedule Read(string path)
    {
        string file = ResolvePath(path);
        string folder = Path.GetDirectoryName(Path.GetFullPath(file)) ?? ".";
        using SqliteSnapshot db = SqliteSnapshot.Open(file);
        if (!db.HasTable("channel") || !db.HasTable("program"))
        {
            throw new InvalidOperationException($"{file} doesn't look like Tunarr's database (it has no channel or program table).");
        }

        var schedule = new ImportedSchedule { Source = ImportSource.Tunarr, From = folder };
        Dictionary<string, (string Title, string? JellyfinId)> shows = Shows(db);
        Dictionary<string, TunarrProgram> programs = Programs(db, shows);
        Dictionary<string, (string Name, List<string> Programs)> customShows = Lists(db, "custom_show", "custom_show_content",
            ["custom_show_uuid", "customShowUuid"], ["content_uuid", "contentUuid"]);
        Dictionary<string, (string Name, List<string> Programs)> fillers = Lists(db, "filler_show", "filler_show_content",
            ["filler_show_uuid", "fillerShowUuid"], ["program_uuid", "programUuid"]);

        string uuid = db.Column("channel", "uuid", "id") ?? "uuid";
        string group = db.Select("channel", "c", "group_title", "groupTitle");
        string icon = db.Select("channel", "c", "icon");
        foreach (Dictionary<string, object?> row in db.Rows($"SELECT c.{SqliteSnapshot.Quote(uuid)} AS Id, c.number AS Number, c.name AS Name, {group} AS GroupTitle, {icon} AS Icon FROM channel c"))
        {
            string id = SqliteSnapshot.Text(row, "Id") ?? Guid.NewGuid().ToString();
            var channel = new ImportedChannel
            {
                SourceId = id,
                Name = SqliteSnapshot.Text(row, "Name") ?? "Channel",
                Number = SqliteSnapshot.Int(row, "Number") ?? 0,
                Group = SqliteSnapshot.Text(row, "GroupTitle") is { } title && title != "tunarr" ? title : null,
                Logo = IconPath(SqliteSnapshot.Text(row, "Icon"))
            };

            ReadFiller(db, channel, id, fillers, programs);
            ReadLineup(channel, Path.Combine(folder, LineupFolder, id + ".json"), programs, shows, customShows);
            schedule.Channels.Add(channel);
        }

        if (!Directory.Exists(Path.Combine(folder, LineupFolder)))
        {
            schedule.Warnings.Add($"There's no {LineupFolder} folder beside the database, so only the channels' programmes (not their order or schedules) could be read.");
        }

        List<ImportedChannel> ordered = [.. schedule.Channels.OrderBy(channel => channel.Number)];
        schedule.Channels.Clear();
        schedule.Channels.AddRange(ordered);
        return schedule;
    }

    private static Dictionary<string, (string, string?)> Shows(SqliteSnapshot db)
    {
        Dictionary<string, (string, string?)> shows = new(StringComparer.OrdinalIgnoreCase);
        if (!db.HasTable("program_grouping"))
        {
            return shows;
        }

        string key = db.Select("program_grouping", "g", "external_key", "externalKey");
        string source = db.Select("program_grouping", "g", "source_type", "sourceType");
        foreach (Dictionary<string, object?> row in db.Rows($"SELECT g.uuid AS Id, g.title AS Title, {key} AS ExternalKey, {source} AS SourceType FROM program_grouping g WHERE g.type = 'show'"))
        {
            if (SqliteSnapshot.Text(row, "Id") is { } id)
            {
                shows[id] = (SqliteSnapshot.Text(row, "Title") ?? "Show", IsJellyfin(SqliteSnapshot.Text(row, "SourceType")) ? SqliteSnapshot.Text(row, "ExternalKey") : null);
            }
        }

        if (db.HasTable("program_grouping_external_id"))
        {
            string group = db.Select("program_grouping_external_id", "x", "group_uuid", "groupUuid");
            string external = db.Select("program_grouping_external_id", "x", "external_key", "externalKey");
            string type = db.Select("program_grouping_external_id", "x", "source_type", "sourceType");
            foreach (Dictionary<string, object?> row in db.Rows($"SELECT {group} AS GroupId, {external} AS ExternalKey FROM program_grouping_external_id x WHERE {type} = 'jellyfin'"))
            {
                if (SqliteSnapshot.Text(row, "GroupId") is { } id && shows.TryGetValue(id, out var show) && show.Item2 is null)
                {
                    shows[id] = (show.Item1, SqliteSnapshot.Text(row, "ExternalKey"));
                }
            }
        }

        return shows;
    }

    private static bool IsJellyfin(string? sourceType) => string.Equals(sourceType, "jellyfin", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, TunarrProgram> Programs(SqliteSnapshot db, Dictionary<string, (string Title, string? JellyfinId)> shows)
    {
        string table = "program";
        string sql = $"SELECT p.uuid AS Id, {db.Select(table, "p", "type")} AS Type, {db.Select(table, "p", "title")} AS Title, "
                     + $"{db.Select(table, "p", "duration")} AS Duration, {db.Select(table, "p", "episode")} AS Episode, "
                     + $"{db.Select(table, "p", "season_number", "seasonNumber")} AS Season, {db.Select(table, "p", "show_title", "showTitle")} AS ShowTitle, "
                     + $"{db.Select(table, "p", "year")} AS Year, {db.Select(table, "p", "external_key", "externalKey")} AS ExternalKey, "
                     + $"{db.Select(table, "p", "source_type", "sourceType")} AS SourceType, {db.Select(table, "p", "file_path", "filePath")} AS FilePath, "
                     + $"{db.Select(table, "p", "tv_show_uuid", "tvShowUuid")} AS ShowId, {db.Select(table, "p", "grandparent_external_key", "grandparentExternalKey")} AS ShowKey "
                     + "FROM program p";

        // Jellyfin's id for each programme, when it was found there too (a Plex programme can carry one).
        Dictionary<string, string> jellyfin = new(StringComparer.OrdinalIgnoreCase);
        if (db.HasTable("program_external_id"))
        {
            string program = db.Select("program_external_id", "x", "program_uuid", "programUuid");
            string external = db.Select("program_external_id", "x", "external_key", "externalKey");
            string type = db.Select("program_external_id", "x", "source_type", "sourceType");
            foreach (Dictionary<string, object?> row in db.Rows($"SELECT {program} AS ProgramId, {external} AS ExternalKey FROM program_external_id x WHERE {type} = 'jellyfin'"))
            {
                if (SqliteSnapshot.Text(row, "ProgramId") is { } id && SqliteSnapshot.Text(row, "ExternalKey") is { } key)
                {
                    jellyfin.TryAdd(id, key);
                }
            }
        }

        Dictionary<string, TunarrProgram> programs = new(StringComparer.OrdinalIgnoreCase);
        foreach (Dictionary<string, object?> row in db.Rows(sql))
        {
            if (SqliteSnapshot.Text(row, "Id") is not { } id)
            {
                continue;
            }

            bool fromJellyfin = IsJellyfin(SqliteSnapshot.Text(row, "SourceType"));
            string? showId = SqliteSnapshot.Text(row, "ShowId");
            (string Title, string? JellyfinId)? show = showId is not null && shows.TryGetValue(showId, out var found) ? found : null;
            bool movie = string.Equals(SqliteSnapshot.Text(row, "Type"), "movie", StringComparison.OrdinalIgnoreCase);
            var media = new ImportedMedia(
                fromJellyfin ? SqliteSnapshot.Text(row, "ExternalKey") : jellyfin.GetValueOrDefault(id),
                SqliteSnapshot.Text(row, "FilePath"),
                SqliteSnapshot.Text(row, "Title") ?? "Programme",
                movie ? null : show?.Title ?? SqliteSnapshot.Text(row, "ShowTitle"),
                movie ? null : show?.JellyfinId ?? (fromJellyfin ? SqliteSnapshot.Text(row, "ShowKey") : null),
                movie ? null : SqliteSnapshot.Int(row, "Season"),
                movie ? null : SqliteSnapshot.Int(row, "Episode"),
                movie,
                SqliteSnapshot.Int(row, "Year"),
                TimeSpan.FromMilliseconds(SqliteSnapshot.Long(row, "Duration") ?? 0));
            programs[id] = new TunarrProgram(id, media, showId);
        }

        return programs;
    }

    /// <summary>Custom shows or filler lists: each one's name and programmes in order.</summary>
    private static Dictionary<string, (string, List<string>)> Lists(SqliteSnapshot db, string table, string contents, string[] owner, string[] program)
    {
        Dictionary<string, (string, List<string>)> lists = new(StringComparer.OrdinalIgnoreCase);
        if (!db.HasTable(table))
        {
            return lists;
        }

        foreach (Dictionary<string, object?> row in db.Rows($"SELECT uuid AS Id, name AS Name FROM {SqliteSnapshot.Quote(table)}"))
        {
            if (SqliteSnapshot.Text(row, "Id") is { } id)
            {
                lists[id] = (SqliteSnapshot.Text(row, "Name") ?? id, []);
            }
        }

        if (db.HasTable(contents) && db.Column(contents, owner) is { } ownerColumn && db.Column(contents, program) is { } programColumn)
        {
            string index = db.Select(contents, "c", "index");
            foreach (Dictionary<string, object?> row in db.Rows(
                         $"SELECT c.{SqliteSnapshot.Quote(ownerColumn)} AS Owner, c.{SqliteSnapshot.Quote(programColumn)} AS Program FROM {SqliteSnapshot.Quote(contents)} c ORDER BY {index}"))
            {
                if (SqliteSnapshot.Text(row, "Owner") is { } id && lists.TryGetValue(id, out var list) && SqliteSnapshot.Text(row, "Program") is { } member)
                {
                    list.Item2.Add(member);
                }
            }
        }

        return lists;
    }

    private static void ReadFiller(SqliteSnapshot db, ImportedChannel channel, string channelId, Dictionary<string, (string Name, List<string> Programs)> fillers,
        Dictionary<string, TunarrProgram> programs)
    {
        if (!db.HasTable("channel_filler_show") || db.Column("channel_filler_show", "channel_uuid", "channelUuid") is not { } channelColumn
            || db.Column("channel_filler_show", "filler_show_uuid", "fillerShowUuid") is not { } fillerColumn)
        {
            return;
        }

        foreach (Dictionary<string, object?> row in db.Rows(
                     $"SELECT {SqliteSnapshot.Quote(fillerColumn)} AS Filler FROM channel_filler_show WHERE {SqliteSnapshot.Quote(channelColumn)} = $id", ("$id", channelId)))
        {
            if (SqliteSnapshot.Text(row, "Filler") is { } id && fillers.TryGetValue(id, out var list))
            {
                channel.Filler.AddRange(list.Programs.Select(program => programs.GetValueOrDefault(program)?.Media).OfType<ImportedMedia>());
            }
        }
    }

    /// <summary>The channel's icon address, from the JSON Tunarr keeps it in.</summary>
    public static string? IconPath(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("path", out JsonElement path)
                   && path.ValueKind == JsonValueKind.String && path.GetString() is { Length: > 0 } value
                ? value
                : null;
        }
        catch (JsonException)
        {
            return json.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? json : null;
        }
    }

    public static ImportedOrder Order(string? order) => order?.ToLowerInvariant() switch
    {
        "shuffle" or "ordered_shuffle" => ImportedOrder.Shuffle,
        "chronological" => ImportedOrder.Chronological,
        "next" or "alphanumeric" => ImportedOrder.SeasonEpisode,
        _ => ImportedOrder.Default
    };

    private static void ReadLineup(ImportedChannel channel, string path, Dictionary<string, TunarrProgram> programs,
        Dictionary<string, (string Title, string? JellyfinId)> shows, Dictionary<string, (string Name, List<string> Programs)> customShows)
    {
        if (!File.Exists(path))
        {
            channel.Notes.Add("Its lineup file wasn't found, so it has no programmes to bring over.");
            return;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        List<(TunarrProgram Program, string? CustomShow)> lineup = [];
        int redirects = 0;
        if (root.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in items.EnumerateArray())
            {
                string? type = Text(item, "type");
                if (type == "redirect")
                {
                    redirects++;
                }

                if (type == "content" && Text(item, "id") is { } id && programs.TryGetValue(id, out TunarrProgram? program))
                {
                    lineup.Add((program, Text(item, "customShowId")));
                }
            }
        }

        if (redirects > 0)
        {
            channel.Notes.Add("Its redirects to other channels aren't brought over.");
        }

        if (root.TryGetProperty("schedule", out JsonElement plan) && plan.ValueKind == JsonValueKind.Object
            && plan.TryGetProperty("slots", out JsonElement slots) && slots.ValueKind == JsonValueKind.Array)
        {
            bool timed = Text(plan, "type") == "time";
            bool weekly = Text(plan, "period") == "week";
            foreach (JsonElement slot in slots.EnumerateArray())
            {
                if (SlotContent(slot, lineup, shows, customShows) is not { } content)
                {
                    continue;
                }

                if (timed && Number(slot, "startTime") is { } startMs)
                {
                    TimeSpan start = TimeSpan.FromMilliseconds(startMs);
                    List<DayOfWeek> days = [];
                    if (weekly)
                    {
                        // A week's slots count from the start of Sunday.
                        days.Add((DayOfWeek)((int)start.TotalDays % 7));
                        start = TimeSpan.FromMilliseconds(startMs % TimeSpan.FromDays(1).TotalMilliseconds);
                    }

                    channel.Slots.Add(new ImportedSlot(start, content, days));
                }
                else
                {
                    if (Number(slot, "weight") is { } weight && weight > 0)
                    {
                        content = content.WithWeight(weight);
                    }

                    channel.Rotation.Add(content);
                }
            }

            if (timed)
            {
                // What the slots play goes round between them too.
                channel.Rotation.AddRange(channel.Slots.Select(slot => slot.Content).Where(content => content.Kind != ImportedContentKind.Unsupported)
                    .DistinctBy(content => content.Name));
            }

            if (channel.Slots.Count > 0 || channel.Rotation.Count > 0)
            {
                return;
            }
        }

        // A plain list: its shows and films, each as often as it came up.
        foreach (IGrouping<string, (TunarrProgram Program, string? CustomShow)> group in lineup.GroupBy(entry =>
                     entry.CustomShow is { } custom ? "custom:" + custom
                     : entry.Program.Media.IsMovie ? "movies"
                     : "show:" + (entry.Program.Media.Show ?? entry.Program.Media.Title)))
        {
            List<ImportedMedia> media = [.. group.Select(entry => entry.Program.Media).DistinctBy(item => item.JellyfinId ?? item.Path ?? item.Title)];
            ImportedContent content = group.Key switch
            {
                var key when key.StartsWith("custom:", StringComparison.Ordinal) && customShows.TryGetValue(key[7..], out var custom) =>
                    new ImportedContent { Kind = ImportedContentKind.Items, Name = custom.Name, Items = media, CollectionName = custom.Name, Order = ImportedOrder.SeasonEpisode },
                "movies" => new ImportedContent { Kind = ImportedContentKind.Items, Name = "Movies", Items = media, Order = ImportedOrder.Shuffle },
                _ => new ImportedContent
                {
                    Kind = ImportedContentKind.Show, Name = media[0].Show ?? media[0].Title, ShowJellyfinId = media[0].ShowJellyfinId, Items = media,
                    Seasons = [.. media.Select(item => item.Season).OfType<int>().Distinct().Order()], Order = ImportedOrder.SeasonEpisode
                }
            };
            content.Weight = group.Count();
            channel.Rotation.Add(content);
        }

        if (lineup.Count > 0)
        {
            channel.Notes.Add("Tunarr played these in a fixed order; here they're a rotation of the same shows and films, in season and episode order, each about as often.");
        }
    }

    /// <summary>What a time or random slot plays.</summary>
    private static ImportedContent? SlotContent(JsonElement slot, List<(TunarrProgram Program, string? CustomShow)> lineup,
        Dictionary<string, (string Title, string? JellyfinId)> shows, Dictionary<string, (string Name, List<string> Programs)> customShows)
    {
        // Newer lineups put the kind on the slot; older ones under "programming".
        JsonElement what = slot.TryGetProperty("programming", out JsonElement nested) && nested.ValueKind == JsonValueKind.Object ? nested : slot;
        ImportedOrder order = Order(Text(slot, "order") ?? Text(what, "order"));
        switch (Text(what, "type"))
        {
            case "show":
                string? showId = Text(what, "showId");
                List<int> seasons = [];
                if (what.TryGetProperty("seasonFilter", out JsonElement filter) && filter.ValueKind == JsonValueKind.Array)
                {
                    seasons.AddRange(filter.EnumerateArray().Where(season => season.ValueKind == JsonValueKind.Number).Select(season => season.GetInt32()));
                }

                if (showId is not null && shows.TryGetValue(showId, out var show))
                {
                    return ImportedContent.ForShow(show.Title, show.JellyfinId, seasons, order);
                }

                // Older lineups name the show by its title; else its episodes on the channel say which it is.
                ImportedMedia? episode = lineup.Select(entry => entry.Program).FirstOrDefault(program => program.ShowId == showId)?.Media;
                string? title = episode?.Show ?? showId;
                return title is null ? null : ImportedContent.ForShow(title, episode?.ShowJellyfinId, seasons, order);
            case "movie":
                List<ImportedMedia> movies = [.. lineup.Select(entry => entry.Program.Media).Where(media => media.IsMovie).DistinctBy(media => media.JellyfinId ?? media.Path ?? media.Title)];
                return movies.Count > 0
                    ? new ImportedContent { Kind = ImportedContentKind.Items, Name = "Movies", Items = movies, Order = order == ImportedOrder.Default ? ImportedOrder.Shuffle : order }
                    : new ImportedContent { Kind = ImportedContentKind.AllMovies, Name = "Movies", Order = order };
            case "custom-show":
                if (Text(what, "customShowId") is { } customId && customShows.TryGetValue(customId, out var custom))
                {
                    List<ImportedMedia> items = [.. lineup.Where(entry => entry.CustomShow == customId).Select(entry => entry.Program.Media)
                        .DistinctBy(media => media.JellyfinId ?? media.Path ?? media.Title)];
                    return new ImportedContent { Kind = ImportedContentKind.Items, Name = custom.Name, Items = items, CollectionName = custom.Name, Order = order };
                }

                return null;
            case "flex":
            case "filler":
                // Gaps are filled with the channel's breaks.
                return null;
            case "redirect":
                return ImportedContent.Unsupported(Text(what, "channelName") ?? "Another channel", "a slot playing another channel isn't brought over");
            case { } other:
                return ImportedContent.Unsupported(other, $"Tunarr's \"{other}\" slots aren't brought over");
            default:
                return null;
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
}
