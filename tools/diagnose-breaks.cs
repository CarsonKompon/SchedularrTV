#:sdk Microsoft.NET.Sdk.Web
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:project ../src/SchedularrTV.Service/SchedularrTV.Service.csproj

using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Query;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Providers.Jellyfin;
using SchedularrTV.Service;

string root = Path.GetFullPath("../src/SchedularrTV.Service", Path.GetDirectoryName(SourcePath())!);
Directory.SetCurrentDirectory(root);
using var http = new HttpClient { BaseAddress = new Uri("http://localhost:5252"), Timeout = TimeSpan.FromMinutes(5) };
using var configDocument = JsonDocument.Parse(await http.GetStringAsync("/api/config"));
var config = SchedulerConfigLoader.Parse(configDocument.RootElement.GetProperty("yaml").GetString()!);
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
var schedules = await http.GetFromJsonAsync<List<ChannelScheduleDto>>("/api/schedules", options) ?? [];
string snapshot = Path.Combine(Path.GetTempPath(), "schedularrtv-profile/media.json");
await using var mediaFile = File.OpenRead(snapshot);
var media = await JsonSerializer.DeserializeAsync<List<MediaItem>>(mediaFile) ?? [];
await using var app = SchedulerHost.Create([], contentRoot: root);
if (args.Contains("--items"))
{
    var jellyfin = app.Services.GetRequiredService<IOptions<JellyfinOptions>>().Value;
    using var client = new HttpClient { BaseAddress = new Uri(jellyfin.BaseUrl.TrimEnd('/') + "/") };
    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
        "MediaBrowser", $"Token=\"{jellyfin.ApiKey}\", Client=\"SchedularrTV\", Device=\"SchedularrTV\", DeviceId=\"schedularrtv\", Version=\"1.0\"");
    string ids = string.Join(',', args.Where(argument => argument != "--items"));
    using var items = JsonDocument.Parse(await client.GetStringAsync("Items?ids=" + Uri.EscapeDataString(ids) + "&fields=Path&enableImages=false"));
    List<string> parents = [];
    foreach (var item in items.RootElement.GetProperty("Items").EnumerateArray())
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Id = item.GetProperty("Id").GetString(), Name = item.GetProperty("Name").GetString(),
            Path = item.TryGetProperty("Path", out var path) ? path.GetString() : null,
            SeasonId = item.TryGetProperty("SeasonId", out var season) ? season.GetString() : null,
            ParentIndexNumber = item.TryGetProperty("ParentIndexNumber", out var number) ? number.ToString() : null
        }));
        if (item.TryGetProperty("SeasonId", out var parent)) parents.Add(parent.GetString()!);
    }
    if (parents.Count > 0)
    {
        using var seasons = JsonDocument.Parse(await client.GetStringAsync("Items?ids=" + Uri.EscapeDataString(string.Join(',', parents.Distinct())) + "&fields=Path&enableImages=false"));
        foreach (var season in seasons.RootElement.GetProperty("Items").EnumerateArray())
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Id = season.GetProperty("Id").GetString(), Name = season.GetProperty("Name").GetString(),
                Path = season.TryGetProperty("Path", out var path) ? path.GetString() : null,
                IndexNumber = season.TryGetProperty("IndexNumber", out var number) ? number.ToString() : null
            }));
    }
    return;
}
string statePath = Path.GetFullPath(app.Services.GetRequiredService<IOptions<SchedulerServiceOptions>>().Value.StatePath);
Console.WriteLine($"State path: {statePath}; snapshot modified: {File.GetLastWriteTime(snapshot):O}; items: {media.Count}");
using var stateDocument = JsonDocument.Parse(await File.ReadAllTextAsync(statePath));
Console.WriteLine($"State JSON kind: {stateDocument.RootElement.ValueKind}");
Console.WriteLine("Global interstitials: " + JsonSerializer.Serialize(config.Settings.Interstitials));
Console.WriteLine("Global promos: " + JsonSerializer.Serialize(config.Settings.Promos));
var resolve = typeof(SchedulingEngine).GetMethod("ResolveInterstitials", BindingFlags.NonPublic | BindingFlags.Static)!;
bool replay = args.Contains("--replay");
bool rotationReport = args.Contains("--rotation");
var requested = args.Where(argument => argument != "--replay" && argument != "--rotation").ToArray();
// No channel ids given: report on every channel.
string[] channels = requested.Length > 0 ? requested : [.. config.Channels.Select(channel => channel.Id)];
var replayState = new InMemoryPlaybackStateStore();
foreach (var property in stateDocument.RootElement.EnumerateObject())
    await replayState.SaveAsync(property.Value.Deserialize<PlaybackState>(options)!);
