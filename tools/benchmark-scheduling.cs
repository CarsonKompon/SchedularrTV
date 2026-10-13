#:property PublishAot=false
#:project ../src/SchedularrTV.Core/SchedularrTV.Core.csproj

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Query;
using SchedularrTV.Core.Scheduling;

// Synthetic, repeatable CPU/allocation benchmark; no live library or playback state needed.
// dotnet run -c Release --file tools/benchmark-scheduling.cs
var ads = Enumerable.Range(0, 256).Select(i => Item($"ad-{i:D4}", 15 * (1 + i % 4), "Commercials")).ToArray();
var irregular = Enumerable.Range(0, 256).Select(i => Item($"ad-{i:D4}", 10.013 + i * 0.137, "Commercials")).ToArray();
Measure("Pack regular ads", 2000, () => KnapsackPacker.Pack(ads, TimeSpan.FromMinutes(8), TimeSpan.FromMilliseconds(250)));
Measure("Pack irregular ads", 2000, () => KnapsackPacker.Pack(irregular, TimeSpan.FromSeconds(479.83), TimeSpan.FromMilliseconds(250)));
var largePool = Enumerable.Range(0, 20000).Select(i => Item($"ad-{i:D6}", 30, "Commercials")).ToArray();
var sequence = MediaSequence.Create("preview", PlaybackOrder.Chronological, largePool, null);
Measure("Preview 256 of 20k", 2000, () => sequence.PreviewForGap(256, item => item.Duration <= TimeSpan.FromMinutes(1)));

var channel = new ChannelConfig
{
    Id = "benchmark", Name = "Benchmark", TimeZone = "UTC", AutoTemporalTags = false,
    BaseRotation = new ContentSelector { Query = "tag:'Shows'", Order = PlaybackOrder.Shuffle },
    PadToNearest = TimeSpan.FromMinutes(30)
};
var config = new SchedulerConfig
{
    Channels = [channel],
    Settings = new SchedulerSettings
    {
        TimeZone = "UTC",
        Interstitials = new InterstitialsConfig
        {
            Commercials = new CommercialsConfig { BaseQuery = "tag:'Commercials'", AutoTemporalTags = false },
            Filler = new FillerTierConfig { Query = "tag:'Missing'" }
        }
    }
};
var library = new MemoryLibrary([.. ads, .. Enumerable.Range(0, 100).Select(i => Item($"show-{i}", 22 * 60, "Shows"))]);
var start = new DateTimeOffset(2026, 1, 12, 0, 0, 0, TimeSpan.Zero);
ChannelSchedule Build() => new ScheduleBuilder(library, new InMemoryPlaybackStateStore())
    .BuildChannelAsync(config, channel, start, start.AddDays(7)).GetAwaiter().GetResult();
Measure("Build 7 days", 10, Build);
var schedule = Build();
Console.WriteLine($"Schedule: {schedule.Entries.Count} entries, SHA256 {Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(schedule)))}");

static MediaItem Item(string id, double seconds, string tag) => new()
{
    Id = id, Title = id, Duration = TimeSpan.FromSeconds(seconds), Tags = [tag]
};

static void Measure<T>(string name, int iterations, Func<T> action)
{
    for (int i = 0; i < 20; i++) action();
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    long allocated = GC.GetTotalAllocatedBytes(true);
    using var process = Process.GetCurrentProcess();
    TimeSpan cpu = process.TotalProcessorTime;
    var timer = Stopwatch.StartNew();
    for (int i = 0; i < iterations; i++) action();
    timer.Stop();
    cpu = process.TotalProcessorTime - cpu;
    allocated = GC.GetTotalAllocatedBytes(true) - allocated;
    Console.WriteLine($"{name}: {timer.Elapsed.TotalMilliseconds / iterations:F3} ms/op, {cpu.TotalMilliseconds / iterations:F3} CPU ms/op, {allocated / iterations:N0} B/op ({iterations} iterations)");
}

sealed class MemoryLibrary(IReadOnlyList<MediaItem> items) : IMediaLibrary
{
    public ValueTask<IReadOnlyList<MediaItem>> SearchAsync(QueryExpression expression, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(QueryEvaluator.Filter(items, expression));
    public ValueTask RefreshAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
