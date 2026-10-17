using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SchedularrTV.Providers.ErsatzTv;

/// <summary>
/// Reads the audio and subtitle streams of the programmes a channel with track preferences is about to play, with
/// ffprobe, plus any subtitle files beside them. Only scheduled files are read, and each once: results are kept on disk
/// until the file's size or date changes.
/// </summary>
public class MediaTrackProbe(IOptions<ErsatzTvOptions> options, ILogger<MediaTrackProbe> logger)
{
    internal sealed record CachedStreams(long Length, long LastWriteTicks, List<MediaStream> Streams);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>Subtitle files ErsatzTV can read, as they're found beside videos.</summary>
    internal static readonly string[] SidecarExtensions = [".srt", ".ass", ".ssa", ".vtt", ".sup"];

    private readonly Lock _gate = new();
    private ConcurrentDictionary<string, CachedStreams>? _cache;
    private string? _cachePath;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Each local file's streams (embedded, then sidecar subtitles). A file that can't be read (missing here, ffprobe
    /// failing) is left out, and its programmes play ErsatzTV's defaults.
    /// </summary>
    public virtual async Task<IReadOnlyDictionary<string, IReadOnlyList<MediaStream>>> ProbeAsync(
        IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        ConcurrentDictionary<string, CachedStreams> cache = Cache();
        Dictionary<string, IReadOnlyList<MediaStream>> results = new(StringComparer.OrdinalIgnoreCase);
        List<(string Path, FileInfo File)> toProbe = [];

        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                continue;
            }

            if (cache.TryGetValue(file.FullName, out CachedStreams? cached)
                && cached.Length == file.Length && cached.LastWriteTicks == file.LastWriteTimeUtc.Ticks)
            {
                results[path] = [.. cached.Streams, .. Sidecars(file)];
            }
            else
            {
                toProbe.Add((path, file));
            }
        }

        if (toProbe.Count == 0)
        {
            return results;
        }

        logger.LogInformation("Reading the audio and subtitle tracks of {Count} programme files.", toProbe.Count);
        ConcurrentDictionary<string, IReadOnlyList<MediaStream>> probed = new(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(toProbe,
            new ParallelOptions { MaxDegreeOfParallelism = SchedularrTV.Core.Platform.HardwareProfile.Pick(4, 2), CancellationToken = cancellationToken },
            async (item, ct) =>
            {
                if (await RunAsync(item.File.FullName, ct).ConfigureAwait(false) is { } output && Parse(output) is { } streams)
                {
                    cache[item.File.FullName] = new CachedStreams(item.File.Length, item.File.LastWriteTimeUtc.Ticks, streams);
                    probed[item.Path] = [.. streams, .. Sidecars(item.File)];
                }
            }).ConfigureAwait(false);

        foreach ((string path, IReadOnlyList<MediaStream> streams) in probed)
        {
            results[path] = streams;
        }

        await SaveAsync(cache, cancellationToken).ConfigureAwait(false);
        return results;
    }

