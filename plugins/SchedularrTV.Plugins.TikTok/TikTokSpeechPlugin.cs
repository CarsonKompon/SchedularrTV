using Microsoft.Extensions.DependencyInjection;

namespace SchedularrTV.Speech.TikTok;

/// <summary>Free TikTok voices through public proxies.</summary>
public sealed class TikTokSpeechPlugin : IPlugin
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.AddHttpClient(TikTokTextToSpeech.ProviderId, http => http.Timeout = TimeSpan.FromSeconds(30));
        services.AddSpeechProvider<TikTokTextToSpeech>();
    }
}
