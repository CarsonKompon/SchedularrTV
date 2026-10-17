using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace SchedularrTV.Writing.LocalAi;

/// <summary>The built-in writer: a small model run in this process, downloaded from the plugin's settings.</summary>
public sealed class LlamaTextGenerator(IOptions<LocalAiOptions> options, LocalAiPaths paths, LlamaEngine engine) : ITextGenerator
{
    public const string GeneratorId = "local-ai";

    // Read on each use: settings saved on the Plugins page apply straight away.
    private LocalAiOptions Options => options.Value;

    public string Id => GeneratorId;

    public string DisplayName => "Local AI (built in)";

    public string CacheIdentity => paths.ModelPath(Options) is { } model && File.Exists(model)
        ? $"{Path.GetFileName(model)}|{new FileInfo(model).Length}"
        : string.Empty;

    public string? SetupProblem
    {
        get
        {
            if (LocalAiPaths.RuntimeId() is null)
            {
                return "The built-in AI can't run on this kind of computer; use an Ollama or LM Studio server instead.";
            }

            if (!LlamaEngine.IsInstalled(paths.Engine))
            {
                return "The AI engine isn't downloaded yet (Local AI writer's settings on the Plugins page).";
            }

            string? model = paths.ModelPath(Options);
            return model is null ? "No model file is chosen (Local AI writer's settings on the Plugins page)."
                : !File.Exists(model) ? $"The AI model isn't downloaded yet: {Path.GetFileName(model)} (Local AI writer's settings on the Plugins page)."
                : null;
        }
    }

    public async Task<string> GenerateAsync(TextGenerationRequest request, CancellationToken cancellationToken = default)
    {
        if (SetupProblem is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        LocalAiOptions settings = Options;
        LlamaEngine.UseEngine(paths.Engine, settings.UseGpu ? paths.GpuEngine : null);
        return await engine.GenerateAsync(paths.ModelPath(settings)!, settings.Threads, TimeSpan.FromMinutes(Math.Max(1, settings.UnloadAfterMinutes)),
            request, cancellationToken, settings.UseGpu).ConfigureAwait(false);
    }
}

/// <summary>A writer on a model server: Ollama, or anything with an OpenAI-style chat API (LM Studio, llama.cpp's server…).</summary>
public sealed class ServerTextGenerator(IOptions<LocalAiOptions> options, IHttpClientFactory clients) : ITextGenerator
{
    public const string GeneratorId = "local-ai-server";

    private LocalAiOptions Options => options.Value;

    public string Id => GeneratorId;

    public string DisplayName => $"Local AI server ({(Options.IsOllama ? "Ollama" : "OpenAI-compatible")})";

    public string CacheIdentity => $"{Options.ServerAddress}|{Options.ServerModel?.Trim()}";

    public string? SetupProblem => string.IsNullOrWhiteSpace(Options.ServerModel)
        ? "No server model is named (Local AI writer's settings on the Plugins page)."
        : null;

    public async Task<string> GenerateAsync(TextGenerationRequest request, CancellationToken cancellationToken = default)
    {
        LocalAiOptions settings = Options;
        if (SetupProblem is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.ServerTimeoutSeconds, 10, 900)));
        HttpClient http = clients.CreateClient(GeneratorId);
        http.Timeout = Timeout.InfiniteTimeSpan;

        var messages = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = request.Instructions },
            new JsonObject { ["role"] = "user", ["content"] = request.Prompt });

        try
        {
            if (settings.IsOllama)
            {
                var body = new JsonObject
                {
                    ["model"] = settings.ServerModel!.Trim(),
                    ["messages"] = messages,
                    ["stream"] = false,
                    ["think"] = false,
                    ["options"] = Sampling(request, ollama: true)
                };
                JsonNode reply = await PostAsync(http, settings, "/api/chat", body, timeout.Token, retryWithout: "think").ConfigureAwait(false);
                return reply["message"]?["content"]?.GetValue<string>() ?? string.Empty;
            }
            else
            {
                var body = new JsonObject
                {
                    ["model"] = settings.ServerModel!.Trim(),
                    ["messages"] = messages,
                    ["stream"] = false
                };
                foreach ((string key, JsonNode? value) in Sampling(request, ollama: false))
                {
                    body[key] = value?.DeepClone();
                }

                JsonNode reply = await PostAsync(http, settings, OpenAiPath(settings, "chat/completions"), body, timeout.Token, retryWithout: "seed").ConfigureAwait(false);
                return reply["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? string.Empty;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The AI server at {settings.ServerAddress} didn't answer within {settings.ServerTimeoutSeconds} seconds.");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Couldn't reach the AI server at {settings.ServerAddress}: {ex.Message}", ex);
        }
    }

    /// <summary>The models the server offers, for picking one in the settings.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(LocalAiOptions settings, CancellationToken cancellationToken)
    {
        HttpClient http = clients.CreateClient(GeneratorId);
        http.Timeout = TimeSpan.FromSeconds(15);
        using var message = new HttpRequestMessage(HttpMethod.Get, settings.ServerAddress + (settings.IsOllama ? "/api/tags" : OpenAiPath(settings, "models")));
        Authorize(message, settings);
        using HttpResponseMessage response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        JsonNode? reply = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false);
        JsonArray items = (settings.IsOllama ? reply?["models"] : reply?["data"]) as JsonArray ?? new JsonArray();
        return [.. items
            .Select(item => (settings.IsOllama ? item?["name"] : item?["id"])?.GetValue<string>())
            .OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    private static JsonObject Sampling(TextGenerationRequest request, bool ollama)
    {
        var sampling = new JsonObject
        {
            ["temperature"] = Math.Clamp(request.Temperature, 0, 2),
            [ollama ? "num_predict" : "max_tokens"] = Math.Clamp(request.MaxTokens, 8, 2048)
        };
        if (request.Seed is { } seed)
        {
            sampling["seed"] = seed;
        }

        return sampling;
    }

    /// <summary>An OpenAI-style path, whether or not the address already ends in <c>/v1</c>.</summary>
    private static string OpenAiPath(LocalAiOptions settings, string path) =>
        settings.ServerAddress.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? $"/{path}" : $"/v1/{path}";

    private static void Authorize(HttpRequestMessage message, LocalAiOptions settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ServerApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ServerApiKey.Trim());
        }
    }

    /// <summary>Posts, and once more without <paramref name="retryWithout"/> if the server turns that option down.</summary>
    private static async Task<JsonNode> PostAsync(HttpClient http, LocalAiOptions settings, string path, JsonObject body,
        CancellationToken cancellationToken, string retryWithout)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, settings.ServerAddress + path)
            {
                Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json")
            };
            Authorize(message, settings);
            using HttpResponseMessage response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return JsonNode.Parse(text) ?? throw new InvalidOperationException("The AI server sent an empty answer.");
            }

            // Older servers, and models that can't think, may refuse an option this sends.
            if (attempt == 0 && (int)response.StatusCode is 400 or 422 && text.Contains(retryWithout, StringComparison.OrdinalIgnoreCase)
                && body.Remove(retryWithout))
            {
                continue;
            }

            throw new InvalidOperationException($"The AI server answered {(int)response.StatusCode}: {Shorten(ErrorText(text))}");
        }
    }

    private static string ErrorText(string text)
    {
        try
        {
            JsonNode? json = JsonNode.Parse(text);
            JsonNode? error = json?["error"];
            return (error is JsonObject ? error["message"]?.GetValue<string>() : error?.GetValue<string>()) ?? text;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return text;
        }
    }

    private static string Shorten(string text) => text.Length > 300 ? text[..300] + "…" : text;
}
