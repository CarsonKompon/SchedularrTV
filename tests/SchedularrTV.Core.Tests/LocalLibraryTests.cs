using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;
using SchedularrTV.Providers.FileSystem;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>Local TV and movie libraries: Sonarr/Radarr/Kodi naming, .nfo files and the pictures beside the videos.</summary>
public sealed class LocalLibraryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "local-library-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    // --- Names -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Clone High - S01E02 - Election Blu-Galoo.mkv", false, 1, 2, null, "Election Blu-Galoo")]
    [InlineData("clone.high.s01e02.720p.web-dl.mkv", false, 1, 2, null, null)]
    [InlineData("Show - S02E03E04 - Two Parter.mkv", false, 2, 3, 4, "Two Parter")]
    [InlineData("Show - S02E03-E04 - Two Parter.mkv", false, 2, 3, 4, "Two Parter")]
    [InlineData("Show - S02E03-04.mkv", false, 2, 3, 4, null)]
    [InlineData("Show - S01E01 - Pilot WEBDL-1080p.mkv", false, 1, 1, null, "Pilot")]
    [InlineData("Show 3x07 Title.avi", false, 3, 7, null, "Title")]
    [InlineData("05 - The Fifth.mkv", true, null, 5, null, "The Fifth")]
    [InlineData("Episode 12.mkv", true, null, 12, null, null)]
    [InlineData("Show 1920x1080 Something.mkv", false, null, null, null, null)]
    public void Episode_names_are_read_the_way_Sonarr_writes_them(string file, bool inSeason, int? season, int? episode, int? last, string? title)
    {
        EpisodeName name = LocalNaming.Episode(file, inSeason);

        Assert.Equal((season, episode, last, title), (name.Season, name.Episode, name.LastEpisode, name.Title));
    }

    [Fact]
    public void A_daily_show_is_dated()
    {
        EpisodeName name = LocalNaming.Episode("The Daily Show - 2024-03-15 - Guest.mkv", inSeasonFolder: false);

        Assert.Equal(new DateOnly(2024, 3, 15), name.Aired);
        Assert.Equal("Guest", name.Title);
    }

    [Theory]
    [InlineData("Season 1", 1)]
    [InlineData("Season 01", 1)]
    [InlineData("S02", 2)]
    [InlineData("Specials", 0)]
    [InlineData("Series 3", 3)]
    [InlineData("Extras", null)]
    [InlineData("Seasoning", null)]
    public void Season_folders_are_recognised(string folder, int? season) => Assert.Equal(season, LocalNaming.SeasonFolder(folder));

    [Fact]
    public void Folder_names_give_the_title_the_year_and_any_ids()
    {
        FolderName name = LocalNaming.Folder("Clone High (2002) [tvdbid-71035] {tmdb-1234}");

        Assert.Equal(("Clone High", (int?)2002), (name.Title, name.Year));
        Assert.Equal("71035", name.Ids["tvdb_id"]);
        Assert.Equal("1234", name.Ids["tmdb_id"]);
        Assert.Null(LocalNaming.Folder("Doctor Who").Year);
    }

    [Theory]
    [InlineData("The Matrix (1999)", "The Matrix", 1999, null)]
    [InlineData("The Matrix (1999) - 1080p Bluray", "The Matrix", 1999, null)]
    [InlineData("The.Matrix.1999.1080p.BluRay.x264", "The Matrix", 1999, null)]
    [InlineData("Kill Bill (2003) - cd2", "Kill Bill", 2003, 2)]
    [InlineData("2001 A Space Odyssey (1968)", "2001 A Space Odyssey", 1968, null)]
    [InlineData("Some Home Movie", "Some Home Movie", null, null)]
    public void Movie_names_are_read_the_way_Radarr_writes_them(string name, string title, int? year, int? part)
    {
        MovieName movie = LocalNaming.Movie(name);

        Assert.Equal((title, year, part), (movie.Title, movie.Year, movie.Part));
    }

    [Theory]
    [InlineData("Movie-trailer.mkv", true)]
    [InlineData("sample.mkv", true)]
    [InlineData("Movie (2010)-featurette.mkv", true)]
    [InlineData("Movie (2010).mkv", false)]
    public void Extras_are_left_out(string file, bool extra) => Assert.Equal(extra, LocalNaming.IsExtraFile(file));

    [Theory]
    [InlineData("Rated PG-13", "PG-13")]
    [InlineData("US:TV-PG", "TV-PG")]
    [InlineData("US:PG-13 / GB:12A", "PG-13")]
    [InlineData("TV-14", "TV-14")]
    public void Ratings_are_tidied(string raw, string rating) => Assert.Equal(rating, LocalNaming.Rating(raw));

    // --- .nfo --------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_tvshow_nfo_is_read()
    {
        NfoInfo show = Assert.Single(NfoReader.Parse("""
            <?xml version="1.0" encoding="utf-8" standalone="yes"?>
            <tvshow>
              <title>Clone High</title>
              <year>2002</year>
              <plot>Clones of history's figures go to high school.</plot>
              <mpaa>US:TV-14</mpaa>
              <genre>Animation</genre>
              <genre>Comedy / Teen</genre>
              <studio>MTV</studio>
              <tag>Cartoons</tag>
              <uniqueid type="tvdb" default="true">71035</uniqueid>
              <imdb_id>tt0305011</imdb_id>
            </tvshow>
            """));

        Assert.Equal(("tvshow", "Clone High", (int?)2002, "TV-14"), (show.Kind, show.Title, show.Year, show.Rating));
        Assert.Equal(["Animation", "Comedy", "Teen"], show.Genres);
        Assert.Equal(["MTV"], show.Studios);
        Assert.Equal(["Cartoons"], show.Tags);
        Assert.Equal("71035", show.Ids["tvdb_id"]);
        Assert.Equal("tt0305011", show.Ids["imdb_id"]);
    }

    [Fact]
    public void A_multi_episode_nfo_with_a_link_after_it_gives_every_episode()
    {
        IReadOnlyList<NfoInfo> episodes = NfoReader.Parse("""
            <episodedetails><title>Part One</title><season>1</season><episode>2</episode><aired>2002-11-02</aired>
              <fileinfo><streamdetails><video><durationinseconds>2640</durationinseconds></video></streamdetails></fileinfo></episodedetails>
            <episodedetails><title>Part Two</title><season>1</season><episode>3</episode></episodedetails>
            https://thetvdb.com/?tab=episode&id=1
            """);

        Assert.Equal(2, episodes.Count);
        Assert.Equal((1, 2, new DateOnly(2002, 11, 2)), (episodes[0].Season!.Value, episodes[0].Episode!.Value, episodes[0].Premiered!.Value));
        Assert.Equal(TimeSpan.FromSeconds(2640), episodes[0].Duration);
        Assert.Equal("Part Two", episodes[1].Title);
    }

    [Fact]
    public void A_movie_nfo_gives_its_set_and_runtime_and_nothing_breaks_on_junk()
    {
        NfoInfo movie = Assert.Single(NfoReader.Parse("""
            <movie><title>The Matrix</title><premiered>1999-03-31</premiered><mpaa>Rated R</mpaa>
              <set><name>The Matrix Collection</name></set><runtime>136</runtime><uniqueid type="tmdb">603</uniqueid></movie>
            """));

        Assert.Equal(("The Matrix", (int?)1999, "R"), (movie.Title, movie.Year, movie.Rating));
        Assert.Equal(["The Matrix Collection"], movie.Sets);
        Assert.Equal(TimeSpan.FromMinutes(136), movie.Duration);
        Assert.Equal("603", movie.Ids["tmdb_id"]);

        Assert.Empty(NfoReader.Parse("https://www.themoviedb.org/movie/603"));
        Assert.Empty(NfoReader.Parse("<movie><title>Broken & bad</title>"));
    }

    // --- Libraries ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_tv_folder_becomes_shows_with_seasons_episodes_metadata_and_pictures()
    {
        string tv = Path.Combine(_folder, "TV");
        string show = Path.Combine(tv, "Clone High (2002) [tvdbid-71035]");
        Write(Path.Combine(show, "tvshow.nfo"),
            "<tvshow><title>Clone High</title><mpaa>TV-14</mpaa><genre>Animation</genre><studio>MTV</studio><tag>Teen</tag></tvshow>");
        Write(Path.Combine(show, "poster.jpg"));
        Write(Path.Combine(show, "fanart.jpg"));
        Video(Path.Combine(show, "Season 01", "Clone High - S01E01 - Escape to Beer Mountain.mkv"),
            "<episodedetails><title>Escape to Beer Mountain: A Rope of Sand</title><season>1</season><episode>1</episode>"
            + "<aired>2002-11-02</aired><plot>Abe runs for class president.</plot><runtime>22</runtime></episodedetails>");
        Video(Path.Combine(show, "Season 01", "Clone High - S01E02E03 - Two Parter.mkv"),
            "<episodedetails><title>Part One</title><episode>2</episode><runtime>44</runtime></episodedetails>"
            + "<episodedetails><title>Part Two</title><episode>3</episode></episodedetails>");
        Write(Path.Combine(show, "Season 01", "Clone High - S01E02E03 - Two Parter-thumb.jpg"));
        Video(Path.Combine(show, "Specials", "Clone High - Making Of.mkv"), "<episodedetails><title>Making Of</title><runtime>10</runtime></episodedetails>");
        Video(Path.Combine(show, "Extras", "Clone High - Trailer.mkv"), "<episodedetails><runtime>2</runtime></episodedetails>");
        Video(Path.Combine(tv, "Cartoons", "Daria", "Season 1", "Daria - 1x01 - Esteemsters.mkv"), "<episodedetails><runtime>22</runtime></episodedetails>");

        FileSystemMediaLibrary library = Library(new MediaFolderOptions { Path = tv, Kind = MediaKind.Episode });
        IReadOnlyList<MediaItem> items = await library.SearchAsync(QueryExpression.All);

        Assert.Equal(4, items.Count);
        Assert.All(items, item => Assert.Equal(MediaKind.Episode, item.Kind));

        MediaItem pilot = items.Single(item => item.EpisodeNumber == 1 && item.SeriesName == "Clone High");
        Assert.Equal("Escape to Beer Mountain: A Rope of Sand", pilot.Title);
        Assert.Equal((1, TimeSpan.FromMinutes(22), "TV-14", 2002), (pilot.SeasonNumber!.Value, pilot.Duration, pilot.Rating, pilot.PremiereDate!.Value.Year));
        Assert.Equal(["Animation"], pilot.Genres);
        Assert.Equal(["MTV"], pilot.Networks);
        Assert.Contains("Teen", pilot.Tags);
        Assert.Equal("71035", pilot.Attributes["series_tvdb_id"]);
        Assert.StartsWith(LocalLibraryIds.ItemPrefix, pilot.Id, StringComparison.Ordinal);
        Assert.StartsWith(LocalLibraryIds.SeriesPrefix, pilot.SeriesId, StringComparison.Ordinal);

        MediaItem twoParter = items.Single(item => item.EpisodeNumber == 2);
        Assert.Equal("Part One / Part Two", twoParter.Title);
        Assert.Equal("3", twoParter.Attributes["episode_end"]);

        Assert.Equal(0, items.Single(item => item.Title == "Making Of").SeasonNumber);

        MediaItem daria = items.Single(item => item.SeriesName == "Daria");
        Assert.Equal((1, 1, "Esteemsters"), (daria.SeasonNumber!.Value, daria.EpisodeNumber!.Value, daria.Title));
        Assert.Contains("Cartoons", daria.Tags);

        // The show's poster and fanart, and the two-parter's own still.
        LocalImages? showImages = await library.ImagesAsync(pilot.SeriesId!);
        Assert.EndsWith("poster.jpg", showImages!.Primary, StringComparison.Ordinal);
        Assert.EndsWith("fanart.jpg", showImages.Backdrop, StringComparison.Ordinal);
        LocalImages? still = await library.ImagesAsync(twoParter.Id);
        Assert.EndsWith("Two Parter-thumb.jpg", still!.Primary, StringComparison.Ordinal);
        Assert.Equal(showImages.Backdrop, still.Backdrop);
        Assert.Null((await library.ImagesAsync(pilot.Id))?.Primary);

        // Queries reach them like Jellyfin's.
        Assert.Equal(3, QueryEvaluator.Filter(items, QueryParser.Parse("show:\"Clone High\" AND genre:Animation")).Count);
    }

    [Fact]
    public async Task A_movie_folder_becomes_movies_with_versions_sets_and_pictures()
    {
        string movies = Path.Combine(_folder, "Movies");
        string matrix = Path.Combine(movies, "The Matrix (1999)");
        Write(Path.Combine(matrix, "movie.nfo"),
            "<movie><title>The Matrix</title><mpaa>Rated R</mpaa><genre>Action</genre><set>The Matrix Collection</set><runtime>136</runtime></movie>");
        Write(Path.Combine(matrix, "poster.jpg"));
        Write(Path.Combine(matrix, "fanart.jpg"));
        Write(Path.Combine(matrix, "The Matrix (1999) - 1080p.mkv"));
        Write(Path.Combine(matrix, "The Matrix (1999) - 4K.mkv"));
        Write(Path.Combine(matrix, "The Matrix (1999)-trailer.mkv"));
        Video(Path.Combine(movies, "Loose.Movie.2010.720p.BluRay.mkv"), "<movie><runtime>90</runtime></movie>");

        FileSystemMediaLibrary library = Library(new MediaFolderOptions { Path = movies, Kind = MediaKind.Movie });
        IReadOnlyList<MediaItem> items = await library.SearchAsync(QueryExpression.All);

        Assert.Equal(3, items.Count);
        MediaItem[] versions = [.. items.Where(item => item.Title == "The Matrix")];
        Assert.Equal(2, versions.Length);
        Assert.Single(versions.Select(item => item.VersionGroupId).Distinct());
        Assert.NotNull(versions[0].VersionGroupId);
        Assert.All(versions, item =>
        {
            Assert.Equal(MediaKind.Movie, item.Kind);
            Assert.Equal(("R", 1999, TimeSpan.FromMinutes(136)), (item.Rating, item.PremiereDate!.Value.Year, item.Duration));
            Assert.Equal(["The Matrix Collection"], item.Collections);
            Assert.Equal(["Action"], item.Genres);
        });

        MediaItem loose = items.Single(item => item.Title == "Loose Movie");
        Assert.Equal(2010, loose.PremiereDate!.Value.Year);
        Assert.Null(loose.VersionGroupId);

        LocalImages? poster = await library.ImagesAsync(versions[0].Id);
        Assert.EndsWith("poster.jpg", poster!.Primary, StringComparison.Ordinal);
        Assert.Null(await library.ImagesAsync(loose.Id));
    }

    [Fact]
    public async Task Commercials_folders_are_unchanged_and_have_no_pictures()
    {
        string ads = Path.Combine(_folder, "Ads");
        Write(Path.Combine(ads, "Nick", "ad.mkv"));

        // No ffprobe and no .nfo: a commercial can't be measured, so it's left out, as before.
        FileSystemMediaLibrary library = Library(new MediaFolderOptions { Path = ads, Tags = ["Commercials"] });
        Assert.Empty(await library.SearchAsync(QueryExpression.All));
        Assert.False(LocalLibraryIds.IsLocalId("fs:0123456789abcdef"));
    }

    private FileSystemMediaLibrary Library(MediaFolderOptions folder) => new(
        Options.Create(new FileSystemLibraryOptions
        {
            Folders = [folder],
            // No ffprobe here: lengths come from the .nfo files.
            FfprobePath = Path.Combine(_folder, "no-ffprobe-here"),
            CachePath = Path.Combine(_folder, "state", "probe-cache.json")
        }),
        NullLogger<FileSystemMediaLibrary>.Instance);

    /// <summary>A video (empty: it's never played) with its .nfo beside it.</summary>
    private static void Video(string path, string nfo)
    {
        Write(path);
        Write(Path.ChangeExtension(path, ".nfo"), nfo);
    }

    private static void Write(string path, string content = "")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
