using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SchedularrTV.Speech.Piper;

public sealed class PiperOptions
{
    public const string SectionName = "Piper";

    private static readonly string Home = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SchedularrTV", "piper");

    /// <summary>The standalone <c>piper</c> program from the Piper releases.</summary>
    public string ExecutablePath { get; set; } = Path.Combine(Home, "piper", OperatingSystem.IsWindows() ? "piper.exe" : "piper");

    /// <summary>Folder of voice models: each <c>.onnx</c> with its <c>.onnx.json</c> beside it.</summary>
    public string VoicesPath { get; set; } = Path.Combine(Home, "voices");

    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Piper, a free text-to-speech engine that runs on this machine. Voices are the models in
/// <see cref="PiperOptions.VoicesPath"/>; multi-speaker models list each speaker as its own voice.
/// </summary>
public sealed class PiperTextToSpeech(IOptions<PiperOptions> options) : ITextToSpeechProvider
{
    public const string ProviderId = "piper";

    // Read on each use: settings saved in the app apply straight away.
    private PiperOptions _options => options.Value;

    public string Id => ProviderId;

    public string DisplayName => "Piper (free, offline)";

    public string? SetupProblem =>
        !File.Exists(_options.ExecutablePath) ? $"Piper isn't installed at {_options.ExecutablePath} (download it, or choose where it is, in the Piper voices plugin's settings on the Plugins page)."
        : !Directory.Exists(_options.VoicesPath) || !Directory.EnumerateFiles(_options.VoicesPath, "*.onnx").Any()
            ? $"No Piper voices in {_options.VoicesPath} (download some in the Piper voices plugin's settings on the Plugins page)."
            : null;

    public Task<IReadOnlyList<TtsVoice>> GetVoicesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Directory.Exists(_options.VoicesPath) ? ListVoices(_options.VoicesPath) : []);

    public async Task<TtsAudio> SynthesizeAsync(string text, string voice, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (SetupProblem is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        (string model, string? speaker) = SplitVoice(voice);
        string modelPath = Path.Combine(_options.VoicesPath, $"{model}.onnx");
        if (!File.Exists(modelPath) || Path.GetDirectoryName(Path.GetFullPath(modelPath)) != Path.GetFullPath(_options.VoicesPath).TrimEnd(Path.DirectorySeparatorChar))
        {
            throw new InvalidOperationException($"There's no Piper voice '{model}' in {_options.VoicesPath}.");
        }

        int? speakerId = speaker is null ? null : SpeakerId(modelPath, speaker)
            ?? throw new InvalidOperationException($"Piper voice '{model}' has no speaker '{speaker}'.");
        string output = Path.Combine(Path.GetTempPath(), $"piper-{Guid.NewGuid():N}.wav");
        try
        {
            await RunAsync(Arguments(modelPath, speakerId, output), text, cancellationToken).ConfigureAwait(false);
            return File.Exists(output)
                ? new TtsAudio(await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false), "wav")
                : throw new InvalidOperationException("Piper finished without writing any audio.");
        }
        finally
        {
            File.Delete(output);
        }
    }

    public static IReadOnlyList<string> Arguments(string modelPath, int? speakerId, string output) =>
        speakerId is { } speaker
            ? ["--model", modelPath, "--speaker", speaker.ToString(CultureInfo.InvariantCulture), "--output_file", output]
            : ["--model", modelPath, "--output_file", output];

    /// <summary>Every voice in the folder, one per speaker for multi-speaker models.</summary>
    public static IReadOnlyList<TtsVoice> ListVoices(string folder)
    {
        List<TtsVoice> voices = [];
        foreach (string model in Directory.EnumerateFiles(folder, "*.onnx"))
        {
            string id = Path.GetFileNameWithoutExtension(model);
            string config = $"{model}.json";
            if (!File.Exists(config))
            {
                voices.Add(new TtsVoice(id, Words(id), "Other"));
                continue;
            }

            try
            {
                using JsonDocument json = JsonDocument.Parse(File.ReadAllText(config));
                JsonElement root = json.RootElement;
                string dataset = Text(root, "dataset") ?? id;
                string quality = root.TryGetProperty("audio", out JsonElement audio) ? Text(audio, "quality") ?? "" : "";
                string language = Language(root);
                string description = $"{Text(root, "language", "country_english") ?? language}. Piper, {quality} quality.";
                List<string> speakers = root.TryGetProperty("speaker_id_map", out JsonElement map) && map.ValueKind == JsonValueKind.Object
                    ? [.. map.EnumerateObject().OrderBy(entry => entry.Value.GetInt32()).Select(entry => entry.Name)]
                    : [];

                if (speakers.Count > 1)
                {
                    voices.AddRange(speakers.Select(speaker =>
                        new TtsVoice($"{id}#{speaker}", $"{Words(dataset)}: {Words(speaker)}, {quality}", language, description)));
                }
                else
                {
                    voices.Add(new TtsVoice(id, $"{Words(dataset)}, {quality}", language, description));
                }
            }
            catch (JsonException)
            {
                voices.Add(new TtsVoice(id, Words(id), "Other"));
            }
        }

        return [.. voices.OrderBy(voice => voice.Language).ThenBy(voice => voice.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static (string Model, string? Speaker) SplitVoice(string voice)
    {
        int hash = voice.IndexOf('#');
        return hash < 0 ? (voice.Trim(), null) : (voice[..hash].Trim(), voice[(hash + 1)..].Trim());
    }

    private static int? SpeakerId(string modelPath, string speaker)
    {
        if (int.TryParse(speaker, NumberStyles.None, CultureInfo.InvariantCulture, out int numbered))
        {
            return numbered;
        }

        using JsonDocument json = JsonDocument.Parse(File.ReadAllText($"{modelPath}.json"));
        return json.RootElement.TryGetProperty("speaker_id_map", out JsonElement map) && map.TryGetProperty(speaker, out JsonElement id)
            ? id.GetInt32()
            : null;
    }

    private async Task RunAsync(IReadOnlyList<string> arguments, string text, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(_options.ExecutablePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(_options.ExecutablePath)!
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        using Process process = Process.Start(info) ?? throw new InvalidOperationException("Piper could not be started.");
        Task<string> error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        Task<string> output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);

        // Piper reads one utterance per line, so the whole read-out goes on a single line.
        await process.StandardInput.WriteLineAsync(string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).AsMemory(), timeout.Token)
            .ConfigureAwait(false);
        process.StandardInput.Close();

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"Piper didn't finish within {_options.Timeout.TotalSeconds:0} seconds.");
        }

        await output.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            string log = (await error.ConfigureAwait(false)).Trim();
            throw new InvalidOperationException($"Piper exited with code {process.ExitCode}: {log[Math.Max(0, log.Length - 1000)..]}");
        }
    }

    private static string Language(JsonElement root)
    {
        string name = Text(root, "language", "name_english") ?? "Other";
        return Text(root, "language", "region") switch
        {
            null or "" => name,
            "GB" => $"UK {name}",
            string region => $"{region} {name}"
        };
    }

    private static string? Text(JsonElement element, params string[] path)
    {
        foreach (string name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
            {
                return null;
            }
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    private static string Words(string value) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('_', ' ').Replace('-', ' '));
}
