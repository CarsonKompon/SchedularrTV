using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using SchedularrTV.Core.Media;

namespace SchedularrTV.Plugins;

/// <summary>
/// Background music as endless raw sound (48 kHz stereo 16-bit PCM): the songs of a <see cref="BackgroundMusic"/>, one
/// after another, shuffled and started again when they run out, each decoded by an ffmpeg of its own (so a library's
/// songs can each need their own address and headers). A few seconds are decoded ahead; reading never waits: when
/// nothing's ready (between songs, or with no songs at all) it's silence.
/// </summary>
/// <remarks>
/// Use it for any plugin that plays music under something: read 20 ms at a time and mix it (<see cref="Read(short[])"/>),
/// or hand ffmpeg a local address to read it from (<see cref="Serve"/>).
/// </remarks>
public sealed class MusicBed : IDisposable
{
    public const int Rate = 48000;
    public const int BytesPerFrame = 4;

    private const int Capacity = Rate * BytesPerFrame * 6;
    private readonly byte[] _ring = new byte[Capacity];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _life = new();
    private readonly string _ffmpeg;
    private readonly ILogger _logger;
    private int _head;
    private int _count;
    private Process? _current;

    private MusicBed(string ffmpeg, ILogger logger)
    {
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    /// <summary>What the music stands for (<see cref="BackgroundMusic.Key"/>), to tell when the settings change.</summary>
    public string Key { get; private init; } = string.Empty;

    /// <summary>Why nothing is playing (no songs found, the library can't be reached), or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>Starts decoding <paramref name="music"/>'s songs; null when there's no music set.</summary>
    public static MusicBed? Start(string ffmpeg, BackgroundMusic? music, IEnumerable<IMusicLibrary> libraries, ILogger logger)
    {
        if (music is not { IsSet: true })
        {
            return null;
        }

        var bed = new MusicBed(ffmpeg, logger) { Key = music.Key };
        IMusicLibrary[] all = [.. libraries];
        BackgroundMusic copy = music.Copy();
        _ = Task.Run(() => bed.ProduceAsync(token => copy.SongsAsync(all, token), copy.Describe(all)), CancellationToken.None);
        return bed;
    }

    /// <summary>
    /// A player for sound that arrives as it plays (a web page's, say), handed in with <see cref="Write"/> rather than
    /// decoded from songs; read and served like any other.
    /// </summary>
    public static MusicBed Live(ILogger logger) => new(string.Empty, logger) { Key = "live" };

    /// <summary>
    /// Adds sound that has just played (48 kHz stereo 16-bit PCM). The oldest is dropped when more than two seconds
    /// are waiting, so a source running a little fast never builds up a delay.
    /// </summary>
    public void Write(ReadOnlySpan<byte> pcm)
    {
        const int Limit = Rate * BytesPerFrame * 2;
        pcm = pcm[..(pcm.Length / BytesPerFrame * BytesPerFrame)];
        if (pcm.Length > Limit)
        {
            pcm = pcm[^(Limit / BytesPerFrame * BytesPerFrame)..];
        }

        lock (_gate)
        {
            int excess = _count + pcm.Length - Limit;
            if (excess > 0)
            {
                excess = (excess + BytesPerFrame - 1) / BytesPerFrame * BytesPerFrame;
                excess = Math.Min(excess, _count);
                _head = (_head + excess) % Capacity;
                _count -= excess;
            }

            int tail = (_head + _count) % Capacity;
            int first = Math.Min(pcm.Length, Capacity - tail);
            pcm[..first].CopyTo(_ring.AsSpan(tail));
            pcm[first..].CopyTo(_ring);
            _count += pcm.Length;
        }
    }

    /// <summary>The next 20 ms (or however long <paramref name="samples"/> is): music where it's ready, silence otherwise.</summary>
    /// <returns>Whether any of it was music.</returns>
    public bool Read(short[] samples)
    {
        byte[] bytes = new byte[samples.Length * 2];
        int got = Read(bytes);
        for (int sample = 0; sample < samples.Length; sample++)
        {
            samples[sample] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(sample * 2));
        }

        return got > 0;
    }

    /// <summary>Fills <paramref name="destination"/> with what's ready, then silence; returns how many bytes were music.</summary>
    public int Read(Span<byte> destination)
    {
        int take;
        lock (_gate)
        {
            // Whole frames only, so left and right never swap.
            take = Math.Min(destination.Length, _count) / BytesPerFrame * BytesPerFrame;
            int first = Math.Min(take, Capacity - _head);
            _ring.AsSpan(_head, first).CopyTo(destination);
            _ring.AsSpan(0, take - first).CopyTo(destination[first..]);
            _head = (_head + take) % Capacity;
            _count -= take;
        }

        destination[take..].Clear();
        return take;
    }

