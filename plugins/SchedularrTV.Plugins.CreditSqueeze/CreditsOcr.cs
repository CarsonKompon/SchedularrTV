using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SchedularrTV.Plugins.Performance;

namespace SchedularrTV.CreditSqueeze;

/// <summary>Percentages refer to the original source, before the squeeze layout is applied.</summary>
public sealed record CreditsRecognition(int TopPercent = 0, int BottomPercent = 100, int MinimumConfidence = 60)
{
    public CreditsRecognition Normalized() => new(Math.Clamp(TopPercent, 0, 95),
        Math.Clamp(BottomPercent, Math.Clamp(TopPercent, 0, 95) + 5, 100), Math.Clamp(MinimumConfidence, 0, 95));
}

public sealed record CreditsOcrOptions(string Executable, string Language, int SampleSeconds, int Version = 3, string? DataDirectory = null,
    CreditsRecognition? Recognition = null);
public sealed record CreditsOcrLine(string Text, double Confidence, CreditsTextBox? Box = null);
public sealed record CreditsOcrResult(IReadOnlyList<CreditsOcrLine> Lines, int Frames, int RejectedDetections = 0, string? Failure = null)
{
    public int UncertainLines => Lines.Count(line => line.Confidence < 60);
    public bool UsedFallback => Failure is not null;
}

/// <summary>Reject recognition noise, without guessing names or using a dictionary of common words.</summary>
public static class CreditsOcrFiltering
{
    public static bool Keep(CreditsOcrLine line, int minimumConfidence = 60)
    {
        if (!double.IsFinite(line.Confidence) || line.Confidence < minimumConfidence || string.IsNullOrWhiteSpace(line.Text)) return false;
        string text = line.Text.Trim();
        var characters = text.EnumerateRunes().Where(rune => !Rune.IsWhiteSpace(rune)).ToArray();
        if (characters.Any(rune => rune.Value == 0xfffd || Rune.IsControl(rune))) return false;
        int letters = characters.Count(Rune.IsLetter);
        // Years beside a copyright mark are useful; isolated numbers and punctuation aren't credits.
        if (letters == 0) return text.Contains('©') && characters.Count(Rune.IsDigit) >= 4;
        if (letters * 2 < characters.Length) return false;
        // A single CJK ideograph or Hangul syllable can be a real word/name. Latin
        // initials remain valid inside a line ("J. K. Simmons"), not as lone detections.
        bool ideographic = characters.Any(rune => rune.Value is >= 0x3400 and <= 0x9fff or >= 0xac00 and <= 0xd7af or >= 0x20000 and <= 0x3134f);
        if (letters == 1 && !ideographic) return false;
        if (letters <= 2 && !ideographic && line.Confidence < Math.Max(minimumConfidence, 90)) return false;
        return true;
    }
}

public interface ICreditsOcrEngine
{
    Task<IReadOnlyList<CreditsOcrLine>> ReadAsync(string image, CreditsOcrOptions options, CancellationToken cancellationToken);
}

/// <summary>Reads the original pixels locally. No language model, invented text, or correction of names.</summary>
public sealed class TesseractCreditsOcr : ICreditsOcrEngine
{
    public async Task<IReadOnlyList<CreditsOcrLine>> ReadAsync(string image, CreditsOcrOptions options, CancellationToken cancellationToken)
    {
        List<string> arguments = [image, "stdout", "-l", options.Language, "--psm", "11"];
        if (options.DataDirectory is { } data) arguments.AddRange(["--tessdata-dir", data]);
        arguments.Add("tsv");
        string tsv = await CreditsOcrTools.RunAsync(options.Executable, arguments, TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
        return ParseTsv(tsv);
    }

    /// <summary>TSV's block/paragraph/line hierarchy keeps names and their headings in OCR reading order.</summary>
    public static IReadOnlyList<CreditsOcrLine> ParseTsv(string tsv)
    {
        Dictionary<string, List<(string Text, double Confidence)>> lines = new(StringComparer.Ordinal);
        Dictionary<string, (int Width, int Height)> pages = new(StringComparer.Ordinal);
        Dictionary<string, (string Page, int Left, int Top, int Right, int Bottom)> boxes = new(StringComparer.Ordinal);
        foreach (string row in tsv.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] cells = row.TrimEnd('\r').Split('\t', 12);
            if (cells.Length != 12) continue;
            bool coordinates = int.TryParse(cells[6], out int left) & int.TryParse(cells[7], out int top)
                & int.TryParse(cells[8], out int width) & int.TryParse(cells[9], out int height);
            if (cells[0] == "1" && coordinates && width > 0 && height > 0)
                pages[cells[1]] = (width, height);
            if (cells[0] != "5" || string.IsNullOrWhiteSpace(cells[11])) continue;
            if (!double.TryParse(cells[10], NumberStyles.Float, CultureInfo.InvariantCulture, out double confidence) || !double.IsFinite(confidence)) continue;
            string key = string.Join(':', cells[1..5]);
            if (!lines.TryGetValue(key, out var words)) lines[key] = words = [];
            words.Add((cells[11].Trim(), Math.Clamp(confidence, 0, 100)));
            if (coordinates && width > 0 && height > 0 && left >= 0 && top >= 0)
            {
                var bounds = boxes.GetValueOrDefault(key, (cells[1], left, top, left + width, top + height));
                boxes[key] = (cells[1], Math.Min(bounds.Item2, left), Math.Min(bounds.Item3, top), Math.Max(bounds.Item4, left + width), Math.Max(bounds.Item5, top + height));
            }
        }
        return lines.Select(pair =>
        {
            var words = pair.Value;
            CreditsTextBox? box = null;
            if (boxes.TryGetValue(pair.Key, out var bounds) && pages.TryGetValue(bounds.Page, out var page))
                box = new((double)bounds.Left / page.Width, (double)bounds.Top / page.Height,
                    (double)(bounds.Right - bounds.Left) / page.Width, (double)(bounds.Bottom - bounds.Top) / page.Height);
            return new CreditsOcrLine(string.Join(' ', words.Select(word => word.Text)),
                words.Sum(word => word.Confidence * word.Text.Length) / words.Sum(word => word.Text.Length), box);
        }).ToArray();
    }
}

