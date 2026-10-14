#:sdk Microsoft.NET.Sdk.Web
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:project ../src/SchedularrTV.Service/SchedularrTV.Service.csproj
#:project ../plugins/SchedularrTV.Plugins.GeneratedPromos/SchedularrTV.Plugins.GeneratedPromos.csproj
#:project ../plugins/SchedularrTV.Plugins.TikTok/SchedularrTV.Plugins.TikTok.csproj

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Query;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Service;
using SchedularrTV.GeneratedPromos;
using SchedularrTV.Speech.TikTok;

// Read-only preview: builds in memory and renders under %TEMP%; never saves config, state or playouts.
string root = Path.GetFullPath("../src/SchedularrTV.Service", Path.GetDirectoryName(SourcePath())!);
Directory.SetCurrentDirectory(root);
string? channelId = args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal) && !IsOptionValue(argument));
string voice = Option("--voice") ?? TikTokVoices.Default;
string? background = Option("--background") is { } path ? Path.GetFullPath(path) : null;
int limit = int.TryParse(Option("--limit"), out int parsed) ? parsed : 3;
double hours = double.TryParse(Option("--hours"), out double requested) ? requested : 3;
string snapshot = Option("--media") ?? Path.Combine(Path.GetTempPath(), "schedularrtv-profile", "media.json");
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(20));

string configPath;
await using (var app = SchedulerHost.Create([], contentRoot: root))
    configPath = Path.GetFullPath(app.Services.GetRequiredService<IOptions<SchedulerServiceOptions>>().Value.ConfigPath);
SchedulerConfig config = SchedulerConfigLoader.Parse(await File.ReadAllTextAsync(configPath, cancellation.Token));
ChannelConfig channel = channelId is null ? config.Channels[0]
    : config.Channels.FirstOrDefault(candidate => string.Equals(candidate.Id, channelId, StringComparison.OrdinalIgnoreCase))
      ?? throw new ArgumentException($"No channel '{channelId}'. Available: {string.Join(", ", config.Channels.Select(c => c.Id))}");
TimeZoneInfo zone = SchedulingEngine.ResolveTimeZone(channel.TimeZone ?? config.Settings.TimeZone);

if (channel.GeneratedPromos is not { Enabled: true, Templates.Count: > 0 } || args.Contains("--builtin"))
{
    GeneratedPromoType type = Enum.TryParse(Option("--type")?.Replace("_", ""), ignoreCase: true, out GeneratedPromoType parsedType)
        ? parsedType : GeneratedPromoType.UpNext;
    GeneratedPromoTemplate template = GeneratedPromoTemplates.ForType(type, voice: voice);
    template.Usage = args.Contains("--fallback-only") ? GeneratedPromoUsage.Fallback : GeneratedPromoUsage.Mixed;
    template.MixChance = 1;
    template.Background = background;
    channel.GeneratedPromos = new GeneratedPromosConfig { Templates = [template] };
}

await using FileStream mediaStream = File.OpenRead(snapshot);
List<MediaItem> media = await JsonSerializer.DeserializeAsync<List<MediaItem>>(mediaStream, cancellationToken: cancellation.Token) ?? [];
DateTimeOffset now = DateTimeOffset.Now;
var timer = Stopwatch.StartNew();
ChannelSchedule schedule = await new ScheduleBuilder(new SnapshotLibrary(media), new InMemoryPlaybackStateStore())
    .BuildChannelAsync(config, channel, SchedulingEngine.AlignToGrid(config, channel, now), now.AddHours(hours), cancellation.Token);
Console.WriteLine($"Config (read only): {configPath}");
Console.WriteLine($"Channel: {channel.Id} {channel.Name}; {hours}h schedule built in {timer.Elapsed.TotalSeconds:F2}s");

string output = Path.Combine(Path.GetTempPath(), "schedularrtv-generated-promos", $"run-{DateTime.Now:yyyyMMdd-HHmmss}");
using var http = new SingleClientFactory();
var service = new GeneratedPromoService(
    Options.Create(new GeneratedPromoOptions { OutputPath = Path.Combine(output, "cache") }),
    new TextToSpeechCatalog([new TikTokTextToSpeech(http)]),
    TimeProvider.System,
    NullLogger<GeneratedPromoService>.Instance);
ScheduleEntry[] generated = [.. schedule.Entries.Where(entry => entry.GeneratedTemplate is not null && entry.End > now)];
Console.WriteLine($"Generated slots: {generated.Length} ({string.Join(", ", generated.GroupBy(entry => entry.Kind).Select(group => $"{group.Count()} {group.Key}"))})");

List<object> results = [];
foreach (ScheduleEntry entry in generated.Take(limit))
{
    GeneratedPromoSource source = GeneratedPromoPlanner.Source(config, channel, entry.BlockName)!;
    GeneratedPromoTemplate template = source.Promos.Templates.First(candidate => candidate.Name == entry.GeneratedTemplate);
    IReadOnlyList<UpNextListing> listings = GeneratedPromoService.Subject(template.Type, schedule, entry, GeneratedPromoResolver.ListingsNeeded(template));
    if (GeneratedPromoResolver.Resolve(template, source.Owner.Name, listings, zone, entry.Start,
            GeneratedImageSubjects.FromSchedule(schedule, entry.Start, entry.End)) is not { } promo)
    {
        Console.WriteLine($"  {entry.Start.ToLocalTime():HH:mm:ss}: skipped, no following programme in the preview window");
        continue;
    }

    timer.Restart();
    GeneratedPromoRender render = await service.RenderAsync(promo, cancellation.Token);
    Console.WriteLine($"  {entry.Start.ToLocalTime():HH:mm:ss} [{render.VoiceError?.GetBaseException().Message ?? "voiced"}] "
        + $"{timer.Elapsed.TotalSeconds:F2}s: {promo.Script}");
    Console.WriteLine($"    {render.Path}");
    results.Add(new { entry.Start, promo.Key, promo.Script, render.Path, Seconds = timer.Elapsed.TotalSeconds, Voiced = render.VoiceError is null });
}

Directory.CreateDirectory(output);
await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }), cancellation.Token);
Console.WriteLine($"Output: {output}");

string? Option(string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

bool IsOptionValue(string argument)
{
    int index = Array.IndexOf(args, argument);
    return index > 0 && args[index - 1].StartsWith("--", StringComparison.Ordinal)
        && args[index - 1] is not ("--builtin" or "--fallback-only");
}

static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

sealed class SnapshotLibrary(IReadOnlyList<MediaItem> items) : IMediaLibrary
{
    public ValueTask<IReadOnlyList<MediaItem>> SearchAsync(QueryExpression expression, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(QueryEvaluator.Filter(items, expression));

    public ValueTask RefreshAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

sealed class SingleClientFactory : IHttpClientFactory, IDisposable
{
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(30) };

    public HttpClient CreateClient(string name) => _client;

    public void Dispose() => _client.Dispose();
}
