using System.Text.RegularExpressions;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Query;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Core.Temporal;
using SchedularrTV.Service.Editor;

namespace SchedularrTV.Service;

/// <summary>DTOs shaped for a UI: flat, JSON-friendly, and free of engine internals.</summary>
public sealed record MediaItemDto(
    string Id,
    string Title,
    string Kind,
    double DurationSeconds,
    string? SeriesName,
    int? Season,
    int? Episode,
    DateTimeOffset? PremiereDate,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Collections,
    string? VersionName,
    string? VersionGroupId);

public sealed record ScheduleEntryDto(
    DateTimeOffset Start,
    DateTimeOffset End,
    double DurationSeconds,
    string Kind,
    string Source,
    string? BlockName,
    string? Title,
    string? SeriesName,
    int? Season,
    int? Episode,
    string? ItemId,
    bool IsTrimmed,
    bool StopBeforeEnd,
    double? SourceDurationSeconds,
    double InPointSeconds,
    int SegmentNumber,
    int SegmentCount,
    string? PromoTargetId,
    string? GeneratedTemplate = null);

public sealed record ChannelScheduleDto(
    string ChannelId,
    string ChannelName,
    int ChannelNumber,
    DateTimeOffset Start,
    DateTimeOffset End,
    double PaddingSeconds,
    IReadOnlyList<ScheduleEntryDto> Entries,
    IReadOnlyList<ScheduleDiagnostic> Diagnostics);

public sealed record PreviewRequest(string? Yaml, string ChannelId, DateTimeOffset? Start, double? Hours);

public sealed record QueryTestRequest(string Query, int? Limit);

public static class ApiMappings
{
    public static MediaItemDto ToDto(this MediaItem item) => new(
        item.Id,
        item.Title,
        item.Kind.ToString(),
        item.Duration.TotalSeconds,
        item.SeriesName,
        item.SeasonNumber,
        item.EpisodeNumber,
        item.PremiereDate,
        item.Tags,
        item.Collections,
        item.VersionName,
        item.VersionGroupId);

    public static ScheduleEntryDto ToDto(this ScheduleEntry entry) => new(
        entry.Start,
        entry.End,
        entry.Duration.TotalSeconds,
        entry.Kind.ToString(),
        entry.Source,
        entry.BlockName,
        entry.Item?.Title,
        entry.Item?.SeriesName,
        entry.Item?.SeasonNumber,
        entry.Item?.EpisodeNumber,
        entry.Item?.Id,
        entry.IsTrimmed,
        entry.StopBeforeEnd,
        entry.SourceDuration?.TotalSeconds,
        entry.InPoint.TotalSeconds,
        entry.SegmentNumber,
        entry.SegmentCount,
        entry.PromoTargetId,
        entry.GeneratedTemplate);

    public static ChannelScheduleDto ToDto(this ChannelSchedule schedule) => new(
        schedule.ChannelId,
        schedule.ChannelName,
        schedule.ChannelNumber,
        schedule.Start,
        schedule.End,
        schedule.TotalPadding.TotalSeconds,
        schedule.Entries.Select(ToDto).ToList(),
        schedule.Diagnostics);
}

public static partial class ApiEndpoints
{
    public static void MapSchedulerApi(this WebApplication app)
    {
        RouteGroupBuilder api = app.MapGroup("/api");

        api.MapGet("/health", (ScheduleCoordinator coordinator, SchedulerConfigProvider config, Hosting.AppRuntime runtime) => Results.Ok(new
        {
            status = config.Validation.IsValid ? "ok" : "config-invalid",
            instance = runtime.InstanceId,
            lastBuiltAt = coordinator.LastBuiltAt,
            channels = config.Current.Channels.Count,
            errors = config.Validation.Errors,
            warnings = config.Validation.Warnings
        }));

        // What SchedularrTV, ErsatzTV and their helpers are using of the computer now, what to change, and the 24/7 channels.
        api.MapGet("/performance", (Performance.ResourceMonitor monitor, AlwaysOnKeeper alwaysOn) => Results.Ok(new
        {
            latest = monitor.Latest,
            advice = monitor.Advice(),
            gpuProblem = monitor.GpuProblem,
            alwaysOn = alwaysOn.Statuses
        }));

        // Lets the tray stop its server cleanly (ErsatzTV and ffmpeg children included) instead of killing it.
        api.MapPost("/app/shutdown", (HttpContext http, Hosting.AppRuntime runtime) =>
        {
            if (!Hosting.AppRuntime.IsControlToken(http.Request.Headers[SchedularrTV.Hosting.AppPaths.ControlTokenHeader]))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            runtime.Shutdown();
            return Results.Accepted();
        });

        MapConfigEndpoints(api);
        MapLibraryEndpoints(api);
        MapScheduleEndpoints(api);
        MapMetadataEndpoints(api);
    }

