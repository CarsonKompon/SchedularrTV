using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SchedularrTV.GeneratedPromos;

/// <summary>
/// What's said in episodes, for promos whose clips are curated by AI: each episode file is transcribed once by a speech
/// recognition plugin (Whisper, from Local AI) and the transcript kept beside the renders, so it's never done twice.
/// </summary>
public sealed class PromoTranscripts(
    IEnumerable<ISpeechTranscriber> transcribers,
    Func<string> ffmpegPath,
    Func<string> folder,
    ILogger logger)
{
    // Transcribing takes minutes; one that was used lately is kept a long while.
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(120);
    private static readonly TimeSpan RetryFailuresAfter = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly ConcurrentDictionary<string, Lazy<Task<Transcript?>>> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Transcript> _memory = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, string Problem)> _failed = new(StringComparer.Ordinal);
    private DateTime _prunedAt = DateTime.MinValue;

    /// <summary>Raised from a worker thread when an episode has been transcribed.</summary>
    public event Action<string>? Transcribed;

    /// <summary>The speech recognition plugins switched on right now.</summary>
    public IReadOnlyList<ISpeechTranscriber> Available => [.. transcribers
        .GroupBy(transcriber => transcriber.Id, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())];

    /// <summary>The one asked for, or with none named the first that's ready (else the first at all); null when there are none.</summary>
    public ISpeechTranscriber? Find(string? id)
    {
        IReadOnlyList<ISpeechTranscriber> available = Available;
        return !string.IsNullOrWhiteSpace(id)
            ? available.FirstOrDefault(transcriber => string.Equals(transcriber.Id, id.Trim(), StringComparison.OrdinalIgnoreCase))
            : available.FirstOrDefault(transcriber => transcriber.SetupProblem is null) ?? available.FirstOrDefault();
    }

    /// <summary>Why <paramref name="id"/> can't transcribe right now, or null when it can.</summary>
    public string? Problem(string? id) => Find(id) is not { } transcriber
        ? string.IsNullOrWhiteSpace(id)
            ? "no speech recognition plugin is installed and switched on (Local AI writer has one)"
            : $"the speech recognition '{id}' isn't installed or is switched off"
        : transcriber.SetupProblem;

    /// <summary>The episode's transcript if it's been made, without waiting; otherwise null, and it's made in the background.</summary>
    public Transcript? Peek(string mediaPath, string? transcriberId, bool queue = true)
    {
        if (Ready(mediaPath, transcriberId, out ISpeechTranscriber? transcriber) is not { } key)
        {
            return null;
        }

        if (Cached(key) is { } transcript)
        {
            return transcript;
        }

        if (queue && RecentFailure(key) is null)
        {
            _ = Start(key, mediaPath, transcriber!);
        }

        return null;
    }

    /// <summary>The episode's transcript, made now if it hasn't been; null when it can't be (no plugin, no speech, an error).</summary>
    public async Task<Transcript?> GetAsync(string mediaPath, string? transcriberId, CancellationToken cancellationToken = default)
    {
        if (Ready(mediaPath, transcriberId, out ISpeechTranscriber? transcriber) is not { } key)
        {
            return null;
        }

        if (Cached(key) is { } transcript)
        {
            return transcript;
        }

        if (RecentFailure(key) is not null)
        {
            return null;
        }

        return await Start(key, mediaPath, transcriber!).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Why the last try at this episode failed, while it's not tried again.</summary>
    public string? LastProblem(string mediaPath, string? transcriberId) =>
        Ready(mediaPath, transcriberId, out _) is { } key ? RecentFailure(key) : Problem(transcriberId);

    /// <summary>Whether this episode is being transcribed right now.</summary>
    public bool IsTranscribing(string mediaPath, string? transcriberId) =>
        Ready(mediaPath, transcriberId, out _) is { } key && _running.ContainsKey(key);

    private string? Ready(string mediaPath, string? transcriberId, out ISpeechTranscriber? transcriber)
    {
        transcriber = Find(transcriberId);
        if (transcriber is null || transcriber.SetupProblem is not null || string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
        {
            return null;
        }

        var file = new FileInfo(mediaPath);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{Path.GetFullPath(mediaPath)}\u001f{file.Length}\u001f{file.LastWriteTimeUtc.Ticks}\u001f{transcriber.Id}\u001f{transcriber.CacheIdentity}")))[..32];
    }

    private Task<Transcript?> Start(string key, string mediaPath, ISpeechTranscriber transcriber)
    {
        Lazy<Task<Transcript?>> call = _running.GetOrAdd(key, _ => new Lazy<Task<Transcript?>>(() => Task.Run(() => RunAsync(key, mediaPath, transcriber))));
        return call.Value;
    }

    private async Task<Transcript?> RunAsync(string key, string mediaPath, ISpeechTranscriber transcriber)
    {
        string directory = folder();
        string wave = Path.Combine(directory, $"{key}.{Guid.NewGuid():N}.partial.wav");
        using WorkScope work = Work.Begin("Generated Promos", "Transcribing episode", Path.GetFileName(mediaPath));
        try
        {
            Directory.CreateDirectory(directory);
            await ExtractAsync(mediaPath, wave).ConfigureAwait(false);
            Transcript transcript = await transcriber.TranscribeAsync(wave).ConfigureAwait(false);
            Keep(key, mediaPath, transcript);
            try
            {
                Transcribed?.Invoke(mediaPath);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "A listener for a finished transcript failed.");
            }

            return transcript;
        }
        catch (Exception ex)
        {
            string problem = ex.GetBaseException().Message;
            _failed[key] = (DateTimeOffset.UtcNow + RetryFailuresAfter, problem);
            logger.LogWarning("Couldn't transcribe {File} with {Transcriber}: {Message}", Path.GetFileName(mediaPath), transcriber.DisplayName, problem);
            return null;
        }
        finally
        {
            _running.TryRemove(key, out _);
            try
            {
                File.Delete(wave);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            PruneSoon();
        }
    }

    /// <summary>The episode's sound as speech recognition wants it: 16 kHz, mono, 16-bit WAV.</summary>
    private async Task ExtractAsync(string mediaPath, string wave)
    {
        var info = new ProcessStartInfo(ffmpegPath())
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in (string[])["-hide_banner", "-loglevel", "error", "-y", "-i", mediaPath, "-vn", "-map", "0:a:0?",
                     "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-f", "wav", wave])
        {
            info.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg could not be started.");
        Work.Track(process, "Generated Promos", "Extracting episode sound");
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0 || !File.Exists(wave) || new FileInfo(wave).Length < 1024)
        {
            throw new InvalidOperationException($"ffmpeg couldn't take the episode's sound: {(await error.ConfigureAwait(false)).Trim()}");
        }
    }

    private string? RecentFailure(string key)
    {
        if (_failed.TryGetValue(key, out (DateTimeOffset Until, string Problem) failure))
        {
            if (DateTimeOffset.UtcNow < failure.Until)
            {
                return failure.Problem;
            }

            _failed.TryRemove(key, out _);
        }

        return null;
    }

    private Transcript? Cached(string key)
    {
        if (_memory.TryGetValue(key, out Transcript? transcript))
        {
            return transcript;
        }

        string path = Path.Combine(folder(), key + ".json");
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            StoredTranscript? stored = JsonSerializer.Deserialize<StoredTranscript>(File.ReadAllText(path), Json);
            if (stored is null)
            {
                return null;
            }

            transcript = new Transcript([.. stored.Segments.Select(segment =>
                new TranscriptSegment(TimeSpan.FromSeconds(segment.Start), TimeSpan.FromSeconds(segment.End), segment.Text))], stored.Language);
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(1))
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }

            _memory[key] = transcript;
            return transcript;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void Keep(string key, string mediaPath, Transcript transcript)
    {
        _memory[key] = transcript;
        try
        {
            string directory = folder();
            Directory.CreateDirectory(directory);
            var stored = new StoredTranscript(Path.GetFileName(mediaPath), transcript.Language,
                [.. transcript.Segments.Select(segment => new StoredSegment(Math.Round(segment.Start.TotalSeconds, 3), Math.Round(segment.End.TotalSeconds, 3), segment.Text))]);
            string path = Path.Combine(directory, key + ".json");
            string partial = Path.Combine(directory, $"{key}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(partial, JsonSerializer.Serialize(stored, Json));
            File.Move(partial, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Couldn't keep a transcript on disk; it's kept until the scheduler restarts.");
        }
    }

    /// <summary>Transcripts nothing has used for months, and leftovers from interrupted work, at most daily.</summary>
    private void PruneSoon()
    {
        if (DateTime.UtcNow - _prunedAt < TimeSpan.FromDays(1))
        {
            return;
        }

        _prunedAt = DateTime.UtcNow;
        try
        {
            string directory = folder();
            if (!Directory.Exists(directory))
            {
                return;
            }

            DateTime abandoned = DateTime.UtcNow - TimeSpan.FromHours(6);
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                try
                {
                    bool leftover = file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".partial.wav", StringComparison.OrdinalIgnoreCase);
                    DateTime written = File.GetLastWriteTimeUtc(file);
                    if (leftover ? written < abandoned : written < DateTime.UtcNow - KeepFor)
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Couldn't tidy the transcripts folder.");
        }
    }

    private sealed record StoredSegment(double Start, double End, string Text);

    private sealed record StoredTranscript(string File, string? Language, List<StoredSegment> Segments);
}
