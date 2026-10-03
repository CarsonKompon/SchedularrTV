using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Plugins.Playout;

namespace SchedularrTV.Plugins.Sample;

/// <summary>The entry point: everything the plugin adds is registered here.</summary>
public sealed class HelloPlugin : IPlugin
{
    public const string Id = "sample.hello";

    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        // Settings from the Plugins page arrive as configuration under Plugins:sample.hello.
        services.AddSingleton(new HelloSettings(context.Configuration["Greeting"] ?? "Hello from a plugin!"));
        services.AddPlayoutProcessor<ScreenBugProcessor>();
    }
}

public sealed record HelloSettings(string Greeting);

/// <summary>
/// Per-channel settings, read from the schedule YAML:
/// <code>
/// channels:
///   - id: ch1
///     plugins:
///       sample.hello:
///         bug: D:\Media\Logos\ch1.png
/// </code>
/// </summary>
public sealed class ScreenBugSettings
{
    public string? Bug { get; set; }
}

/// <summary>
/// Tags every programme with the channel's screen bug. An output that can burn in overlays would read the
/// <c>sample.hello.bug</c> annotation; ErsatzTV Next ignores it, so this is safe to run as-is.
/// </summary>
public sealed class ScreenBugProcessor : IPlayoutProcessor
{
    public string Name => "Sample screen bug";

    public ValueTask<ChannelSchedule> ProcessAsync(PlayoutContext context, CancellationToken cancellationToken)
    {
        ChannelSchedule schedule = context.Schedule;
        List<ScheduleEntry> entries = [];
        bool changed = false;
        foreach (ScheduleEntry entry in schedule.Entries)
        {
            // Block settings override the channel's, which override settings.plugins.
            if (entry.Kind == ScheduleEntryKind.Program && context.Settings<ScreenBugSettings>(HelloPlugin.Id, entry)?.Bug is { Length: > 0 } bug)
            {
                Dictionary<string, string> annotations = entry.Annotations is null ? [] : new(entry.Annotations);
                annotations[$"{HelloPlugin.Id}.bug"] = bug;
                entries.Add(entry with { Annotations = annotations });
                changed = true;
            }
            else
            {
                entries.Add(entry);
            }
        }

        return ValueTask.FromResult(changed ? schedule with { Entries = entries } : schedule);
    }
}
