namespace SchedularrTV.Service.Editor;

/// <summary>
/// Reads the library shortly after the scheduler starts, so the library pages and the channel wizard open on it
/// rather than waiting minutes for a large library the first time one is visited.
/// </summary>
public sealed class LibraryWarmup(LibraryBrowser library) : BackgroundService
{
    // Long enough for startup (plugins, the first schedule build) to get going first.
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(Delay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        library.WarmUp();
    }
}
