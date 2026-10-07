using System.Buffers.Binary;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace SchedularrTV.News;

/// <summary>
/// A show's sound, as one endless MPEG-TS (AAC) stream for the show's own ffmpeg: the rundown's voice-overs, each at
/// its segment's time on the show's clock (<see cref="NewsCast"/>), over the show's music, which dips under the voice.
/// The mix is made here, 20 ms at a time and a little ahead of the clock, and encoded by an ffmpeg of its own.
/// </summary>
public sealed class NewsSound(NewsStore store, NewsCasts casts, NewsSettings settings, TimeProvider clock, ILogger<NewsSound> logger)
{
    private const int Rate = 48000;
    private const int FramesPerChunk = Rate / 50;
    private const int BytesPerFrame = 4;
    private static readonly TimeSpan Ahead = TimeSpan.FromMilliseconds(250);
    private static readonly string[] MusicExtensions = [".mp3", ".m4a", ".aac", ".ogg", ".opus", ".flac", ".wav"];

    public async Task ServeAsync(string showId, HttpContext context)
    {
        if (store.Find(showId) is not { } show)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        CancellationToken cancellationToken = context.RequestAborted;
        context.Response.ContentType = "video/mp2t";
        context.Response.Headers.CacheControl = "no-cache, no-store";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await context.Response.StartAsync(cancellationToken).ConfigureAwait(false);

        using IDisposable listening = casts.Listen(show);
        using Process encoder = StartEncoder();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task outward = encoder.StandardOutput.BaseStream.CopyToAsync(context.Response.Body, stop.Token);
        Music? music = null;
        try
        {
            await MixAsync(show, encoder.StandardInput.BaseStream, outward, mix => music = mix, stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The show's ffmpeg went away.
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            music?.Dispose();
            Kill(encoder);
            try
            {
                await outward.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }
    }

    private async Task MixAsync(NewsShow show, Stream input, Task outward, Action<Music?> musicChanged, CancellationToken cancellationToken)
    {
        var paced = Stopwatch.StartNew();
        long startedAt = clock.GetUtcNow().ToUnixTimeMilliseconds();
        long written = 0;
        byte[] chunk = new byte[FramesPerChunk * BytesPerFrame];
        short[] speech = new short[FramesPerChunk * 2];
        short[] bed = new short[FramesPerChunk * 2];
        double musicGain = show.MusicVolume;
        Music? music = null;
        string? musicFolder = null;
        Clip? clip = null;
        NewsCast cast = casts.Use(show.Id, show, speak: true);
        long checkedAt = -1;

        try
        {
            while (!cancellationToken.IsCancellationRequested && !outward.IsCompleted)
            {
                // The show's settings, re-read every second, so a change of music or volume is heard without restarting.
                long second = written / Rate;
                if (second != checkedAt)
                {
                    checkedAt = second;
                    show = store.Find(show.Id) ?? show;
                    // Also what keeps the rundown fresh while the show is on air.
                    cast = casts.Use(show.Id, show, speak: true);
                    if (show.MusicFolder != musicFolder)
                    {
                        music?.Dispose();
                        musicFolder = show.MusicFolder;
                        music = Music.Start(settings.Ffmpeg, musicFolder, logger);
                        musicChanged(music);
                    }
                }

                // What's on at this chunk, by the show's clock.
                long at = startedAt + written * 1000 / Rate;
                (Rundown? rundown, long start, _) = cast.At(at);
                RundownItem? item = rundown?.At(at - start);
                long intoVoice = item is null ? -1 : at - start - item.StartMs - RundownItem.LeadMs;

                Array.Clear(speech);
                bool speaking = false;
                if (item?.Clip is { } path && intoVoice >= 0 && intoVoice < item.ClipMs)
                {
                    if (clip?.Path != path)
                    {
                        clip?.Dispose();
                        clip = Clip.Open(path);
                    }

                    speaking = clip is not null && clip.Read(intoVoice * Rate / 1000, speech);
                }

                Array.Clear(bed);
                if (music is not null && !music.Read(bed))
                {
                    // The songs ran out or ffmpeg stopped: start them again next second.
                    music.Dispose();
                    music = null;
                    musicFolder = null;
                    musicChanged(null);
                }

                // The music dips under the voice, smoothly.
                double target = show.MusicVolume * (speaking || (item?.Clip is not null && intoVoice >= -300 && intoVoice < item.ClipMs + 300) ? 0.22 : 1.0);
                double speechGain = show.SpeechVolume;
                for (int sample = 0; sample < speech.Length; sample++)
                {
                    if ((sample & 63) == 0)
                    {
                        musicGain += (target - musicGain) * 0.04;
                    }

                    int mixed = (int)(speech[sample] * speechGain + bed[sample] * musicGain);
                    BinaryPrimitives.WriteInt16LittleEndian(chunk.AsSpan(sample * 2), (short)Math.Clamp(mixed, short.MinValue, short.MaxValue));
                }

                await input.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
                written += FramesPerChunk;

                // Kept a little ahead of real time: enough that the encoder never runs dry, little enough to stay in step with the picture.
                TimeSpan due = TimeSpan.FromSeconds((double)written / Rate) - Ahead - paced.Elapsed;
                if (due > TimeSpan.Zero)
                {
                    await Task.Delay(due, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            clip?.Dispose();
            music?.Dispose();
            musicChanged(null);
        }
    }

    private Process StartEncoder()
    {
        var start = new ProcessStartInfo(settings.Ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in new[]
                 {
                     "-hide_banner", "-loglevel", "warning", "-nostdin",
                     "-f", "s16le", "-ar", "48000", "-ac", "2", "-i", "pipe:0",
                     "-c:a", "aac", "-b:a", "160k", "-ar", "48000", "-ac", "2",
                     "-f", "mpegts", "-mpegts_flags", "+resend_headers", "-muxdelay", "0", "-flush_packets", "1", "pipe:1"
                 })
        {
            start.ArgumentList.Add(argument);
        }

        Process process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start ffmpeg ({settings.Ffmpeg}).");
        process.ErrorDataReceived += (_, line) =>
        {
            if (!string.IsNullOrWhiteSpace(line.Data))
            {
                logger.LogDebug("ffmpeg (news sound): {Line}", line.Data);
            }
        };
        process.BeginErrorReadLine();
        return process;
    }

    private static void Kill(Process process)
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

    /// <summary>A voice-over's PCM file, read from wherever the clock says.</summary>
    private sealed class Clip : IDisposable
    {
        private readonly FileStream _file;
        private readonly byte[] _buffer = new byte[FramesPerChunk * BytesPerFrame];

        private Clip(string path, FileStream file)
        {
            Path = path;
            _file = file;
        }

        public string Path { get; }

        public static Clip? Open(string path)
        {
            try
            {
                return new Clip(path, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Fills <paramref name="samples"/> from <paramref name="frame"/> on (silence past the end); false when there was nothing.</summary>
        public bool Read(long frame, short[] samples)
        {
            long offset = frame * BytesPerFrame;
            if (offset >= _file.Length)
            {
                return false;
            }

            _file.Position = offset;
            int read = 0;
            while (read < _buffer.Length)
            {
                int got = _file.Read(_buffer, read, _buffer.Length - read);
                if (got == 0)
                {
                    break;
                }

                read += got;
            }

            for (int sample = 0; sample < read / 2; sample++)
            {
                samples[sample] = BinaryPrimitives.ReadInt16LittleEndian(_buffer.AsSpan(sample * 2));
            }

            return read > 0;
        }

        public void Dispose() => _file.Dispose();
    }

    /// <summary>The show's music: the folder's songs, shuffled and looped, decoded to PCM by an ffmpeg as it's needed.</summary>
    private sealed class Music : IDisposable
    {
        private readonly Process _process;
        private readonly Stream _output;
        private readonly string _playlist;
        private readonly byte[] _buffer = new byte[FramesPerChunk * BytesPerFrame];

        private Music(Process process, string playlist)
        {
            _process = process;
            _output = process.StandardOutput.BaseStream;
            _playlist = playlist;
        }

        public static Music? Start(string ffmpeg, string? folder, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                return null;
            }

            string[] songs;
            try
            {
                songs = [.. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .Where(path => MusicExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("News: couldn't read the music folder {Folder}: {Message}", folder, ex.Message);
                return null;
            }

            if (songs.Length == 0)
            {
                return null;
            }

            Random.Shared.Shuffle(songs);
            string playlist = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"schedularrtv-news-{Guid.NewGuid():N}.txt");
            File.WriteAllLines(playlist, songs.Select(song => $"file '{LiveStream.ConcatPath(song)}'").Prepend("ffconcat version 1.0"));

            var start = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (string argument in new[]
                     {
                         "-hide_banner", "-loglevel", "error", "-nostdin",
                         "-stream_loop", "-1", "-f", "concat", "-safe", "0", "-i", playlist,
                         "-vn", "-ac", "2", "-ar", "48000", "-f", "s16le", "pipe:1"
                     })
            {
                start.ArgumentList.Add(argument);
            }

            try
            {
                Process process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg didn't start.");
                process.ErrorDataReceived += (_, line) =>
                {
                    if (!string.IsNullOrWhiteSpace(line.Data))
                    {
                        logger.LogDebug("ffmpeg (news music): {Line}", line.Data);
                    }
                };
                process.BeginErrorReadLine();
                return new Music(process, playlist);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                logger.LogWarning("News: couldn't start ffmpeg for the music: {Message}", ex.Message);
                File.Delete(playlist);
                return null;
            }
        }

        /// <summary>The next 20 ms of music; false once it has ended.</summary>
        public bool Read(short[] samples)
        {
            int read = 0;
            while (read < _buffer.Length)
            {
                int got = _output.Read(_buffer, read, _buffer.Length - read);
                if (got == 0)
                {
                    return false;
                }

                read += got;
            }

            for (int sample = 0; sample < samples.Length; sample++)
            {
                samples[sample] = BinaryPrimitives.ReadInt16LittleEndian(_buffer.AsSpan(sample * 2));
            }

            return true;
        }

        public void Dispose()
        {
            Kill(_process);
            _process.Dispose();
            try
            {
                File.Delete(_playlist);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