foreach (var channel in config.Channels.Where(channel => channels.Contains(channel.Id)))
{
    var schedule = schedules.FirstOrDefault(schedule => schedule.ChannelId == channel.Id);
    Console.WriteLine($"\nCHANNEL {channel.Id} {channel.Name}; temporal={channel.AutoTemporalTags}; shorts={JsonSerializer.Serialize(channel.InterstitialShows)}");
    if (schedule is null) continue;
    Console.WriteLine($"RANGE {schedule.Start:O} - {schedule.End:O}");
    if (rotationReport)
    {
        foreach (var group in schedule.Entries.Where(entry => entry.Kind == "Program").GroupBy(entry => entry.SeriesName))
            Console.WriteLine($"  LIVE {group.Key}: {group.Count()} entries, {group.Select(entry => entry.ItemId).Distinct().Count()} episodes");
        if (channel.BaseRotation is { } selector && channel.Follow?.Enabled != true)
        {
            var sessionType = typeof(SchedulingEngine).Assembly.GetType("SchedularrTV.Core.Scheduling.ScheduleBuildSession")!;
            var session = Activator.CreateInstance(sessionType, config, new SnapshotLibrary(media), replayState, channel,
                SchedulingEngine.ResolveTimeZone(channel.TimeZone ?? config.Settings.TimeZone), schedule.Start)!;
            var query = (QueryExpression)sessionType.GetMethod("ResolveExpression")!.Invoke(session, [selector, 0])!;
            var sequence = await (ValueTask<MediaSequence?>)sessionType.GetMethod("GetSequenceAsync")!
                .Invoke(session, [selector, "shared", CancellationToken.None])!;
            var includes = (List<ContentInclude>)sessionType.GetMethod("ResolveIncludes", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(session, [selector, 0])!;
            foreach (var group in sequence!.Pool.GroupBy(TitleRotation.TitleOf))
                Console.WriteLine($"  POOL {group.Key}: {group.Count()} episodes");
            using var livePool = JsonDocument.Parse(await http.GetStringAsync("/api/library/search?limit=1&query=" + Uri.EscapeDataString(query.ToQueryString())));
            Console.WriteLine($"  LIBRARY raw snapshot={QueryEvaluator.Filter(media, query).Count}, live={livePool.RootElement.GetProperty("total")}");
            Console.WriteLine($"  KEY {sequence.Key}");
            if (stateDocument.RootElement.TryGetProperty(sequence.Key, out var stateElement))
            {
                var state = stateElement.Deserialize<PlaybackState>(options)!;
                var settings = selector.Collection is { } collection ? config.GlobalCollections[collection] : selector;
                var (rules, titleOrder) = TitleRotation.RulesFrom(includes);
                var legacy = TitleRotation.Build(sequence.Pool, selector.TitleOrder ?? settings.TitleOrder ?? TitleOrder.Ordered,
                    selector.Order ?? settings.Order ?? PlaybackOrder.Shuffle, rules, titleOrder, $"{config.Settings.Seed}|{sequence.Key}");
                Console.WriteLine($"  STATE cursor={state.Cursor}/{legacy.Count}, last={state.LastItemId}, perTitle={state.Titles?.Count ?? 0}");
                foreach (var group in MediaSequence.CreatePreordered(sequence.Key, legacy, state).PreviewOrder(60).GroupBy(TitleRotation.TitleOf))
                    Console.WriteLine($"  OLD NEXT60 {group.Key}: {group.Count()}");
            }
        }
        if (replay)
        {
            var built = await new ScheduleBuilder(new SnapshotLibrary(media), replayState).BuildChannelAsync(config, channel, schedule.Start, schedule.End);
            foreach (var group in built.Entries.Where(entry => entry.Kind == ScheduleEntryKind.Program && !entry.IsSlotIntro && !entry.IsSlotOutro)
                         .GroupBy(entry => entry.Item?.SeriesName))
                Console.WriteLine($"  REPLAY {group.Key}: {group.Count()} entries, {group.Select(entry => entry.Item?.Id).Distinct().Count()} episodes");
            var looney = built.Entries.FirstOrDefault(entry => entry.Kind == ScheduleEntryKind.Program && entry.Item?.SeriesName == "Looney Tunes");
            if (looney is not null)
            {
                var boundary = SchedulingEngine.SlotBoundaryAfter(config, channel, looney.Start);
                foreach (var entry in built.Entries.Where(entry => entry.Start >= looney.Start && entry.Start < boundary))
                    Console.WriteLine($"  SLOT {entry.Start:HH:mm:ss} {entry.Kind} {entry.Duration.TotalSeconds:F2}s {entry.Item?.Title}");
            }
        }
        continue;
    }
    foreach (var group in schedule.Entries.GroupBy(entry => entry.Kind))
        Console.WriteLine($"  {group.Key}: {group.Count()} entries / {group.Sum(entry => entry.DurationSeconds) / 60:F2} minutes");
    foreach (var padding in schedule.Entries.Where(entry => entry.Kind == "Padding" && entry.DurationSeconds >= 60).Take(4))
        Console.WriteLine($"  PADDING {padding.Start.ToLocalTime():O} {padding.DurationSeconds:F2}s block={padding.BlockName}");
    foreach (var diagnostic in schedule.Diagnostics.Take(5)) Console.WriteLine($"  DIAG {diagnostic.Message}");
    var blockNames = schedule.Entries.Where(entry => entry.Kind == "Padding" || entry.Kind == "Filler").Select(entry => entry.BlockName).Distinct().ToArray();
    HashSet<string> currentKeys = [];
    foreach (string? name in blockNames.Length == 0 ? new string?[] { null } : blockNames)
    {
        var block = channel.Blocks.FirstOrDefault(block => block.Name == name);
        var resolved = (InterstitialsConfig)resolve.Invoke(null, [config, channel, block])!;
        DateTimeOffset moment = schedule.Entries.FirstOrDefault(entry => entry.BlockName == name && entry.Kind == "Padding")?.Start ?? schedule.Start;
        var query = InterstitialQueryBuilder.BuildCommercials(resolved.Commercials!, moment, config.Settings.TemporalTags);
        var commercials = resolved.Commercials!;
        string key = $"{channel.Id}|commercials|{DeterministicRandom.Hash($"{query.ToQueryString()}|{commercials.Order ?? PlaybackOrder.Shuffle}|{commercials.MinItemDuration}|{commercials.MaxItemDuration}"):x16}";
        currentKeys.Add(key);
        var pool = QueryEvaluator.Filter(media, query);
        var eligible = pool.Where(item => item.Duration >= (resolved.Commercials!.MinItemDuration ?? TimeSpan.Zero)
            && item.Duration <= (resolved.Commercials.MaxItemDuration ?? TimeSpan.MaxValue)).ToArray();
        using var livePool = JsonDocument.Parse(await http.GetStringAsync("/api/library/search?limit=1&query=" + Uri.EscapeDataString(query.ToQueryString())));
        Console.WriteLine($"  BLOCK {name ?? "(base)"} ADS query={query.ToQueryString()}; snapshot={pool.Count}; live={livePool.RootElement.GetProperty("total")}; eligible={eligible.Length}");
        Console.WriteLine("    SETTINGS " + JsonSerializer.Serialize(resolved));
        Console.WriteLine("    DURATIONS " + string.Join(", ", eligible.OrderBy(item => item.Duration).Take(3).Concat(eligible.OrderByDescending(item => item.Duration).Take(3)).Select(item => $"{item.Title}:{item.Duration.TotalSeconds:F2}s")));
        var fillerQuery = InterstitialQueryBuilder.BuildFiller(resolved.Filler!, moment, config.Settings.TemporalTags);
        Console.WriteLine($"    FILLER query={fillerQuery.ToQueryString()}; snapshot={QueryEvaluator.Filter(media, fillerQuery).Count}");
    }
    if (stateDocument.RootElement.ValueKind == JsonValueKind.Object)
    {
        foreach (var property in stateDocument.RootElement.EnumerateObject().Where(property => currentKeys.Contains(property.Name)))
        {
            var state = property.Value.Deserialize<SchedularrTV.Core.Ordering.PlaybackState>(options)!;
            var remaining = state.Bag.Select(id => media.FirstOrDefault(item => item.Id == id)).Where(item => item is not null).ToArray();
            Console.WriteLine($"  STATE {property.Name} bag={state.Bag.Count}, known={remaining.Length}, shortest={remaining.Select(item => item!.Duration.TotalSeconds).DefaultIfEmpty().Min():F2}s, under10min={remaining.Count(item => item!.Duration.TotalMinutes <= 10)}; first="
                + string.Join(", ", remaining.Take(3).Select(item => $"{item!.Title}:{item.Duration.TotalSeconds:F2}s")));
        }
    }
    if (replay)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var built = await new ScheduleBuilder(new SnapshotLibrary(media), replayState).BuildChannelAsync(config, channel,
            schedule.Start, schedule.End);
        Console.WriteLine($"  REPLAY {channel.Id} seconds={timer.Elapsed.TotalSeconds:F2} livePaddingMin={schedule.PaddingSeconds / 60:F2} fixedPaddingMin={built.TotalPadding.TotalMinutes:F2} fixedCommercialMin={built.Entries.Where(entry => entry.Kind == ScheduleEntryKind.Commercial).Sum(entry => entry.Duration.TotalMinutes):F2} fixedFillerMin={built.Entries.Where(entry => entry.Kind == ScheduleEntryKind.Filler).Sum(entry => entry.Duration.TotalMinutes):F2}");
        foreach (var gap in built.Entries.Where(entry => entry.Kind == ScheduleEntryKind.Padding && entry.Duration.TotalSeconds > 30).Take(6))
            Console.WriteLine($"    REMAINING {gap.Start.ToLocalTime():O} {gap.Duration.TotalSeconds:F2}s block={gap.BlockName}");
    }
}

static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

sealed class SnapshotLibrary(IReadOnlyList<MediaItem> items) : IMediaLibrary
{
    public ValueTask<IReadOnlyList<MediaItem>> SearchAsync(QueryExpression expression, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(QueryEvaluator.Filter(items, expression));
    public ValueTask RefreshAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}