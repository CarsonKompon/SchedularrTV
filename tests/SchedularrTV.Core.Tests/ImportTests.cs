using Microsoft.Data.Sqlite;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Service.Import;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class ImportTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "import-tests-" + Guid.NewGuid().ToString("N"));

    public ImportTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TestFolders.Delete(_folder);
    }

    private static readonly MediaItem GoodBurger = new()
    {
        Id = "movie-1", Title = "Good Burger", Kind = MediaKind.Movie, Duration = TimeSpan.FromMinutes(95),
        PremiereDate = new DateTimeOffset(1997, 7, 25, 0, 0, 0, TimeSpan.Zero), Path = "/media/movies/Good Burger (1997).mkv"
    };

    private static IReadOnlyList<MediaItem> Library() =>
    [
        TestMedia.Episode("Rugrats", 1, 1, TimeSpan.FromMinutes(22)),
        TestMedia.Episode("Rugrats", 1, 2, TimeSpan.FromMinutes(22)),
        TestMedia.Episode("Doug", 1, 1, TimeSpan.FromMinutes(22)),
        TestMedia.Episode("Doug", 1, 2, TimeSpan.FromMinutes(22)),
        GoodBurger
    ];

    private string Database(string name, string sql)
    {
        string path = Path.Combine(_folder, name);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
        return path;
    }

    private static void Valid(ImportPlan plan)
    {
        var config = new SchedulerConfig { Channels = [.. plan.Channels.Select(channel => channel.Channel).OfType<ChannelConfig>()] };
        foreach ((string key, ContentSelector collection) in plan.Collections)
        {
            config.GlobalCollections[key] = collection;
        }

        // Through YAML and back, as it will be saved.
        SchedulerConfig saved = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config));
        ConfigValidationResult result = SchedulerConfigLoader.Validate(saved);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void An_ErsatzTV_classic_schedule_becomes_blocks_a_rotation_and_a_shared_collection()
    {
        string path = Database(ErsatzTvImporter.DatabaseName, """
            CREATE TABLE Channel (Id INTEGER PRIMARY KEY, Name TEXT, Number TEXT, "Group" TEXT, FallbackFillerId INTEGER);
            CREATE TABLE MediaItem (Id INTEGER PRIMARY KEY, LibraryPathId INTEGER, State INTEGER);
            CREATE TABLE Show (Id INTEGER PRIMARY KEY);
            CREATE TABLE ShowMetadata (Id INTEGER PRIMARY KEY, ShowId INTEGER, Title TEXT, Year INTEGER);
            CREATE TABLE JellyfinShow (Id INTEGER PRIMARY KEY, ItemId TEXT);
            CREATE TABLE Season (Id INTEGER PRIMARY KEY, SeasonNumber INTEGER, ShowId INTEGER);
            CREATE TABLE Episode (Id INTEGER PRIMARY KEY, SeasonId INTEGER);
            CREATE TABLE EpisodeMetadata (Id INTEGER PRIMARY KEY, EpisodeId INTEGER, Title TEXT, EpisodeNumber INTEGER);
            CREATE TABLE Movie (Id INTEGER PRIMARY KEY);
            CREATE TABLE MovieMetadata (Id INTEGER PRIMARY KEY, MovieId INTEGER, Title TEXT, Year INTEGER);
            CREATE TABLE Collection (Id INTEGER PRIMARY KEY, Name TEXT);
            CREATE TABLE CollectionItem (CollectionId INTEGER, MediaItemId INTEGER, CustomIndex INTEGER);
            CREATE TABLE ProgramSchedule (Id INTEGER PRIMARY KEY, Name TEXT);
            CREATE TABLE ProgramScheduleItem (Id INTEGER PRIMARY KEY, ProgramScheduleId INTEGER, "Index" INTEGER, StartTime TEXT, CollectionType INTEGER,
                CollectionId INTEGER, MediaItemId INTEGER, MultiCollectionId INTEGER, SmartCollectionId INTEGER, PlaybackOrder INTEGER);
            CREATE TABLE ProgramScheduleFloodItem (Id INTEGER PRIMARY KEY);
            CREATE TABLE ProgramScheduleMultipleItem (Id INTEGER PRIMARY KEY, MultipleMode INTEGER, Count TEXT);
            CREATE TABLE Playout (Id INTEGER PRIMARY KEY, ChannelId INTEGER, ProgramScheduleId INTEGER, ScheduleKind INTEGER, ScheduleFile TEXT);

            INSERT INTO Channel VALUES (1, 'Cartoons', '5', 'Kids', NULL), (2, 'Scripted', '6.1', 'ErsatzTV', NULL);
            INSERT INTO Show VALUES (10), (11);
            INSERT INTO ShowMetadata VALUES (1, 10, 'Rugrats', 1991), (2, 11, 'Doug', 1991);
            INSERT INTO JellyfinShow VALUES (10, 'Rugrats');
            INSERT INTO Season VALUES (20, 1, 11);
            INSERT INTO Episode VALUES (30, 20);
            INSERT INTO EpisodeMetadata VALUES (1, 30, 'Doug Bags a Neematoad', 2);
            INSERT INTO Movie VALUES (40);
            INSERT INTO MovieMetadata VALUES (1, 40, 'Good Burger', 1997);
            INSERT INTO Collection VALUES (1, 'Weekend');
            INSERT INTO CollectionItem VALUES (1, 30, 0), (1, 40, 1);
            INSERT INTO ProgramSchedule VALUES (1, 'Cartoons');
            INSERT INTO ProgramScheduleItem VALUES
                (1, 1, 0, '08:00:00', 1, NULL, 10, NULL, NULL, 6),
                (2, 1, 1, NULL, 2, NULL, 20, NULL, NULL, 0),
                (3, 1, 2, '20:00:00', 0, 1, NULL, NULL, NULL, 3);
            INSERT INTO ProgramScheduleMultipleItem VALUES (2, 0, '3');
            INSERT INTO Playout VALUES (1, 1, 1, 1, NULL), (2, 2, NULL, 4, '/scripts/channel.py');
            """);

        ImportedSchedule schedule = ErsatzTvImporter.Read(_folder);
        Assert.Equal(new[] { "Cartoons", "Scripted" }, schedule.Channels.Select(channel => channel.Name));
        Assert.Equal(6, schedule.Channels[1].Number);
        Assert.Contains(schedule.Channels[1].Notes, note => note.Contains("scripted", StringComparison.Ordinal));

        ImportPlan plan = new ImportTranslator(Library()).Plan(schedule, new SchedulerConfig());
        ChannelConfig cartoons = plan.Channels[0].Channel!;
        Assert.Equal(("cartoons", 5, "Kids"), (cartoons.Id, cartoons.Number, cartoons.Group));

        // 8am to 8pm: Rugrats, then Doug's first season; 8pm to 8am: the Weekend collection.
        Assert.Equal(2, cartoons.Blocks.Count);
        BlockConfig day = cartoons.Blocks[0], night = cartoons.Blocks[1];
        Assert.Equal((TimeSpan.FromHours(8), TimeSpan.FromHours(20)), (day.Time!.Value, day.EndTime!.Value));
        Assert.Equal((TimeSpan.FromHours(20), TimeSpan.FromHours(8)), (night.Time!.Value, night.EndTime!.Value));
        Assert.Equal(new[] { "Rugrats", "Doug" }, day.Shows[0].Include.Select(include => include.Show));
        Assert.Equal("Rugrats", day.Shows[0].Include[0].ShowId);
        Assert.Equal(PlaybackOrder.SeasonEpisode, day.Shows[0].Include[0].Order);
        Assert.Equal(new[] { 1 }, day.Shows[0].Include[1].Seasons);

        string key = night.Shows[0].Collection!;
        Assert.Equal(PlaybackOrder.Shuffle, night.Shows[0].Order);
        List<ContentInclude> weekend = plan.Collections[key].Include;
        Assert.Equal(new[] { "S01E02" }, weekend.Single(include => include.Show == "Doug").Episodes);
        Assert.Equal("id IN ['movie-1']", weekend.Single(include => include.Query is not null).Query);

        // Round and round: everything, Doug's season three times as often (it played three at a time).
        List<ContentInclude> rotation = cartoons.BaseRotation!.Include;
        Assert.Equal(3, rotation.Single(include => include.Show == "Doug" && include.Seasons.Count > 0).Weight);
        Assert.Equal(1, rotation.Single(include => include.Show == "Rugrats").Weight);

        Assert.Null(plan.Channels[1].Channel);
        Assert.Equal(0, plan.Channels[0].Missing);
        Valid(plan);
    }

    [Fact]
    public void A_Tunarr_list_becomes_a_weighted_rotation_and_its_time_slots_blocks()
    {
        string path = Database(TunarrImporter.DatabaseName, """
            CREATE TABLE channel (uuid TEXT PRIMARY KEY, number INTEGER, name TEXT, group_title TEXT, icon TEXT);
            CREATE TABLE program (uuid TEXT PRIMARY KEY, type TEXT, title TEXT, duration INTEGER, episode INTEGER, season_number INTEGER, show_title TEXT,
                year INTEGER, external_key TEXT, source_type TEXT, file_path TEXT, tv_show_uuid TEXT);
            CREATE TABLE program_grouping (uuid TEXT PRIMARY KEY, type TEXT, title TEXT, external_key TEXT, source_type TEXT);
            CREATE TABLE custom_show (uuid TEXT PRIMARY KEY, name TEXT);
            CREATE TABLE custom_show_content (content_uuid TEXT, custom_show_uuid TEXT, "index" INTEGER);

            INSERT INTO channel VALUES ('ch-1', 7, 'Toons', 'tunarr', '{"path":"http://tunarr.local/logo.png","width":0}'), ('ch-2', 8, 'Evenings', 'Nights', NULL), ('ch-3', 7, 'Empty', NULL, NULL);
            INSERT INTO program VALUES
                ('p1', 'episode', 'Tommy Pickles and the Great White Thing', 1320000, 1, 1, NULL, 1991, 'Rugrats-s01e01', 'jellyfin', NULL, 'g1'),
                ('p2', 'episode', 'Barbecue Story', 1320000, 2, 1, NULL, 1991, 'Rugrats-s01e02', 'jellyfin', NULL, 'g1'),
                ('p3', 'movie', 'Good Burger', 5700000, NULL, NULL, NULL, 1997, '123', 'plex', NULL, NULL),
                ('p4', 'episode', 'Somewhere else', 1320000, 9, 9, 'Not here', 2000, 'x', 'plex', NULL, NULL);
            INSERT INTO program_grouping VALUES ('g1', 'show', 'Rugrats', 'Rugrats', 'jellyfin');
            """);
        string lineups = Path.Combine(_folder, TunarrImporter.LineupFolder);
        Directory.CreateDirectory(lineups);
        File.WriteAllText(Path.Combine(lineups, "ch-1.json"), """
            { "version": 6, "items": [
                { "type": "content", "id": "p1", "durationMs": 1320000 }, { "type": "content", "id": "p2", "durationMs": 1320000 },
                { "type": "content", "id": "p1", "durationMs": 1320000 }, { "type": "offline", "durationMs": 60000 },
                { "type": "content", "id": "p3", "durationMs": 5700000 }, { "type": "content", "id": "p4", "durationMs": 1320000 } ] }
            """);
        File.WriteAllText(Path.Combine(lineups, "ch-2.json"), """
            { "version": 6, "items": [ { "type": "content", "id": "p1", "durationMs": 1320000 }, { "type": "content", "id": "p3", "durationMs": 5700000 } ],
              "schedule": { "type": "time", "period": "day", "slots": [
                { "type": "show", "showId": "g1", "order": "next", "startTime": 64800000 },
                { "type": "movie", "order": "shuffle", "startTime": 72000000 } ] } }
            """);

        ImportedSchedule schedule = TunarrImporter.Read(path);
        Assert.Equal("http://tunarr.local/logo.png", schedule.Channels[0].Logo);

        ImportPlan plan = new ImportTranslator(Library()).Plan(schedule, new SchedulerConfig { Channels = [new ChannelConfig { Id = "toons", Name = "Mine", Number = 7 }] });

        // A plain list: Rugrats three times as often as the film; the programme Jellyfin doesn't have is counted, not added.
        ImportedChannelPlan toons = plan.Channels.Single(channel => channel.Source.Name == "Toons");
        ChannelConfig first = toons.Channel!;
        Assert.Equal(("toons-2", 9), (first.Id, first.Number));
        Assert.Equal("http://tunarr.local/logo.png", first.Logo);
        Assert.Null(first.Group);
        Assert.Empty(first.Blocks);
        ContentInclude rugrats = first.BaseRotation!.Include.Single(include => include.ShowId == "Rugrats");
        Assert.Equal(3, rugrats.Weight);
        Assert.Equal(PlaybackOrder.SeasonEpisode, rugrats.Order);
        Assert.Equal(1, first.BaseRotation.Include.Single(include => include.Query == "id IN ['movie-1']").Weight);
        Assert.Equal(1, toons.Missing);
        Assert.Contains("Not here", toons.MissingNames);

        // Time slots: Rugrats 6pm to 8pm, films 8pm to 6pm, and both between.
        ChannelConfig evenings = plan.Channels.Single(channel => channel.Source.Name == "Evenings").Channel!;
        Assert.Equal(("Nights", 8), (evenings.Group, evenings.Number));
        Assert.Equal(new[] { (TimeSpan.FromHours(18), TimeSpan.FromHours(20)), (TimeSpan.FromHours(20), TimeSpan.FromHours(18)) },
            evenings.Blocks.Select(block => (block.Time!.Value, block.EndTime!.Value)));
        Assert.Equal(PlaybackOrder.Shuffle, evenings.Blocks[1].Shows[0].Order);
        Assert.Equal(2, evenings.BaseRotation!.Include.Count);

        // A channel with no lineup at all has nothing to bring over.
        Assert.Null(plan.Channels.Single(channel => channel.Source.Name == "Empty").Channel);
        Valid(plan);
    }

    [Fact]
    public void Programmes_are_found_by_id_by_file_under_another_folder_and_by_episode()
    {
        var translator = new ImportTranslator(
        [
            new MediaItem { Id = "0f8b2c1e9a4d4b7e8c1f2a3b4c5d6e7f", Title = "One", Kind = MediaKind.Episode, SeriesId = "s", SeriesName = "Show", SeasonNumber = 1, EpisodeNumber = 1, Path = @"D:\TV\Show\Season 1\One.mkv" },
            new MediaItem { Id = "b", Title = "Two", Kind = MediaKind.Episode, SeriesId = "s", SeriesName = "Show", SeasonNumber = 1, EpisodeNumber = 2, Path = @"D:\TV\Show\Season 1\Two.mkv" }
        ]);

        Assert.Equal("0f8b2c1e9a4d4b7e8c1f2a3b4c5d6e7f", translator.Find(new ImportedMedia("0F8B2C1E-9A4D-4B7E-8C1F-2A3B4C5D6E7F", null, "One"))?.Id);
        Assert.Equal("b", translator.Find(new ImportedMedia(null, "/mnt/tv/Show/Season 1/Two.mkv", "Two"))?.Id);
        Assert.Equal("b", translator.Find(new ImportedMedia(null, null, "Two", "show", null, 1, 2))?.Id);
        Assert.Null(translator.Find(new ImportedMedia(null, null, "Three", "Show", null, 1, 3)));
    }
}
