using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SchedularrTV.Providers.FileSystem;

internal sealed record ProbeEntry(long Length, long LastWriteTicks, long DurationTicks);

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
        ConcurrentDictionary<string, TimeSpan> durations = new(StringComparer.OrdinalIgnoreCase);
        List<FileInfo> toProbe = [];

        foreach (FileInfo file in files)
        {
            if (_cache.TryGetValue(file.FullName, out ProbeEntry? cached)
                && cached.Length == file.Length
                && cached.LastWriteTicks == file.LastWriteTimeUtc.Ticks)
            {
                durations[file.FullName] = TimeSpan.FromTicks(cached.DurationTicks);
            }
            else
            {
                toProbe.Add(file);
            }
        }

        if (toProbe.Count > 0)
        {
            logger.LogInformation("Probing {Count} new or changed media files ({Cached} cached).", toProbe.Count, durations.Count);

            int completed = 0;

            await Parallel.ForEachAsync(
                toProbe,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.ProbeConcurrency), CancellationToken = cancellationToken },
                async (file, ct) =>
                {
                    TimeSpan? duration = await ProbeAsync(file.FullName, ct).ConfigureAwait(false);
                    if (duration is { } value)
                    {
                        durations[file.FullName] = value;
                        _cache[file.FullName] = new ProbeEntry(file.Length, file.LastWriteTimeUtc.Ticks, value.Ticks);
                    }

                    int done = Interlocked.Increment(ref completed);
                    if (done % 500 == 0)
                    {
                        logger.LogInformation("Probed {Done}/{Total} files.", done, toProbe.Count);
                    }
                }).ConfigureAwait(false);

            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        return durations;
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
