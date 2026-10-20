using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Configuration;

namespace SchedularrTV.GeneratedPromos;

public sealed record CleanupMedia(int Width, int Height, double Duration, double Fps, string Codec = "");
public sealed record CleanupProgress(string State, double Fraction = 0, string? Error = null);

/// <summary>One bounded source-processing queue, independent of the promo render queue and Blazor circuits.</summary>
public sealed class PromoVideoCleanupService(IOptions<GeneratedPromoOptions> options, ILogger<PromoVideoCleanupService> logger,
    string? pluginDirectory = null) : IDisposable
{
    public const string EngineVersion = "text-removal-v2";
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CleanupProgress> _progress = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CleanupMedia> _media = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slot = new(1);
    private readonly SemaphoreSlim _install = new(1);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConditionalWeakTable<GeneratedPromoTemplate, Dictionary<string, GeneratedVideoCleanup>> _drafts = new();
    private readonly Lock _draftGate = new();
    public event Action? Changed;
    private string Root => Path.Combine(options.Value.SaveFolder ?? options.Value.OutputPath, "video-cleanup");
    public string CacheDirectory => Root;
    private string Engine => Path.Combine(Root, "engine");
    private string Python => Path.Combine(Engine, OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
    // The host loads plugins from streams, so Assembly.Location is empty there. Its
    // context supplies the actual installation directory; disk-loaded tools/tests
    // may still fall back to the assembly directory.
    private string AssetDirectory => Path.Combine(pluginDirectory
        ?? Path.GetDirectoryName(typeof(PromoVideoCleanupService).Assembly.Location)
        ?? AppContext.BaseDirectory, "wwwroot");
    private string Worker => Path.Combine(AssetDirectory, "text-removal.py");
    private string Requirements => Path.Combine(AssetDirectory, "text-removal-requirements.txt");
    public bool Installed => File.Exists(Python) && File.Exists(Path.Combine(Engine, "ready-v1"));
    public string? InstallError { get; private set; }
    public bool Installing { get; private set; }

    public static string Identity(GeneratedVideoCleanup recipe)
    {
        Validate(recipe);
        var file = new FileInfo(recipe.Source);
        string source = $"{Path.GetFullPath(recipe.Source)}|{(file.Exists ? file.Length : 0)}|{(file.Exists ? file.LastWriteTimeUtc.Ticks : 0)}";
        // Names are UI labels; source changes, correction geometry and algorithm changes invalidate the work.
        string spec = JsonSerializer.Serialize(recipe.Regions.Select(r => new
        {
            r.Start, r.End, r.Track, r.WholeRegion, r.StableSurface, r.Margin, r.Sensitivity, r.Ink,
            Keys = r.Keys.OrderBy(k => k.At)
        }));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{EngineVersion}|{source}|{spec}")))[..32];
    }

    public static void Validate(GeneratedVideoCleanup recipe)
    {
        recipe.Validate();
    }

    public string? Cached(GeneratedVideoCleanup recipe)
    {
        string path = Path.Combine(Root, Identity(recipe), "clean.mkv");
        if (!File.Exists(path) || !File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "mask.mkv"))) return null;
        try
        {
            string folder = Path.GetDirectoryName(path)!;
            if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) > TimeSpan.FromHours(12))
                Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow);
        }
        catch (IOException) { }
        return path;
    }

    public CleanupProgress Status(GeneratedVideoCleanup recipe)
    {
        string key = Identity(recipe);
        return Cached(recipe) is not null ? new("Ready", 1) : _progress.GetValueOrDefault(key) ?? new("Not processed");
    }

    public void KeepDraft(GeneratedPromoTemplate template, GeneratedVideoCleanup recipe)
    {
        lock (_draftGate) _drafts.GetOrCreateValue(template)[recipe.Source] = GeneratedPromoEditing.Copy(recipe);
    }

    public GeneratedVideoCleanup? Draft(GeneratedPromoTemplate template, string source)
    {
        lock (_draftGate) return _drafts.TryGetValue(template, out var drafts) && drafts.TryGetValue(source, out var draft)
            ? GeneratedPromoEditing.Copy(draft) : null;
    }

    public void ForgetDraft(GeneratedPromoTemplate template, string source)
    {
        lock (_draftGate) if (_drafts.TryGetValue(template, out var drafts)) drafts.Remove(source);
    }

    public void Cancel(GeneratedVideoCleanup recipe)
    {
        if (_cancellations.TryGetValue(Identity(recipe), out var cancellation)) cancellation.Cancel();
    }

    public async Task InstallAsync()
    {
        await _install.WaitAsync(_stop.Token);
        try
        {
            EnsureAssets();
            if (Installed) return;
            Installing = true; InstallError = null; Notify();
            Directory.CreateDirectory(Root);
            await RunAsync(options.Value.CleanupPython, ["-c", "import sys; assert (3,10) <= sys.version_info[:2] < (3,14), 'Use Python 3.10 through 3.13 for the text-removal engine'"], _stop.Token);
            await RunAsync(options.Value.CleanupPython, ["-m", "venv", Engine], _stop.Token);
            await RunAsync(Python, ["-m", "pip", "install", "--disable-pip-version-check", "--only-binary=:all:", "-r", Requirements], _stop.Token);
            await RunAsync(Python, ["-c", "import cv2, numpy; print(cv2.__version__)"], _stop.Token);
            await File.WriteAllTextAsync(Path.Combine(Engine, "ready-v1"), EngineVersion, _stop.Token);
        }
        catch (Exception ex) when (!_stop.IsCancellationRequested)
        {
            InstallError = ex.GetBaseException().Message;
            logger.LogWarning(ex, "Could not install the text-removal engine");
        }
        finally { Installing = false; _install.Release(); Notify(); }
    }

    public Task<string> StartAsync(GeneratedVideoCleanup recipe, CancellationToken wait = default)
    {
        // Never let later UI edits mutate an in-flight request.
        var snapshot = JsonSerializer.Deserialize<GeneratedVideoCleanup>(JsonSerializer.Serialize(recipe))!;
        string key = Identity(snapshot);
        if (Cached(snapshot) is { } cached) return Task.FromResult(cached);
        EnsureAssets();
        if (!Installed) throw new InvalidOperationException("Install the text-removal engine in Remove text first (Python 3.10+ is required).");
        return _jobs.GetOrAdd(key, _ => new Lazy<Task<string>>(() => ProcessAsync(key, snapshot))).Value.WaitAsync(wait);
    }

    private async Task<string> ProcessAsync(string key, GeneratedVideoCleanup recipe)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _cancellations[key] = cancel;
        string folder = Path.Combine(Root, key);
        bool entered = false;
        try
        {
            Set(key, new("Queued"));
            await _slot.WaitAsync(cancel.Token); entered = true;
            TrimUnused();
            CleanupMedia media = await ProbeAsync(recipe.Source, cancel.Token);
            if (media.Duration > 120 || media.Width > 3840 || media.Height > 2160 || media.Fps > 120)
                throw new InvalidOperationException("Text removal currently supports clips up to 2 minutes, 3840×2160 and 120 fps. Trim longer footage first.");
            if (recipe.Regions.SelectMany(r => r.Keys).Any(k => k.At >= media.Duration))
                throw new InvalidOperationException("A reference frame is past the end of this video.");
            Directory.CreateDirectory(folder);
            string request = Path.Combine(folder, "request.json");
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new
            {
                recipe.Source, recipe.Regions, Ffmpeg = options.Value.FfmpegPath, Ffprobe = options.Value.FfprobePath,
                Output = Path.Combine(folder, "clean.partial.mkv"), Tracking = Path.Combine(folder, "tracking.json")
            }), cancel.Token);
            using WorkScope work = Work.Begin("Generated Promos", "Removing video text", Path.GetFileName(recipe.Source));
            await RunAsync(Python, [Worker, request], cancel.Token, line =>
            {
                if (line.StartsWith("PROGRESS ", StringComparison.Ordinal))
                {
                    string[] bits = line.Split(' ', 3);
                    if (bits.Length == 3 && double.TryParse(bits[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                        Set(key, new(bits[2], Math.Clamp(value, 0, 1)));
                }
            });
            string result = Path.Combine(folder, "clean.mkv");
            if (Identity(recipe) != key) throw new IOException("The source video changed during processing. Analyze it again.");
            if (!File.Exists(Path.Combine(folder, "clean.partial.mkv"))) throw new IOException("The cleanup engine produced no video.");
            if (!File.Exists(Path.Combine(folder, "mask.partial.mkv"))) throw new IOException("The cleanup engine produced no removal-mask preview.");
            File.Move(Path.Combine(folder, "mask.partial.mkv"), Path.Combine(folder, "mask.mkv"), overwrite: true);
            File.Move(Path.Combine(folder, "clean.partial.mkv"), result, overwrite: true);
            Set(key, new("Ready", 1));
            return result;
        }
        catch (OperationCanceledException) { Set(key, new("Cancelled")); throw; }
        catch (Exception ex)
        {
            Set(key, new("Failed", Error: ex.GetBaseException().Message));
            logger.LogWarning(ex, "Text removal failed for {Source}", recipe.Source);
            throw;
        }
        finally
        {
            if (entered) _slot.Release();
            _cancellations.TryRemove(key, out _);
            _jobs.TryRemove(key, out _);
            foreach (string file in new[] { "clean.partial.mkv", "mask.partial.mkv", "frames.gray" })
                try { File.Delete(Path.Combine(folder, file)); } catch (IOException) { }
        }
    }

    /// <summary>Prepare visual sources only. Audio references retain their original file and timestamps.</summary>
    public async Task<PromoComposition> PrepareAsync(PromoComposition composition, GeneratedPromoTemplate template,
        bool wait, CancellationToken cancellationToken = default)
    {
        if (template.VideoCleanup is not { Count: > 0 }) return composition;
        async Task<string> Resolve(string source)
        {
            var recipe = template.VideoCleanup.FirstOrDefault(r => r.Enabled && r.Regions.Count > 0 && SameSource(r.Source, source));
            if (recipe is null) return source;
            if (Cached(recipe) is { } cached) return cached;
            // Still previews never launch expensive jobs just because the playhead moved.
            return wait ? await StartAsync(recipe, cancellationToken) : source;
        }
        var layers = new List<PromoLayer>();
        foreach (PromoLayer layer in composition.Layers)
            layers.Add(layer is PromoVideoLayer video ? video with { Path = await Resolve(video.Path) } : layer);
        return composition with
        {
            BackgroundPath = composition.BackgroundPath is { } bg ? await Resolve(bg) : null,
            Layers = layers
        };
    }

    public static bool SameSource(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public async Task<CleanupMedia> ProbeAsync(string source, CancellationToken cancellationToken = default)
    {
        string key = $"{Path.GetFullPath(source)}|{File.GetLastWriteTimeUtc(source).Ticks}";
        if (_media.TryGetValue(key, out var cached)) return cached;
        string json = await RunAsync(options.Value.FfprobePath,
            ["-v", "error", "-select_streams", "v:0", "-show_streams", "-show_format", "-of", "json", source], cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var s = doc.RootElement.GetProperty("streams")[0];
        int width = s.GetProperty("width").GetInt32(), height = s.GetProperty("height").GetInt32();
        if (s.TryGetProperty("side_data_list", out var side) && side.EnumerateArray().Any(d => d.TryGetProperty("rotation", out var rotation) && Math.Abs(rotation.GetDouble()) % 180 > 1))
            (width, height) = (height, width);
        string rate = s.GetProperty("avg_frame_rate").GetString() ?? "30/1";
        string[] parts = rate.Split('/');
        double fps = double.Parse(parts[0], CultureInfo.InvariantCulture) / (parts.Length > 1 ? double.Parse(parts[1], CultureInfo.InvariantCulture) : 1);
        double duration = double.Parse(s.TryGetProperty("duration", out var own) ? own.GetString()! : doc.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        if (!double.IsFinite(fps) || fps <= 0 || !double.IsFinite(duration) || duration <= 0) throw new InvalidDataException("The source has no usable video timing.");
        var media = new CleanupMedia(width, height, duration, fps, s.GetProperty("codec_name").GetString() ?? "");
        _media[key] = media;
        return media;
    }

    public async Task<byte[]> FrameAsync(string source, double at, CancellationToken cancellationToken = default, bool mask = false)
    {
        var media = await ProbeAsync(source, cancellationToken);
        int index = Math.Clamp((int)Math.Round(at * media.Fps), 0, Math.Max(0, (int)Math.Ceiling(media.Duration * media.Fps - 1e-6) - 1));
        // Use the worker's normalized frame numbering, not input seeking: MP4 video may
        // begin later than its container/audio, which otherwise shifts the selection.
        List<string> arguments = ["-v", "error"];
        if (media.Codec is "vp8" or "vp9") arguments.AddRange(["-c:v", media.Codec == "vp8" ? "libvpx" : "libvpx-vp9"]);
        arguments.AddRange(["-i", source, "-frames:v", "1", "-vf",
            $"setpts=PTS-STARTPTS,fps={media.Fps.ToString("0.#########", CultureInfo.InvariantCulture)},select=eq(n\\,{index}),scale=1280:1280:force_original_aspect_ratio=decrease"
                + (mask ? ",format=rgba,colorchannelmixer=rr=0:ra=1:gg=0:ga=0.15:bb=0:ba=0.55:ar=0.6:aa=0" : ""),
            "-f", "image2pipe", "-c:v", "png", "-"]);
        var info = Info(options.Value.FfmpegPath, arguments);
        using var process = Process.Start(info) ?? throw new IOException("Could not start FFmpeg.");
        using var registration = cancellationToken.Register(() => Kill(process));
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var buffer = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(buffer, cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0 || buffer.Length == 0) throw new IOException("Could not read that video frame. " + await error);
        return buffer.ToArray();
    }

    public string? TrackingReport(GeneratedVideoCleanup recipe)
    {
        string file = Path.Combine(Root, Identity(recipe), "tracking.json");
        return File.Exists(file) ? File.ReadAllText(file) : null;
    }

    public Task<byte[]> MaskFrameAsync(GeneratedVideoCleanup recipe, double at, CancellationToken cancellationToken = default)
    {
        string clean = Cached(recipe) ?? throw new InvalidOperationException("Analyze this recipe before previewing its removal mask.");
        return FrameAsync(Path.Combine(Path.GetDirectoryName(clean)!, "mask.mkv"), at, cancellationToken, mask: true);
    }

    private static ProcessStartInfo Info(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    private static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token, Action<string>? output = null)
    {
        using var process = Process.Start(Info(executable, arguments)) ?? throw new IOException($"Could not start {executable}.");
        Work.Track(process, "Generated Promos", "Video cleanup engine");
        using var registration = token.Register(() => Kill(process));
        Task<string> errors = process.StandardError.ReadToEndAsync(token);
        var text = new StringBuilder();
        while (await process.StandardOutput.ReadLineAsync(token) is { } line)
        {
            text.AppendLine(line); output?.Invoke(line);
        }
        await process.WaitForExitAsync(token);
        token.ThrowIfCancellationRequested();
        string error = await errors;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(executable)} failed: {error}");
        return text.ToString();
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }

    private void EnsureAssets()
    {
        foreach (string file in new[] { Worker, Requirements })
            if (!File.Exists(file))
                throw new FileNotFoundException($"The Generated promos plugin is missing '{file}'. Rebuild or reinstall the plugin including its wwwroot files.", file);
    }

    private void TrimUnused()
    {
        if (!Directory.Exists(Root)) return;
        foreach (string directory in Directory.EnumerateDirectories(Root))
        {
            string key = Path.GetFileName(directory);
            if (key.Length != 32 || !key.All(char.IsAsciiHexDigit) || _jobs.ContainsKey(key)) continue;
            try
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) > TimeSpan.FromDays(90))
                    Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogDebug(ex, "Cleanup cache entry is still in use"); }
        }
    }
    private void Set(string key, CleanupProgress value) { _progress[key] = value; Notify(); }
    private void Notify()
    {
        try { Changed?.Invoke(); } catch (Exception ex) { logger.LogDebug(ex, "Cleanup listener disconnected"); }
    }
    public void Dispose() { _stop.Cancel(); _stop.Dispose(); }
}