    private static void MapConfigEndpoints(RouteGroupBuilder api)
    {
        RouteGroupBuilder group = api.MapGroup("/config");

        group.MapGet("/", (SchedulerConfigProvider provider, LogoStore logos) => Results.Ok(new
        {
            path = provider.ConfigPath,
            logoPath = logos.Root,
            yaml = provider.ReadRaw(),
            config = provider.Current,
            validation = provider.Validation
        }));

        group.MapPost("/validate", (RawYaml body) =>
        {
            try
            {
                SchedulerConfig parsed = SchedulerConfigLoader.Parse(body.Yaml);
                return Results.Ok(SchedulerConfigLoader.Validate(parsed));
            }
            catch (Exception ex) when (ex is SchedulerConfigException or YamlDotNet.Core.YamlException or QueryParseException)
            {
                return Results.Ok(new ConfigValidationResult(false, [ex.Message], []));
            }
        });

        group.MapPut("/", (RawYaml body, SchedulerConfigProvider provider) =>
        {
            try
            {
                ConfigValidationResult result = provider.Save(body.Yaml);
                return result.IsValid ? Results.Ok(result) : Results.BadRequest(result);
            }
            catch (Exception ex) when (ex is SchedulerConfigException or YamlDotNet.Core.YamlException or QueryParseException)
            {
                return Results.BadRequest(new ConfigValidationResult(false, [ex.Message], []));
            }
        }).RequireApiKey();

        // Round-trips the object model back to YAML so an editor can build config structurally.
        group.MapPost("/serialize", (SchedulerConfig config) => Results.Text(SchedulerConfigLoader.Serialize(config), "text/yaml"));

        group.MapPost("/reload", (SchedulerConfigProvider provider) =>
        {
            provider.Reload();
            return Results.Ok(provider.Validation);
        }).RequireApiKey();

        // Earlier versions of the file, newest first; one is put aside before every change.
        group.MapGet("/backups", (SchedulerConfigProvider provider) => Results.Ok(provider.Backups.List()));

        group.MapGet("/backups/{id}", (string id, SchedulerConfigProvider provider) =>
            provider.Backups.Find(id) is { } backup && provider.Backups.Read(id) is { } yaml
                ? Results.Ok(new { backup, yaml })
                : Results.NotFound());

        group.MapPost("/backups/{id}/restore", (string id, SchedulerConfigProvider provider, ConfigEditor editor) =>
        {
            try
            {
                ConfigValidationResult? result = provider.Restore(id);
                if (result is null)
                {
                    return Results.NotFound();
                }

                if (!result.IsValid)
                {
                    return Results.BadRequest(result);
                }

                // The web editor shows what's on disk now (unsaved edits there were made against the old version).
                editor.Reload();
                return Results.Ok(result);
            }
            catch (Exception ex) when (ex is SchedulerConfigException or YamlDotNet.Core.YamlException or QueryParseException)
            {
                return Results.BadRequest(new ConfigValidationResult(false, [ex.Message], []));
            }
        }).RequireApiKey();
    }

    private static void MapLibraryEndpoints(RouteGroupBuilder api)
    {
        RouteGroupBuilder group = api.MapGroup("/library");

        group.MapGet("/search", async (string query, int? limit, IMediaLibrary library, CancellationToken ct) =>
        {
            if (!QueryParser.TryParse(query, out QueryExpression expression, out string? error))
            {
                return Results.BadRequest(new { error });
            }

            IReadOnlyList<MediaItem> items = await library.SearchAsync(expression, ct);
            return Results.Ok(new
            {
                total = items.Count,
                normalizedQuery = expression.ToQueryString(),
                items = items.Take(Math.Clamp(limit ?? 100, 1, 1000)).Select(i => i.ToDto())
            });
        });

        group.MapPost("/query/parse", (QueryTestRequest request) =>
            QueryParser.TryParse(request.Query, out QueryExpression expression, out string? error)
                ? Results.Ok(new { valid = true, normalized = expression.ToQueryString(), ast = expression })
                : Results.Ok(new { valid = false, error }));

        group.MapPost("/refresh", async (IMediaLibrary library, CancellationToken ct) =>
        {
            await library.RefreshAsync(ct);
            return Results.Ok(new { refreshed = true });
        }).RequireApiKey();

        // Only ids a running media source owns and a fixed set of image types are looked up: from the media server, the
        // pictures beside local files, or a library plugin.
        group.MapGet("/artwork/{id}", async (string id, string? type, int? w, [Microsoft.AspNetCore.Mvc.FromServices] IEnumerable<IArtworkSource> sources, HttpContext http,
            CancellationToken ct) =>
        {
            string imageType = type ?? ArtworkTypes.Primary;
            if (id.Length > 256 || !sources.Any(source => source.Owns(id)) || !ArtworkLinks.ImageTypes.Contains(imageType))
            {
                return Results.NotFound();
            }

            if (await ArtworkTypes.FindAsync(sources, id, imageType, Math.Clamp(w ?? 300, 32, 1280), ct) is not { } image)
            {
                return Results.NotFound();
            }

            http.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.File(image.Content, image.ContentType);
        });
    }

