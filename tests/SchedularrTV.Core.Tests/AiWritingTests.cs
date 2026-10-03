using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Configuration;
using SchedularrTV.GeneratedPromos;
using SchedularrTV.Plugins.Writing;
using SchedularrTV.Service.Writing;
using SchedularrTV.Writing.LocalAi;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class AiWritingTests : IDisposable
{
    private static readonly DateTimeOffset Day = new(2026, 10, 3, 17, 0, 0, TimeSpan.Zero);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ai-writing-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.Delete(_folder);

    /// <summary>A writer that answers with whatever the test says, counting how often it's asked.</summary>
    internal sealed class FakeGenerator(Func<TextGenerationRequest, string> reply, string id = "fake") : ITextGenerator
    {
        private int _calls;

        public int Calls => _calls;

        public TextGenerationRequest? Last { get; private set; }

        public string Id => id;

        public string DisplayName => "Fake writer";

        public Task<string> GenerateAsync(TextGenerationRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Last = request;
            return Task.FromResult(reply(request));
        }
    }

    private AiTextWriter Writer(params ITextGenerator[] generators) =>
        new(new TextGeneratorCatalog(generators), Path.Combine(_folder, "ai-text"), TimeProvider.System, NullLogger<AiTextWriter>.Instance);

    private static AiTextJob UpNextJob(AiTextConfig? config = null) => new()
    {
        Config = config ?? new AiTextConfig(),
        Channel = "The Zone",
        Purpose = "the announcer's voice-over for an Up Next promo",
        Fallback = "Coming up next... SpongeBob SquarePants. Then Rugrats at 6!",
        Facts =
        [
            new("Next", "SpongeBob SquarePants, right after this break"),
            new("Then", "Rugrats, at 6"),
            new("Day", "Tonight")
        ],
        MustSay = ["SpongeBob SquarePants", "Rugrats"],
        MaxWords = 25,
        Seed = "zone|17:00"
    };

    // --- Cleaning and checking ---------------------------------------------------------------------

    [Fact]
    public void Replies_lose_reasoning_labels_quotes_and_emoji()
    {
        Assert.Equal("Coming up next: SpongeBob!", AiTextRules.Clean("<think>Let me see.</think>\n\"Coming up next: SpongeBob!\" 🍍", AiTextUse.Spoken));
        Assert.Equal("Up next, Rugrats!", AiTextRules.Clean("Here's your voice-over:\nVoice-over: Up next, Rugrats!", AiTextUse.Spoken));
        Assert.Equal("Up next, Rugrats! Only on The Zone.", AiTextRules.Clean("Up next, Rugrats!\nOnly on The Zone.", AiTextUse.Spoken));
        Assert.Equal("Line one\nLine two", AiTextRules.Clean("**Line one**\nLine two\nLine three", AiTextUse.Shown));
        Assert.Equal(string.Empty, AiTextRules.Clean("<think>still thinking about it", AiTextUse.Spoken));
    }

    [Fact]
    public void Writing_must_name_every_show_and_make_up_no_times_or_days()
    {
        AiTextJob job = UpNextJob();

        Assert.Null(AiTextRules.Check("Coming up next, SpongeBob, then Rugrats at 6!", job));
        Assert.Null(AiTextRules.Check("Tonight: SpongeBob SquarePants, then Rugrats at 6.", job));
        Assert.Contains("left out Rugrats", AiTextRules.Check("Coming up next, SpongeBob!", job));
        Assert.Contains("number 7", AiTextRules.Check("SpongeBob SquarePants, then Rugrats at 7!", job));
        Assert.Contains("tomorrow", AiTextRules.Check("Tomorrow: SpongeBob SquarePants, then Rugrats at 6.", job));
        Assert.Contains("formatting", AiTextRules.Check("# SpongeBob SquarePants, then Rugrats at 6", job));
        Assert.Contains("ran long", AiTextRules.Check(string.Join(' ', Enumerable.Repeat("SpongeBob SquarePants and Rugrats", 12)), job));
        Assert.Contains("nothing", AiTextRules.Check("  ", job));
    }

    [Fact]
    public void A_time_given_may_be_written_any_usual_way_and_a_squashed_one_gets_its_colon_back()
    {
        AiTextJob job = UpNextJob() with
        {
            Facts = [new("Next", "SpongeBob SquarePants, right after this break"), new("Then", "Rugrats, at 2:30"), new("Day", "Today")],
            Fallback = "Coming up next... SpongeBob SquarePants. Then Rugrats at 2:30!"
        };

        foreach (string time in (string[])["2:30", "2.30", "230", "14:30", "2"])
        {
            Assert.Null(AiTextRules.Check($"SpongeBob SquarePants, then Rugrats at {time}!", job));
        }

        Assert.Contains("number 3:30", AiTextRules.Check("SpongeBob SquarePants, then Rugrats at 3:30!", job));
        Assert.Equal("SpongeBob SquarePants, then Rugrats at 2:30!", AiTextRules.Tidy("SpongeBob SquarePants, then Rugrats at 230!", job));
    }

    [Fact]
    public void The_request_carries_the_facts_style_and_limit_and_the_same_job_asks_the_same_way()
    {
        AiTextJob job = UpNextJob(new AiTextConfig { Style = "A pirate", Prompt = "Mention the weekend", MaxWords = 20 });

        TextGenerationRequest request = AiTextRules.Request(job);

        Assert.Contains("Style: A pirate", request.Instructions);
        Assert.Contains("at most 20 words", request.Instructions);
        Assert.Contains("- Then: Rugrats, at 6", request.Prompt);
        Assert.Contains("Mention the weekend", request.Prompt);
        Assert.Contains("The usual wording", request.Prompt);
        Assert.Equal(request.Seed, AiTextRules.Request(job).Seed);
        Assert.NotEqual(request.Seed, AiTextRules.Request(job, attempt: 1).Seed);
        Assert.NotEqual(request.Seed, AiTextRules.Request(job with { Seed = "another airing" }).Seed);
    }

    [Fact]
    public void How_often_the_model_writes_is_fixed_per_seed()
    {
        Assert.False(AiTextRules.Chosen(UpNextJob(new AiTextConfig { Chance = 0 })));
        Assert.True(AiTextRules.Chosen(UpNextJob(new AiTextConfig { Chance = 1 })));

        int chosen = Enumerable.Range(0, 400).Count(index => AiTextRules.Chosen(UpNextJob(new AiTextConfig { Chance = 0.5 }) with { Seed = $"airing {index}" }));
        Assert.InRange(chosen, 140, 260);
        AiTextJob half = UpNextJob(new AiTextConfig { Chance = 0.5 });
        Assert.Equal(AiTextRules.Chosen(half), AiTextRules.Chosen(half));
    }

    // --- The writer ---------------------------------------------------------------------------------

    [Fact]
    public async Task Good_writing_is_kept_and_reused_even_after_a_restart()
    {
        var generator = new FakeGenerator(_ => "Up next it's SpongeBob, then Rugrats at 6!");

        AiTextResult first = await Writer(generator).WriteAsync(UpNextJob());
        AiTextResult again = await Writer(generator).WriteAsync(UpNextJob());

        Assert.True(first.Written);
        Assert.Equal("Up next it's SpongeBob, then Rugrats at 6!", first.Text);
        Assert.Equal(first, again);
        Assert.Equal(1, generator.Calls);
    }

    [Fact]
    public async Task Writing_that_fails_the_checks_is_tried_twice_then_the_usual_wording_is_used_and_remembered()
    {
        var generator = new FakeGenerator(_ => "What a lovely evening for cartoons!");
        AiTextWriter writer = Writer(generator);

        AiTextResult result = await writer.WriteAsync(UpNextJob());
        await writer.WriteAsync(UpNextJob());

        Assert.False(result.Written);
        Assert.Equal(UpNextJob().Fallback, result.Text);
        Assert.Contains("left out", result.Problem);
        Assert.Equal(2, generator.Calls);
    }

    [Fact]
    public async Task Without_a_writer_or_when_it_errors_the_usual_wording_is_used_and_says_why()
    {
        AiTextResult none = await Writer().WriteAsync(UpNextJob());
        Assert.False(none.Written);
        Assert.Contains("no AI writer", none.Problem);

        AiTextResult broken = await Writer(new FakeGenerator(_ => throw new InvalidOperationException("model not loaded"))).WriteAsync(UpNextJob());
        Assert.Equal(UpNextJob().Fallback, broken.Text);
        Assert.Contains("model not loaded", broken.Problem);

        AiTextResult skipped = await Writer(new FakeGenerator(_ => "SpongeBob then Rugrats at 6")).WriteAsync(UpNextJob(new AiTextConfig { Chance = 0 }));
        Assert.False(skipped.Written);
        Assert.Null(skipped.Problem);
    }

    [Fact]
    public async Task Peek_answers_at_once_and_writes_in_the_background()
    {
        var generator = new FakeGenerator(_ => "SpongeBob, then Rugrats at 6. Stay tuned!");
        AiTextWriter writer = Writer(generator);
        var done = new TaskCompletionSource<AiTextResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        writer.Written += (_, result) => done.TrySetResult(result);

        AiTextResult now = writer.Peek(UpNextJob());
        Assert.False(now.Written);
        Assert.Equal(UpNextJob().Fallback, now.Text);

        AiTextResult later = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(later.Written);
        Assert.Equal(later.Text, writer.Peek(UpNextJob()).Text);
    }

    // --- Generated promos -----------------------------------------------------------------------------

    [Fact]
    public void A_promo_asks_for_its_voice_over_with_its_shows_repeats_and_episode_description()
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext();
        IReadOnlyList<UpNextListing> listings =
        [
            new("SpongeBob SquarePants", Day, "Help Wanted", Overview: "SpongeBob applies for a job at the Krusty Krab."),
            new("SpongeBob SquarePants", Day.AddMinutes(30)),
            new("Rugrats", Day.AddHours(1))
        ];

        ResolvedGeneratedPromo usual = GeneratedPromoResolver.Resolve(template, "The Zone", listings, TimeZoneInfo.Utc)!;
        Assert.Null(usual.ScriptJob);
        Assert.Empty(usual.LayerJobs);

        template.Voice!.Writer = new AiTextConfig { Style = "Excitable" };
        template.Layers.First(layer => layer.Kind == GeneratedLayerKind.Text).Writer = new AiTextConfig();
        ResolvedGeneratedPromo written = GeneratedPromoResolver.Resolve(template, "The Zone", listings, TimeZoneInfo.Utc)!;

        AiTextJob job = written.ScriptJob!;
        Assert.Equal(written.Script, job.Fallback);
        Assert.Equal(new[] { "SpongeBob SquarePants" }, job.MustSay);
        Assert.Contains(job.Facts, fact => fact.Value.Contains("the same show again", StringComparison.Ordinal));
        Assert.Contains(job.Facts, fact => fact.Value.Contains("Krusty Krab", StringComparison.Ordinal));
        Assert.Contains(job.Guidance, line => line.Contains("even more", StringComparison.Ordinal));
        Assert.Single(written.LayerJobs);
        Assert.NotEqual(usual.Key, written.Key);
    }

    [Fact]
    public async Task A_promo_is_rendered_with_the_AI_voice_over_in_place_of_the_usual_one()
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext();
        template.Voice!.Writer = new AiTextConfig();
        IReadOnlyList<UpNextListing> listings = [new("SpongeBob SquarePants", Day), new("SpongeBob SquarePants", Day.AddMinutes(30))];
        ResolvedGeneratedPromo promo = GeneratedPromoResolver.Resolve(template, "The Zone", listings, TimeZoneInfo.Utc)!;
        AiTextWriter writer = Writer(new FakeGenerator(_ => "Next up, SpongeBob SquarePants, then even more of it, right here!"));

        ResolvedGeneratedPromo ready = await GeneratedPromoWriting.WriteAsync(promo, writer, CancellationToken.None);

        Assert.Equal("Next up, SpongeBob SquarePants, then even more of it, right here!", ready.Script);
        Assert.Equal(promo.Key, ready.Key);
        Assert.Equal(promo, await GeneratedPromoWriting.WriteAsync(promo, null, CancellationToken.None));
    }

    // --- Local AI plugin -------------------------------------------------------------------------------

    [Fact]
    public void Model_choices_resolve_to_files_in_the_models_folder_or_your_own()
    {
        var paths = new LocalAiPaths(_folder);

        Assert.Equal(Path.Combine(_folder, "models", "Qwen3.5-2B-Q4_K_M.gguf"), paths.ModelPath(new LocalAiOptions()));
        Assert.Equal(Path.Combine(_folder, "elsewhere", "Qwen3.5-4B-Q4_K_M.gguf"), paths.ModelPath("qwen3.5-4b", null, "elsewhere"));
        Assert.Equal(Path.Combine(_folder, "mine.gguf"), paths.ModelPath(LocalAiModels.Custom, "mine.gguf", null));
        Assert.Null(paths.ModelPath(LocalAiModels.Custom, " ", null));
    }

    [Fact]
    public void The_engine_package_is_unpacked_for_this_computer_only()
    {
        Directory.CreateDirectory(_folder);
        string package = Path.Combine(_folder, "backend.nupkg");
        using (ZipArchive archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            foreach (string name in (string[])["LLamaSharpRuntimes/win-x64/native/avx2/llama.dll", "LLamaSharpRuntimes/win-x64/native/avx2/ggml.dll",
                         "LLamaSharpRuntimes/linux-x64/native/avx2/libllama.so", "build/netstandard2.0/LLamaSharp.Backend.Cpu.props"])
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write("x");
            }
        }

        string engine = Path.Combine(_folder, "engine");
        Assert.False(LlamaEngine.IsInstalled(engine));

        Assert.Equal(2, LocalAiDownloads.Unpack(package, "win-x64", engine));
        Assert.True(File.Exists(Path.Combine(engine, "runtimes", "win-x64", "native", "avx2", "llama.dll")));
        Assert.False(Directory.Exists(Path.Combine(engine, "runtimes", "linux-x64")));
        Assert.True(LlamaEngine.IsInstalled(engine));
    }

    [Fact]
    public async Task An_Ollama_server_is_asked_without_thinking_and_once_more_without_it_if_refused()
    {
        var handler = new RecordingHandler(request =>
            request.Body.Contains("\"think\"", StringComparison.Ordinal)
                ? (HttpStatusCode.BadRequest, """{"error":"model does not support think"}""")
                : (HttpStatusCode.OK, """{"message":{"role":"assistant","content":"Up next, SpongeBob!"}}"""));
        var server = new ServerTextGenerator(Options.Create(new LocalAiOptions { ServerModel = "qwen3.5:2b" }), new Clients(handler));

        string reply = await server.GenerateAsync(new TextGenerationRequest("Be brief.", "Promo SpongeBob.") { Seed = 5 });

        Assert.Equal("Up next, SpongeBob!", reply);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("http://localhost:11434/api/chat", handler.Requests[0].Url);
        JsonNode body = JsonNode.Parse(handler.Requests[1].Body)!;
        Assert.Equal("qwen3.5:2b", (string?)body["model"]);
        Assert.Equal(5, (int)body["options"]!["seed"]!);
        Assert.Equal("system", (string?)body["messages"]![0]!["role"]);
    }

    [Fact]
    public async Task An_OpenAI_style_server_gets_its_chat_completions_with_the_key()
    {
        var handler = new RecordingHandler(_ => (HttpStatusCode.OK, """{"choices":[{"message":{"content":"Rugrats, next!"}}]}"""));
        var settings = new LocalAiOptions { ServerKind = LocalAiOptions.OpenAiCompatible, ServerUrl = "http://box:1234/v1/", ServerModel = "qwen", ServerApiKey = "secret" };
        var server = new ServerTextGenerator(Options.Create(settings), new Clients(handler));

        Assert.Equal("Rugrats, next!", await server.GenerateAsync(new TextGenerationRequest("Be brief.", "Promo Rugrats.")));
        Assert.Equal("http://box:1234/v1/chat/completions", handler.Requests[0].Url);
        Assert.Equal("Bearer secret", handler.Requests[0].Authorization);
        Assert.Null(new ServerTextGenerator(Options.Create(settings), new Clients(handler)).SetupProblem);
        Assert.NotNull(new ServerTextGenerator(Options.Create(new LocalAiOptions()), new Clients(handler)).SetupProblem);
    }

    private sealed record Sent(string Url, string Body, string? Authorization);

    private sealed class RecordingHandler(Func<Sent, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var sent = new Sent(request.RequestUri!.ToString(),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.Authorization?.ToString());
            Requests.Add(sent);
            (HttpStatusCode status, string body) = answer(sent);
            return new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
