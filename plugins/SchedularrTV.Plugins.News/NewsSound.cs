using System.Buffers.Binary;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using SchedularrTV.Core.Media;
using SchedularrTV.Plugins;

namespace SchedularrTV.News;

/// <summary>
/// A show's sound, as one endless MPEG-TS (AAC) stream for the show's own ffmpeg: the rundown's voice-overs, each at
/// its segment's time on the show's clock (<see cref="NewsCast"/>), over the show's music, which dips under the voice.
/// The mix is made here, 20 ms at a time and a little ahead of the clock, and encoded by an ffmpeg of its own.
/// </summary>
public sealed class NewsSound(
    NewsStore store,
    NewsCasts casts,
    NewsSettings settings,
    TimeProvider clock,
    ILogger<NewsSound> logger,
    IEnumerable<IMusicLibrary> musicLibraries)
{
    private const int Rate = MusicBed.Rate;
    private const int FramesPerChunk = Rate / 50;
    private const int BytesPerFrame = MusicBed.BytesPerFrame;
    private static readonly TimeSpan Ahead = TimeSpan.FromMilliseconds(250);

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
        MusicBed? music = null;
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

    private async Task MixAsync(NewsShow show, Stream input, Task outward, Action<MusicBed?> musicChanged, CancellationToken cancellationToken)
    {
        var paced = Stopwatch.StartNew();
        long startedAt = clock.GetUtcNow().ToUnixTimeMilliseconds();
        long written = 0;
        byte[] chunk = new byte[FramesPerChunk * BytesPerFrame];
        short[] speech = new short[FramesPerChunk * 2];
        short[] bed = new short[FramesPerChunk * 2];
        double musicGain = show.MusicVolume;
        MusicBed? music = null;
        string? musicKey = null;
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
                    BackgroundMusic? wanted = show.Music ?? BackgroundMusic.Tidy(null, show.MusicFolder);
                    if (wanted?.Key != musicKey)
                    {
                        music?.Dispose();
                        musicKey = wanted?.Key;
                        music = MusicBed.Start(settings.Ffmpeg, wanted, musicLibraries, logger);
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

                // The music player starts its songs again itself when they run out; between songs it's silence.
                Array.Clear(bed);
                music?.Read(bed);

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
        SchedularrTV.Plugins.Performance.Work.TrackAs(process, "News", "Encoding news sound");
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

}
