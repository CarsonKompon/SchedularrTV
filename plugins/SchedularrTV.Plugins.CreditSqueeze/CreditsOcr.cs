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
    // YAML constructs plugin settings through a real parameterless constructor;
    // optional positional arguments alone do not provide one to reflection.
    public CreditsRecognition() : this(0, 100, 60) { }
    public CreditsRecognition Normalized() => new(Math.Clamp(TopPercent, 0, 95),
        Math.Clamp(BottomPercent, Math.Clamp(TopPercent, 0, 95) + 5, 100), Math.Clamp(MinimumConfidence, 0, 95));
}

public sealed record CreditsOcrOptions(string Executable, string Language, int SampleSeconds, int Version = 7, string? DataDirectory = null,
    CreditsRecognition? Recognition = null, int MaxWidth = 1920, string? QualityDataDirectory = null, string Profile = "balanced", bool DetectionPass = false);
public sealed record CreditsOcrLine(string Text, double Confidence, CreditsTextBox? Box = null);
public sealed record CreditsOcrPage(TimeSpan At, IReadOnlyList<CreditsOcrLine> Lines);
public sealed record CreditsOcrSample(TimeSpan At, IReadOnlyList<CreditsOcrLine> Lines, bool StaticFrame = false,
    CreditsLayoutKind Layout = CreditsLayoutKind.Unknown, int? VisualGroup = null, bool BlankFrame = false);
public sealed record CreditsOcrStatistics(int SampledFrames, int UniqueFrames, int RecognizerProcesses, int RetryFrames, long ElapsedMilliseconds, string Model, int CachedFrames = 0, int ConsensusCorrections = 0);
public sealed record CreditsOcrResult(IReadOnlyList<CreditsOcrLine> Lines, int Frames, int RejectedDetections = 0, string? Failure = null,
    IReadOnlyList<CreditsOcrPage>? Pages = null, IReadOnlyList<CreditsOcrSample>? Samples = null, CreditsOcrStatistics? Statistics = null)
{
    public int UncertainLines => Lines.Count(line => line.Confidence < 85);
    public bool UsedFallback => Failure is not null;

    /// <summary>Preserve complete scans (including blank ones); hold consecutive identical scans as one page.</summary>
    public static IReadOnlyList<CreditsOcrPage> ScanPages(IReadOnlyList<IReadOnlyList<CreditsOcrLine>> frames, int sampleSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleSeconds, 1);
        List<CreditsOcrPage> pages = [];
        for (int index = 0; index < frames.Count; index++)
        {
            CreditsOcrLine[] lines = frames[index].Select(line => line with { Text = line.Text.Trim(), Box = null }).ToArray();
            if (pages.Count > 0 && pages[^1].Lines.Select(line => line.Text).SequenceEqual(lines.Select(line => line.Text), StringComparer.Ordinal))
            {
                var previous = pages[^1];
                pages[^1] = previous with { Lines = previous.Lines.Select((line, at) => lines[at].Confidence > line.Confidence ? lines[at] : line).ToArray() };
            }
            else pages.Add(new(TimeSpan.FromSeconds((double)index * sampleSeconds), lines));
        }
        return pages;
    }
}

/// <summary>Reject recognition noise, without guessing names or using a dictionary of common words.</summary>
public static class CreditsOcrFiltering
{
    /// <summary>A few repeated scraps aren't a usable credits transcript, however confident the OCR engine is.</summary>
    public static bool Usable(IReadOnlyList<CreditsOcrLine> lines)
    {
        var distinct = lines.Where(line => !string.IsNullOrWhiteSpace(line.Text)).DistinctBy(line => line.Text.Trim(), StringComparer.OrdinalIgnoreCase).ToArray();
        int Letters(string text) => text.EnumerateRunes().Sum(rune => Rune.IsLetter(rune)
            ? rune.Value is >= 0x3400 and <= 0x9fff or >= 0xac00 and <= 0xd7af ? 3 : 1 : 0);
        return distinct.Length >= 2 && distinct.Sum(line => Letters(line.Text)) >= 10
            || distinct.Length == 1 && distinct[0].Confidence >= 85 && Letters(distinct[0].Text) >= 16
                && distinct[0].Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3;
    }
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

public sealed record CreditsOcrBatchResult(IReadOnlyList<IReadOnlyList<CreditsOcrLine>> Frames, int Processes, int RetriedFrames, string Model, int CachedFrames = 0);
public interface ICreditsOcrBatchEngine : ICreditsOcrEngine
{
    Task<CreditsOcrBatchResult> ReadBatchAsync(IReadOnlyList<string> images, CreditsOcrOptions options, CancellationToken cancellationToken);
}

/// <summary>Reads the original pixels locally. No language model, invented text, or correction of names.</summary>
public sealed class TesseractCreditsOcr : ICreditsOcrBatchEngine
{
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, IReadOnlyList<CreditsOcrLine>> _frameCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheOrder = new();
    public async Task<IReadOnlyList<CreditsOcrLine>> ReadAsync(string image, CreditsOcrOptions options, CancellationToken cancellationToken) =>
        (await ReadBatchAsync([image], options, cancellationToken).ConfigureAwait(false)).Frames[0];

