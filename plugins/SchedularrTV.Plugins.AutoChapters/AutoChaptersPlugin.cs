namespace SchedularrTV.AutoChapters;

/// <summary>Chapter markers from fades to black, for programmes without any, so mid-roll breaks land where breaks were.</summary>
public sealed class AutoChaptersPlugin : IPlugin
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton(new ChapterStore(context.DataDirectory));
        services.AddSingleton(new AutoChaptersSettings(context));
        services.AddSingleton<ChapterScanner>();
        services.AddHostedService(sp => sp.GetRequiredService<ChapterScanner>());
        services.AddMediaEnricher<ChapterEnricher>();
        services.AddPlayoutProcessor<UpcomingProgrammes>();
    }
}
