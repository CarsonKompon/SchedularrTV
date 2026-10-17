using System.Runtime.InteropServices;
using System.Text;
using LLama;
using LLama.Abstractions;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using LLama.Transformers;
using SchedularrTV.Plugins.Performance;

namespace SchedularrTV.Writing.LocalAi;

/// <summary>
/// Runs GGUF models in this process with LLamaSharp (llama.cpp). The model is loaded on first use and let go of after
/// a while without use, so its memory isn't held all day for a few promos an hour. One piece is written at a time.
/// </summary>
public sealed class LlamaEngine : IDisposable
{
    /// <summary>The LLamaSharp version the plugin is built against; the downloaded engine must be the same one.</summary>
    public const string BackendVersion = "0.27.0";

    private const uint ContextSize = 4096;

    private static readonly Lock NativeGate = new();
    private static string? _nativeFolder;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<LlamaEngine> _logger;
    private readonly Timer _idle;
    private LLamaWeights? _weights;
    private ModelParams? _parameters;
    private string? _loadedKey;
    private bool _skipThinking;
    private DateTime _lastUsed = DateTime.UtcNow;
    private TimeSpan _unloadAfter = TimeSpan.FromMinutes(10);

    public LlamaEngine(ILogger<LlamaEngine> logger)
    {
        _logger = logger;
        _idle = new Timer(_ => UnloadIfIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>The model file loaded right now, if any.</summary>
    public string? Loaded => _weights is null ? null : _parameters?.ModelPath;

    /// <summary>Whether the engine for this computer has been downloaded into <paramref name="folder"/>.</summary>
    public static bool IsInstalled(string folder)
    {
        string runtimes = Path.Combine(folder, "runtimes");
        return Directory.Exists(runtimes)
            && Directory.EnumerateFiles(runtimes, "*llama.*", SearchOption.AllDirectories).Any(file => !file.EndsWith(".part", StringComparison.Ordinal));
    }

    /// <summary>Whether the graphics card (Vulkan) add-on has been downloaded into <paramref name="folder"/>.</summary>
    public static bool IsGpuInstalled(string folder) =>
        LocalAiPaths.RuntimeId() is { } runtime
        && File.Exists(Path.Combine(folder, "runtimes", runtime, "native", "vulkan", LocalAiGpu.LibraryFile("llama")));

    /// <summary>
    /// Whether the engine was set up with the graphics card add-on. It's chosen once, when the engine is first used, so
    /// an add-on downloaded afterwards is only used after a restart.
    /// </summary>
    public static bool GpuOffered { get; private set; }

    /// <summary>Whether <see cref="UseEngine"/> has run (the engine's builds are chosen for this run of the app).</summary>
    public static bool EngineChosen => _nativeFolder is not null;

    /// <summary>
    /// Points LLamaSharp at the downloaded engine, and at the graphics card add-on when <paramref name="gpuFolder"/> is
    /// given (tried first, with the processor build as the fallback). It can only be done once, before the engine is
    /// first used, so a different folder later needs a restart.
    /// </summary>
    public static void UseEngine(string folder, string? gpuFolder = null)
    {
        lock (NativeGate)
        {
            if (_nativeFolder is not null)
            {
                if (!string.Equals(_nativeFolder, folder, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The AI engine moved since it was started; restart the scheduler to use it.");
                }

                return;
            }

            // CUDA builds aren't downloaded, and LLamaSharp's own Vulkan check needs the vulkaninfo tool, which most
            // computers don't have; the Vulkan build is offered by VulkanFirst instead, only when a driver is found.
            NativeLibraryConfigContainer config = NativeLibraryConfig.All
                .WithSearchDirectory(folder)
                .WithCuda(false)
                .WithVulkan(false)
                .WithLogCallback(NativeLog);

            bool gpu = gpuFolder is not null && LocalAiGpu.Supported && IsGpuInstalled(gpuFolder) && LocalAiGpu.DriverFound;
            if (gpu)
            {
                // Searched after the processor engine: the Vulkan build's processor part (ggml-cpu) comes from there.
                config.WithSearchDirectory(gpuFolder!).WithSelectingPolicy(new VulkanFirst());
            }

            GpuOffered = gpu;
            _nativeFolder = folder;
        }
    }

    /// <summary>The graphics card the model runs on, as llama.cpp names it; null while it runs on the processor.</summary>
    public string? Device { get; private set; }

    /// <summary>Why the graphics card wasn't used for the last load although it was wanted; null when it was, or wasn't wanted.</summary>
    public string? GpuProblem { get; private set; }

    /// <summary>The name llama.cpp gives the first Vulkan device, from its log as the engine starts.</summary>
    private static string? _vulkanDevice;

    // llama.cpp hands its log over in pieces; each line is put together before it's looked at.
    [ThreadStatic] private static StringBuilder? _logLine;

    private static void NativeLog(LLamaLogLevel level, string message)
    {
        StringBuilder line = _logLine ??= new StringBuilder();
        line.Append(message);
        if (!message.EndsWith('\n'))
        {
            return;
        }

        string text = line.ToString().Trim();
        line.Clear();

        // "ggml_vulkan: 0 = NVIDIA GeForce RTX 3070 (NVIDIA) | uma: 0 | fp16: 1 | …"
        int at = text.IndexOf("ggml_vulkan:", StringComparison.Ordinal);
        if (at >= 0 && _vulkanDevice is null)
        {
            string rest = text[(at + "ggml_vulkan:".Length)..].Trim();
            int equals = rest.IndexOf('=');
            if (equals > 0 && int.TryParse(rest[..equals].Trim(), out _))
            {
                string name = rest[(equals + 1)..];
                int end = name.IndexOfAny(['(', '|']);
                name = (end > 0 ? name[..end] : name).Trim();
                if (name.Length > 0)
                {
                    _vulkanDevice = name;
                }
            }
        }
    }

    /// <summary>Tries llama.cpp's Vulkan build first, then LLamaSharp's usual choice of processor builds.</summary>
    private sealed class VulkanFirst : INativeLibrarySelectingPolicy
    {
        private readonly DefaultNativeLibrarySelectingPolicy _usual = new();

        public IEnumerable<INativeLibrary> Apply(NativeLibraryConfig.Description description, SystemInfo systemInfo,
            NativeLogConfig.LLamaLogCallback? logCallback = null)
        {
            yield return new VulkanBuild(description.Library, description.AvxLevel);
            foreach (INativeLibrary library in _usual.Apply(description, systemInfo, logCallback))
            {
                yield return library;
            }
        }
    }

    /// <summary>llama.cpp's Vulkan build, where its add-on package puts it (<c>runtimes/&lt;system&gt;/native/vulkan/</c>).</summary>
    private sealed class VulkanBuild(NativeLibraryName name, AvxLevel avxLevel) : INativeLibrary
    {
        // Marked as Vulkan, so LLamaSharp loads ggml-vulkan with it, and the processor part for this processor.
        public NativeLibraryMetadata? Metadata => new(name, false, true, avxLevel);

        public IEnumerable<string> Prepare(SystemInfo systemInfo, NativeLogConfig.LLamaLogCallback? logCallback = null)
        {
            if (LocalAiPaths.RuntimeId() is not { } runtime)
            {
                yield break;
            }

            string file = LocalAiGpu.LibraryFile(name == NativeLibraryName.Mtmd ? "mtmd" : "llama");
            yield return $"runtimes/{runtime}/native/vulkan/{file}";
        }
    }

    public async Task<string> GenerateAsync(string modelPath, int threads, TimeSpan unloadAfter, TextGenerationRequest request,
        CancellationToken cancellationToken, bool useGpu = false)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _unloadAfter = unloadAfter <= TimeSpan.Zero ? TimeSpan.FromMinutes(10) : unloadAfter;
            LLamaWeights weights = await LoadAsync(modelPath, threads, useGpu, cancellationToken).ConfigureAwait(false);

            // Named once it has the engine (and knows where it runs), so time spent waiting its turn isn't counted as working.
            using WorkScope work = Work.Begin("Local AI", Device is null ? "Generating text" : "Generating text (graphics card)",
                Path.GetFileName(modelPath));
            var executor = new StatelessExecutor(weights, _parameters!);
            using var sampling = new DefaultSamplingPipeline
            {
                Temperature = (float)Math.Clamp(request.Temperature, 0, 2),
                TopP = 0.9f,
                RepeatPenalty = 1.1f,
                Seed = request.Seed is { } seed ? (uint)seed : (uint)Random.Shared.Next()
            };
            var inference = new InferenceParams
            {
                MaxTokens = Math.Clamp(request.MaxTokens, 8, 1024),
                AntiPrompts = ["<|im_end|>", "<|im_start|>", "<|endoftext|>", "<end_of_turn>", "<|eot_id|>", "</s>"],
                SamplingPipeline = sampling
            };

            var reply = new StringBuilder();
            await foreach (string piece in executor.InferAsync(Prompt(weights, request), inference, cancellationToken).ConfigureAwait(false))
            {
                reply.Append(piece);
            }

            return WithoutStops(reply.ToString());
        }
        finally
        {
            _lastUsed = DateTime.UtcNow;
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _idle.Dispose();
        _weights?.Dispose();
        _weights = null;
    }

    private async Task<LLamaWeights> LoadAsync(string modelPath, int threads, bool useGpu, CancellationToken cancellationToken)
    {
        int useThreads = threads > 0 ? threads : Math.Max(1, Environment.ProcessorCount / 2);
        // Apple silicon's engine always draws on the graphics through Metal; elsewhere only the Vulkan add-on can.
        bool metal = OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64;
        bool gpu = useGpu && GpuOffered && !metal;
        string key = $"{Path.GetFullPath(modelPath)}|{File.GetLastWriteTimeUtc(modelPath).Ticks}|{useThreads}|{gpu}";
        if (_weights is not null && _loadedKey == key)
        {
            return _weights;
        }

        _weights?.Dispose();
        _weights = null;
        _loadedKey = null;

        _logger.LogInformation("Loading AI model {Model}{Where}.", Path.GetFileName(modelPath), gpu ? " onto the graphics card" : string.Empty);
        using WorkScope loading = Work.Begin("Local AI", "Loading AI model", Path.GetFileName(modelPath));

        ModelParams parameters = Parameters(modelPath, useThreads, metal ? 99 : gpu ? 999 : 0);
        LLamaWeights weights;
        GpuProblem = null;
        try
        {
            weights = await LLamaWeights.LoadFromFileAsync(parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (gpu && ex is not OperationCanceledException)
        {
            // Out of the card's memory, a driver that won't take it…: the processor still can.
            GpuProblem = $"The graphics card couldn't load the model ({ex.GetBaseException().Message}), so it ran on the processor.";
            _logger.LogWarning("Loading {Model} onto the graphics card failed ({Message}); using the processor instead.",
                Path.GetFileName(modelPath), ex.GetBaseException().Message);
            parameters = Parameters(modelPath, useThreads, 0);
            weights = await LLamaWeights.LoadFromFileAsync(parameters, cancellationToken).ConfigureAwait(false);
            gpu = false;
        }

        // The add-on may have been offered but not loaded (LLamaSharp falls back to the processor build by itself).
        bool onCard = gpu && LocalAiGpu.IsLoaded(LocalAiGpu.LibraryFile("ggml-vulkan"));
        if (gpu && !onCard && GpuProblem is null)
        {
            GpuProblem = "The graphics card add-on didn't load (see the server log), so the model ran on the processor.";
        }

        Device = onCard ? _vulkanDevice ?? "the graphics card" : null;
        if (onCard)
        {
            _logger.LogInformation("The AI model runs on {Device}.", Device);
        }

        _weights = weights;
        _parameters = parameters;
        _loadedKey = key;

        // Qwen3-style models think out loud first unless the reply is opened with an empty thought, which is what their
        // own template does when thinking is off.
        _skipThinking = weights.Metadata.TryGetValue("tokenizer.chat_template", out string? template)
            && template.Contains("<think>", StringComparison.Ordinal);
        return weights;
    }

    private static ModelParams Parameters(string modelPath, int threads, int gpuLayers) => new(modelPath)
    {
        ContextSize = ContextSize,
        // More layers than any of these models has: all of it goes on the card.
        GpuLayerCount = gpuLayers,
        Threads = threads,
        BatchThreads = threads
    };

    private string Prompt(LLamaWeights weights, TextGenerationRequest request)
    {
        string prompt;
        try
        {
            var template = new LLamaTemplate(weights.NativeHandle, strict: false) { AddAssistant = true };
            template.Add("system", request.Instructions);
            template.Add("user", request.Prompt);
            prompt = PromptTemplateTransformer.ToModelPrompt(template);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A model without a template llama.cpp knows: ChatML, which most small instruction models understand.
            _logger.LogDebug(ex, "The model's chat template couldn't be applied; using ChatML.");
            prompt = $"<|im_start|>system\n{request.Instructions}<|im_end|>\n<|im_start|>user\n{request.Prompt}<|im_end|>\n<|im_start|>assistant\n";
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            prompt = $"<|im_start|>system\n{request.Instructions}<|im_end|>\n<|im_start|>user\n{request.Prompt}<|im_end|>\n<|im_start|>assistant\n";
        }

        return _skipThinking ? prompt + "<think>\n\n</think>\n\n" : prompt;
    }

    private static string WithoutStops(string reply)
    {
        foreach (string stop in (string[])["<|im_end|>", "<|im_start|>", "<|endoftext|>", "<end_of_turn>", "<|eot_id|>", "</s>"])
        {
            int at = reply.IndexOf(stop, StringComparison.Ordinal);
            if (at >= 0)
            {
                reply = reply[..at];
            }
        }

        return reply.Trim();
    }

    private void UnloadIfIdle()
    {
        if (_weights is null || DateTime.UtcNow - _lastUsed < _unloadAfter || !_gate.Wait(0))
        {
            return;
        }

        try
        {
            if (_weights is not null)
            {
                _logger.LogInformation("Letting go of the AI model after {Minutes:0} minutes unused.", _unloadAfter.TotalMinutes);
                _weights.Dispose();
                _weights = null;
                _loadedKey = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
