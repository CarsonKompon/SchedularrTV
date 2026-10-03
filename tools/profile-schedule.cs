#:sdk Microsoft.NET.Sdk.Web
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:project ../src/SchedularrTV.Service/SchedularrTV.Service.csproj

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Publishing;
using SchedularrTV.Core.Query;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Providers.ErsatzTv;
using SchedularrTV.Providers.FileSystem;
using SchedularrTV.Service;

if (args.Length == 0 || args[0] is not ("capture" or "build" or "changes"))
{
    Console.WriteLine("Run from the repository root: dotnet run --file tools/profile-schedule.cs -- capture|build|changes [snapshot-directory] [hours]");
    return;
}
string root = Path.GetFullPath("../src/SchedularrTV.Service", Path.GetDirectoryName(SourcePath())!);
string scratch = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(Path.GetTempPath(), "schedularrtv-profile");
Directory.CreateDirectory(scratch);
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(20));
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
if (args[0] == "capture")
{
    if (File.Exists(Path.Combine(scratch, "media.json"))) throw new InvalidOperationException("Use a new snapshot directory to capture fresh inputs.");
    Directory.SetCurrentDirectory(root);
    await using var app = SchedulerHost.Create([], builder =>
    {
        builder.Services.PostConfigure<SchedulerServiceOptions>(options =>
        {
            File.Copy(Path.GetFullPath(options.ConfigPath), Path.Combine(scratch, "schedule.yaml"), true);
            string state = Path.GetFullPath(options.StatePath);
            if (File.Exists(state)) File.Copy(state, Path.Combine(scratch, "initial-state.json"), true);
            options.ConfigPath = Path.Combine(scratch, "schedule.yaml");
            options.StatePath = Path.Combine(scratch, "unused-state.json");
            options.LogoPath = Path.Combine(scratch, "logos");
            options.AutoRebuild = false;
            options.WatchConfig = false;
        });
        builder.Services.PostConfigure<FileSystemLibraryOptions>(options =>
        {
            string cache = Path.GetFullPath(options.CachePath);
            options.CachePath = Path.Combine(scratch, "probe-cache.json");
            if (File.Exists(cache)) File.Copy(cache, options.CachePath, true);
        });
        builder.Services.PostConfigure<ErsatzTvOptions>(options =>
        {
            options.ManageProcess = false;
            options.ConfigRoot = Path.Combine(scratch, "ersatztv");
        });
        builder.Services.RemoveAll<IPlayoutPublisher>();
    }, contentRoot: root);
    var provider = app.Services.GetRequiredService<SchedulerConfigProvider>();
    if (!provider.Validation.IsValid) throw new InvalidOperationException("The current configuration is invalid.");
    var timer = Stopwatch.StartNew();
    Console.WriteLine("CAPTURE: loading real library metadata; no builds or publishing.");
    var media = await app.Services.GetRequiredService<IMediaLibrary>().SearchAsync(QueryExpression.All, cancellation.Token);
    double seconds = timer.Elapsed.TotalSeconds;
    await using (var output = File.Create(Path.Combine(scratch, "media.json")))
        await JsonSerializer.SerializeAsync(output, media, cancellationToken: cancellation.Token);
    await File.WriteAllTextAsync(Path.Combine(scratch, "capture.json"), JsonSerializer.Serialize(new
    {
        Start = DateTimeOffset.UtcNow, LibrarySeconds = seconds, Items = media.Count,
        ConfigHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(provider.ConfigPath, cancellation.Token)))
    }), cancellation.Token);
    Console.WriteLine($"CAPTURE: {media.Count} items in {seconds:F2}s; snapshot at {scratch}");
    return;
}

