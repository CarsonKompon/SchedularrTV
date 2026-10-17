using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SchedularrTV.Providers.FileSystem;

/// <param name="Chapters">The file's chapters, when it was probed for them; null when it wasn't (filler is probed for its length only).</param>
internal sealed record ProbeEntry(long Length, long LastWriteTicks, long DurationTicks, ProbedChapter[]? Chapters = null);

internal sealed record ProbedChapter(long StartTicks, string? Title);

/// <summary>A file's length, and its chapters (start, name) when they were asked for.</summary>
internal sealed record ProbeResult(TimeSpan Duration, IReadOnlyList<(TimeSpan Start, string? Name)> Chapters);

/// <summary>
/// Runs ffprobe to get file durations, caching results on disk.
/// </summary>
/// <remarks>
/// A commercials library is easily 10k files; probing all of them on every startup would take
/// minutes. The cache is keyed on length + last-write time so edited files are re-probed and
/// everything else is free.
/// </remarks>
internal sealed class MediaProbe(FileSystemLibraryOptions options, ILogger logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly ConcurrentDictionary<string, ProbeEntry> _cache = Load(options.CachePath);

    public async Task<IReadOnlyDictionary<string, TimeSpan>> ProbeAllAsync(
        IReadOnlyList<FileInfo> files,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, ProbeResult> results = await ProbeAllAsync(files, chapters: false, cancellationToken).ConfigureAwait(false);
        return results.ToDictionary(pair => pair.Key, pair => pair.Value.Duration, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every file's length (and, with <paramref name="chapters"/>, its chapters), from the cache where the file hasn't
    /// changed. A file that can't be probed is missing from the result.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ProbeResult>> ProbeAllAsync(
        IReadOnlyList<FileInfo> files,
        bool chapters,
        CancellationToken cancellationToken)
    {
        ConcurrentDictionary<string, ProbeResult> results = new(StringComparer.OrdinalIgnoreCase);
        List<FileInfo> toProbe = [];

        foreach (FileInfo file in files)
        {
            if (_cache.TryGetValue(file.FullName, out ProbeEntry? cached)
                && cached.Length == file.Length
                && cached.LastWriteTicks == file.LastWriteTimeUtc.Ticks
                && (!chapters || cached.Chapters is not null))
            {
                results[file.FullName] = Result(cached);
            }
            else
            {
                toProbe.Add(file);
            }
        }

        if (toProbe.Count > 0)
        {
            logger.LogInformation("Probing {Count} new or changed media files ({Cached} cached).", toProbe.Count, results.Count);

            int completed = 0;

            await Parallel.ForEachAsync(
                toProbe,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.ProbeConcurrency), CancellationToken = cancellationToken },
                async (file, ct) =>
                {
                    ProbeEntry? probed = await ProbeAsync(file, chapters, ct).ConfigureAwait(false);
                    if (probed is not null)
                    {
                        results[file.FullName] = Result(probed);
                        _cache[file.FullName] = probed;
                    }

                    int done = Interlocked.Increment(ref completed);
                    if (done % 500 == 0)
                    {
                        logger.LogInformation("Probed {Done}/{Total} files.", done, toProbe.Count);
                    }
                }).ConfigureAwait(false);

            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    private static ProbeResult Result(ProbeEntry entry) => new(
        TimeSpan.FromTicks(entry.DurationTicks),
        [.. (entry.Chapters ?? []).Select(chapter => (TimeSpan.FromTicks(chapter.StartTicks), chapter.Title))]);

    private async Task<ProbeEntry?> ProbeAsync(FileInfo file, bool chapters, CancellationToken cancellationToken)
    {
        if (!chapters)
        {
            return await ProbeAsync(file.FullName, cancellationToken).ConfigureAwait(false) is { } duration
                ? new ProbeEntry(file.Length, file.LastWriteTimeUtc.Ticks, duration.Ticks)
                : null;
        }

        string? output = await RunAsync(file.FullName,
            ["-v", "error", "-show_entries", "format=duration:chapter=start_time:chapter_tags=title", "-of", "json"], cancellationToken).ConfigureAwait(false);
        if (output is null)
        {
            return null;
        }

        try
        {
            using JsonDocument json = JsonDocument.Parse(output);
            JsonElement root = json.RootElement;
            if (!root.TryGetProperty("format", out JsonElement format) || !format.TryGetProperty("duration", out JsonElement durationText)
                || !double.TryParse(durationText.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                || seconds <= 0 || double.IsInfinity(seconds))
            {
                return null;
            }

            List<ProbedChapter> found = [];
            if (root.TryGetProperty("chapters", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement chapter in list.EnumerateArray())
                {
                    if (chapter.TryGetProperty("start_time", out JsonElement start)
                        && double.TryParse(start.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double at) && at >= 0)
                    {
                        string? title = chapter.TryGetProperty("tags", out JsonElement tags) && tags.TryGetProperty("title", out JsonElement name)
                            ? name.GetString()
                            : null;
                        found.Add(new ProbedChapter(TimeSpan.FromSeconds(at).Ticks, title));
                    }
                }
            }

            return new ProbeEntry(file.Length, file.LastWriteTimeUtc.Ticks, TimeSpan.FromSeconds(seconds).Ticks, [.. found]);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>ffprobe's output for a file, or null when it fails.</summary>
    private async Task<string?> RunAsync(string path, string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.FfprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add(path);

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return null;
            }

            SchedularrTV.Plugins.Performance.Work.Track(process, "Media library", "Probing media files");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.ProbeTimeout);

            Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
            string output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await errors.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                logger.LogDebug("ffprobe exited {Code} for {Path}.", process.ExitCode, path);
                return null;
            }

            return output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("ffprobe timed out for {Path}.", path);
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            logger.LogDebug(ex, "ffprobe failed for {Path}.", path);
            return null;
        }
    }

    private async Task<TimeSpan?> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.FfprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // ArgumentList quotes each value separately, so a path can never break out into extra args.
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-show_entries");
        startInfo.ArgumentList.Add("format=duration");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
        startInfo.ArgumentList.Add(path);

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return null;
            }

            SchedularrTV.Plugins.Performance.Work.Track(process, "Media library", "Probing media files");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.ProbeTimeout);

            string output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                logger.LogDebug("ffprobe exited {Code} for {Path}.", process.ExitCode, path);
                return null;
            }

            if (double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                && seconds > 0
                && !double.IsInfinity(seconds))
            {
                return TimeSpan.FromSeconds(seconds);
            }

            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("ffprobe timed out for {Path}.", path);
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            logger.LogWarning(ex, "ffprobe failed for {Path}.", path);
            return null;
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(options.CachePath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temp = options.CachePath + ".tmp";
            await using (FileStream stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, _cache, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, options.CachePath, overwrite: true);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not persist the probe cache; durations will be re-probed next run.");
        }
    }

    private static ConcurrentDictionary<string, ProbeEntry> Load(string path)
    {
        if (!File.Exists(path))
        {
            return new ConcurrentDictionary<string, ProbeEntry>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            Dictionary<string, ProbeEntry>? loaded = JsonSerializer.Deserialize<Dictionary<string, ProbeEntry>>(stream, JsonOptions);
            return new ConcurrentDictionary<string, ProbeEntry>(loaded ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new ConcurrentDictionary<string, ProbeEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
