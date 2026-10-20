using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SchedularrTV.Plugins.Performance;

namespace SchedularrTV.CreditSqueeze;

public sealed record CreditsOcrOptions(string Executable, string Language, int SampleSeconds, int Version = 1);
public sealed record CreditsOcrLine(string Text, double Confidence);
public sealed record CreditsOcrResult(IReadOnlyList<CreditsOcrLine> Lines, int Frames)
{
    public int UncertainLines => Lines.Count(line => line.Confidence < 60);
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
        string tsv = await CreditsOcrTools.RunAsync(options.Executable,
            [image, "stdout", "-l", options.Language, "--psm", "11", "tsv"], TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
        return ParseTsv(tsv);
    }

    /// <summary>TSV's block/paragraph/line hierarchy keeps names and their headings in OCR reading order.</summary>
    public static IReadOnlyList<CreditsOcrLine> ParseTsv(string tsv)
    {
        Dictionary<string, List<(string Text, double Confidence)>> lines = new(StringComparer.Ordinal);
        foreach (string row in tsv.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] cells = row.TrimEnd('\r').Split('\t', 12);
            if (cells.Length != 12 || cells[0] != "5" || string.IsNullOrWhiteSpace(cells[11])) continue;
            if (!double.TryParse(cells[10], NumberStyles.Float, CultureInfo.InvariantCulture, out double confidence) || !double.IsFinite(confidence)) continue;
            string key = string.Join(':', cells[1..5]);
            if (!lines.TryGetValue(key, out var words)) lines[key] = words = [];
            words.Add((cells[11].Trim(), Math.Clamp(confidence, 0, 100)));
        }
        return lines.Values.Select(words => new CreditsOcrLine(string.Join(' ', words.Select(word => word.Text)),
            words.Sum(word => word.Confidence * word.Text.Length) / words.Sum(word => word.Text.Length))).ToArray();
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

    public static string SourceStamp(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "missing";
    }

    public static string Key(SqueezeJob job, CreditsOcrOptions options) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { Path = Path.GetFullPath(job.SourcePath), Stamp = SourceStamp(job.SourcePath), job.SourceStart, job.Length, Options = options }))));

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
                     "-an", "-vf", $"fps=1/{options.SampleSeconds}:start_time=0,scale=w='trunc(min(1920,iw*sar*2)/2)*2':h=-2,setsar=1,format=gray",
                     "-frames:v", frames.ToString(CultureInfo.InvariantCulture), Path.Combine(work, "%06d.png")], TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
                string[] images = Directory.GetFiles(work, "*.png").Order(StringComparer.Ordinal).ToArray();
                var transcript = new CreditsTranscript();
                foreach (string image in images)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    transcript.Add(await engine.ReadAsync(image, options, cancellationToken).ConfigureAwait(false));
                    File.Delete(image);
                }
                if (transcript.Lines.Count == 0)
                    throw new InvalidOperationException("Credits Text: OCR found no readable credits. Check the source segment, Tesseract language data and sampling interval. The original credits will keep playing until a squeeze is ready.");
                if (SourceStamp(job.SourcePath) != stamp)
                    throw new InvalidOperationException("Credits Text: the source video changed while OCR was reading it; try again.");
                var result = new CreditsOcrResult(transcript.Lines.ToArray(), images.Length);
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
        var info = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["OMP_THREAD_LIMIT"] = "1";
        using var process = new Process { StartInfo = info };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"Credits Text: couldn't start '{executable}'. Install Tesseract with its language data and set its executable in the Credit squeeze plugin settings (ffmpeg is also required).", ex);
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
