using System.Runtime.InteropServices;

namespace SchedularrTV.Writing.LocalAi;

/// <summary>The plugin's settings, from the Plugins page (<c>Plugins:local-ai</c>).</summary>
public sealed class LocalAiOptions
{
    public const string Ollama = "Ollama";
    public const string OpenAiCompatible = "OpenAI-compatible";

    /// <summary>A <see cref="LocalAiModels"/> id, or <see cref="LocalAiModels.Custom"/> for <see cref="ModelFile"/>.</summary>
    public string Model { get; set; } = LocalAiModels.ForThisComputer;

    public string? ModelFile { get; set; }

    public string? ModelsFolder { get; set; }

    /// <summary>0 uses half the processor's threads.</summary>
    public int Threads { get; set; }

    public int UnloadAfterMinutes { get; set; } = 10;

    /// <summary>
    /// <see cref="LocalAiGpu.Automatic"/> runs the built-in model and Whisper on the graphics card when its add-on is
    /// downloaded and a Vulkan driver is found; <see cref="LocalAiGpu.Off"/> keeps them on the processor.
    /// </summary>
    public string Gpu { get; set; } = LocalAiGpu.Automatic;

    public bool UseGpu => !string.Equals(Gpu?.Trim(), LocalAiGpu.Off, StringComparison.OrdinalIgnoreCase);

    public string ServerKind { get; set; } = Ollama;

    public string? ServerUrl { get; set; }

    public string? ServerModel { get; set; }

    public string? ServerApiKey { get; set; }

    public int ServerTimeoutSeconds { get; set; } = 120;

    /// <summary>A <see cref="WhisperModels"/> id, or <see cref="WhisperModels.Custom"/> for <see cref="WhisperModelFile"/>.</summary>
    public string WhisperModel { get; set; } = WhisperModels.Default;

    /// <summary>Your own ggml Whisper model, used when <see cref="WhisperModel"/> is <see cref="WhisperModels.Custom"/>.</summary>
    public string? WhisperModelFile { get; set; }

    /// <summary>Two-letter language the episodes speak (<c>en</c>), or empty to have Whisper work it out.</summary>
    public string? WhisperLanguage { get; set; }

    public bool IsOllama => !string.Equals(ServerKind?.Trim(), OpenAiCompatible, StringComparison.OrdinalIgnoreCase);

    /// <summary>The server's address, or the usual one for its type.</summary>
    public string ServerAddress =>
        (string.IsNullOrWhiteSpace(ServerUrl) ? IsOllama ? "http://localhost:11434" : "http://localhost:1234" : ServerUrl.Trim()).TrimEnd('/');
}

/// <summary>A model the plugin can download.</summary>
public sealed record LocalAiModel(string Id, string Name, string Description, string Repository, string File, long Size);

/// <summary>
/// Models offered for download: Qwen3.5's small instruction-tuned models (Apache 2.0), 4-bit, from Unsloth's GGUF
/// conversions on Hugging Face.
/// </summary>
public static class LocalAiModels
{
    public const string Default = "qwen3.5-2b";
    public const string Smallest = "qwen3.5-0.8b";
    public const string Custom = "custom";

    /// <summary>
    /// The model used when none is chosen: <see cref="Default"/>, or on a low-power computer with less than about 6 GB
    /// of memory (a 2 or 4 GB Raspberry Pi) <see cref="Smallest"/>, which leaves room for the browsers and ffmpegs of
    /// the live channels.
    /// </summary>
    public static string ForThisComputer =>
        SchedularrTV.Core.Platform.HardwareProfile.IsLowPower && SchedularrTV.Core.Platform.HardwareProfile.Detected.MemoryGb < 6 ? Smallest : Default;

    public static readonly IReadOnlyList<LocalAiModel> All =
    [
        new(Smallest, "Qwen3.5 0.8B", "The smallest and quickest. Fine for short captions; its voice-overs are plainer and more often set aside.",
            "unsloth/Qwen3.5-0.8B-GGUF", "Qwen3.5-0.8B-Q4_K_M.gguf", 532_517_120),
        new(Default, "Qwen3.5 2B", "The one to start with: a few seconds a promo on an ordinary processor, and good at following the rules.",
            "unsloth/Qwen3.5-2B-GGUF", "Qwen3.5-2B-Q4_K_M.gguf", 1_280_835_840),
        new("qwen3.5-4b", "Qwen3.5 4B", "Funnier and more reliable, but twice as slow; wants 8 GB of memory or more.",
            "unsloth/Qwen3.5-4B-GGUF", "Qwen3.5-4B-Q4_K_M.gguf", 2_740_937_888)
    ];

