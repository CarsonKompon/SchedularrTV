using System.Runtime.InteropServices;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using LLama.Transformers;

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

    /// <summary>
    /// Points LLamaSharp at the downloaded engine. It can only be done once, before the engine is first used, so a
    /// different folder later needs a restart.
    /// </summary>
    public static void UseEngine(string folder)
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

            // Only the CPU engine is downloaded (it includes Metal on Apple silicon); not looking for CUDA or Vulkan
            // builds saves a string of failed loads.
            NativeLibraryConfig.All
                .WithSearchDirectory(folder)
                .WithCuda(false)
                .WithVulkan(false);
            _nativeFolder = folder;
        }
    }

    public async Task<string> GenerateAsync(string modelPath, int threads, TimeSpan unloadAfter, TextGenerationRequest request,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _unloadAfter = unloadAfter <= TimeSpan.Zero ? TimeSpan.FromMinutes(10) : unloadAfter;
            LLamaWeights weights = await LoadAsync(modelPath, threads, cancellationToken).ConfigureAwait(false);
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

    private async Task<LLamaWeights> LoadAsync(string modelPath, int threads, CancellationToken cancellationToken)
    {
        int useThreads = threads > 0 ? threads : Math.Max(1, Environment.ProcessorCount / 2);
        string key = $"{Path.GetFullPath(modelPath)}|{File.GetLastWriteTimeUtc(modelPath).Ticks}|{useThreads}";
        if (_weights is not null && _loadedKey == key)
        {
            return _weights;
        }

        _weights?.Dispose();
        _weights = null;
        _loadedKey = null;

        var parameters = new ModelParams(modelPath)
        {
            ContextSize = ContextSize,
            // Apple silicon's engine draws on the GPU through Metal; everywhere else this one runs on the processor.
            GpuLayerCount = OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64 ? 99 : 0,
            Threads = useThreads,
            BatchThreads = useThreads
        };

        _logger.LogInformation("Loading AI model {Model}.", Path.GetFileName(modelPath));
        LLamaWeights weights = await LLamaWeights.LoadFromFileAsync(parameters, cancellationToken).ConfigureAwait(false);
        _weights = weights;
        _parameters = parameters;
        _loadedKey = key;

        // Qwen3-style models think out loud first unless the reply is opened with an empty thought, which is what their
        // own template does when thinking is off.
        _skipThinking = weights.Metadata.TryGetValue("tokenizer.chat_template", out string? template)
            && template.Contains("<think>", StringComparison.Ordinal);
        return weights;
    }

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