    private static void MapScheduleEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/schedules", (ScheduleCoordinator coordinator) =>
            Results.Ok(coordinator.Latest.Select(s => s.ToDto())));

        api.MapGet("/schedules/{channelId}", (string channelId, ScheduleCoordinator coordinator) =>
        {
            ChannelSchedule? schedule = coordinator.Latest
                .FirstOrDefault(s => string.Equals(s.ChannelId, channelId, StringComparison.OrdinalIgnoreCase));

            return schedule is null ? Results.NotFound() : Results.Ok(schedule.ToDto());
        });

        api.MapPost("/rebuild", async (ScheduleCoordinator coordinator, bool? publish, CancellationToken ct) =>
        {
            IReadOnlyList<ChannelSchedule> schedules = await coordinator.RebuildAsync(publish ?? true, replan: true, ct);
            return Results.Ok(schedules.Select(s => s.ToDto()));
        }).RequireApiKey();

        // /api/generated-promos/files/... is mapped by the Generated promos plugin.

        api.MapPost("/channels/{channelId}/reset-rotation", async (
            string channelId,
            IPlaybackStateStore state,
            ScheduleCoordinator coordinator,
            CancellationToken ct) =>
        {
            // Keys are "{channelId}|{scope}|{identity}", so the channel prefix covers every rule on it.
            await state.ResetAsync($"{channelId}|", ct);
            await coordinator.RebuildAsync(publish: true, replan: true, ct);
            return Results.Ok(new { channelId, reset = true });
        }).RequireApiKey();

        api.MapPost("/preview", async (
            PreviewRequest request,
            SchedulerConfigProvider provider,
            ScheduleCoordinator coordinator,
            TimeProvider time,
            CancellationToken ct) =>
        {
            SchedulerConfig config;
            try
            {
                config = string.IsNullOrWhiteSpace(request.Yaml)
                    ? provider.Current
                    : SchedulerConfigLoader.Parse(request.Yaml);
            }
            catch (Exception ex) when (ex is SchedulerConfigException or YamlDotNet.Core.YamlException or QueryParseException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            ChannelConfig? channel = config.Channels
                .FirstOrDefault(c => string.Equals(c.Id, request.ChannelId, StringComparison.OrdinalIgnoreCase));

            if (channel is null)
            {
                return Results.NotFound(new { error = $"Unknown channel '{request.ChannelId}'." });
            }

            DateTimeOffset start = request.Start ?? time.GetUtcNow();
            DateTimeOffset end = start + TimeSpan.FromHours(Math.Clamp(request.Hours ?? 24, 0.25, 168));

            try
            {
                ChannelSchedule schedule = await coordinator.PreviewAsync(config, channel, start, end, ct);
                return Results.Ok(schedule.ToDto());
            }
            catch (SchedulerConfigurationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    /// <summary>Static vocabulary the editor needs to render dropdowns without hard-coding them.</summary>
    private static void MapMetadataEndpoints(RouteGroupBuilder api) =>
        api.MapGet("/metadata", () => Results.Ok(new
        {
            playbackOrders = Enum.GetNames<PlaybackOrder>(),
            recurrences = Enum.GetNames<Recurrence>(),
            repeatModifiers = Enum.GetNames<RepeatModifier>(),
            filterScopes = Enum.GetNames<FilterScope>(),
            entryKinds = Enum.GetNames<ScheduleEntryKind>(),
            daysOfWeek = Enum.GetNames<DayOfWeek>(),
            queryFields = new[]
            {
                "id", "title", "plot", "tag", "collection", "genre", "network", "show", "show_id",
                "kind", "season", "episode", "year", "duration", "path", "rating", "promo_type"
            },
            queryOperators = new[] { ":", "==", "!=", ">", ">=", "<", "<=", "CONTAINS", "MATCHES", "IN" },
            defaultHolidays = TemporalTagOptions.DefaultHolidays,
            defaultPromoWindows = PromoConfig.DefaultWindows,
            timeZones = TimeZoneInfo.GetSystemTimeZones().Select(z => z.Id)
        }));

    private static RouteHandlerBuilder RequireApiKey(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<ApiKeyEndpointFilter>();
}

public sealed record RawYaml(string Yaml);
