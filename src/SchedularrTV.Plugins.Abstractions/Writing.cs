namespace SchedularrTV.Plugins.Writing;

/// <summary>
/// One piece of writing asked of a language model: <paramref name="Instructions"/> say who it is and the rules it
/// follows (a chat model's system message), <paramref name="Prompt"/> what to write this time.
/// </summary>
public sealed record TextGenerationRequest(string Instructions, string Prompt)
{
    /// <summary>The most it may write, in the model's tokens (a word is one or two).</summary>
    public int MaxTokens { get; init; } = 160;

    /// <summary>0 always picks the likeliest word; around 0.8 reads naturally and varies.</summary>
    public double Temperature { get; init; } = 0.8;

    /// <summary>Fixes the random choices, so the same request writes the same thing; null varies every time.</summary>
    public int? Seed { get; init; }
}

/// <summary>
/// A language model that writes promo voice-overs, on-screen text and the like, selectable by <see cref="Id"/>.
/// Register one with <see cref="PluginServiceCollectionExtensions.AddTextGenerator{T}"/>.
/// </summary>
public interface ITextGenerator
{
    /// <summary>Stored in templates, so it must never change once released.</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>The model and its settings; changing them must not reuse writing done with the old ones.</summary>
    string CacheIdentity => string.Empty;

    /// <summary>Why it can't write right now, such as a model not downloaded yet; null when it can.</summary>
    string? SetupProblem => null;

    /// <summary>The model's reply, as written: callers clean it up and check it.</summary>
    Task<string> GenerateAsync(TextGenerationRequest request, CancellationToken cancellationToken = default);
}
