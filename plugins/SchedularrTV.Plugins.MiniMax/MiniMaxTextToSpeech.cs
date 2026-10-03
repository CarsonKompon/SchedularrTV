using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SchedularrTV.Speech.MiniMax;

public sealed class MiniMaxOptions
{
    public const string SectionName = "MiniMax";

    /// <summary>Entered in the plugin's settings; falls back to <c>MINIMAX_API_KEY</c> in the environment or <c>.env</c>.</summary>
    public string? ApiKey { get; set; }

    public string BaseUrl { get; set; } = "https://api.minimax.io";

    public string Model { get; set; } = "speech-2.8-turbo";

    public int SampleRate { get; set; } = 32000;

    public int Bitrate { get; set; } = 128000;

    /// <summary>Language hint for pronunciation; <c>auto</c> lets the model detect it.</summary>
    public string LanguageBoost { get; set; } = "auto";

    /// <summary>How long the account's voice list is reused before it is fetched again.</summary>
    public TimeSpan VoiceListCacheDuration { get; set; } = TimeSpan.FromHours(6);
}

/// <summary>MiniMax T2A v2 speech and the account's system, cloned and generated voices.</summary>
public sealed class MiniMaxTextToSpeech(
    IHttpClientFactory httpClientFactory,
    IOptions<MiniMaxOptions> options,
    TimeProvider clock) : ITextToSpeechProvider
{
    public const string ProviderId = "minimax";
    public const string KeyVariable = "MINIMAX_API_KEY";

    // Read on each use: settings saved in the app apply straight away.
    private MiniMaxOptions _options => options.Value;
    private readonly SemaphoreSlim _voiceGate = new(1, 1);
    private IReadOnlyList<TtsVoice>? _voices;
    private DateTimeOffset _voicesAt;
    private string? _voicesKey;

    public string Id => ProviderId;

    public string DisplayName => "MiniMax";

    public string CacheIdentity => $"{_options.Model}|{_options.SampleRate}|{_options.Bitrate}|{_options.LanguageBoost}";

    public string? SetupProblem => string.IsNullOrWhiteSpace(_options.ApiKey)
        ? "Add your MiniMax API key in the MiniMax voices plugin's settings on the Plugins page."
        : null;

    public async Task<IReadOnlyList<TtsVoice>> GetVoicesAsync(CancellationToken cancellationToken = default)
    {
        // A different key is a different account with its own cloned voices.
        if (_voicesKey != _options.ApiKey)
        {
            _voices = null;
        }

        if (_voices is { } cached && clock.GetUtcNow() - _voicesAt < _options.VoiceListCacheDuration)
        {
            return cached;
        }

        await _voiceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_voices is { } fresh && clock.GetUtcNow() - _voicesAt < _options.VoiceListCacheDuration)
            {
                return fresh;
            }

            using JsonDocument json = await PostAsync("/v1/get_voice", new { voice_type = "all" }, cancellationToken).ConfigureAwait(false);
            List<TtsVoice> voices = [.. Voices(json.RootElement, "system_voice", custom: false)];
            voices.AddRange(Voices(json.RootElement, "voice_cloning", custom: true));
            voices.AddRange(Voices(json.RootElement, "voice_generation", custom: true));

            _voices = [.. voices.DistinctBy(voice => voice.Id).OrderBy(voice => voice.Language).ThenBy(voice => voice.Name, StringComparer.OrdinalIgnoreCase)];
            _voicesAt = clock.GetUtcNow();
            _voicesKey = _options.ApiKey;
            return _voices;
        }
        finally
        {
            _voiceGate.Release();
        }
    }

    public async Task<TtsAudio> SynthesizeAsync(string text, string voice, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(voice);

        using JsonDocument json = await PostAsync("/v1/t2a_v2", new
        {
            model = _options.Model,
            text,
            stream = false,
            output_format = "hex",
            language_boost = _options.LanguageBoost,
            voice_setting = new { voice_id = voice, speed = 1, vol = 1, pitch = 0 },
            audio_setting = new { sample_rate = _options.SampleRate, bitrate = _options.Bitrate, format = "mp3", channel = 1 }
        }, cancellationToken).ConfigureAwait(false);

        string? hex = json.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("audio", out JsonElement audio) ? audio.GetString() : null;
        return string.IsNullOrEmpty(hex)
            ? throw new InvalidOperationException("MiniMax returned no audio.")
            : new TtsAudio(Convert.FromHexString(hex), "mp3");
    }

    /// <summary>System voice ids usually start with their language, e.g. <c>English_expressive_narrator</c> or <c>czech_male_1_v1</c>.</summary>
    public static string LanguageOf(string voiceId)
    {
        int underscore = voiceId.IndexOf('_');
        return underscore > 0 && Languages.FirstOrDefault(language =>
                   string.Equals(language, voiceId[..underscore], StringComparison.OrdinalIgnoreCase)) is { } known
            ? known
            : "Other";
    }

    private static IEnumerable<TtsVoice> Voices(JsonElement root, string property, bool custom)
    {
        if (!root.TryGetProperty(property, out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement item in list.EnumerateArray())
        {
            if (item.TryGetProperty("voice_id", out JsonElement id) && id.GetString() is { Length: > 0 } voiceId)
            {
                string? name = item.TryGetProperty("voice_name", out JsonElement voiceName) ? voiceName.GetString() : null;
                string? description = item.TryGetProperty("description", out JsonElement lines) && lines.ValueKind == JsonValueKind.Array
                    ? string.Join(" ", lines.EnumerateArray().Select(line => line.GetString()).Where(line => !string.IsNullOrWhiteSpace(line)))
                    : null;
                yield return new TtsVoice(
                    voiceId,
                    string.IsNullOrWhiteSpace(name) ? voiceId : name,
                    custom ? "Custom" : LanguageOf(voiceId),
                    string.IsNullOrWhiteSpace(description) ? null : description);
            }
        }
    }

    private async Task<JsonDocument> PostAsync(string path, object body, CancellationToken cancellationToken)
    {
        if (SetupProblem is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_options.BaseUrl.TrimEnd('/') + "/"), path.TrimStart('/')))
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        using HttpResponseMessage response = await httpClientFactory.CreateClient(ProviderId)
            .SendAsync(request, cancellationToken).ConfigureAwait(false);
        string content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(content);
        }
        catch (JsonException) when (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"MiniMax returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        }

        if (json.RootElement.TryGetProperty("base_resp", out JsonElement status)
            && status.TryGetProperty("status_code", out JsonElement code) && code.TryGetInt64(out long value) && value != 0)
        {
            string message = status.TryGetProperty("status_msg", out JsonElement text) ? text.GetString() ?? "" : "";
            json.Dispose();
            throw new InvalidOperationException($"MiniMax error {value}: {message}".TrimEnd(' ', ':'));
        }

        if (!response.IsSuccessStatusCode)
        {
            json.Dispose();
            throw new HttpRequestException($"MiniMax returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        }

        return json;
    }

    private static readonly string[] Languages =
    [
        "Afrikaans", "Arabic", "Bulgarian", "Cantonese", "Catalan", "Chinese", "Chinese (Mandarin)", "Croatian", "Czech", "Danish",
        "Dutch", "English", "Filipino", "Finnish", "French", "German", "Greek", "Hebrew", "Hindi", "Hungarian", "Indonesian",
        "Italian", "Japanese", "Korean", "Malay", "Norwegian", "Persian", "Polish", "Portuguese", "Romanian", "Russian",
        "Slovak", "Slovenian", "Spanish", "Swedish", "Tamil", "Thai", "Turkish", "Ukrainian", "Vietnamese"
    ];
}
