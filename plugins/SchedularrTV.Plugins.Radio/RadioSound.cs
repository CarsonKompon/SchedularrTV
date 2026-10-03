using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using SchedularrTV.Providers.Jellyfin;

namespace SchedularrTV.Radio;

/// <summary>
/// A station's sound, as one endless MPEG-TS (AAC) stream for the station's own ffmpeg: library songs one after
/// another, or an internet station relayed. It also keeps <see cref="RadioPlayers"/> up to date, which is what the
/// station's page shows, so the picture follows the sound.
/// </summary>
public sealed class RadioSound(
    RadioStore store,
    RadioSettings settings,
    RadioPlayers players,
    OnlineLookups online,
    IServiceProvider services,
    IHttpClientFactory httpClients,
    ILogger<RadioSound> logger)
{
    public const string StreamClientName = "radio-stream";

    public async Task ServeAsync(string stationId, HttpContext context)
    {
        if (store.Find(stationId) is not { } station)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        CancellationToken cancellationToken = context.RequestAborted;
        context.Response.ContentType = "video/mp2t";
        context.Response.Headers.CacheControl = "no-cache, no-store";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await context.Response.StartAsync(cancellationToken).ConfigureAwait(false);

        var clock = Stopwatch.StartNew();
        try
        {
            if (station.Source == RadioSource.Internet)
            {
                await InternetAsync(station, context.Response.Body, clock, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await LibraryAsync(station, context.Response.Body, clock, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // The station's ffmpeg went away.
        }
        finally
        {
            players.Clear(stationId);
        }
    }

    // --- Library songs ---------------------------------------------------------------------------

    private async Task LibraryAsync(RadioStation station, Stream output, Stopwatch clock, CancellationToken cancellationToken)
    {
        Queue<JellyfinTrack> queue = new();
        while (!cancellationToken.IsCancellationRequested)
        {
            station = store.Find(station.Id) ?? station;
            JellyfinClient? jellyfin = services.GetService<JellyfinClient>();
            if (queue.Count < 2 && jellyfin is not null)
            {
                try
                {
                    foreach (JellyfinTrack track in await jellyfin.GetTracksAsync(Query(station), station.Shuffle, cancellationToken).ConfigureAwait(false))
                    {
                        queue.Enqueue(track);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Radio {Station}: couldn't get songs from Jellyfin: {Message}", station.Name, ex.Message);
                }
            }

            if (jellyfin is null || queue.Count == 0)
            {
                players.Set(station.Id, new NowPlayingState(null, DateTimeOffset.UtcNow, null,
                    jellyfin is null ? "Jellyfin isn't set up." : "No songs match this station's music."));
                await PlayAsync(output, Silence(TimeSpan.FromSeconds(15), clock.Elapsed), clock, null, cancellationToken).ConfigureAwait(false);
                continue;
            }

            JellyfinTrack song = queue.Dequeue();
            SongInfo info = Library(song, []);
            SongInfo? next = queue.TryPeek(out JellyfinTrack? following) ? Library(following, []) : null;
            (string url, string authorization) = jellyfin.GetAudioStream(song.Id);

            // The page shows the song from its first sound; its lyrics follow as soon as they're found.
            RadioStation current = station;
            List<string> arguments = SongArguments(url, authorization, song.Duration, clock.Elapsed);
            await PlayAsync(output, arguments, clock, () =>
            {
                players.Set(current.Id, new NowPlayingState(info, DateTimeOffset.UtcNow, next));
                if (current.ShowLyrics)
                {
                    _ = AddLyricsAsync(current, song, jellyfin, cancellationToken);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static JellyfinMusicQuery Query(RadioStation station) => station.Pick switch
    {
        MusicPick.Playlist when station.PlaylistId is { } playlist => new JellyfinMusicQuery(PlaylistId: playlist),
        MusicPick.Genres when station.Genres.Count > 0 => new JellyfinMusicQuery(Genres: station.Genres),
        MusicPick.Artists when station.Artists.Count > 0 => new JellyfinMusicQuery(Artists: station.Artists),
        _ => new JellyfinMusicQuery(Limit: 500)
    };

    /// <summary>A library song as the page shows it; its cover comes through the plugin, which asks Jellyfin.</summary>
    public static SongInfo Library(JellyfinTrack track, IReadOnlyList<LyricLine> lyrics) => new(
        track.Title,
        track.Artist,
        track.Album,
        track.Year,
        $"{RadioSettings.RoutePrefix}/art/{Uri.EscapeDataString(track.ArtItemId)}",
        (long)track.Duration.TotalMilliseconds,
        lyrics);

    private async Task AddLyricsAsync(RadioStation station, JellyfinTrack song, JellyfinClient jellyfin, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<LyricLine> lyrics = [.. (await jellyfin.GetLyricsAsync(song.Id, cancellationToken).ConfigureAwait(false))
                .Select(line => new LyricLine(line.Text, line.Start is { } start ? (long)start.TotalMilliseconds : null))];
            if (lyrics.Count == 0 && station.LookUpOnline && song.Artist.Length > 0)
            {
                lyrics = await online.LyricsAsync(song.Artist, song.Title, song.Album, song.Duration, cancellationToken).ConfigureAwait(false);
            }

            if (lyrics.Count > 0)
            {
                players.Update(station.Id, state => state.Song is { } playing && playing.Title == song.Title ? state with { Song = playing with { Lyrics = lyrics } } : state);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Radio {Station}: no lyrics for {Title}.", station.Name, song.Title);
        }
    }

    /// <summary>The ffmpeg command for one library song: read at its own speed, its sound only, stamped on the running clock.</summary>
    public static List<string> SongArguments(string url, string authorization, TimeSpan duration, TimeSpan offset) =>
    [
        "-hide_banner", "-loglevel", "warning", "-nostdin", "-re",
        "-headers", authorization + "\r\n",
        "-i", url,
        "-vn", "-map", "0:a:0", "-t", Seconds(duration + TimeSpan.FromSeconds(1)),
        .. Encode(offset)
    ];

    private static List<string> Silence(TimeSpan duration, TimeSpan offset) =>
    [
        "-hide_banner", "-loglevel", "warning", "-nostdin", "-re",
        "-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000",
        "-t", Seconds(duration),
        .. Encode(offset)
    ];

    private static IEnumerable<string> Encode(TimeSpan offset) =>
    [
        "-af", "aresample=48000:async=1,aformat=channel_layouts=stereo",
        "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2",
        "-output_ts_offset", Seconds(offset),
        "-f", "mpegts", "-mpegts_flags", "+resend_headers", "-muxdelay", "0", "pipe:1"
    ];

    private static string Seconds(TimeSpan value) => Math.Max(value.TotalSeconds, 0).ToString("0.###", CultureInfo.InvariantCulture);

    // --- Internet radio --------------------------------------------------------------------------

    private async Task InternetAsync(RadioStation station, Stream output, Stopwatch clock, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            station = store.Find(station.Id) ?? station;
            string? address = null;
            try
            {
                address = station.StreamUrl is { } configured ? await StreamUrls.ResolveAsync(httpClients.CreateClient(StreamClientName), configured, cancellationToken).ConfigureAwait(false) : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Radio {Station}: couldn't read the station's playlist: {Message}", station.Name, ex.Message);
            }

            if (address is null)
            {
                players.Set(station.Id, new NowPlayingState(null, DateTimeOffset.UtcNow, null, "The station's address can't be reached."));
                await PlayAsync(output, Silence(TimeSpan.FromSeconds(15), clock.Elapsed), clock, null, cancellationToken).ConfigureAwait(false);
                continue;
            }

            Show(station, null);
            if (StreamUrls.IsHls(address))
            {
                // HLS carries no titles along the way; ffmpeg reads it as it is.
                await PlayAsync(output, [
                    "-hide_banner", "-loglevel", "warning", "-nostdin", "-i", address, "-vn", "-map", "0:a:0", .. Encode(clock.Elapsed)
                ], clock, null, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await IcyAsync(station, address, output, clock, cancellationToken).ConfigureAwait(false);
            }

            // Dropped: a moment's silence, then again.
            await PlayAsync(output, Silence(TimeSpan.FromSeconds(3), clock.Elapsed), clock, null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads a Shoutcast/Icecast stream with its titles: the audio goes to ffmpeg (which makes it AAC on the running
    /// clock), and each new "Artist - Title" becomes what's playing.
    /// </summary>
    private async Task IcyAsync(RadioStation station, string address, Stream output, Stopwatch clock, CancellationToken cancellationToken)
    {
        using HttpClient http = httpClients.CreateClient(StreamClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Radio {Station}: the station answered {Code}.", station.Name, (int)response.StatusCode);
            return;
        }

        int metaInterval = response.Headers.TryGetValues("icy-metaint", out IEnumerable<string>? values)
            && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int interval) ? interval : 0;

        var start = new ProcessStartInfo(settings.Ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in (List<string>)["-hide_banner", "-loglevel", "warning", "-i", "pipe:0", "-vn", "-map", "0:a:0", .. Encode(clock.Elapsed)])
        {
            start.ArgumentList.Add(argument);
        }

        using Process ffmpeg = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start ffmpeg ({settings.Ffmpeg}).");
        ffmpeg.ErrorDataReceived += (_, line) =>
        {
            if (!string.IsNullOrWhiteSpace(line.Data))
            {
                logger.LogDebug("ffmpeg (radio {Station}): {Line}", station.Name, line.Data);
            }
        };
        ffmpeg.BeginErrorReadLine();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task outward = ffmpeg.StandardOutput.BaseStream.CopyToAsync(output, stop.Token);
        try
        {
            await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var reader = new IcyReader(metaInterval);
            Stream input = ffmpeg.StandardInput.BaseStream;
            byte[] buffer = new byte[16 * 1024];
            while (!stop.IsCancellationRequested && !outward.IsCompleted)
            {
                int read = await body.ReadAsync(buffer, stop.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                foreach (IcyChunk chunk in reader.Read(buffer.AsSpan(0, read)))
                {
                    if (chunk.Audio is { } audio)
                    {
                        await input.WriteAsync(audio, stop.Token).ConfigureAwait(false);
                    }
                    else if (chunk.Title is { } title)
                    {
                        Show(station, title);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            logger.LogInformation("Radio {Station}: the station's stream dropped: {Message}", station.Name, ex.Message);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                if (!ffmpeg.HasExited)
                {
                    ffmpeg.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            try
            {
                await outward.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Shows an internet station's song (or just the station, without a title), then its cover and lyrics once found.</summary>
    private void Show(RadioStation station, string? streamTitle)
    {
        if (string.IsNullOrWhiteSpace(streamTitle))
        {
            players.Set(station.Id, new NowPlayingState(new SongInfo(station.Name, string.Empty, null, null, null, null, []), DateTimeOffset.UtcNow, null));
            return;
        }

        (string artist, string title) = StreamTitle.Split(streamTitle);
        if (players.Get(station.Id)?.Song is { } playing && playing.Title == title && playing.Artist == artist)
        {
            return;
        }

        players.Set(station.Id, new NowPlayingState(new SongInfo(title, artist, null, null, null, null, []), DateTimeOffset.UtcNow, null));
        if (station.LookUpOnline && artist.Length > 0)
        {
            _ = Task.Run(async () =>
            {
                OnlineSong found = await online.FindAsync(artist, title, null, CancellationToken.None).ConfigureAwait(false);
                players.Update(station.Id, state => state.Song is { } song && song.Title == title && song.Artist == artist
                    ? state with { Song = song with { Art = found.Art, Album = found.Album, Lyrics = station.ShowLyrics ? found.Lyrics : [] } }
                    : state);
            });
        }
    }

    // --- Shared ----------------------------------------------------------------------------------

    /// <summary>Runs one ffmpeg, its output appended to the stream; <paramref name="started"/> runs when its first sound arrives.</summary>
    private async Task PlayAsync(Stream output, List<string> arguments, Stopwatch clock, Action? started, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(settings.Ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        TimeSpan began = clock.Elapsed;
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start ffmpeg ({settings.Ffmpeg}).");
        process.ErrorDataReceived += (_, line) =>
        {
            if (!string.IsNullOrWhiteSpace(line.Data))
            {
                logger.LogDebug("ffmpeg (radio): {Line}", line.Data);
            }
        };
        process.BeginErrorReadLine();

        try
        {
            Stream source = process.StandardOutput.BaseStream;
            byte[] buffer = new byte[188 * 100];
            bool first = true;
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (first)
                {
                    first = false;
                    started?.Invoke();
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        // A song that wouldn't play at all mustn't spin.
        if (clock.Elapsed - began < TimeSpan.FromSeconds(1))
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>One piece of an ICY stream: audio to pass on, or a new title.</summary>
public readonly record struct IcyChunk(byte[]? Audio, string? Title);

/// <summary>
/// Separates a Shoutcast/Icecast stream's audio from the metadata blocks it interleaves every <c>icy-metaint</c> bytes
/// (a length byte, times 16, of <c>StreamTitle='…';</c>). With no interval, everything is audio.
/// </summary>
public sealed class IcyReader(int interval)
{
    private int _untilMeta = interval;
    private int _metaLeft = -1;
    private readonly List<byte> _meta = [];

    public IEnumerable<IcyChunk> Read(ReadOnlySpan<byte> data)
    {
        List<IcyChunk> chunks = [];
        if (interval <= 0)
        {
            chunks.Add(new IcyChunk(data.ToArray(), null));
            return chunks;
        }

        int index = 0;
        while (index < data.Length)
        {
            if (_metaLeft < 0 && _untilMeta > 0)
            {
                int take = Math.Min(_untilMeta, data.Length - index);
                chunks.Add(new IcyChunk(data.Slice(index, take).ToArray(), null));
                index += take;
                _untilMeta -= take;
                continue;
            }

            if (_metaLeft < 0)
            {
                // The length byte.
                _metaLeft = data[index++] * 16;
                _meta.Clear();
            }

            int metaTake = Math.Min(_metaLeft, data.Length - index);
            for (int offset = 0; offset < metaTake; offset++)
            {
                _meta.Add(data[index + offset]);
            }

            index += metaTake;
            _metaLeft -= metaTake;
            if (_metaLeft == 0)
            {
                string text = Encoding.UTF8.GetString([.. _meta]).TrimEnd('\0');
                if (StreamTitle.FromMetadata(text) is { } title)
                {
                    chunks.Add(new IcyChunk(null, title));
                }

                _metaLeft = -1;
                _untilMeta = interval;
            }
        }

        return chunks;
    }
}

/// <summary>Finds the stream in an internet station's playlist (.m3u or .pls), or takes the address as it is.</summary>
public static class StreamUrls
{
    public static bool IsHls(string url) =>
        Path.GetExtension(new Uri(url, UriKind.Absolute).AbsolutePath).Equals(".m3u8", StringComparison.OrdinalIgnoreCase);

    public static async Task<string?> ResolveAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        string extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        if (extension is not (".m3u" or ".pls"))
        {
            return uri.ToString();
        }

        string playlist = await http.GetStringAsync(uri, cancellationToken).ConfigureAwait(false);
        return FromPlaylist(playlist, uri);
    }

    /// <summary>The first stream a .pls (<c>File1=…</c>) or .m3u (the first address line) names.</summary>
    public static string? FromPlaylist(string playlist, Uri? relativeTo = null)
    {
        foreach (string raw in playlist.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('['))
            {
                continue;
            }

            string candidate = line.StartsWith("File", StringComparison.OrdinalIgnoreCase) && line.Contains('=')
                ? line[(line.IndexOf('=') + 1)..].Trim()
                : line.Contains('=') ? string.Empty : line;
            Uri? stream = null;
            bool parsed = candidate.Length > 0 && (relativeTo is null
                ? Uri.TryCreate(candidate, UriKind.Absolute, out stream)
                : Uri.TryCreate(relativeTo, candidate, out stream));
            if (parsed && stream is not null && (stream.Scheme == Uri.UriSchemeHttp || stream.Scheme == Uri.UriSchemeHttps))
            {
                return stream.ToString();
            }
        }

        return null;
    }
}
