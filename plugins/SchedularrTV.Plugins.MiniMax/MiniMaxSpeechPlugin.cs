using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SchedularrTV.Speech.MiniMax;

/// <summary>Paid, natural-sounding MiniMax voices, including the account's own cloned and designed ones.</summary>
public sealed class MiniMaxSpeechPlugin : IPlugin
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        // Before it was a plugin the key lived under "MiniMax" in the app settings, so that's still read; the
        // plugin's own settings (from the Plugins page) win, and the environment variable is the last resort.
        services.AddOptions<MiniMaxOptions>()
            .Bind(context.AppConfiguration.GetSection(MiniMaxOptions.SectionName))
            .Bind(context.Configuration)
            .PostConfigure(minimax =>
            {
                if (string.IsNullOrWhiteSpace(minimax.ApiKey))
                {
                    minimax.ApiKey = context.AppConfiguration[MiniMaxTextToSpeech.KeyVariable];
                }
            });
        services.AddHttpClient(MiniMaxTextToSpeech.ProviderId, http => http.Timeout = TimeSpan.FromSeconds(60));
        services.AddSpeechProvider<MiniMaxTextToSpeech>();
    }
}
