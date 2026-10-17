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

/// <summary>One stretch of speech in a recording: when it's said, and what.</summary>
public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);

/// <summary>What's said in a recording, in time order.</summary>
public sealed record Transcript(IReadOnlyList<TranscriptSegment> Segments, string? Language = null);

/// <summary>
/// Speech recognition (Whisper and the like): writes down what's said in an episode, so promos can pick out its
/// moments. Register one with <see cref="PluginServiceCollectionExtensions.AddSpeechTranscriber{T}"/>.
/// </summary>
public interface ISpeechTranscriber
{
    /// <summary>Stored in templates, so it must never change once released.</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>The model and its settings; changing them must not reuse transcripts made with the old ones.</summary>
    string CacheIdentity => string.Empty;

    /// <summary>Why it can't transcribe right now, such as a model not downloaded yet; null when it can.</summary>
    string? SetupProblem => null;

    /// <summary>
    /// Transcribes <paramref name="wavePath"/>: a 16 kHz, mono, 16-bit PCM WAV file the host has made from the episode.
    /// Segment times are from the start of that file.
    /// </summary>
    Task<Transcript> TranscribeAsync(string wavePath, CancellationToken cancellationToken = default);
}
