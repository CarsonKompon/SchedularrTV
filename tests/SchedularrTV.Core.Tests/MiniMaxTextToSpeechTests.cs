using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SchedularrTV.Service;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class MiniMaxTextToSpeechTests
{
    [Fact]
    public async Task Synthesis_posts_the_model_and_voice_and_decodes_hex_audio()
    {
        var handler = new Handler(_ => """{"data":{"audio":"494433","status":2},"extra_info":{"audio_length":900},"base_resp":{"status_code":0,"status_msg":"success"}}""");
        MiniMaxTextToSpeech tts = Create(handler);

        TtsAudio audio = await tts.SynthesizeAsync("Coming up next... SpongeBob!", "English_expressive_narrator");

        Assert.Equal("mp3", audio.Extension);
        Assert.Equal("ID3"u8.ToArray(), audio.Data);
        HttpRequestMessage request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.minimax.io/v1/t2a_v2", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("test-key", request.Headers.Authorization.Parameter);
        using JsonDocument body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("speech-2.8-turbo", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("Coming up next... SpongeBob!", body.RootElement.GetProperty("text").GetString());
        Assert.Equal("hex", body.RootElement.GetProperty("output_format").GetString());
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("English_expressive_narrator", body.RootElement.GetProperty("voice_setting").GetProperty("voice_id").GetString());
        Assert.Equal("mp3", body.RootElement.GetProperty("audio_setting").GetProperty("format").GetString());
    }

    [Fact]
    public async Task Api_errors_surface_the_status_without_the_key_and_a_missing_key_never_calls_out()
    {
        MiniMaxTextToSpeech failing = Create(new Handler(_ => """{"data":null,"base_resp":{"status_code":1004,"status_msg":"authentication failed"}}"""));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => failing.SynthesizeAsync("Hi", "English_Graceful_Lady"));
        Assert.Contains("1004", error.Message);
        Assert.Contains("authentication failed", error.Message);
        Assert.DoesNotContain("test-key", error.Message);

        var handler = new Handler(_ => "{}");
        MiniMaxTextToSpeech missing = Create(handler, key: null);
        Assert.NotNull(missing.SetupProblem);
        await Assert.ThrowsAsync<InvalidOperationException>(() => missing.SynthesizeAsync("Hi", "English_Graceful_Lady"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Voice_list_groups_by_language_includes_custom_voices_and_is_cached()
    {
        var handler = new Handler(_ => """
            {
              "system_voice": [
                {"voice_id":"English_expressive_narrator","voice_name":"Expressive Narrator","description":["A clear narrator."]},
                {"voice_id":"Chinese (Mandarin)_Reliable_Executive","voice_name":"Reliable Executive","description":[]},
                {"voice_id":"Japanese_KindLady","voice_name":"Kind Lady"},
                {"voice_id":"czech_male_1_v1","voice_name":"Assured presenter"},
                {"voice_id":"Robot_Armor","voice_name":"Robot armor"}
              ],
              "voice_cloning": [{"voice_id":"my-announcer","description":[],"created_time":"2026-01-01"}],
              "voice_generation": null,
              "base_resp": {"status_code":0,"status_msg":"success"}
            }
            """);
        MiniMaxTextToSpeech tts = Create(handler);

        IReadOnlyList<TtsVoice> voices = await tts.GetVoicesAsync();
        await tts.GetVoicesAsync();

        Assert.Single(handler.Requests);
        Assert.Equal("https://api.minimax.io/v1/get_voice", handler.Requests[0].RequestUri!.ToString());
        Assert.Equal("all", JsonDocument.Parse(handler.Bodies[0]).RootElement.GetProperty("voice_type").GetString());
        TtsVoice narrator = voices.Single(voice => voice.Id == "English_expressive_narrator");
        Assert.Equal(("Expressive Narrator", "English", "A clear narrator."), (narrator.Name, narrator.Language, narrator.Description));
        Assert.Equal("Chinese (Mandarin)", voices.Single(voice => voice.Id.StartsWith("Chinese", StringComparison.Ordinal)).Language);
        Assert.Equal("Japanese", voices.Single(voice => voice.Id == "Japanese_KindLady").Language);
        Assert.Equal("Czech", voices.Single(voice => voice.Id == "czech_male_1_v1").Language);
        Assert.Equal("Other", voices.Single(voice => voice.Id == "Robot_Armor").Language);
        Assert.Equal(("my-announcer", "Custom"), voices.Where(voice => voice.Id == "my-announcer").Select(voice => (voice.Name, voice.Language)).Single());
    }

    [Fact]
    public void Cache_identity_changes_with_the_model()
    {
        MiniMaxTextToSpeech turbo = Create(new Handler(_ => "{}"));
        MiniMaxTextToSpeech hd = Create(new Handler(_ => "{}"), model: "speech-2.8-hd");

        Assert.Contains("speech-2.8-turbo", turbo.CacheIdentity);
        Assert.NotEqual(turbo.CacheIdentity, hd.CacheIdentity);
        Assert.DoesNotContain("test-key", turbo.CacheIdentity);
    }

    [Fact]
    public void Dot_env_files_parse_quotes_comments_and_exports()
    {
        IReadOnlyDictionary<string, string> values = DotEnvFile.Parse(
        [
            "# local secrets",
            "MINIMAX_API_KEY=abc123",
            "export QUOTED = \"has # hash\"",
            "SINGLE='x y'",
            "TRAILING=value # note",
            "not a pair",
            ""
        ]);

        Assert.Equal("abc123", values["MINIMAX_API_KEY"]);
        Assert.Equal("has # hash", values["QUOTED"]);
        Assert.Equal("x y", values["SINGLE"]);
        Assert.Equal("value", values["TRAILING"]);
        Assert.Equal(4, values.Count);
    }

    [Fact]
    public void Dot_env_is_found_in_a_parent_directory()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dotenv-{Guid.NewGuid():N}");
        string nested = Path.Combine(root, "src", "service");
        Directory.CreateDirectory(nested);
        try
        {
            File.WriteAllText(Path.Combine(root, ".env"), "A=1");
            Assert.Equal(Path.Combine(root, ".env"), DotEnvFile.Find(null, nested));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static MiniMaxTextToSpeech Create(Handler handler, string? key = "test-key", string model = "speech-2.8-turbo") =>
        new(new Factory(handler), Options.Create(new MiniMaxOptions { ApiKey = key, Model = model }), TimeProvider.System);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(request), Encoding.UTF8, "application/json") };
        }
    }
}
