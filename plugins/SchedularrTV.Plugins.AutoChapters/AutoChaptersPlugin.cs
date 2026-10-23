using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace SchedularrTV.AutoChapters;

/// <summary>Chapter markers from fades to black, for programmes without any, so mid-roll breaks land where breaks were.</summary>
public sealed class AutoChaptersPlugin : IPlugin, IPluginEndpoints
{
    public const string ApiPrefix = "/api/auto-chapters";

    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton(new ChapterStore(context.DataDirectory));
        services.AddSingleton(new AutoChaptersSettings(context));
        services.AddSingleton<ChapterScanner>();
        services.AddHostedService(sp => sp.GetRequiredService<ChapterScanner>());
        services.AddMediaEnricher<ChapterEnricher>();
        services.AddPlayoutProcessor<UpcomingProgrammes>();

        // The review queue: playing programmes back, and writing approved markers into their files.
        services.AddSingleton<ReviewMedia>();
        services.AddSingleton<ChapterWriter>();

        // Slides mid-roll breaks onto the break points found, as the schedule goes out (see MidRollAligner). One
        // instance, so the status page reads what it last did.
        services.AddSingleton<MidRollAligner>();
        services.AddSingleton<SchedularrTV.Plugins.Playout.IPlayoutProcessor>(provider => provider.GetRequiredService<MidRollAligner>());
    }

    /// <summary>
    /// The review player's video: the file as it is (for browsers that can play it), a converted preview from any point
    /// (for those that can't) and single frames. Files are only ever found by the id of an analysed programme.
    /// </summary>
    public void MapEndpoints(IEndpointRouteBuilder endpoints, IPluginContext context)
    {
        endpoints.MapGet(ApiPrefix + "/review/{key}/video", (string key, ChapterStore store, AutoChaptersSettings settings) =>
        {
            if (Resolve(key, store, settings) is not { } file)
            {
                return Results.NotFound();
            }

            // Opened so the file can still be replaced while it's playing (when chapters are written into it).
            var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous);
            string type = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".webm" or ".mkv" => "video/webm",
                ".mov" => "video/quicktime",
                _ => "video/mp4"
            };
            return Results.Stream(stream, type, enableRangeProcessing: true);
        });

        endpoints.MapGet(ApiPrefix + "/review/{key}/stream", async (string key, string? start, HttpContext http, ChapterStore store, AutoChaptersSettings settings,
            ReviewMedia media) =>
        {
            if (Resolve(key, store, settings) is not { } file)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await media.StreamAsync(file, Number(start), http);
        });

        endpoints.MapGet(ApiPrefix + "/review/{key}/frame", async (string key, string? t, int? h, ChapterStore store, AutoChaptersSettings settings,
            ReviewMedia media, HttpContext http) =>
        {
            if (Resolve(key, store, settings) is not { } file)
            {
                return Results.NotFound();
            }

            byte[]? jpeg = await media.FrameAsync(file, Number(t), h ?? 360, http.RequestAborted);
            if (jpeg is null)
            {
                return Results.NotFound();
            }

            http.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.File(jpeg, "image/jpeg");
        });
    }

    private static string? Resolve(string key, ChapterStore store, AutoChaptersSettings settings) =>
        store.FindByKey(key) is { } record ? settings.LocalPath(record.Path) : null;

    private static double Number(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? Math.Max(0, value) : 0;
}