using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(scratch, "capture.json"), cancellation.Token));
DateTimeOffset now = capture.RootElement.GetProperty("Start").GetDateTimeOffset();
var config = SchedulerConfigLoader.Parse(await File.ReadAllTextAsync(Path.Combine(scratch, "schedule.yaml"), cancellation.Token));
TimeSpan horizon = args.Length > 2 ? TimeSpan.FromHours(double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture)) : config.Settings.Horizon;
await using var mediaStream = File.OpenRead(Path.Combine(scratch, "media.json"));
var items = await JsonSerializer.DeserializeAsync<List<MediaItem>>(mediaStream, cancellationToken: cancellation.Token) ?? [];
string runDirectory = Path.Combine(scratch, $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}");
Directory.CreateDirectory(runDirectory);
string statePath = Path.Combine(runDirectory, "state.json");
string initialState = Path.Combine(scratch, "initial-state.json");
if (File.Exists(initialState)) File.Copy(initialState, statePath);
await using var stateStore = new JsonPlaybackStateStore(statePath);
var library = new MeasuredLibrary(items);
if (args[0] == "changes")
{
    config.Settings.Horizon = horizon;
    using var provider = new SchedulerConfigProvider(Options.Create(new SchedulerServiceOptions
    {
        ConfigPath = Path.Combine(runDirectory, "schedule.yaml"), AutoRebuild = false, WatchConfig = false
    }), NullLogger<SchedulerConfigProvider>.Instance);
    if (!provider.Save(SchedulerConfigLoader.Serialize(config)).IsValid) throw new InvalidOperationException("The captured configuration is invalid.");
    var coordinator = new ScheduleCoordinator(provider, new ScheduleBuilder(library, stateStore), library, [],
        NullLogger<ScheduleCoordinator>.Instance, NullLoggerFactory.Instance, new CapturedClock(now));
    List<object> measurements = [];
    IReadOnlyList<ChannelSchedule> previous = [];
    foreach (string stage in new[] { "initial", "one-channel-save", "unchanged-save" })
    {
        if (stage != "initial")
        {
            SchedulerConfig edited = SchedulerConfigLoader.Parse(provider.ReadRaw());
            if (stage == "one-channel-save") edited.Channels[0].Name += " (profile)";
            if (!provider.Save(SchedulerConfigLoader.Serialize(edited)).IsValid) throw new InvalidOperationException("The edited snapshot is invalid.");
        }
        library.Reset();
        var timer = Stopwatch.StartNew();
        var schedules = await coordinator.RebuildAsync(false, true, cancellation.Token, onlyChanged: true);
        double seconds = timer.Elapsed.TotalSeconds;
        if (coordinator.Failures.Count > 0) throw new InvalidOperationException(string.Join("; ", coordinator.Failures.Values.Select(failure => failure.Message)));
        int changed = schedules.Count(schedule => !previous.Any(old => ReferenceEquals(old, schedule)));
        int expected = stage == "initial" ? config.Channels.Count : stage == "one-channel-save" ? 1 : 0;
        if (changed != expected) throw new InvalidOperationException($"{stage}: expected {expected} changed schedules, got {changed}.");
        measurements.Add(new { Stage = stage, Seconds = seconds, RebuiltChannels = changed, library.Queries, library.QuerySeconds });
        Console.WriteLine($"{stage}: {seconds:F3}s, rebuilt={changed}/{schedules.Count}, queries={library.Queries}, querySeconds={library.QuerySeconds:F3}");
        previous = schedules;
    }
    await File.WriteAllTextAsync(Path.Combine(runDirectory, "results.json"), JsonSerializer.Serialize(measurements,
        new JsonSerializerOptions { WriteIndented = true }), cancellation.Token);
    Console.WriteLine($"Selective-save results: {runDirectory}");
    return;
}
var scheduler = new ScheduleBuilder(library, stateStore);
List<object> results = [];
var total = Stopwatch.StartNew();
Console.WriteLine($"BUILD: {config.Channels.Count} channels, {horizon.TotalHours}h, {items.Count} items; {runDirectory}");
foreach (var channel in config.Channels)
{
    cancellation.Token.ThrowIfCancellationRequested();
    library.Reset();
    var timer = Stopwatch.StartNew();
    Console.WriteLine($"START {channel.Id} {channel.Name}");
    var schedule = await scheduler.BuildChannelAsync(config, channel,
        SchedulingEngine.AlignToGrid(config, channel, now), now + horizon, cancellation.Token);
    double seconds = timer.Elapsed.TotalSeconds;
    string hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(schedule)));
    results.Add(new { channel.Id, channel.Name, Seconds = seconds, library.Queries, library.QuerySeconds, Entries = schedule.Entries.Count, Hash = hash });
    Console.WriteLine($"DONE {channel.Id}: {seconds:F2}s, queries={library.Queries}, querySeconds={library.QuerySeconds:F2}, entries={schedule.Entries.Count}, hash={hash}");
    await File.WriteAllTextAsync(Path.Combine(runDirectory, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }), cancellation.Token);
}
Console.WriteLine($"TOTAL {total.Elapsed.TotalSeconds:F2}s");

static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

sealed class CapturedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

sealed class MeasuredLibrary(IReadOnlyList<MediaItem> items) : IMediaLibrary
{
    public int Queries { get; private set; }
    public double QuerySeconds { get; private set; }
    public void Reset() { Queries = 0; QuerySeconds = 0; }
    public ValueTask<IReadOnlyList<MediaItem>> SearchAsync(QueryExpression expression, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        var result = QueryEvaluator.Filter(items, expression);
        QuerySeconds += timer.Elapsed.TotalSeconds;
        Queries++;
        return ValueTask.FromResult(result);
    }
    public ValueTask RefreshAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}