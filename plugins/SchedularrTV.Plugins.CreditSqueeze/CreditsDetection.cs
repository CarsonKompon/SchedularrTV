using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using SchedularrTV.GeneratedPromos;

namespace SchedularrTV.CreditSqueeze;

public sealed record CreditsInterval(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Length => End - Start;
}
public sealed record CreditsDetectionResult(CreditsInterval? Interval, string Message, DateTimeOffset CheckedAt);

/// <summary>Conservative temporal evidence, not just "OCR found a word".</summary>
public static class CreditsBoundaryAnalysis
{
    private static readonly Regex Role = new(@"^(executive producers?|co[- ]?producers?|producers?|directors?( of photography)?|writers?|cast|starring|voices?|editors?|animation|animators?|storyboards?|music|sound|special thanks|post[- ]production( \w+){0,3}|production( assistants?| supervisors?| coordinators?| managers?)?)\s*:?(\s*)$|^(produced|directed|written|edited|created|distributed)\s+by\b|^based on\b|^copyright\b|©",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static CreditsInterval? Find(IReadOnlyList<CreditsOcrSample> samples, TimeSpan length, double step)
    {
        if (samples.Count == 0 || step <= 0) return null;
        bool Text(CreditsOcrSample sample) => sample.Lines.Count >= 2 && sample.Lines.Sum(line => line.Text.Count(char.IsLetter)) >= 10
            && sample.Lines.Count(line => line.Confidence >= 80) >= 1;
        bool Scroll(int index)
        {
            if (index > 0 && samples[index].Layout == CreditsLayoutKind.Card && samples[index - 1].Layout == CreditsLayoutKind.Card) return false;
            return samples[index].Layout == CreditsLayoutKind.Scroll || samples[index].VisualGroup is null && index > 0
                && samples[index - 1].VisualGroup is null && CreditsLayouts.Moving(samples[index - 1].Lines, samples[index].Lines);
        }
        var evidence = samples.Select((sample, index) => new { Index = index, Strong = Text(sample) || sample.Lines.Any(line => line.Confidence >= 80 && Role.IsMatch(line.Text)),
            Role = sample.Lines.Any(line => line.Confidence >= 75 && Role.IsMatch(line.Text)),
            Dense = sample.Lines.Count >= 5 && sample.Lines.Sum(line => line.Text.Count(char.IsLetter)) >= 35
                && sample.Lines.Count(line => line.Confidence >= 85) >= 4,
            Scroll = Scroll(index), Card = CreditsLayouts.NameCard(sample) }).ToArray();
        CreditsInterval? best = null;
        double bestScore = 0;
        List<int> run = [];
        void Finish()
        {
            if (run.Count < 3) { run.Clear(); return; }
            var start = samples[run[0]].At;
            // The first measurable scroll movement corroborates the preceding window too.
            if (evidence[run[0]].Scroll && run[0] > 0 && Text(samples[run[0] - 1])
                && (start - samples[run[0] - 1].At).TotalSeconds <= step * 1.1) start = samples[run[0] - 1].At;
            var end = samples[run[^1]].At + TimeSpan.FromSeconds(step);
            if (end > length) end = length;
            bool credible = run.Any(i => evidence[i].Role || evidence[i].Scroll) || run.Count(i => evidence[i].Dense || evidence[i].Card) >= 3;
            double density = run.Count * step / Math.Max(step, (end - start).TotalSeconds);
            if (credible && end - start >= TimeSpan.FromSeconds(8) && density >= .55
                && CreditsOcrFiltering.Usable(run.SelectMany(i => samples[i].Lines).ToArray()))
            {
                double score = run.Count + run.Count(i => evidence[i].Role) * 2 + run.Count(i => evidence[i].Scroll) * 2;
                if (score > bestScore) { bestScore = score; best = new(start, end); }
            }
            run.Clear();
        }
        foreach (var item in evidence)
        {
            // Ordinary dialogue subtitles may continue after the roll. They must not extend
            // an interval merely because a role heading appeared much earlier in the run.
            if (!item.Strong || !(item.Role || item.Dense || item.Scroll || item.Card)) continue;
            if (run.Count > 0)
            {
                int previous = run[^1];
                bool blankTransition = samples.Skip(previous + 1).Take(item.Index - previous - 1).All(sample => sample.BlankFrame);
                double gap = (samples[item.Index].At - samples[previous].At).TotalSeconds;
                if (gap > (blankTransition ? Math.Max(6, step * 2.1) : Math.Max(3, step * 2.1))) Finish();
            }
            run.Add(item.Index);
        }
        Finish();
        return best;
    }
}

/// <summary>Background, cached source-boundary detection. Publishing never waits for OCR.</summary>
public sealed class CreditsDetection(CreditSqueezeSettings settings, CreditsOcr ocr, TimeProvider clock, ILogger<CreditsDetection> logger,
    TesseractSetup? setup = null) : BackgroundService
{
    private sealed record Request(string Key, string Path, TimeSpan Duration, CreditsRecognition Recognition);
    private readonly ConcurrentDictionary<string, CreditsDetectionResult> _known = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failed = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Request> _requests = new();
    private readonly SemaphoreSlim _wake = new(0), _gate = new(1, 1);
    private string Folder => Path.Combine(settings.DataDirectory, "detected-credits");
    public event Action? Found;
    public int Pending => _queued.Count;
    public string? LastProblem { get; private set; }
    private string Key(string path, TimeSpan duration, CreditsRecognition recognition) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { Version = 2, Path = Path.GetFullPath(path), Stamp = CreditsOcr.SourceStamp(path), duration,
            Options = settings.Ocr with { Recognition = recognition.Normalized(), SampleSeconds = 1 }, settings.DetectionWindow }))));

    public CreditsDetectionResult? Known(string path, TimeSpan duration, CreditsRecognition recognition)
    {
        if (!File.Exists(path)) return null;
        string key = Key(path, duration, recognition);
        if (_known.TryGetValue(key, out var result) && clock.GetUtcNow() - result.CheckedAt < TimeSpan.FromDays(3)) return result;
        try
        {
            string file = Path.Combine(Folder, key + ".json");
            if (File.Exists(file) && JsonSerializer.Deserialize<CreditsDetectionResult>(File.ReadAllText(file)) is { } stored
                && clock.GetUtcNow() - stored.CheckedAt < TimeSpan.FromDays(3)) return _known[key] = stored;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return null;
    }

    public void Want(string path, TimeSpan duration, CreditsRecognition recognition)
    {
        if (!File.Exists(path)) return;
        if (Known(path, duration, recognition) is not null) return;
        string key = Key(path, duration, recognition);
        if (_failed.TryGetValue(key, out var failed) && clock.GetUtcNow() - failed < TimeSpan.FromMinutes(30)) return;
        if (_queued.TryAdd(key, 0)) { _requests.Enqueue(new(key, path, duration, recognition)); _wake.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await _wake.WaitAsync(stoppingToken);
            if (!_requests.TryDequeue(out var request)) continue;
            string key = request.Key;
            try { await DetectAsync(request.Path, request.Duration, request.Recognition, stoppingToken); LastProblem = null; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _failed[key] = clock.GetUtcNow(); LastProblem = ex.Message; logger.LogWarning("Credit detection failed for {File}: {Problem}", Path.GetFileName(request.Path), ex.Message); }
            finally { _queued.TryRemove(key, out _); }
        }
    }

    public async Task<CreditsDetectionResult> DetectAsync(string path, TimeSpan duration, CreditsRecognition recognition, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Known(path, duration, recognition) is { } existing) return existing;
            if (!File.Exists(path)) throw new IOException($"Credits detection cannot read {Path.GetFileName(path)}.");
            if (setup is not null && await setup.CheckAsync(cancellationToken) is { Ready: false } unavailable)
                throw new InvalidOperationException($"Credits detection needs Tesseract. Open Render preview to set it up. {unavailable.Problem}");
            string key = Key(path, duration, recognition);
            string stamp = CreditsOcr.SourceStamp(path);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            int processes = 0, reused = 0;
            void Count(CreditsOcrResult result)
            {
                if (result.Statistics is not { } stats) return;
                processes += stats.RecognizerProcesses;
                reused += stats.SampledFrames - stats.UniqueFrames + stats.CachedFrames;
            }
            if (duration <= TimeSpan.Zero)
            {
                string probe = Path.IsPathRooted(settings.Ffmpeg) ? Path.Combine(Path.GetDirectoryName(settings.Ffmpeg)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe") : "ffprobe";
                try { duration = TimeSpan.FromSeconds(await PromoVideoDecoder.DurationAsync(probe, path, cancellationToken)); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { throw new InvalidOperationException("Credits detection timed out while reading the source duration."); }
            }
            if (duration <= TimeSpan.Zero) throw new InvalidOperationException("Couldn't determine the source duration for credits detection.");
            using var work = SchedularrTV.Plugins.Performance.Work.Begin("Credit Squeeze", "Detecting credits", Path.GetFileName(path));
            var start = duration > settings.DetectionWindow ? duration - settings.DetectionWindow : TimeSpan.Zero;
            var options = settings.Ocr with { Recognition = recognition.Normalized(), SampleSeconds = 2, MaxWidth = 1280, DetectionPass = true };
            SqueezeJob ScanJob(TimeSpan from, TimeSpan length, CreditsOcrOptions config) => new(path, from, length, new("", null, "", null), [], "", "", "#ffffff", "#000000", 1, false)
                { CreditsOcr = config, CreditsOcrSourceStamp = stamp };
            var coarse = await ocr.ScanAsync(ScanJob(start, duration - start, options), cancellationToken);
            Count(coarse);
            var interval = CreditsBoundaryAnalysis.Find(coarse.Samples ?? [], duration - start, 2);
            // Difficult fonts may be missed by the cheap locator pass. An installed accuracy
            // model gets a second chance over the same window, not merely the last 60 seconds.
            if (interval is null && options.QualityDataDirectory is not null && options.Profile != "fast")
            {
                options = options with { DetectionPass = false, MaxWidth = 1920 };
                coarse = await ocr.ScanAsync(ScanJob(start, duration - start, options), cancellationToken);
                Count(coarse);
                interval = CreditsBoundaryAnalysis.Find(coarse.Samples ?? [], duration - start, 2);
            }
            CreditsInterval? found = null;
            string message = CreditsOcrFiltering.Usable(coarse.Lines)
                ? "Text was found, but no reliable credits interval could be established. The episode will play unchanged."
                : $"No reliable credits interval was found ({coarse.Lines.Count} retained text line(s)). The episode will play unchanged.";
            if (interval is not null && (interval.Start > TimeSpan.Zero || start == TimeSpan.Zero))
            {
                var fineStart = start + interval.Start - TimeSpan.FromSeconds(2);
                if (fineStart < TimeSpan.Zero) fineStart = TimeSpan.Zero;
                var fineEnd = start + interval.End + TimeSpan.FromSeconds(2);
                if (fineEnd > duration) fineEnd = duration;
                options = options with { SampleSeconds = 1, MaxWidth = 1920, DetectionPass = false };
                var fine = await ocr.ScanAsync(ScanJob(fineStart, fineEnd - fineStart, options), cancellationToken);
                Count(fine);
                var refined = CreditsBoundaryAnalysis.Find(fine.Samples ?? [], fineEnd - fineStart, 1);
                if (refined is not null)
                {
                    found = new(fineStart + refined.Start, fineStart + refined.End);
                    message = $"Detected credits: {found.Start:g}–{found.End:g} ({found.Length.TotalSeconds:0.#}s; 1-second sampling).";
                    var samples = (fine.Samples ?? []).Where(sample => sample.At >= refined.Start && sample.At < refined.End)
                        .Select(sample => sample with { At = sample.At - refined.Start }).ToArray();
                    var frames = samples.Select(sample => sample.Lines).ToArray();
                    var transcript = new CreditsTranscript(); foreach (var sample in samples) transcript.Add(sample);
                    var result = new CreditsOcrResult(transcript.Lines.Select(line => line with { Box = null }).ToArray(), frames.Length,
                        Pages: CreditsOcrResult.ScanPages(frames, 1), Samples: samples, Statistics: fine.Statistics);
                    if (CreditsOcrFiltering.Usable(result.Lines)) await ocr.StoreAsync(ScanJob(found.Start, found.Length, options), result, cancellationToken);
                }
            }
            else if (interval is not null) message = "Credits reach the beginning of the scan window. Increase the detection window to locate their start; no automatic squeeze was selected.";
            message += $" Recorded scan work: {processes} OCR calls, {reused} reused frame(s), {elapsed.Elapsed.TotalSeconds:0.#}s.";
            var answer = new CreditsDetectionResult(found, message, clock.GetUtcNow());
            Directory.CreateDirectory(Folder);
            string target = Path.Combine(Folder, key + ".json"), temporary = target + ".tmp";
            try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(answer), cancellationToken); File.Move(temporary, target, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            _known[key] = answer;
            _failed.TryRemove(key, out _);
            Found?.Invoke();
            return answer;
        }
        finally { _gate.Release(); }
    }

    public override void Dispose() { _wake.Dispose(); _gate.Dispose(); base.Dispose(); }
}
