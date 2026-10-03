namespace SchedularrTV.GeneratedPromos;

// TtsVoice, TtsAudio and ITextToSpeechProvider are part of the plugin contract (SchedularrTV.Plugins.Speech); the
// voices themselves come from the voice plugins (TikTok, MiniMax, Piper or any other).

public sealed class TextToSpeechCatalog(IEnumerable<ITextToSpeechProvider> providers)
{
    // Built on each use, not kept: the scheduler leaves out voice plugins that are switched off while it runs.
    // A plugin reusing another's id must not stop the app starting; the first registered wins.
    private Dictionary<string, ITextToSpeechProvider> _providers => providers
        .GroupBy(provider => provider.Id, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ITextToSpeechProvider> Providers => _providers.Values;

    public ITextToSpeechProvider Get(string id)
    {
        Dictionary<string, ITextToSpeechProvider> available = _providers;
        return available.TryGetValue(id, out ITextToSpeechProvider? provider)
            ? provider
            : throw new InvalidOperationException(available.Count == 0
                ? $"No text-to-speech provider '{id}': no voice plugins are installed and enabled (see Plugins)."
                : $"No text-to-speech provider '{id}'; its plugin may be missing or disabled. Available: {string.Join(", ", available.Keys)}.");
    }
}
