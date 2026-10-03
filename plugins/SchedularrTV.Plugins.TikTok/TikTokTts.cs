using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SchedularrTV.Speech.TikTok;

public sealed class TikTokTextToSpeech(IHttpClientFactory httpClientFactory) : ITextToSpeechProvider
{
    public const string ProviderId = "tiktok";

    private readonly IReadOnlyList<TtsVoice> _voices = [.. TikTokVoices.English.Select(voice => new TtsVoice(voice.Value, voice.Key))];

    public string Id => ProviderId;

    public string DisplayName => "TikTok";

    public Task<IReadOnlyList<TtsVoice>> GetVoicesAsync(CancellationToken cancellationToken = default) => Task.FromResult(_voices);

    public async Task<TtsAudio> SynthesizeAsync(string text, string voice, CancellationToken cancellationToken = default) =>
        new(await new TikTokTtsClient(httpClientFactory.CreateClient(ProviderId))
            .SynthesizeAsync(text, TikTokVoices.Resolve(voice), cancellationToken).ConfigureAwait(false), "mp3");
}

public sealed record TikTokTtsEndpoint(Uri Url, string ResponseField);

/// <summary>
/// TikTok voices through the public proxy endpoints used by markrzv/TikTok-Voice-TTS: endpoints are
/// tried in order, long text is split into 300-byte chunks, and the chunks are fetched in parallel.
/// </summary>
public sealed partial class TikTokTtsClient(HttpClient http, IReadOnlyList<TikTokTtsEndpoint>? endpoints = null)
{
    private const int ChunkBytes = 300;

    public static IReadOnlyList<TikTokTtsEndpoint> DefaultEndpoints { get; } =
    [
        new(new Uri("https://tiktok-tts.weilnet.workers.dev/api/generation"), "data"),
        new(new Uri("https://gesserit.co/api/tiktok-tts"), "base64")
    ];

    /// <summary>Returns MP3 audio.</summary>
    public async Task<byte[]> SynthesizeAsync(string text, string voice, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(voice);

        IReadOnlyList<string> chunks = SplitText(text);
        List<Exception> failures = [];

        foreach (TikTokTtsEndpoint endpoint in endpoints ?? DefaultEndpoints)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                byte[][] audio = await Task.WhenAll(chunks.Select(chunk => FetchAsync(endpoint, chunk, voice, attempt)))
                    .ConfigureAwait(false);
                return [.. audio.SelectMany(part => part)];
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                failures.Add(new InvalidOperationException($"{endpoint.Url.Host}: {ex.Message}", ex));
            }
        }

        throw new AggregateException("Every TikTok TTS endpoint failed.", failures);
    }

    public static IReadOnlyList<string> SplitText(string text)
    {
        List<string> pieces = [];
        foreach (Match sentence in Punctuated().Matches(text))
        {
            if (Bytes(sentence.Value) > ChunkBytes)
            {
                pieces.AddRange(Words().Matches(sentence.Value).Select(word => word.Value));
            }
            else
            {
                pieces.Add(sentence.Value);
            }
        }

        List<string> chunks = [];
        string current = string.Empty;
        foreach (string piece in pieces)
        {
            if (Bytes(current) + Bytes(piece) <= ChunkBytes)
            {
                current += piece;
                continue;
            }

            if (current.Length > 0)
            {
                chunks.Add(current);
            }

            current = piece;
        }

        if (current.Length > 0)
        {
            chunks.Add(current);
        }

        return chunks;
    }

    private async Task<byte[]> FetchAsync(TikTokTtsEndpoint endpoint, string chunk, string voice, CancellationTokenSource attempt)
    {
        try
        {
            // The proxy rejects chunked bodies and anonymous clients; aiohttp sends both headers by default.
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { text = chunk, voice }), Encoding.UTF8, "application/json")
            };
            request.Headers.UserAgent.ParseAdd("SchedularrTV/1.0");
            using HttpResponseMessage response = await http
                .SendAsync(request, attempt.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using Stream body = await response.Content.ReadAsStreamAsync(attempt.Token).ConfigureAwait(false);
            using JsonDocument json = await JsonDocument.ParseAsync(body, cancellationToken: attempt.Token).ConfigureAwait(false);

            string? data = json.RootElement.TryGetProperty(endpoint.ResponseField, out JsonElement value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

            return string.IsNullOrEmpty(data)
                ? throw new InvalidOperationException("The response contained no audio.")
                : Convert.FromBase64String(data);
        }
        catch
        {
            // One failed chunk makes the whole endpoint unusable, so stop its siblings.
            await attempt.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static int Bytes(string value) => Encoding.UTF8.GetByteCount(value);

    [GeneratedRegex(@".*?[.,!?:;-]|.+", RegexOptions.Singleline)]
    private static partial Regex Punctuated();

    [GeneratedRegex(@".*?[ ]|.+", RegexOptions.Singleline)]
    private static partial Regex Words();
}

/// <summary>Voice ids accepted by the TikTok endpoints, keyed by the names the Python project uses.</summary>
public static class TikTokVoices
{
    public const string Default = "en_us_006";

    public static IReadOnlyDictionary<string, string> English { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["US_FEMALE_1"] = "en_us_001",
        ["US_FEMALE_2"] = "en_us_002",
        ["US_MALE_1"] = "en_us_006",
        ["US_MALE_2"] = "en_us_007",
        ["US_MALE_3"] = "en_us_009",
        ["US_MALE_4"] = "en_us_010",
        ["UK_MALE_1"] = "en_uk_001",
        ["UK_MALE_2"] = "en_uk_003",
        ["AU_FEMALE_1"] = "en_au_001",
        ["AU_MALE_1"] = "en_au_002",
        ["MALE_NARRATION"] = "en_male_narration",
        ["MALE_FUNNY"] = "en_male_funny",
        ["FEMALE_EMOTIONAL"] = "en_female_emotional",
        ["MALE_JARVIS"] = "en_male_jarvis",
        ["MALE_UKBUTLER"] = "en_male_ukbutler",
        ["MALE_WIZARD"] = "en_male_wizard",
        ["MALE_GRINCH"] = "en_male_grinch",
        ["MALE_DEADPOOL"] = "en_male_deadpool",
        ["GHOST_HOST"] = "en_male_ghosthost",
        ["PIRATE"] = "en_male_pirate",
        ["STITCH"] = "en_us_stitch",
        ["C3PO"] = "en_us_c3po"
    };

    public static string Resolve(string nameOrId) =>
        English.TryGetValue(nameOrId, out string? id) ? id : nameOrId;
}