/// <summary>Joins repeated cards and overlapping scroll windows without globally removing repeated names or roles.</summary>
public sealed class CreditsTranscript
{
    private readonly List<CreditsOcrLine> _lines = [];
    private List<CreditsOcrLine> _previous = [];
    public IReadOnlyList<CreditsOcrLine> Lines => _lines;

    public void Add(IReadOnlyList<CreditsOcrLine> frame)
    {
        List<CreditsOcrLine> current = frame.Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .Select(line => line with { Text = line.Text.Trim() }).ToList();
        if (current.Count == 0)
        {
            // A blank sample (often a fade) must not cause the same card to be credited twice.
            return;
        }

        bool Same(CreditsOcrLine a, CreditsOcrLine b) => string.Equals(a.Text, b.Text, StringComparison.Ordinal);
        int overlap = Math.Min(_previous.Count, current.Count);
        while (overlap > 0 && !Enumerable.Range(0, overlap).All(index => Same(_previous[_previous.Count - overlap + index], current[index]))) overlap--;
        if (overlap == 0)
        {
            // A scroll sometimes shows only a subset of the preceding frame before new lines arrive.
            for (int start = 0; start + current.Count <= _previous.Count; start++)
                if (Enumerable.Range(0, current.Count).All(index => Same(_previous[start + index], current[index]))) return;
        }
        // Identical OCR wording can have clearer evidence in a later frame; keep its best confidence.
        for (int index = 0; index < overlap; index++)
        {
            int at = _lines.Count - overlap + index;
            if (current[index].Confidence > _lines[at].Confidence) _lines[at] = current[index];
        }
        _lines.AddRange(current.Skip(overlap));
        _previous = current;
    }
}