    /// <summary>
    /// The audio and subtitle streams in ffprobe's <c>-show_streams</c> JSON; null when it isn't that.
    /// </summary>
    internal static List<MediaStream>? Parse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("streams", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            List<MediaStream> streams = [];
            foreach (JsonElement stream in list.EnumerateArray())
            {
                MediaStreamKind? kind = Text(stream, "codec_type") switch
                {
                    "audio" => MediaStreamKind.Audio,
                    "subtitle" => MediaStreamKind.Subtitle,
                    _ => null
                };
                if (kind is null || !stream.TryGetProperty("index", out JsonElement index) || index.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                JsonElement tags = stream.TryGetProperty("tags", out JsonElement t) ? t : default;
                JsonElement disposition = stream.TryGetProperty("disposition", out JsonElement d) ? d : default;
                streams.Add(new MediaStream(
                    index.GetInt32(),
                    kind.Value,
                    Text(stream, "codec_name"),
                    Tag(tags, "language"),
                    Tag(tags, "title"),
                    Flag(disposition, "default"),
                    Flag(disposition, "forced") || Tag(tags, "title")?.Contains("forced", StringComparison.OrdinalIgnoreCase) == true,
                    Flag(disposition, "hearing_impaired") || Tag(tags, "title") is { } title
                        && (title.Contains("SDH", StringComparison.Ordinal) || title.Contains("hearing", StringComparison.OrdinalIgnoreCase))));
            }

            return streams;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Subtitle files beside a video, named after it: <c>Show S01E01.srt</c>, <c>Show S01E01.en.srt</c>,
    /// <c>Show S01E01.eng.forced.srt</c>, <c>Show S01E01.en.sdh.srt</c>. Looked for each time, as they come and go
    /// without the video changing.
    /// </summary>
    internal static IEnumerable<MediaStream> Sidecars(FileInfo video)
    {
        string stem = Path.GetFileNameWithoutExtension(video.Name);
        foreach (FileInfo file in Beside(video, stem).OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!SidecarExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase)
                || !file.Name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The parts between the video's name and the extension: language, forced, sdh/cc, default.
            string[] parts = file.Name[(stem.Length + 1)..^file.Extension.Length]
                .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string? language = null;
            bool forced = false, impaired = false, isDefault = false;
            foreach (string part in parts)
            {
                switch (part.ToLowerInvariant())
                {
                    case "forced" or "foreign":
                        forced = true;
                        break;
                    case "sdh" or "cc" or "hi":
                        impaired = true;
                        break;
                    case "default":
                        isDefault = true;
                        break;
                    default:
                        language ??= LanguageCodes.Normalize(part) is { Length: <= 3 } code ? code : null;
                        break;
                }
            }

            string codec = file.Extension.ToLowerInvariant() switch
            {
                ".srt" => "subrip",
                ".ass" => "ass",
                ".ssa" => "ssa",
                ".vtt" => "webvtt",
                _ => "hdmv_pgs_subtitle"
            };
            yield return new MediaStream(0, MediaStreamKind.Subtitle, codec, language, null, isDefault, forced, impaired, file.FullName);
        }
    }

    private static List<FileInfo> Beside(FileInfo video, string stem)
    {
        try
        {
            return video.Directory is { } folder ? [.. folder.EnumerateFiles(stem + ".*")] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    private async Task<string?> RunAsync(string path, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(options.Value.FfprobePath) ? "ffprobe" : options.Value.FfprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string argument in new[]
                 {
                     "-v", "error", "-show_entries",
                     "stream=index,codec_type,codec_name:stream_tags=language,title:stream_disposition=default,forced,hearing_impaired",
                     "-of", "json", path
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return null;
            }

            SchedularrTV.Plugins.Performance.Work.Track(process, "ErsatzTV", "Reading audio and subtitle tracks");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
            string output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await errors.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                logger.LogDebug("ffprobe exited {Code} reading the tracks of {Path}.", process.ExitCode, path);
                return null;
            }

            return output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("ffprobe timed out reading the tracks of {Path}.", path);
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            logger.LogDebug(ex, "ffprobe failed reading the tracks of {Path}.", path);
            return null;
        }
    }

    private ConcurrentDictionary<string, CachedStreams> Cache()
    {
        lock (_gate)
        {
            string path = options.Value.TrackProbeCachePath;
            if (_cache is null || _cachePath != path)
            {
                _cache = Load(path);
                _cachePath = path;
            }

            return _cache;
        }
    }

    private static ConcurrentDictionary<string, CachedStreams> Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using FileStream stream = File.OpenRead(path);
                if (JsonSerializer.Deserialize<Dictionary<string, CachedStreams>>(stream, JsonOptions) is { } loaded)
                {
                    return new ConcurrentDictionary<string, CachedStreams>(loaded, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }

        return new ConcurrentDictionary<string, CachedStreams>(StringComparer.OrdinalIgnoreCase);
    }

    private async Task SaveAsync(ConcurrentDictionary<string, CachedStreams> cache, CancellationToken cancellationToken)
    {
        string path = options.Value.TrackProbeCachePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            string temp = path + ".tmp";
            await using (FileStream stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, cache, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Couldn't keep the programme track cache; files will be read again next time.");
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Tag(JsonElement tags, string name)
    {
        if (tags.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Some muxers write upper-case tag names (LANGUAGE).
        foreach (JsonProperty property in tags.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static bool Flag(JsonElement disposition, string name) =>
        disposition.ValueKind == JsonValueKind.Object && disposition.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number && value.GetInt32() == 1;
}
