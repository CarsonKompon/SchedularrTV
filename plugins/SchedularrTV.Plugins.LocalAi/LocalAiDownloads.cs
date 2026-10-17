using System.Collections.Concurrent;
using System.IO.Compression;

namespace SchedularrTV.Writing.LocalAi;

/// <summary>
/// Downloads the native engine and the models in the background, so they carry on when the settings dialog is closed
/// (a model is a gigabyte or more). The dialog shows progress from here.
/// </summary>
public sealed class LocalAiDownloads(LocalAiPaths paths, ILogger<LocalAiDownloads> logger) : IDisposable
{
    public const string EngineKey = "engine";

    private static readonly HttpClient Http = CreateClient();
    private readonly ConcurrentDictionary<string, double> _progress = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stopping = new();

    private readonly ConcurrentDictionary<string, DateTime> _finished = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Raised from download threads as progress is made (false) and when a download starts or ends (true): listeners
    /// can skip some progress, but never a start or an end.
    /// </summary>
    public event Action<bool>? Changed;

    /// <summary>Downloaded completely in the last few minutes, so the settings can say so.</summary>
    public bool JustFinished(string key) => _finished.TryGetValue(key, out DateTime at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(5);

    /// <summary>The download is in, and it's being unpacked or moved into place.</summary>
    public bool IsFinishing(string key) => _progress.TryGetValue(key, out double done) && done >= 1;

    /// <summary>The NuGet package holding llama.cpp built for every kind of computer; only this computer's part is kept.</summary>
    public static string EngineUrl =>
        $"https://api.nuget.org/v3-flatcontainer/llamasharp.backend.cpu/{LlamaEngine.BackendVersion}/llamasharp.backend.cpu.{LlamaEngine.BackendVersion}.nupkg";

    public bool EngineInstalled => LlamaEngine.IsInstalled(paths.Engine);

    /// <summary>0 to 1 while downloading; null when not.</summary>
    public double? Progress(string key) => _progress.TryGetValue(key, out double done) ? done : null;

    /// <summary>Why the last download of this failed, until it's tried again.</summary>
    public string? Error(string key) => _errors.GetValueOrDefault(key);

    public bool IsDownloading(string key) => _running.ContainsKey(key);

    public void Dispose()
    {
        _stopping.Cancel();
        foreach (CancellationTokenSource running in _running.Values)
        {
            running.Cancel();
        }
    }

    public void StartEngine() => Start(EngineKey, InstallEngineAsync);

    public const string WhisperEngineKey = "whisper-engine";

    /// <summary>The NuGet package holding whisper.cpp built for every kind of computer (or without AVX, for older processors).</summary>
    public static string WhisperEngineUrl
    {
        get
        {
            string package = WhisperEngine.NeedsNoAvx ? "whisper.net.runtime.noavx" : "whisper.net.runtime";
            return $"https://api.nuget.org/v3-flatcontainer/{package}/{WhisperEngine.RuntimeVersion}/{package}.{WhisperEngine.RuntimeVersion}.nupkg";
        }
    }

    public bool WhisperEngineInstalled => WhisperEngine.IsInstalled(paths.WhisperEngine);

    public void StartWhisperEngine() => Start(WhisperEngineKey, InstallWhisperEngineAsync);

    /// <summary>
    /// Downloads Whisper.net's runtime package and unpacks this computer's libraries into the Whisper engine folder,
    /// laid out as Whisper.net looks for them (<c>runtimes/&lt;system&gt;/…</c>, or <c>runtimes/noavx/&lt;system&gt;/…</c>).
    /// </summary>
    private async Task InstallWhisperEngineAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        string runtime = WhisperEngine.RuntimeFolder()
            ?? throw new PlatformNotSupportedException("There's no built-in speech recognition for this kind of computer.");
        string staging = Path.Combine(Path.GetTempPath(), $"schedularrtv-whisper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            string package = Path.Combine(staging, "runtime.nupkg");
            await DownloadAsync(WhisperEngineUrl, package, progress, cancellationToken).ConfigureAwait(false);

            string unpacked = Path.Combine(staging, "engine");
            string folder = Path.Combine(unpacked, Path.GetRelativePath(paths.WhisperEngine, WhisperEngine.LibraryFolder(paths.WhisperEngine)));
            int files = UnpackWhisper(package, runtime, folder);
            if (files == 0 || !File.Exists(Path.Combine(folder, WhisperEngine.LibraryName)))
            {
                throw new InvalidDataException($"The speech recognition download had nothing for {runtime}.");
            }

            string target = paths.WhisperEngine;
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            CopyFolder(unpacked, target);
        }
        finally
        {
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Copies the libraries the package has for <paramref name="runtime"/> (<c>win-x64</c>, <c>macos-arm64</c>…) straight
    /// into <paramref name="target"/>, wherever in the package they're kept (<c>build/win-x64/</c>,
    /// <c>runtimes/win-x64/native/</c>…). Builds for graphics cards and other accelerators are left out.
    /// </summary>
    public static int UnpackWhisper(string package, string runtime, string target)
    {
        // NuGet names macOS "osx"; Whisper.net's own folders say "macos".
        string[] names = runtime.StartsWith("macos-", StringComparison.Ordinal) ? [runtime, "osx-" + runtime["macos-".Length..]] : [runtime];
        string[] skip = ["cuda", "cuda12", "vulkan", "coreml", "openvino"];
        int count = 0;
        using ZipArchive archive = ZipFile.OpenRead(package);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = Uri.UnescapeDataString(entry.FullName.Replace('\\', '/'));
            if (name.EndsWith('/') || name.Contains("..", StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = name.Split('/');
            int at = Array.FindIndex(parts, part => names.Contains(part, StringComparer.OrdinalIgnoreCase));
            if (at < 0 || at >= parts.Length - 1 || parts.Take(at).Any(part => skip.Contains(part, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            string file = parts[^1];
            if (!(file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
                  || file.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".metal", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Directory.CreateDirectory(target);
            entry.ExtractToFile(Path.Combine(target, file), overwrite: true);
            count++;
        }

        return count;
    }

    public void StartModel(LocalAiModel model, string folder) =>
        Start(model.Id, (progress, token) => DownloadAsync(LocalAiModels.Url(model), Path.Combine(folder, model.File), progress, token));

    public void Cancel(string key)
    {
        if (_running.TryGetValue(key, out CancellationTokenSource? running))
        {
            running.Cancel();
        }
    }

    private void Start(string key, Func<IProgress<double>, CancellationToken, Task> download)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        if (!_running.TryAdd(key, cancellation))
        {
            cancellation.Dispose();
            return;
        }

        _errors.TryRemove(key, out _);
        _finished.TryRemove(key, out _);
        _progress[key] = 0;
        Raise(true);
        var progress = new Reporter(value =>
        {
            // 1 is said once the whole file is in hand; it's only done when the download below returns.
            _progress[key] = Math.Min(value, 1);
            Raise(value >= 1);
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await download(progress, cancellation.Token).ConfigureAwait(false);
                _finished[key] = DateTime.UtcNow;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _errors[key] = ex.GetBaseException().Message;
                logger.LogWarning("Local AI download {Item} failed: {Message}", key, ex.GetBaseException().Message);
            }
            finally
            {
                _running.TryRemove(key, out _);
                _progress.TryRemove(key, out _);
                cancellation.Dispose();
                Raise(true);
            }
        });
    }

    /// <summary>
    /// Downloads LLamaSharp's CPU backend package and unpacks this computer's folder of it into the engine folder, laid
    /// out as LLamaSharp looks for it (<c>runtimes/&lt;system&gt;/native/…</c>).
    /// </summary>
    private async Task InstallEngineAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        string runtime = LocalAiPaths.RuntimeId()
            ?? throw new PlatformNotSupportedException("There's no built-in AI engine for this kind of computer. Use an Ollama or LM Studio server instead.");
        string staging = Path.Combine(Path.GetTempPath(), $"schedularrtv-local-ai-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            string package = Path.Combine(staging, "backend.nupkg");
            await DownloadAsync(EngineUrl, package, progress, cancellationToken).ConfigureAwait(false);

            string unpacked = Path.Combine(staging, "engine");
            int files = Unpack(package, runtime, unpacked);
            if (files == 0)
            {
                throw new InvalidDataException($"The engine download had nothing for {runtime}.");
            }

            string target = paths.Engine;
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            CopyFolder(unpacked, target);
        }
        finally
        {
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Copies the package's files for <paramref name="runtime"/> to <c>runtimes/&lt;runtime&gt;/</c> under <paramref name="target"/>.</summary>
    public static int Unpack(string package, string runtime, string target)
    {
        string prefix = $"LLamaSharpRuntimes/{runtime}/";
        int count = 0;
        using ZipArchive archive = ZipFile.OpenRead(package);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = Uri.UnescapeDataString(entry.FullName.Replace('\\', '/'));
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || name.EndsWith('/'))
            {
                continue;
            }

            string relative = name[prefix.Length..];
            if (relative.Contains("..", StringComparison.Ordinal))
            {
                continue;
            }

            string destination = Path.Combine(target, "runtimes", runtime, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
            count++;
        }

        return count;
    }

    /// <summary>Downloads to a temporary name beside the target and moves it into place when complete.</summary>
    private static async Task DownloadAsync(string url, string destination, IProgress<double> progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using HttpResponseMessage response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        string partial = destination + ".part";
        try
        {
            await using (Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (FileStream file = File.Create(partial))
            {
                byte[] buffer = new byte[1 << 17];
                long done = 0;
                long reported = 0;
                int read;
                while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    done += read;
                    if (total is > 0 && done - reported > total.Value / 200)
                    {
                        reported = done;
                        progress.Report((double)done / total.Value);
                    }
                }
            }

            File.Move(partial, destination, overwrite: true);
            progress.Report(1);
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }

    private static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private void Raise(bool milestone)
    {
        try
        {
            Changed?.Invoke(milestone);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "A download progress listener failed.");
        }
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SchedularrTV-LocalAi/1.0");
        return http;
    }

    /// <summary>Reports straight away on the download's thread, unlike System.Progress, which posts to a context.</summary>
    private sealed class Reporter(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