    public async Task<CreditsOcrBatchResult> ReadBatchAsync(IReadOnlyList<string> images, CreditsOcrOptions options, CancellationToken cancellationToken)
    {
        IReadOnlyList<CreditsOcrLine>[] result = images.Select(_ => (IReadOnlyList<CreditsOcrLine>)Array.Empty<CreditsOcrLine>()).ToArray();
        bool quality = !options.DetectionPass && options.Profile != "fast" && options.QualityDataDirectory is not null;
        var effective = quality ? options with { DataDirectory = options.QualityDataDirectory } : options;
        string model = quality ? "tessdata_best" : options.DetectionPass ? "fast boundary scan" : "configured Tesseract model";
        List<int> nonBlank = [];
        bool[] blank = new bool[images.Count];
        string?[] cacheKeys = new string?[images.Count];
        int cached = 0;
        for (int index = 0; index < images.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(images[index]))
            {
                await using var file = File.OpenRead(images[index]);
                string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken));
                string key = $"{options.Executable}|{options.Language}|{effective.DataDirectory}|{options.Profile}|{options.DetectionPass}|{options.Recognition?.MinimumConfidence ?? 60}|{hash}";
                cacheKeys[index] = key;
                lock (_cacheGate)
                {
                    if (_frameCache.TryGetValue(key, out var known)) { result[index] = known; cached++; continue; }
                }
            }
            using var bitmap = SkiaSharp.SKBitmap.Decode(images[index]);
            if (bitmap is null || !CreditsOcrPreprocessing.IsBlank(bitmap)) nonBlank.Add(index); else blank[index] = true;
        }
        int processes = 0, retried = 0;
        const int batchSize = 16;
        foreach (var batch in nonBlank.Chunk(batchSize))
        {
            var lines = await ReadPassAsync(batch.Select(index => images[index]).ToArray(), effective, "11", cancellationToken).ConfigureAwait(false);
            processes++;
            for (int index = 0; index < batch.Length; index++) result[batch[index]] = lines[index];
        }
        if (options.DetectionPass || options.Profile == "fast") return Finish();
        int confidence = (options.Recognition ?? new()).Normalized().MinimumConfidence;
        foreach (var batch in nonBlank.Where(index => CreditsOcrPreprocessing.NeedsRetry(result[index], confidence)).Chunk(batchSize))
        {
            List<string> processed = [];
            try
            {
                foreach (int index in batch)
                {
                    using var bitmap = SkiaSharp.SKBitmap.Decode(images[index]) ?? throw new IOException("Could not decode an OCR frame.");
                    using var contrast = CreditsOcrPreprocessing.Contrast(bitmap);
                    using var encoded = contrast.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                    string path = Path.Combine(Path.GetDirectoryName(images[index])!, ".ocr-contrast-" + Guid.NewGuid().ToString("N") + ".png");
                    processed.Add(path);
                    await File.WriteAllBytesAsync(path, encoded.ToArray(), cancellationToken).ConfigureAwait(false);
                }
                processes++; retried += batch.Length;
                var alternate = await ReadPassAsync(processed, effective, "6", cancellationToken).ConfigureAwait(false);
                for (int index = 0; index < batch.Length; index++) result[batch[index]] = CreditsOcrPreprocessing.Prefer(result[batch[index]], alternate[index], confidence);
                if (options.Profile == "accurate")
                {
                    processes++;
                    alternate = await ReadPassAsync(batch.Select(index => images[index]).ToArray(), effective, "3", cancellationToken).ConfigureAwait(false);
                    for (int index = 0; index < batch.Length; index++) result[batch[index]] = CreditsOcrPreprocessing.Prefer(result[batch[index]], alternate[index], confidence);
                }
            }
            catch (InvalidOperationException) { /* A failed retry must not discard the primary result. */ }
            finally { foreach (string path in processed) if (File.Exists(path)) File.Delete(path); }
        }
        return Finish();

        CreditsOcrBatchResult Finish()
        {
            lock (_cacheGate)
            {
                for (int index = 0; index < result.Length; index++)
                {
                    if (cacheKeys[index] is not { } key || _frameCache.ContainsKey(key)) continue;
                    if (!blank[index] && CreditsOcrPreprocessing.NeedsRetry(result[index], (options.Recognition ?? new()).Normalized().MinimumConfidence)) continue;
                    while (_frameCache.Count >= 256 && _cacheOrder.TryDequeue(out var oldest)) _frameCache.Remove(oldest);
                    _frameCache[key] = result[index]; _cacheOrder.Enqueue(key);
                }
            }
            return new(result, processes, retried, model, cached);
        }
    }

    private static async Task<IReadOnlyList<IReadOnlyList<CreditsOcrLine>>> ReadPassAsync(IReadOnlyList<string> images, CreditsOcrOptions options, string mode, CancellationToken cancellationToken)
    {
        if (images.Count == 0) return [];
        string? list = null;
        try
        {
            string input = images[0];
            if (images.Count > 1)
            {
                if (images.Any(path => path.Contains('\n') || path.Contains('\r'))) throw new IOException("OCR frame paths must not contain newlines.");
                list = Path.Combine(Path.GetDirectoryName(images[0])!, ".ocr-input-" + Guid.NewGuid().ToString("N") + ".txt");
                await File.WriteAllLinesAsync(list, images.Select(Path.GetFullPath), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                input = list;
            }
            List<string> arguments = [input, "stdout", "-l", options.Language, "--oem", "1", "--psm", mode,
                "-c", "tessedit_create_tsv=1", "-c", "tessedit_create_txt=0"];
            if (options.DataDirectory is { } data) arguments.AddRange(["--tessdata-dir", data]);
            string tsv = await CreditsOcrTools.RunAsync(options.Executable, arguments, TimeSpan.FromSeconds(Math.Clamp(images.Count * 8, 45, 180)), cancellationToken).ConfigureAwait(false);
            return ParseTsvPages(tsv, images.Count);
        }
        finally { if (list is not null && File.Exists(list)) File.Delete(list); }
    }

    public static IReadOnlyList<IReadOnlyList<CreditsOcrLine>> ParseTsvPages(string tsv, int count)
    {
        var pages = Enumerable.Range(0, count).Select(_ => new StringBuilder()).ToArray();
        foreach (string row in tsv.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var cells = row.Split('\t', 3);
            if (cells.Length >= 2 && int.TryParse(cells[1], out int page) && page >= 1 && page <= count) pages[page - 1].AppendLine(row);
        }
        return pages.Select(page => ParseTsv(page.ToString())).ToArray();
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
    private List<int> _previousPositions = [];
    private CreditsLayoutKind _layout;
    private int? _visualGroup;
    public IReadOnlyList<CreditsOcrLine> Lines => _lines;

    public void Add(CreditsOcrSample sample)
    {
        bool sameWords = _previous.Select(line => line.Text).SequenceEqual(sample.Lines.Select(line => line.Text), StringComparer.OrdinalIgnoreCase);
        bool transition = sample.Layout != CreditsLayoutKind.Unknown && _layout != CreditsLayoutKind.Unknown && sample.Layout != _layout;
        bool newCard = sample.Layout == CreditsLayoutKind.Card && _layout == CreditsLayoutKind.Card
            && sample.VisualGroup is not null && _visualGroup is not null && sample.VisualGroup != _visualGroup;
        if (!sameWords && (transition || newCard)) { _previous = []; _previousPositions = []; }
        AddCore(sample.Lines);
        if (sample.Layout != CreditsLayoutKind.Unknown) _layout = sample.Layout;
        if (sample.Lines.Count > 0) _visualGroup = sample.VisualGroup;
    }

    public void Add(IReadOnlyList<CreditsOcrLine> frame) => AddCore(frame);

    private void AddCore(IReadOnlyList<CreditsOcrLine> frame)
    {
        List<CreditsOcrLine> current = frame.Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .Select(line => line with { Text = line.Text.Trim() }).ToList();
        if (current.Count == 0)
        {
            // A blank sample (often a fade) must not cause the same card to be credited twice.
            return;
        }

        bool Same(CreditsOcrLine a, CreditsOcrLine b) => CreditsTextAgreement.Identity(a.Text) == CreditsTextAgreement.Identity(b.Text);
        List<int> matched = [];
        // Align a prefix of this scan against the previous scrolling window, allowing
        // a missed line in that window. Never globally deduplicate names/roles.
        for (int start = 0; start < _previous.Count; start++)
        {
            List<int> candidate = [];
            int cursor = start;
            foreach (var line in current)
            {
                while (cursor < _previous.Count && !Same(_previous[cursor], line)) cursor++;
                if (cursor == _previous.Count) break;
                candidate.Add(cursor++);
            }
            if (candidate.Count > matched.Count && (candidate.Count == current.Count || candidate[^1] == _previous.Count - 1)) matched = candidate;
        }
        List<int> positions = [];
        for (int index = 0; index < matched.Count; index++)
        {
            int at = _previousPositions[matched[index]];
            if (current[index].Confidence > _lines[at].Confidence) _lines[at] = current[index];
            positions.Add(at);
        }
        if (matched.Count == current.Count) return; // retain the wider window through partial scans/fades
        foreach (var line in current.Skip(matched.Count)) { positions.Add(_lines.Count); _lines.Add(line); }
        _previous = current; _previousPositions = positions;
    }

}

/// <summary>Samples only the source's credits, OCRs those full-size frames once, and caches the transcript separately from designs.</summary>
public sealed class CreditsOcr(CreditSqueezeSettings settings, ICreditsOcrEngine engine) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string Folder => Path.Combine(settings.SaveFolder, "_credits-text");

    public async Task StoreAsync(SqueezeJob job, CreditsOcrResult result, CancellationToken cancellationToken)
    {
        if (result.UsedFallback || !CreditsOcrFiltering.Usable(result.Lines)) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (job.CreditsOcrSourceStamp is { } expected && CreditsOcr.SourceStamp(job.SourcePath) != expected)
                throw new InvalidOperationException("The source changed while detecting its credits. Please retry.");
            Directory.CreateDirectory(Folder);
            string cache = Path.Combine(Folder, Key(job, job.CreditsOcr ?? settings.Ocr) + ".json");
            string temporary = cache + ".tmp";
            try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(result), cancellationToken); File.Move(temporary, cache, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _gate.Release(); }
    }

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
        JsonSerializer.Serialize(new { PipelineVersion = 7, Path = Path.GetFullPath(job.SourcePath), Stamp = SourceStamp(job.SourcePath), job.SourceStart, job.Length, Options = options }))));

    public static string FrameFilter(CreditsOcrOptions options)
    {
        var area = (options.Recognition ?? new()).Normalized();
        string crop = area.TopPercent == 0 && area.BottomPercent == 100 ? "" :
            $",crop=iw:'max(2,trunc(ih*{area.BottomPercent - area.TopPercent}/100/2)*2)':0:'trunc(ih*{area.TopPercent}/100/2)*2'";
        // Normalize anamorphic pixel shape before resizing. Resetting SAR only after a
        // width-only resize distorts DVD-era fonts and makes recognition substantially harder.
        return $"fps=1/{options.SampleSeconds}:start_time=0{crop},scale=w='max(2,trunc(iw*sar/2)*2)':h=ih,setsar=1,"
            + $"scale=w='trunc(min({Math.Clamp(options.MaxWidth, 640, 3840)},iw*2)/2)*2':h=-2,format=rgb24";
    }

    public Task<CreditsOcrResult> ReadAsync(SqueezeJob job, CancellationToken cancellationToken) => ScanAsync(job, cancellationToken, requireUsable: true);

    public async Task<CreditsOcrResult> ScanAsync(SqueezeJob job, CancellationToken cancellationToken, bool requireUsable = false)
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
            string cache = Path.Combine(Folder, Key(job, options) + (requireUsable ? "" : "-scan") + ".json");
            Directory.CreateDirectory(Folder);
            try
            {
                if (File.Exists(cache) && JsonSerializer.Deserialize<CreditsOcrResult>(await File.ReadAllTextAsync(cache, cancellationToken).ConfigureAwait(false))
                    is { Samples.Count: > 0 } found && (!requireUsable || CreditsOcrFiltering.Usable(found.Lines)))
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
                var elapsed = Stopwatch.StartNew();
                string N(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
                int frames = (int)Math.Ceiling(job.Length.TotalSeconds / options.SampleSeconds);
                await CreditsOcrTools.RunAsync(settings.Ffmpeg,
                    ["-y", "-hide_banner", "-loglevel", "error", "-nostdin", "-ss", N(job.SourceStart.TotalSeconds), "-t", N(job.Length.TotalSeconds), "-i", job.SourcePath,
                     "-an", "-vf", FrameFilter(options),
                     "-frames:v", frames.ToString(CultureInfo.InvariantCulture), Path.Combine(work, "%06d.png")], TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
                string[] images = Directory.GetFiles(work, "*.png").Order(StringComparer.Ordinal).ToArray();
                var transcript = new CreditsTranscript();
                int confidence = (options.Recognition ?? new()).Normalized().MinimumConfidence;
                var reuse = new CreditsFrameReuse();
                List<int> frameMap = [];
                foreach (string image in images)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int before = reuse.Unique.Count;
                    frameMap.Add(await reuse.AddAsync(image, cancellationToken).ConfigureAwait(false));
                    if (reuse.Unique.Count == before) File.Delete(image);
                }
                IReadOnlyList<IReadOnlyList<CreditsOcrLine>> unique;
                int processes, retries, cachedFrames = 0;
                string model;
                if (engine is ICreditsOcrBatchEngine batch)
                {
                    var batchResult = await batch.ReadBatchAsync(reuse.Unique, options, cancellationToken).ConfigureAwait(false);
                    unique = batchResult.Frames; processes = batchResult.Processes; retries = batchResult.RetriedFrames; model = batchResult.Model; cachedFrames = batchResult.CachedFrames;
                }
                else
                {
                    List<IReadOnlyList<CreditsOcrLine>> read = [];
                    foreach (string image in reuse.Unique) read.Add(await engine.ReadAsync(image, options, cancellationToken).ConfigureAwait(false));
                    unique = read; processes = read.Count; retries = 0; model = engine.GetType().Name;
                }
                var detections = frameMap.Select(index => unique[index]).ToArray();
                cancellationToken.ThrowIfCancellationRequested();
                var cleaned = CreditsOcrAnalysis.Clean(detections, confidence);
                int rejected = detections.Sum(frame => frame.Count) - cleaned.Sum(frame => frame.Count);
                var classified = CreditsLayouts.Classify(cleaned.Select((lines, index) => new CreditsOcrSample(
                    TimeSpan.FromSeconds((double)index * options.SampleSeconds), lines,
                    index > 0 && frameMap[index] == frameMap[index - 1] || index + 1 < frameMap.Count && frameMap[index] == frameMap[index + 1],
                    VisualGroup: frameMap[index], BlankFrame: reuse.Blank[frameMap[index]])).ToArray());
                var (samples, corrections) = CreditsTextAgreement.Refine(classified);
                foreach (var sample in samples) transcript.Add(sample);
                if (requireUsable && !CreditsOcrFiltering.Usable(transcript.Lines))
                    throw new InvalidOperationException("Credits Text: OCR found no readable credits or too little reliable text after filtering. Check the original ending and recognition settings. Isolated fragments are not treated as a successful transcript.");
                if (SourceStamp(job.SourcePath) != stamp)
                    throw new InvalidOperationException("Credits Text: the source video changed while OCR was reading it; try again.");
                var result = new CreditsOcrResult(transcript.Lines.Select(line => line with { Box = null }).ToArray(), images.Length, rejected,
                    Pages: CreditsOcrResult.ScanPages(samples.Select(sample => sample.Lines).ToArray(), options.SampleSeconds),
                    Samples: samples, Statistics: new(images.Length, reuse.Unique.Count, processes, retries, elapsed.ElapsedMilliseconds, model, cachedFrames, corrections));
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
