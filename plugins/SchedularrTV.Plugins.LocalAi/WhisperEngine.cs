using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Microsoft.Extensions.Options;
using SchedularrTV.Plugins.Performance;
using SchedularrTV.Plugins.Speech;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace SchedularrTV.Writing.LocalAi;

/// <summary>
/// Writes down what's said in episodes with Whisper (whisper.cpp, through Whisper.net), run in this process. Like the
/// writer's engine, the native library and the models are downloaded from the plugin's settings. One recording is
/// transcribed at a time: it uses the whole processor.
/// </summary>
public sealed class WhisperEngine(ILogger<WhisperEngine> logger)
{
    /// <summary>The Whisper.net version the plugin is built against; the downloaded runtime must be the same one.</summary>
    public const string RuntimeVersion = "1.9.1";

    private static readonly Lock NativeGate = new();
    private static string? _nativeFolder;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// The folder name Whisper.net looks for this computer's library in (<c>runtimes/&lt;this&gt;/</c>), or null when
    /// there's no build for it.
    /// </summary>
    public static string? RuntimeFolder()
    {
        string? platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : null;
        string? architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.Arm when OperatingSystem.IsLinux() => "arm",
            _ => null
        };
        return platform is null || architecture is null ? null : $"{platform}-{architecture}";
    }

    /// <summary>
    /// Whether this processor needs the build without AVX: Whisper.net's usual one wants AVX, AVX2 and FMA on Intel and
    /// AMD processors.
    /// </summary>
    public static bool NeedsNoAvx =>
        RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.X86
        && !OperatingSystem.IsMacOS()
        && !(Avx.IsSupported && Avx2.IsSupported && Fma.IsSupported);

    /// <summary>Where in the engine folder this computer's library goes, as Whisper.net looks for it.</summary>
    public static string LibraryFolder(string engine) =>
        NeedsNoAvx ? Path.Combine(engine, "runtimes", "noavx", RuntimeFolder() ?? "unknown") : Path.Combine(engine, "runtimes", RuntimeFolder() ?? "unknown");

    /// <summary>Whether the library for this computer has been downloaded into <paramref name="engine"/>.</summary>
    public static bool IsInstalled(string engine) => File.Exists(Path.Combine(LibraryFolder(engine), LibraryName));

    /// <summary>The main library's file name on this kind of computer.</summary>
    public static string LibraryName => OperatingSystem.IsWindows() ? "whisper.dll" : OperatingSystem.IsMacOS() ? "libwhisper.dylib" : "libwhisper.so";

    /// <summary>
    /// Points Whisper.net at the downloaded library. It can only be done once, before it's first used, so a different
    /// folder later needs a restart.
    /// </summary>
    public static void UseEngine(string engine)
    {
        lock (NativeGate)
        {
            if (_nativeFolder is not null)
            {
                if (!string.Equals(_nativeFolder, engine, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The speech recognition engine moved since it was started; restart the scheduler to use it.");
                }

                return;
            }

            // Whisper.net looks for runtimes/… beside the folder of this path; only the processor builds are downloaded.
            RuntimeOptions.LibraryPath = Path.Combine(engine, "whisper");
            RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
            _nativeFolder = engine;
        }
    }

    /// <summary>What's said in <paramref name="wavePath"/> (16 kHz mono WAV), in time order.</summary>
    public async Task<Transcript> TranscribeAsync(string modelPath, int threads, string? language, string wavePath, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        // Named once it has the engine, so time spent waiting its turn isn't counted as working.
        using WorkScope work = Work.Begin("Local AI", "Whisper transcribing", Path.GetFileName(wavePath));
        try
        {
            int useThreads = threads > 0 ? threads : Math.Max(1, Environment.ProcessorCount / 2);
            logger.LogInformation("Transcribing {File} with Whisper model {Model}.", Path.GetFileName(wavePath), Path.GetFileName(modelPath));
            DateTime started = DateTime.UtcNow;

            // Loaded for each recording: transcripts are kept, so an episode is only ever done once, and a model held
            // all day for that would waste the memory.
            using WhisperFactory factory = WhisperFactory.FromPath(modelPath);
            string wanted = string.IsNullOrWhiteSpace(language) ? "auto" : language.Trim().ToLowerInvariant();
            using WhisperProcessor processor = factory.CreateBuilder()
                .WithLanguage(wanted)
                .WithThreads(useThreads)
                .Build();

            List<TranscriptSegment> segments = [];
            string? detected = null;
            await using FileStream wave = File.OpenRead(wavePath);
            await foreach (SegmentData segment in processor.ProcessAsync(wave, cancellationToken).ConfigureAwait(false))
            {
                string text = segment.Text?.Trim() ?? string.Empty;
                if (text.Length > 0)
                {
                    segments.Add(new TranscriptSegment(segment.Start, segment.End, text));
                }

                detected ??= string.IsNullOrWhiteSpace(segment.Language) ? null : segment.Language;
            }

            logger.LogInformation("Transcribed {File}: {Count} lines in {Seconds:0} seconds.", Path.GetFileName(wavePath), segments.Count,
                (DateTime.UtcNow - started).TotalSeconds);
            return new Transcript(segments, detected ?? (wanted == "auto" ? null : wanted));
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>The built-in speech recognition: Whisper, downloaded from the plugin's settings.</summary>
public sealed class WhisperTranscriber(IOptions<LocalAiOptions> options, LocalAiPaths paths, WhisperEngine engine) : ISpeechTranscriber
{
    public const string TranscriberId = "local-ai-whisper";

    private LocalAiOptions Options => options.Value;

    public string Id => TranscriberId;

    public string DisplayName => "Whisper (built in)";

    public string CacheIdentity => paths.WhisperModelPath(Options) is { } model && File.Exists(model)
        ? $"{Path.GetFileName(model)}|{new FileInfo(model).Length}|{Options.WhisperLanguage?.Trim().ToLowerInvariant()}"
        : string.Empty;

    public string? SetupProblem
    {
        get
        {
            if (WhisperEngine.RuntimeFolder() is null)
            {
                return "Speech recognition can't run on this kind of computer.";
            }

            if (!WhisperEngine.IsInstalled(paths.WhisperEngine))
            {
                return "The speech recognition engine isn't downloaded yet (Local AI writer's settings on the Plugins page).";
            }

            string? model = paths.WhisperModelPath(Options);
            return model is null ? "No Whisper model file is chosen (Local AI writer's settings on the Plugins page)."
                : !File.Exists(model) ? $"The Whisper model isn't downloaded yet: {Path.GetFileName(model)} (Local AI writer's settings on the Plugins page)."
                : null;
        }
    }

    public async Task<Transcript> TranscribeAsync(string wavePath, CancellationToken cancellationToken = default)
    {
        if (SetupProblem is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        WhisperEngine.UseEngine(paths.WhisperEngine);
        LocalAiOptions settings = Options;
        return await engine.TranscribeAsync(paths.WhisperModelPath(settings)!, settings.Threads, settings.WhisperLanguage, wavePath, cancellationToken)
            .ConfigureAwait(false);
    }
}