/// <summary>Samples only the source's credits, OCRs those full-size frames once, and caches the transcript separately from designs.</summary>
public sealed class CreditsOcr(CreditSqueezeSettings settings, ICreditsOcrEngine engine) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string Folder => Path.Combine(settings.SaveFolder, "_credits-text");

    /// <summary>Fallback is resolved per selected canvas and never saved as a successful OCR transcript.</summary>
    public async Task<CreditsOcrResult> ReadForCanvasAsync(SqueezeJob job, SqueezeCanvas canvas, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadAsync(job, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (canvas.HasCreditsTextFallback &&
            ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new([], 0, Failure: ex.Message);
        }
    }

    public static string SourceStamp(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "missing";
    }

    public static string Key(SqueezeJob job, CreditsOcrOptions options) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { PipelineVersion = 3, Path = Path.GetFullPath(job.SourcePath), Stamp = SourceStamp(job.SourcePath), job.SourceStart, job.Length, Options = options }))));

    public static string FrameFilter(CreditsOcrOptions options)
    {
        var area = (options.Recognition ?? new()).Normalized();
        string crop = area.TopPercent == 0 && area.BottomPercent == 100 ? "" :
            $",crop=iw:'max(2,trunc(ih*{area.BottomPercent - area.TopPercent}/100/2)*2)':0:'trunc(ih*{area.TopPercent}/100/2)*2'";
        return $"fps=1/{options.SampleSeconds}:start_time=0{crop},scale=w='trunc(min(1920,iw*sar*2)/2)*2':h=-2,setsar=1,format=gray";
    }

    public async Task<CreditsOcrResult> ReadAsync(SqueezeJob job, CancellationToken cancellationToken)
    {
        if (job.Length <= TimeSpan.Zero || job.Length > TimeSpan.FromMinutes(15) || job.SourceStart < TimeSpan.Zero)
            throw new InvalidOperationException("Credits Text needs a source credits segment between 0 and 15 minutes long.");
        if (!File.Exists(job.SourcePath)) throw new InvalidOperationException($"Credits Text: the source video isn't there: {job.SourcePath}");
        CreditsOcrOptions options = job.CreditsOcr ?? settings.Ocr;
        if (options.SampleSeconds is < 1 or > 5 || string.IsNullOrWhiteSpace(options.Language))
            throw new InvalidOperationException("Credits Text: choose an OCR language and a sampling interval of 1 to 5 seconds.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string stamp = SourceStamp(job.SourcePath);
            if (job.CreditsOcrSourceStamp is { } expected && expected != stamp)
                throw new InvalidOperationException("Credits Text: the source video changed while this squeeze was waiting; rebuild the schedule to read it again.");
            string cache = Path.Combine(Folder, Key(job, options) + ".json");
            Directory.CreateDirectory(Folder);
            try
            {
                if (File.Exists(cache) && JsonSerializer.Deserialize<CreditsOcrResult>(await File.ReadAllTextAsync(cache, cancellationToken).ConfigureAwait(false))
                    is { Lines.Count: > 0 } found && found.Lines.All(line => line is not null && !string.IsNullOrWhiteSpace(line.Text)))
                {
                    File.SetLastWriteTimeUtc(cache, DateTime.UtcNow);
                    return found;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException) { /* A partial cache is read again from the video. */ }

            string work = Path.Combine(Folder, ".work-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                using var activity = Work.Begin("Credit Squeeze", "Reading credits text", Path.GetFileName(job.SourcePath));
                string N(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
                int frames = (int)Math.Ceiling(job.Length.TotalSeconds / options.SampleSeconds);
                await CreditsOcrTools.RunAsync(settings.Ffmpeg,
                    ["-y", "-hide_banner", "-loglevel", "error", "-nostdin", "-ss", N(job.SourceStart.TotalSeconds), "-t", N(job.Length.TotalSeconds), "-i", job.SourcePath,
                     "-an", "-vf", FrameFilter(options),
                     "-frames:v", frames.ToString(CultureInfo.InvariantCulture), Path.Combine(work, "%06d.png")], TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
                string[] images = Directory.GetFiles(work, "*.png").Order(StringComparer.Ordinal).ToArray();
                var transcript = new CreditsTranscript();
                int confidence = (options.Recognition ?? new()).Normalized().MinimumConfidence;
                List<IReadOnlyList<CreditsOcrLine>> detections = [];
                foreach (string image in images)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    detections.Add(await engine.ReadAsync(image, options, cancellationToken).ConfigureAwait(false));
                    File.Delete(image);
                }
                cancellationToken.ThrowIfCancellationRequested();
                var cleaned = CreditsOcrAnalysis.Clean(detections, confidence);
                int rejected = detections.Sum(frame => frame.Count) - cleaned.Sum(frame => frame.Count);
                foreach (var frame in cleaned) transcript.Add(frame.Select(line => line with { Box = null }).ToArray());
                if (transcript.Lines.Count == 0)
                    throw new InvalidOperationException("Credits Text: OCR found no readable credits after filtering. Check the recognition area, confidence threshold and source segment under Text recognition. The original credits will keep playing until a squeeze is ready.");
                if (SourceStamp(job.SourcePath) != stamp)
                    throw new InvalidOperationException("Credits Text: the source video changed while OCR was reading it; try again.");
                var result = new CreditsOcrResult(transcript.Lines.ToArray(), images.Length, rejected);
                string temporary = Path.Combine(work, "result.json");
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(result), cancellationToken).ConfigureAwait(false);
                File.Move(temporary, cache, overwrite: true);
                Cleanup(cache);
                return result;
            }
            finally { Directory.Delete(work, recursive: true); }
        }
        finally { _gate.Release(); }
    }

    private void Cleanup(string current)
    {
        foreach (string path in Directory.EnumerateFiles(Folder, "*.json"))
        {
            try
            {
                if (path != current && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > settings.KeepFor) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public void Dispose() => _gate.Dispose();
}

internal static class CreditsOcrTools
{
    public static async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["OMP_THREAD_LIMIT"] = "1";
        using var process = new Process { StartInfo = info };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"Couldn't start '{executable}'. Check that this tool is installed and its path is correct in the Credit squeeze plugin settings.", ex);
        }
        Work.Track(process, "Credit Squeeze", "Credits OCR");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(timeout);
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(stop.Token).ConfigureAwait(false);
            string errors = await error.ConfigureAwait(false);
            string text = await output.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException($"Credits Text: {Path.GetFileName(executable)} failed: {errors.Trim()[..Math.Min(1500, errors.Trim().Length)]}");
            return text;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Credits Text: {Path.GetFileName(executable)} timed out while reading the credits.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            await Task.WhenAll(output, error).ConfigureAwait(false);
        }
    }
}
