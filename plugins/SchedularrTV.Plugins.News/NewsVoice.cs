using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SchedularrTV.Plugins.Speech;

namespace SchedularrTV.News;

/// <summary>
/// Voice-overs for the rundown, made by whichever voice plugin the show picks (Piper, TikTok, MiniMax…) and kept as raw
/// 16-bit stereo 48 kHz PCM, the form the show's sound is mixed in, so playing one is just reading the file. Each is
/// kept by a hash of the voice and the words, so a story still in the news isn't voiced again at the next refresh.
/// </summary>
/// <param name="providers">The voices of the plugins switched on (the list follows their switches as it's read).</param>
/// <param name="dataDirectory">The plugin's data folder; the voice-overs are kept in its <c>voice</c> folder.</param>
public sealed class NewsVoice(IEnumerable<ITextToSpeechProvider> providers, NewsSettings settings, string dataDirectory, ILogger<NewsVoice> logger)
{
    /// <summary>Bytes per millisecond of the PCM: 48 frames of 2 channels of 2 bytes.</summary>
    public const int BytesPerMs = 48 * 4;

    private readonly string _folder = Path.Combine(dataDirectory, "voice");
    private readonly SemaphoreSlim _oneAtATime = new(2, 2);

    public IReadOnlyList<ITextToSpeechProvider> Providers => [.. providers];

    public ITextToSpeechProvider? Find(string? id) =>
        id is null ? null : providers.FirstOrDefault(provider => string.Equals(provider.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Why the show can't be voiced right now, or null when it can.</summary>
    public string? Problem(NewsShow show)
    {
        if (!show.Speaks)
        {
            return null;
        }

        return Find(show.VoiceProvider) is { } provider
            ? provider.SetupProblem
            : $"The voice plugin \"{show.VoiceProvider}\" isn't installed or is switched off.";
    }

    /// <summary>The voice-over for <paramref name="text"/>: its PCM file and length; made now if it isn't kept already.</summary>
    public async Task<(string Path, long Ms)> ClipAsync(ITextToSpeechProvider provider, string voice, string text, CancellationToken cancellationToken)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{provider.Id}|{provider.CacheIdentity}|{voice}|{text}")))[..32].ToLowerInvariant();
        string path = Path.Combine(_folder, hash + ".pcm");
        if (File.Exists(path))
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return (path, new FileInfo(path).Length / BytesPerMs);
        }

        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_folder);
            TtsAudio audio = await provider.SynthesizeAsync(text, voice, cancellationToken).ConfigureAwait(false);
            string extension = audio.Extension.TrimStart('.') is { Length: > 0 } ext ? ext : "mp3";
            string source = Path.Combine(_folder, $"{hash}.source.{extension}");
            string temp = path + ".tmp";
            await File.WriteAllBytesAsync(source, audio.Data, cancellationToken).ConfigureAwait(false);
            try
            {
                await ConvertAsync(source, temp, cancellationToken).ConfigureAwait(false);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                TryDelete(source);
                TryDelete(temp);
            }

            return (path, new FileInfo(path).Length / BytesPerMs);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>Voice-overs not used for a few days go.</summary>
    public void Tidy()
    {
        try
        {
            if (!Directory.Exists(_folder))
            {
                return;
            }

            DateTime cutoff = DateTime.UtcNow - TimeSpan.FromDays(3);
            foreach (string file in Directory.EnumerateFiles(_folder))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "News: couldn't tidy the voice-overs.");
        }
    }

    private async Task ConvertAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(settings.Ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", source,
                     "-vn", "-ac", "2", "-ar", "48000", "-f", "s16le", "-acodec", "pcm_s16le", destination
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start ffmpeg ({settings.Ffmpeg}).");
        Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0 || !File.Exists(destination))
        {
            throw new InvalidOperationException($"ffmpeg couldn't read the voice-over: {(await errors.ConfigureAwait(false)).Trim()}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
