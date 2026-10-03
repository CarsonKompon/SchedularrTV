using Microsoft.AspNetCore.Builder;
using SchedularrTV.Service;
using SchedularrTV.Service.Hosting;

// Saving app settings restarts the host in place, so the new connection details are picked up
// without the process (or the tray watching it) having to start again.
while (true)
{
    WebApplication app = SchedulerHost.Create(args);
    await using (app)
    {
        await app.RunAsync();
        if (!app.Services.GetRequiredService<AppRuntime>().RestartRequested)
        {
            break;
        }
    }
}

/// <summary>Exposed so integration tests can use WebApplicationFactory.</summary>
public partial class Program;
