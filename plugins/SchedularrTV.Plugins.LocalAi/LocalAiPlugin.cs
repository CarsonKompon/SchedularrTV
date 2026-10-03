using Microsoft.Extensions.DependencyInjection;

namespace SchedularrTV.Writing.LocalAi;

/// <summary>
/// Free language models for writing voice-overs and on-screen text: one built in (downloaded from the settings) and,
/// optionally, one on an Ollama or LM Studio server.
/// </summary>
public sealed class LocalAiPlugin : IPlugin
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.AddOptions<LocalAiOptions>().Bind(context.Configuration);
        services.AddSingleton(new LocalAiPaths(context.DataDirectory));
        services.AddSingleton<LocalAiDownloads>();
        services.AddSingleton<LlamaEngine>();
        services.AddHttpClient(ServerTextGenerator.GeneratorId);

        // Resolvable on their own for the settings panel, and listed as writers for everything else.
        services.AddSingleton<LlamaTextGenerator>();
        services.AddSingleton<ServerTextGenerator>();
        services.AddSingleton<ITextGenerator>(provider => provider.GetRequiredService<LlamaTextGenerator>());
        services.AddSingleton<ITextGenerator>(provider => provider.GetRequiredService<ServerTextGenerator>());
    }
}
