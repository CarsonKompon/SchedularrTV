using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SchedularrTV.Speech.Piper;

/// <summary>Piper: free voices that run offline on this machine.</summary>
public sealed class PiperSpeechPlugin : IPlugin
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        // Before it was a plugin these lived under "Piper" in the app settings, so that's still read; the plugin's
        // own settings (from the Plugins page) win. Relative paths are taken from the data folder.
        services.AddOptions<PiperOptions>()
            .Bind(context.AppConfiguration.GetSection(PiperOptions.SectionName))
            .Bind(context.Configuration)
            .PostConfigure(options =>
            {
                options.ExecutablePath = Rooted(context.AppDataDirectory, options.ExecutablePath);
                options.VoicesPath = Rooted(context.AppDataDirectory, options.VoicesPath);
            });
        services.AddSingleton(new PiperPaths(context.AppDataDirectory));
        services.AddSpeechProvider<PiperTextToSpeech>();
    }

    private static string Rooted(string dataDirectory, string path) =>
        string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(dataDirectory, path));
}
