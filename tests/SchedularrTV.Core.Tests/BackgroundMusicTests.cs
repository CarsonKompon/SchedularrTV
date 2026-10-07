using SchedularrTV.Core.Media;
using SchedularrTV.LiveCapture;
using SchedularrTV.Plugins;
using SchedularrTV.WebChannels;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>Background music from a folder or any music library, shared by every plugin that plays music under something.</summary>
public sealed class BackgroundMusicTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "background-music-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void An_earlier_music_folder_becomes_folder_music_and_empty_music_is_none()
    {
        BackgroundMusic? music = BackgroundMusic.Tidy(null, " \"D:\\Music\\Beds\" ");
        Assert.NotNull(music);
        Assert.False(music.FromLibrary);
        Assert.Equal(@"D:\Music\Beds", music.Folder);

        Assert.Null(BackgroundMusic.Tidy(null, "  "));
        Assert.Null(BackgroundMusic.Tidy(new BackgroundMusic { Folder = " " }));
    }

    [Fact]
    public void Library_music_keeps_only_what_its_pick_uses()
    {
        BackgroundMusic? music = BackgroundMusic.Tidy(new BackgroundMusic
        {
            Folder = "D:/old",
            LibraryId = " jellyfin ",
            Pick = BackgroundMusicPick.Genres,
            PlaylistId = "p1",
            Genres = ["Jazz", " jazz ", "", "Lounge"]
        });

        Assert.NotNull(music);
        Assert.True(music.FromLibrary);
        Assert.Null(music.Folder);
        Assert.Null(music.PlaylistId);
        Assert.Equal("jellyfin", music.LibraryId);
        Assert.Equal(["Jazz", "Lounge"], music.Genres);
        Assert.Equal(["Jazz", "Lounge"], music.Query.Genres!);
        Assert.Null(music.Query.PlaylistId);
        Assert.True(music.IsComplete);
        Assert.False(new BackgroundMusic { LibraryId = "local", Pick = BackgroundMusicPick.Playlist }.IsComplete);
    }

    [Fact]
    public void The_key_changes_with_the_songs_it_stands_for()
    {
        var jazz = new BackgroundMusic { LibraryId = "jellyfin", Pick = BackgroundMusicPick.Genres, Genres = ["Jazz"] };
        BackgroundMusic copy = jazz.Copy();
        Assert.Equal(jazz.Key, copy.Key);

        copy.Genres.Add("Lounge");
        Assert.NotEqual(jazz.Key, copy.Key);
        Assert.Single(jazz.Genres);
        Assert.NotEqual(BackgroundMusic.FromFolder("D:/a").Key, BackgroundMusic.FromFolder("D:/b").Key);
    }

    [Fact]
    public async Task Songs_come_from_the_folder_or_the_library_it_names()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "sub"));
        File.WriteAllText(Path.Combine(_folder, "a.mp3"), "x");
        File.WriteAllText(Path.Combine(_folder, "sub", "b.flac"), "x");
        File.WriteAllText(Path.Combine(_folder, "cover.jpg"), "x");
        File.WriteAllText(Path.Combine(_folder, "._a.mp3"), "x");

        List<MusicStream> fromFolder = await Collect(BackgroundMusic.FromFolder(_folder), []);
        Assert.Equal(["a.mp3", "b.flac"], fromFolder.Select(song => Path.GetFileName(song.Input)).Order());

        var library = new FakeLibrary("jellyfin", ["t1", "t2"]);
        List<MusicStream> fromLibrary = await Collect(new BackgroundMusic { LibraryId = "JELLYFIN" }, [new FakeLibrary("local", ["x"]), library]);
        Assert.Equal(["http://music.test/t1", "http://music.test/t2"], fromLibrary.Select(song => song.Input).Order());
        Assert.All(fromLibrary, song => Assert.Equal("Authorization: test", song.Headers));

        // A library that isn't there plays nothing, rather than another library's songs.
        Assert.Empty(await Collect(new BackgroundMusic { LibraryId = "plex" }, [library]));
    }

    [Fact]
    public void Library_songs_are_decoded_one_by_one_with_their_headers()
    {
        string decode = string.Join(' ', MusicBed.DecodeArguments(new MusicStream("http://music.test/t1", "Authorization: test")));
        Assert.Contains("-headers", decode, StringComparison.Ordinal);
        Assert.Contains("-i http://music.test/t1 -vn -map 0:a:0 -ac 2 -ar 48000 -f s16le pipe:1", decode, StringComparison.Ordinal);
        Assert.DoesNotContain("-headers", MusicBed.DecodeArguments(new MusicStream(@"D:\Music\a.flac")));

        string copy = string.Join(' ', MusicFiles.CopyArguments(new MusicStream("http://music.test/t1", "Authorization: test"), "out.mka"));
        Assert.Contains("-c:a copy -f matroska out.mka", copy, StringComparison.Ordinal);
    }

    [Fact]
    public void A_live_page_reads_library_music_as_raw_sound_from_the_player()
    {
        var spec = new LiveStreamSpec
        {
            Id = "w", Name = "W", PageUrl = "http://localhost/page", Ffmpeg = "ffmpeg", Audio = LiveAudio.Music,
            Music = new BackgroundMusic { LibraryId = "jellyfin" }, Volume = 0.5
        };

        string arguments = string.Join(' ', LiveStream.Arguments(spec, null, "tcp://127.0.0.1:5000"));
        Assert.Contains("-f s16le -ar 48000 -ac 2 -thread_queue_size 1024 -i tcp://127.0.0.1:5000", arguments, StringComparison.Ordinal);
        Assert.Contains("[1:a]aformat=channel_layouts=stereo,volume=0.5[a]", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("concat", arguments, StringComparison.Ordinal);
        Assert.NotEqual(spec.Fingerprint, (spec with { Music = new BackgroundMusic { LibraryId = "local" } }).Fingerprint);
    }

    [Fact]
    public async Task A_promo_gets_the_same_song_each_time_and_a_file_stays_where_it_is()
    {
        string song = Path.Combine(_folder, "beds", "one.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(song)!);
        File.WriteAllText(song, "x");
        var library = new FakeLibrary("local", ["a", "b", "c"], file: song);
        var music = new BackgroundMusic { LibraryId = "local" };

        string? first = await MusicFiles.PickFileAsync(music, [library], "promo-key", "ffmpeg", Path.Combine(_folder, "cache"));
        string? again = await MusicFiles.PickFileAsync(music, [library], "promo-key", "ffmpeg", Path.Combine(_folder, "cache"));

        Assert.Equal(song, first);
        Assert.Equal(first, again);
        Assert.False(Directory.Exists(Path.Combine(_folder, "cache")));
        Assert.Null(await MusicFiles.PickFileAsync(new BackgroundMusic { LibraryId = "jellyfin" }, [library], "k", "ffmpeg", _folder));
    }

    [Fact]
    public void Without_songs_the_player_gives_silence()
    {
        Assert.Null(MusicBed.Start("ffmpeg", null, [], Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));

        using MusicBed bed = MusicBed.Start("ffmpeg", BackgroundMusic.FromFolder(Path.Combine(_folder, "none")), [], Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)!;
        short[] samples = [1, 2, 3, 4];
        Assert.False(bed.Read(samples));
        Assert.All(samples, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void A_web_channel_music_folder_from_an_earlier_version_carries_over()
    {
        var store = new ChannelStore(_folder);
        store.Save([new WebChannel { Name = "Weather", MusicFolder = "D:/Music/Weather" }]);

        WebChannel saved = store.Channels[0];
        Assert.Null(saved.MusicFolder);
        Assert.Equal("D:/Music/Weather", saved.Music?.Folder);

        WebChannel? reloaded = new ChannelStore(_folder).Find(saved.Id);
        Assert.Equal("D:/Music/Weather", reloaded?.Music?.Folder);
        Assert.DoesNotContain("music_folder", File.ReadAllText(Path.Combine(_folder, "channels.json")), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<List<MusicStream>> Collect(BackgroundMusic music, IEnumerable<IMusicLibrary> libraries)
    {
        List<MusicStream> songs = [];
        await foreach (MusicStream song in music.SongsAsync(libraries))
        {
            songs.Add(song);
        }

        return songs;
    }

    private sealed class FakeLibrary(string id, string[] trackIds, string? file = null) : IMusicLibrary
    {
        public string Id => id;

        public string Name => id;

        public bool IsConfigured => true;

        public Task<IReadOnlyList<MusicTrack>> GetTracksAsync(MusicQuery query, bool shuffle, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MusicTrack>>([.. trackIds.Select(track => new MusicTrack(track, track, [], null, null, TimeSpan.FromMinutes(3), null, [], null))]);

        public Task<IReadOnlyList<MusicChoice>> GetPlaylistsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MusicChoice>>([]);

        public Task<IReadOnlyList<MusicChoice>> GetGenresAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MusicChoice>>([]);

        public Task<IReadOnlyList<MusicChoice>> GetArtistsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MusicChoice>>([]);

        public Task<IReadOnlyList<MusicLyricLine>> GetLyricsAsync(string trackId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MusicLyricLine>>([]);

        public ValueTask<MusicStream?> GetStreamAsync(string trackId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<MusicStream?>(file is not null ? new MusicStream(file) : new MusicStream("http://music.test/" + trackId, "Authorization: test"));

        public ValueTask<ArtworkImage?> GetArtAsync(string artId, int width, CancellationToken cancellationToken = default) => ValueTask.FromResult<ArtworkImage?>(null);
    }
}
