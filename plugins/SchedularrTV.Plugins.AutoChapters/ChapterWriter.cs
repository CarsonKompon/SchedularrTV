using System.Globalization;
using System.Text;

namespace SchedularrTV.AutoChapters;

/// <summary>How writing chapters into a file went.</summary>
public sealed record WriteResult(bool Applied, string Message);

/// <summary>
/// Writes reviewed markers into the video files themselves as chapters, without re-encoding anything: Matroska and
/// WebM in place with mkvpropedit when MKVToolNix is installed, otherwise (and for MP4 and MOV) by copying every stream
/// into a new file with the chapters added, checking it, and swapping it in. One file at a time, in the background.
/// </summary>
public sealed class ChapterWriter(
    ChapterStore store,
    AutoChaptersSettings settings,
    ReviewMedia media,
    IServiceProvider services,
    ILogger<ChapterWriter> logger)
{
    private static readonly HashSet<string> Matroska = new(StringComparer.OrdinalIgnoreCase) { ".mkv", ".mka", ".mk3d", ".webm" };
    private static readonly HashSet<string> QuickTime = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov" };

    private readonly SemaphoreSlim _one = new(1);
    private readonly Lock _gate = new();
    private readonly List<string> _writing = [];

    /// <summary>Raised when a file has been written (or couldn't be), with its record as saved.</summary>
    public event Action<ChapterRecord, WriteResult>? Finished;

    /// <summary>Titles being written or waiting to be.</summary>
    public IReadOnlyList<string> Writing
    {
        get
        {
            lock (_gate)
            {
                return [.. _writing];
            }
        }
    }

    /// <summary>Why chapters can't be written into this file; null when they can.</summary>
    public static string? Unsupported(string path)
    {
        string extension = Path.GetExtension(path);
        return Matroska.Contains(extension) || QuickTime.Contains(extension)
            ? null
            : $"{(extension.Length > 1 ? extension[1..].ToUpperInvariant() : "These")} files can't hold chapter markers (only MKV, WebM, MP4, M4V and MOV can).";
    }

    /// <summary>
    /// Approves a record's markers and writes them into its file in the background. Returns at once with why it won't be
    /// written, or null when it's on its way (see <see cref="Finished"/>).
    /// </summary>
    public string? Queue(ChapterRecord record)
    {
        string? file = settings.LocalPath(record.Path);
        string? problem = file is null
            ? "The file isn't reachable from this computer."
            : Unsupported(file) ?? (record.Markers.Count == 0 ? "There are no markers to write." : null);
        if (problem is not null)
        {
            return problem;
        }

        lock (_gate)
        {
            _writing.Add(record.Title);
        }

        _ = Task.Run(async () =>
        {
            await _one.WaitAsync().ConfigureAwait(false);
            WriteResult result;
            try
            {
                result = await WriteAsync(file!, record.Markers, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Auto Chapters couldn't write chapters into {File}.", file);
                result = new WriteResult(false, $"Couldn't write the chapters: {ex.GetBaseException().Message}");
            }
            finally
            {
                _one.Release();
                lock (_gate)
                {
                    _writing.Remove(record.Title);
                }
            }

            // Saved against the file as it now is, so it isn't analysed again because it changed.
            ChapterRecord saved = (store.Find(record.Path) ?? record).Copy();
            saved.Review = result.Applied ? ReviewState.Applied : ReviewState.Approved;
            saved.ReviewNote = result.Message;
            if (result.Applied && new FileInfo(file!) is { Exists: true } written)
            {
                saved.Size = written.Length;
                saved.Modified = written.LastWriteTimeUtc;
            }

            store.Save(saved);
            if (result.Applied)
            {
                services.GetService<IMediaLibraryNotifier>()?.LibraryChanged();
            }

            Finished?.Invoke(saved, result);
        });
        return null;
    }

    /// <summary>Writes chapters starting at 0 and at each marker into <paramref name="file"/>.</summary>
    public async Task<WriteResult> WriteAsync(string file, IReadOnlyList<double> markers, CancellationToken cancellationToken)
    {
        if (Unsupported(file) is { } unsupported)
        {
            return new WriteResult(false, unsupported);
        }

        MediaInfo before = await media.ProbeAsync(file, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<double> starts = Starts(markers, before.Duration);
        if (starts.Count < 2)
        {
            return new WriteResult(false, "There are no markers inside the programme to write.");
        }

        if (Matroska.Contains(Path.GetExtension(file)) && settings.Mkvpropedit is { } mkvpropedit)
        {
            WriteResult? inPlace = await MkvpropeditAsync(mkvpropedit, file, starts, cancellationToken).ConfigureAwait(false);
            if (inPlace is not null)
            {
                media.Forget(file);
                return inPlace;
            }
        }

        WriteResult copied = await RemuxAsync(file, starts, before, cancellationToken).ConfigureAwait(false);
        media.Forget(file);
        return copied;
    }

    /// <summary>Where chapters start: 0, then each marker at least a second from the last and inside the programme.</summary>
    public static IReadOnlyList<double> Starts(IEnumerable<double> markers, double duration)
    {
        List<double> starts = [0];
        foreach (double marker in markers.Order())
        {
            if (marker - starts[^1] >= 1 && (duration <= 0 || marker < duration - 1))
            {
                starts.Add(Math.Round(marker, 3));
            }
        }

        return starts;
    }

    /// <summary>The chapters as ffmpeg's metadata file.</summary>
    public static string FfMetadata(IReadOnlyList<double> starts, double duration)
    {
        var text = new StringBuilder(";FFMETADATA1\n");
        for (int index = 0; index < starts.Count; index++)
        {
            double end = index + 1 < starts.Count ? starts[index + 1] : Math.Max(duration, starts[index] + 1);
            text.Append("[CHAPTER]\nTIMEBASE=1/1000\n")
                .Append("START=").Append(((long)Math.Round(starts[index] * 1000)).ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append("END=").Append(((long)Math.Round(end * 1000)).ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append("title=Chapter ").Append(index + 1).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>The chapters in the simple (OGM) format mkvpropedit reads.</summary>
    public static string SimpleChapters(IReadOnlyList<double> starts)
    {
        var text = new StringBuilder();
        for (int index = 0; index < starts.Count; index++)
        {
            string number = (index + 1).ToString("00", CultureInfo.InvariantCulture);
            text.Append("CHAPTER").Append(number).Append('=').Append(TimeSpan.FromSeconds(starts[index]).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture)).Append('\n')
                .Append("CHAPTER").Append(number).Append("NAME=Chapter ").Append(index + 1).Append('\n');
        }

        return text.ToString();
    }

    private async Task<WriteResult?> MkvpropeditAsync(string mkvpropedit, string file, IReadOnlyList<double> starts, CancellationToken cancellationToken)
    {
        string chapters = Path.Combine(Path.GetTempPath(), $"schedularrtv-chapters-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(chapters, SimpleChapters(starts), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            var (exit, lines) = await Ffmpeg.RunAsync(mkvpropedit, [file, "--chapters", chapters], null, "Writing chapters", cancellationToken)
                .ConfigureAwait(false);

            // 0 is done, 1 is done with warnings, 2 is an error.
            if (exit is 0 or 1)
            {
                return new WriteResult(true, $"{starts.Count} chapters written into the file with mkvpropedit.");
            }

            logger.LogWarning("mkvpropedit couldn't write chapters into {File} (exit {Exit}): {Tail}; copying the file with ffmpeg instead.",
                file, exit, string.Join(" ", lines.TakeLast(3)));
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            logger.LogWarning("mkvpropedit couldn't be run ({Message}); copying the file with ffmpeg instead.", ex.Message);
            return null;
        }
        finally
        {
            TryDelete(chapters);
        }
    }

    private async Task<WriteResult> RemuxAsync(string file, IReadOnlyList<double> starts, MediaInfo before, CancellationToken cancellationToken)
    {
        string folder = Path.GetDirectoryName(file)!;
        string extension = Path.GetExtension(file);
        string temp = Path.Combine(folder, $".{Path.GetFileNameWithoutExtension(file)}.chapters-{Guid.NewGuid():N}{extension}");
        string metadata = Path.Combine(Path.GetTempPath(), $"schedularrtv-chapters-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(metadata, FfMetadata(starts, before.Duration), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            long size = new FileInfo(file).Length;
            try
            {
                // Network shares can't be asked; ffmpeg will say if it runs out of room.
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(file))!);
                if (drive.IsReady && drive.AvailableFreeSpace < size + (64L << 20))
                {
                    return new WriteResult(false, "Not enough free space beside the file to write a copy with chapters.");
                }
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
            }

            // Every stream copied as it is (nothing re-encoded); first everything, then just picture, sound and
            // subtitles if something else in the file (a data or attachment stream) won't copy into the same format.
            List<string>? failure = null;
            string[][] attempts = [["-map", "0"], ["-map", "0:v", "-map", "0:a?", "-map", "0:s?"]];
            string format = extension.ToLowerInvariant() switch
            {
                ".webm" => "webm",
                ".mov" => "mov",
                ".mp4" or ".m4v" => "mp4",
                _ => "matroska"
            };
            foreach (string[] streams in attempts)
            {
                TryDelete(temp);
                List<string> arguments = ["-hide_banner", "-nostats", "-nostdin", "-loglevel", "error", "-y", "-i", file, "-f", "ffmetadata", "-i", metadata];
                arguments.AddRange(streams);
                arguments.AddRange(["-map_metadata", "0", "-map_chapters", "1", "-c", "copy"]);
                if (format is "mp4" or "mov")
                {
                    arguments.AddRange(["-movflags", "+faststart"]);
                }

                arguments.AddRange(["-f", format, temp]);
                var (exit, lines) = await Ffmpeg.RunAsync(settings.Ffmpeg, arguments, null, "Writing chapters", cancellationToken).ConfigureAwait(false);
                if (exit == 0 && File.Exists(temp))
                {
                    failure = null;
                    break;
                }

                failure = lines;
            }

            if (failure is not null)
            {
                return new WriteResult(false, $"ffmpeg couldn't copy the file with chapters: {string.Join(" ", failure.TakeLast(2)).Trim()}");
            }

            // Checked before it replaces anything: as long as the original, as big (give or take), and with the chapters.
            MediaInfo after = await media.ProbeAsync(temp, cancellationToken).ConfigureAwait(false);
            media.Forget(temp);
            long written = new FileInfo(temp).Length;
            if (Math.Abs(after.Duration - before.Duration) > 1.5 || written < size * 0.9 || after.Chapters < starts.Count)
            {
                return new WriteResult(false,
                    $"The copy with chapters didn't check out ({after.Duration:0.#}s against {before.Duration:0.#}s, {after.Chapters} chapters), so the original was left alone.");
            }

            // The file may be open for a moment more (the preview, a frame grab, ErsatzTV reading it): keep trying briefly.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temp, file, overwrite: true);
                    break;
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 20)
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new WriteResult(false,
                        "The file is in use (perhaps airing right now), so the chapters couldn't be written. Re-open it from Recently reviewed and try again later.");
                }
            }

            return new WriteResult(true, $"{starts.Count} chapters written into the file (streams copied, nothing re-encoded).");
        }
        finally
        {
            TryDelete(temp);
            TryDelete(metadata);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
