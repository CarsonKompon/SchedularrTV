namespace SchedularrTV.Plugins.Speech;

public sealed record TtsVoice(string Id, string Name, string Language = "English", string? Description = null);

/// <summary>Encoded speech plus the file extension ffmpeg should read it as.</summary>
public sealed record TtsAudio(byte[] Data, string Extension);

/// <summary>
/// A text-to-speech backend selectable by <see cref="Id"/> from a promo template. Register one with
/// <see cref="PluginServiceCollectionExtensions.AddSpeechProvider{T}"/>.
/// </summary>
public interface ITextToSpeechProvider
{
    /// <summary>Stored in templates, so it must never change once released.</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Model and audio settings; changing them must not reuse voice-overs made with the old ones.</summary>
    string CacheIdentity => string.Empty;

    /// <summary>Why the provider cannot be used right now, such as a missing API key; null when it can.</summary>
    string? SetupProblem => null;

    Task<IReadOnlyList<TtsVoice>> GetVoicesAsync(CancellationToken cancellationToken = default);

    Task<TtsAudio> SynthesizeAsync(string text, string voice, CancellationToken cancellationToken = default);
}