    /// <summary>
    /// Serves the music at a local address (<c>tcp://127.0.0.1:port</c>) that one ffmpeg can read as
    /// <c>-f s16le -ar 48000 -ac 2 -i</c>, at real time (a second ahead), until <paramref name="cancellationToken"/> ends.
    /// </summary>
    public string Serve(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _life.Token);
            try
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false);
                listener.Stop();
                client.NoDelay = true;
                await using NetworkStream output = client.GetStream();
                await PaceAsync(output, stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
                // ffmpeg went away, or the music stopped.
            }
            finally
            {
                listener.Stop();
            }
        }, CancellationToken.None);
        return $"tcp://127.0.0.1:{port}";
    }

    /// <summary>Writes the music to <paramref name="output"/> in step with the clock, a second ahead.</summary>
    public async Task PaceAsync(Stream output, CancellationToken cancellationToken)
    {
        byte[] chunk = new byte[Rate / 50 * BytesPerFrame];
        var clock = Stopwatch.StartNew();
        long written = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            Read(chunk);
            await output.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            written += Rate / 50;
            TimeSpan due = TimeSpan.FromSeconds((double)written / Rate) - TimeSpan.FromSeconds(1) - clock.Elapsed;
            if (due > TimeSpan.Zero)
            {
                await Task.Delay(due, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ProduceAsync(Func<CancellationToken, IAsyncEnumerable<MusicStream>> songs, string what)
    {
        CancellationToken token = _life.Token;
        int failures = 0;
        while (!token.IsCancellationRequested)
        {
            bool any = false;
            try
            {
                await foreach (MusicStream song in songs(token).ConfigureAwait(false))
                {
                    any = true;
                    long played = await PlayAsync(song, token).ConfigureAwait(false);
                    if (played > 0)
                    {
                        Problem = null;
                    }

                    // Half a second or less: it couldn't be read. Several in a row and something's wrong, so wait a while.
                    failures = played < Rate * BytesPerFrame / 2 ? failures + 1 : 0;
                    if (failures >= 5)
                    {
                        Problem = $"The songs ({what}) can't be played; trying again shortly.";
                        _logger.LogWarning("Background music ({Music}): five songs in a row couldn't be played.", what);
                        failures = 0;
                        await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Problem = $"Couldn't get the songs ({what}): {ex.Message}";
                _logger.LogWarning("Background music ({Music}): {Message}", what, ex.Message);
                any = false;
            }

            if (!any)
            {
                Problem ??= $"No songs found ({what}).";
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(60), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>The ffmpeg command that decodes one song to raw sound.</summary>
    public static IReadOnlyList<string> DecodeArguments(MusicStream song) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostdin",
        .. song.Headers is { Length: > 0 } headers ? (string[])["-headers", headers + "\r\n"] : [],
        "-i", song.Input,
        "-vn", "-map", "0:a:0", "-ac", "2", "-ar", Rate.ToString(System.Globalization.CultureInfo.InvariantCulture), "-f", "s16le", "pipe:1"
    ];

    /// <summary>Decodes one song into the buffer, waiting while it's full; returns the bytes it gave.</summary>
    private async Task<long> PlayAsync(MusicStream song, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in DecodeArguments(song))
        {
            start.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg didn't start.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Problem = $"Couldn't start ffmpeg ({_ffmpeg}) for the music: {ex.Message}";
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return 0;
        }

        using (process)
        {
            _current = process;
            process.ErrorDataReceived += (_, line) =>
            {
                if (!string.IsNullOrWhiteSpace(line.Data))
                {
                    _logger.LogDebug("ffmpeg (background music): {Line}", line.Data);
                }
            };
            process.BeginErrorReadLine();

            Stream input = process.StandardOutput.BaseStream;
            byte[] buffer = new byte[16 * 1024];
            long total = 0;
            try
            {
                while (true)
                {
                    int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    int offset = 0;
                    while (offset < read)
                    {
                        offset += Append(buffer, offset, read - offset);
                        if (offset < read)
                        {
                            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    total += read;
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
            finally
            {
                Kill(process);
                _current = null;
            }

            return total;
        }
    }

    /// <summary>Adds as much of the bytes as there's room for; returns how many.</summary>
    private int Append(byte[] buffer, int offset, int length)
    {
        lock (_gate)
        {
            int room = Math.Min(Capacity - _count, length);
            int tail = (_head + _count) % Capacity;
            int first = Math.Min(room, Capacity - tail);
            buffer.AsSpan(offset, first).CopyTo(_ring.AsSpan(tail));
            buffer.AsSpan(offset + first, room - first).CopyTo(_ring);
            _count += room;
            return room;
        }
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

    public void Dispose()
    {
        if (_life.IsCancellationRequested)
        {
            return;
        }

        _life.Cancel();
        if (_current is { } process)
        {
            Kill(process);
        }
    }
}