    public static LocalAiModel? Find(string? id) =>
        All.FirstOrDefault(model => string.Equals(model.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string Url(LocalAiModel model) => $"https://huggingface.co/{model.Repository}/resolve/main/{model.File}";
}

/// <summary>
/// Whisper models offered for download (MIT), whisper.cpp's ggml conversions on Hugging Face. They understand most
/// languages; the transcripts only need to be good enough to find a promo's moments.
/// </summary>
public static class WhisperModels
{
    public const string Default = "whisper-base";
    public const string Custom = "custom";

    public static readonly IReadOnlyList<LocalAiModel> All =
    [
        new("whisper-tiny", "Whisper tiny", "The quickest: a 22-minute episode in a minute or two on an ordinary processor. Misses more words.",
            "ggerganov/whisper.cpp", "ggml-tiny.bin", 77_691_713),
        new(Default, "Whisper base", "The one to start with: a few minutes an episode, and good enough to find the funny lines.",
            "ggerganov/whisper.cpp", "ggml-base.bin", 147_951_465),
        new("whisper-small", "Whisper small", "Hears more clearly through music and shouting, but about three times as slow as base.",
            "ggerganov/whisper.cpp", "ggml-small.bin", 487_601_967)
    ];

    public static LocalAiModel? Find(string? id) =>
        All.FirstOrDefault(model => string.Equals(model.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Where the plugin keeps things: relative paths in settings are taken from its data folder.</summary>
public sealed class LocalAiPaths(string dataDirectory)
{
    public string DataDirectory { get; } = dataDirectory;

    /// <summary>The native engine for this computer, kept apart per engine version so an update downloads its own.</summary>
    public string Engine => Path.Combine(DataDirectory, "engine", LlamaEngine.BackendVersion);

    /// <summary>
    /// The engine's graphics card (Vulkan) add-on, kept apart from <see cref="Engine"/> so downloading either again
    /// leaves the other alone. Laid out the same way (<c>runtimes/&lt;system&gt;/native/vulkan/…</c>).
    /// </summary>
    public string GpuEngine => Path.Combine(DataDirectory, "engine-gpu", LlamaEngine.BackendVersion);

    public string ModelsFolder(string? setting) => Resolve(setting) ?? Path.Combine(DataDirectory, "models");

    /// <summary>The model file a choice means, or null when there's none (no custom file set).</summary>
    public string? ModelPath(string? model, string? modelFile, string? modelsFolder) =>
        string.Equals(model?.Trim(), LocalAiModels.Custom, StringComparison.OrdinalIgnoreCase)
            ? Resolve(modelFile)
            : Path.Combine(ModelsFolder(modelsFolder), (LocalAiModels.Find(model) ?? LocalAiModels.Find(LocalAiModels.ForThisComputer)!).File);

    public string? ModelPath(LocalAiOptions options) => ModelPath(options.Model, options.ModelFile, options.ModelsFolder);

    /// <summary>Whisper's native library for this computer, kept apart per version like <see cref="Engine"/>.</summary>
    public string WhisperEngine => Path.Combine(DataDirectory, "whisper-engine", LocalAi.WhisperEngine.RuntimeVersion);

    /// <summary>The Whisper model file a choice means (kept in the models folder), or null when there's none.</summary>
    public string? WhisperModelPath(string? model, string? modelFile, string? modelsFolder) =>
        string.Equals(model?.Trim(), WhisperModels.Custom, StringComparison.OrdinalIgnoreCase)
            ? Resolve(modelFile)
            : Path.Combine(ModelsFolder(modelsFolder), (WhisperModels.Find(model) ?? WhisperModels.Find(WhisperModels.Default)!).File);

    public string? WhisperModelPath(LocalAiOptions options) => WhisperModelPath(options.WhisperModel, options.WhisperModelFile, options.ModelsFolder);

    private string? Resolve(string? value)
    {
        string? trimmed = value?.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(trimmed) ? null
            : Path.IsPathRooted(trimmed) ? trimmed
            : Path.GetFullPath(Path.Combine(DataDirectory, trimmed));
    }

    /// <summary>The runtime folder name llama.cpp's builds use for this computer, as LLamaSharp looks for it.</summary>
    public static string? RuntimeId()
    {
        bool arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        if (OperatingSystem.IsWindows())
        {
            return arm ? "win-arm64" : RuntimeInformation.OSArchitecture == Architecture.X64 ? "win-x64" : null;
        }

        if (OperatingSystem.IsMacOS())
        {
            return arm ? "osx-arm64" : "osx-x64";
        }

        if (OperatingSystem.IsLinux())
        {
            return arm ? "linux-arm64"
                : RuntimeInformation.OSArchitecture != Architecture.X64 ? null
                : RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.OrdinalIgnoreCase) ? "linux-musl-x64"
                : "linux-x64";
        }

        return null;
    }
}
