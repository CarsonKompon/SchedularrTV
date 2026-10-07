using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Media;
using SchedularrTV.Providers.FileSystem;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>Music folders for radio channels, and the generic library contracts Jellyfin and local folders share.</summary>
public sealed class LocalMusicTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "local-music-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Timed_lyrics_are_read_from_lrc_and_plain_ones_line_by_line()
    {
        IReadOnlyList<MusicLyricLine> timed = LocalMusicLibrary.ParseLyrics("""
            [ar:Someone]
            [ti:A Song]
            [00:12.50]First line
            [00:20.00][01:05.25]Chorus
            [00:30:10]Third
            """);

        Assert.Equal(["First line", "Chorus", "Third", "Chorus"], timed.Select(line => line.Text));
        Assert.Equal(TimeSpan.FromSeconds(12.5), timed[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(30.1), timed[2].Start);
        Assert.Equal(TimeSpan.FromSeconds(65.25), timed[3].Start);

        IReadOnlyList<MusicLyricLine> plain = LocalMusicLibrary.ParseLyrics("One\r\n\r\nTwo");
        Assert.Equal(["One", "Two"], plain.Select(line => line.Text));
        Assert.All(plain, line => Assert.Null(line.Start));
    }

    [Theory]
    [InlineData(@"Daft Punk\Discovery\01 - One More Time.flac", "One More Time", "Daft Punk", "Discovery", 1)]
    [InlineData(@"Discovery\03. Digital Love.mp3", "Digital Love", null, "Discovery", 3)]
    [InlineData(@"Daft Punk - Around the World.mp3", "Around the World", "Daft Punk", null, null)]
    public void Untagged_songs_are_named_from_their_folders_and_file(string relative, string title, string? artist, string? album, int? track)
    {
        string root = Path.Combine(_folder, "Music");
        string path = Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar));

        Assert.Equal((title, artist, album, track), LocalMusicLibrary.FromNames(root, path));
    }

    [Fact]
    public void Tags_are_read_from_the_container_and_the_stream()
    {
        string path = Path.Combine(_folder, "song.ogg");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(path, "x");

        MusicProbeEntry? entry = LocalMusicLibrary.Parse(new FileInfo(path), """
            { "streams": [ { "tags": { "TITLE": "Stream Title", "ARTIST": "Band" } } ],
              "format": { "duration": "201.5", "tags": { "album": "Record" } } }
            """);

        Assert.NotNull(entry);
        Assert.Equal(TimeSpan.FromSeconds(201.5).Ticks, entry.DurationTicks);
        Assert.Equal("Record", entry.Tags["album"]);
        Assert.Equal("Stream Title", entry.Tags["title"]);
        Assert.Null(LocalMusicLibrary.Parse(new FileInfo(path), """{ "format": { } }"""));
    }

    [Fact]
    public async Task A_music_folder_is_a_music_library_and_not_schedule_items()
    {
        string music = Path.Combine(_folder, "Music");
        Directory.CreateDirectory(Path.Combine(music, "Band", "Record"));
        File.WriteAllText(Path.Combine(music, "Band", "Record", "01 - Song.flac"), "x");
        var options = Options.Create(new FileSystemLibraryOptions
        {
            Folders = [new MediaFolderOptions { Path = music, Kind = MediaKind.Music }],
            FfprobePath = Path.Combine(_folder, "no-ffprobe-here"),
            CachePath = Path.Combine(_folder, "state", "probe-cache.json")
        });

        var library = new LocalMusicLibrary(options, NullLogger<LocalMusicLibrary>.Instance);
        Assert.True(library.IsConfigured);
        Assert.Equal("local", library.Id);
        // No ffprobe: a song can't be measured, so it isn't offered (as with videos).
        Assert.Empty(await library.GetTracksAsync(new MusicQuery(), shuffle: false));

        var videos = new FileSystemMediaLibrary(options, NullLogger<FileSystemMediaLibrary>.Instance);
        Assert.Empty(await videos.SearchAsync(SchedularrTV.Core.Query.QueryExpression.All));
        Assert.Empty(await videos.GetLibrariesAsync());
        Assert.False(videos.Owns("0123456789abcdef0123456789abcdef"));
    }

    [Fact]
    public void A_station_takes_the_library_it_names_or_the_first_set_up()
    {
        Assert.Null(MusicLibraries.Pick([], "jellyfin"));
        Assert.Equal(50, new MediaSourceProgress("Reading", 5, 10, DateTimeOffset.UnixEpoch).Percent);
        Assert.Null(new MediaSourceProgress("Reading", 5, null, DateTimeOffset.UnixEpoch).Percent);
    }
}
