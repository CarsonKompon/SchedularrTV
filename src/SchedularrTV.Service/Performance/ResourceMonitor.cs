using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Platform;
using SchedularrTV.Core.Publishing;
using SchedularrTV.Providers.ErsatzTv;

namespace SchedularrTV.Service.Performance;

/// <summary>One process's share of the computer at a sample.</summary>
/// <param name="Role">What it is, in words: "ffmpeg", "Browser", "Channel worker"…</param>
/// <param name="CpuPercent">Of the whole computer (every core together is 100).</param>
public sealed record ProcessUsage(int Pid, string Name, string Role, double CpuPercent, long MemoryBytes);

/// <summary>What one part of the setup is using: SchedularrTV itself, ErsatzTV and its transcoders, or SchedularrTV's helpers.</summary>
public sealed record UsageGroup(string Key, string Name, string Description, double CpuPercent, long MemoryBytes, IReadOnlyList<ProcessUsage> Processes);

/// <summary>Everything measured at one moment.</summary>
/// <param name="Transcodes">ffmpeg processes ErsatzTV is running: about one per channel that's playing.</param>
public sealed record ResourceSample(
    DateTimeOffset At,
    int Cores,
    double? SystemCpuPercent,
    long? SystemMemoryUsedBytes,
    long? SystemMemoryTotalBytes,
    IReadOnlyList<UsageGroup> Groups,
    IReadOnlyList<GpuUsage> Gpus,
    int Transcodes)
{
    public UsageGroup? Group(string key) => Groups.FirstOrDefault(group => group.Key == key);

    public double SetupCpuPercent => Groups.Sum(group => group.CpuPercent);

    public long SetupMemoryBytes => Groups.Sum(group => group.MemoryBytes);
}

/// <summary>A suggestion drawn from the numbers.</summary>
public sealed record PerformanceAdvice(AdviceLevel Level, string Title, string Detail);

public enum AdviceLevel
{
    Info,
    Warning
}

/// <summary>
/// Measures what SchedularrTV, ErsatzTV and everything they start (ffmpeg transcoders, hidden browsers for live page
/// channels, IPTV relays) take of the computer's CPU, memory and NVIDIA graphics card, every <see cref="Interval"/>,
/// keeping the last <see cref="HistoryLength"/> in memory for the Performance page.
/// </summary>
/// <remarks>
/// Processes are found by walking down from this one and from ErsatzTV (the supervised process, or any running
/// <c>ersatztv</c> program when it's started some other way), so a transcoder is counted under whichever started it.
/// </remarks>
public sealed class ResourceMonitor(
    IServiceProvider services,
    IOptions<ErsatzTvOptions> ersatzOptions,
    AlwaysOnChannels alwaysOn,
    TimeProvider clock,
    ILogger<ResourceMonitor> logger) : BackgroundService
{
    public const string SetupKey = "schedularrtv";
    public const string ErsatzKey = "ersatztv";
    public const string HelpersKey = "helpers";

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan HistoryLength = TimeSpan.FromMinutes(15);

    // Kept in a field rather than captured, as it also goes to the GPU reader (CS9124 otherwise).
    private readonly ILogger _logger = logger;
    private readonly SystemUsage _system = new();
    private readonly GpuReader _gpu = new(logger);
    private readonly Dictionary<int, (DateTime Started, TimeSpan Cpu, DateTimeOffset At)> _cpu = [];
    private readonly Queue<ResourceSample> _history = new();
    private readonly Lock _gate = new();

    private (DateTimeOffset At, int Software, int Total) _encoders = (DateTimeOffset.MinValue, 0, 0);

    /// <summary>Raised after each sample, for an open Performance page.</summary>
    public event Action? Sampled;

    /// <summary>The newest sample; null until the first is taken.</summary>
    public ResourceSample? Latest
    {
        get
        {
            lock (_gate)
            {
                return _history.Count > 0 ? _history.Last() : null;
            }
        }
    }

    /// <summary>The samples kept, oldest first.</summary>
    public IReadOnlyList<ResourceSample> History
    {
        get
        {
            lock (_gate)
            {
                return [.. _history];
            }
        }
    }

    /// <summary>Why the graphics card couldn't be read; null when it was, or before trying.</summary>
    public string? GpuProblem => _gpu.Problem;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ResourceSample sample = await SampleAsync(stoppingToken).ConfigureAwait(false);
                lock (_gate)
                {
                    _history.Enqueue(sample);
                    while (_history.Count > 0 && sample.At - _history.Peek().At > HistoryLength)
                    {
                        _history.Dequeue();
                    }
                }

                Sampled?.Invoke();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Couldn't measure resource use this time.");
            }

            try
            {
                await Task.Delay(Interval, clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<ResourceSample> SampleAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        int cores = Math.Max(1, Environment.ProcessorCount);
        IReadOnlyList<ProcessEntry> table = ProcessTable.Read();
        Dictionary<int, Process?> opened = [];

        try
        {
            Process? Open(int pid)
            {
                if (!opened.TryGetValue(pid, out Process? process))
                {
                    try
                    {
                        process = Process.GetProcessById(pid);
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                    {
                        process = null;
                    }

                    opened[pid] = process;
                }

                return process;
            }

            DateTime? StartedAt(int pid)
            {
                try
                {
                    return Open(pid)?.StartTime;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    return null;
                }
            }

            int self = Environment.ProcessId;
            Dictionary<int, ProcessEntry> byPid = table.GroupBy(entry => entry.Pid).ToDictionary(group => group.Key, group => group.First());

            // ErsatzTV: the process supervised here, or any running ersatztv program when it's started some other way.
            List<int> ersatzRoots = [];
            if (services.GetService<ErsatzTvProcessSupervisor>()?.ProcessId is { } supervised)
            {
                ersatzRoots.Add(supervised);
            }
            else
            {
                ersatzRoots.AddRange(table
                    .Where(entry => IsErsatz(entry.Name)
                                    && !(byPid.TryGetValue(entry.ParentPid, out ProcessEntry? parent) && IsErsatz(parent.Name)))
                    .Select(entry => entry.Pid));
            }

            IReadOnlyList<ProcessEntry> ersatz = ProcessTable.Descendants(table, ersatzRoots, StartedAt);
            HashSet<int> ersatzPids = [.. ersatz.Select(entry => entry.Pid)];

            // Ours: this process, the tray that started it (when it did), and everything started from here that isn't ErsatzTV.
            List<ProcessEntry> setup = byPid.TryGetValue(self, out ProcessEntry? me) ? [me] : [new ProcessEntry(self, 0, ProcessTable.Normalize(Environment.ProcessPath ?? "schedularrtv"))];
            if (me is not null && byPid.TryGetValue(me.ParentPid, out ProcessEntry? tray) && tray.Name.StartsWith("schedularrtv", StringComparison.Ordinal))
            {
                setup.Add(tray);
            }

            IReadOnlyList<ProcessEntry> helpers = [.. ProcessTable.Descendants(table, [self], StartedAt)
                .Where(entry => entry.Pid != self && !ersatzPids.Contains(entry.Pid))];

            UsageGroup setupGroup = Measure(SetupKey, "SchedularrTV", "The app itself: scheduling, the web pages, the API.", setup, Open, now, cores);
            UsageGroup ersatzGroup = Measure(ErsatzKey, "ErsatzTV", "The streaming server and the ffmpeg transcoder for each channel that's playing.", ersatz, Open, now, cores);
            UsageGroup helperGroup = Measure(HelpersKey, "Helpers", "What SchedularrTV starts: hidden browsers and ffmpeg for live page channels, IPTV relays, promo and credit renders.", helpers, Open, now, cores);

            // Forget processes that have gone, so a reused id starts afresh.
            HashSet<int> alive = [.. setup.Concat(ersatz).Concat(helpers).Select(entry => entry.Pid)];
            foreach (int gone in _cpu.Keys.Where(pid => !alive.Contains(pid)).ToList())
            {
                _cpu.Remove(gone);
            }

            (long? used, long? total) = SystemUsage.Memory();
            IReadOnlyList<GpuUsage> gpus = await _gpu.ReadAsync(now, cancellationToken).ConfigureAwait(false);

            return new ResourceSample(now, cores, _system.CpuPercent(), used, total, [setupGroup, ersatzGroup, helperGroup], gpus,
                ersatz.Count(entry => entry.Name == "ffmpeg"));
        }
        finally
        {
            foreach (Process? process in opened.Values)
            {
                process?.Dispose();
            }
        }
    }

    private UsageGroup Measure(string key, string name, string description, IEnumerable<ProcessEntry> entries, Func<int, Process?> open, DateTimeOffset now, int cores)
    {
        List<ProcessUsage> processes = [];
        foreach (ProcessEntry entry in entries)
        {
            if (open(entry.Pid) is not { } process)
            {
                continue;
            }

            try
            {
                TimeSpan cpu = process.TotalProcessorTime;
                long memory = process.WorkingSet64;
                DateTime started = process.StartTime;
                double percent = 0;
                if (_cpu.TryGetValue(entry.Pid, out var last) && last.Started == started && now > last.At)
                {
                    percent = Math.Clamp((cpu - last.Cpu).TotalMilliseconds / ((now - last.At).TotalMilliseconds * cores) * 100, 0, 100);
                }

                _cpu[entry.Pid] = (started, cpu, now);
                processes.Add(new ProcessUsage(entry.Pid, entry.Name, Role(entry.Name), percent, memory));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Exited part-way, or not ours to read.
            }
        }

        processes.Sort((a, b) => b.CpuPercent.CompareTo(a.CpuPercent));
        return new UsageGroup(key, name, description, processes.Sum(process => process.CpuPercent), processes.Sum(process => process.MemoryBytes), processes);
    }

    private static bool IsErsatz(string name) => name.StartsWith("ersatztv", StringComparison.Ordinal);

    /// <summary>A process's part in the setup, in words, from its program name.</summary>
    public static string Role(string name) => name switch
    {
        "ffmpeg" => "Transcoder (ffmpeg)",
        "ffprobe" => "Media probe (ffprobe)",
        // Linux cuts process names to 15 characters.
        "ersatztv-channel" or "ersatztv-channe" => "Channel worker",
        "chrome" or "msedge" or "chromium" or "chromium-browser" or "headless_shell" or "google chrome" => "Browser",
        "curl" => "Stream pull (curl)",
        "yt-dlp" => "Stream resolver (yt-dlp)",
        "piper" => "Voice (Piper)",
        _ when IsErsatz(name) => "ErsatzTV",
        _ when name.StartsWith("schedularrtv", StringComparison.Ordinal) => "SchedularrTV",
        _ => name
    };

    /// <summary>
    /// What the numbers suggest changing, from the last minute of samples: how much more the computer can take, and
    /// what's taking the most.
    /// </summary>
    public IReadOnlyList<PerformanceAdvice> Advice()
    {
        IReadOnlyList<ResourceSample> history = History;
        if (history.Count == 0)
        {
            return [];
        }

        ResourceSample latest = history[^1];
        ResourceSample[] recent = [.. history.Where(sample => latest.At - sample.At <= TimeSpan.FromMinutes(1))];
        double? systemCpu = Average(recent.Select(sample => sample.SystemCpuPercent));
        double ersatzCpu = recent.Average(sample => sample.Group(ErsatzKey)?.CpuPercent ?? 0);
        double helperCpu = recent.Average(sample => sample.Group(HelpersKey)?.CpuPercent ?? 0);
        double transcodes = recent.Average(sample => sample.Transcodes);
        List<PerformanceAdvice> advice = [];

        if (systemCpu is > 85)
        {
            advice.Add(new PerformanceAdvice(AdviceLevel.Warning, "The CPU is nearly flat out",
                $"The computer has averaged {systemCpu:0}% CPU over the last minute. Channels that can't encode in real time stutter and buffer. "
                + "Turn off 24/7 on channels nobody needs instantly, lower the resolution or bitrate in their channel.json, or move transcoding to the graphics card."));
        }

        if (latest.SystemMemoryUsedBytes is { } used && latest.SystemMemoryTotalBytes is { } total && total > 0 && used > total * 0.9)
        {
            advice.Add(new PerformanceAdvice(AdviceLevel.Warning, "Memory is nearly full",
                $"{Bytes(used)} of {Bytes(total)} is in use. Live page channels are the hungriest helpers (a hidden browser each); a shorter keep-alive in their plugin settings frees it sooner."));
        }

        if (transcodes >= 0.5 && ersatzCpu > 0)
        {
            double each = ersatzCpu / transcodes;
            string room = systemCpu is { } cpu && each > 0.5
                ? $" At that rate there's room for about {Math.Max(0, (int)Math.Floor((90 - cpu) / each))} more at once before the CPU runs out."
                : string.Empty;
            advice.Add(new PerformanceAdvice(AdviceLevel.Info, "Cost of a playing channel",
                $"ErsatzTV is transcoding {transcodes:0.#} channel{(Math.Round(transcodes, 1) == 1 ? "" : "s")}, about {each:0.#}% CPU each.{room}"));
        }

        (int software, int channels) = SoftwareEncodedChannels(latest.At);
        if (software > 0 && (transcodes >= 0.5 && ersatzCpu / transcodes > 10 || systemCpu is > 60))
        {
            advice.Add(new PerformanceAdvice(AdviceLevel.Info, "ErsatzTV is encoding on the CPU",
                $"{software} of {channels} channel{(channels == 1 ? "" : "s")} have no hardware acceleration set (\"accel\": null in their channel.json under {ChannelsFolder()}). "
                + "Setting accel to your graphics card's (nvenc, qsv, vaapi, videotoolbox) usually cuts a channel's CPU by most of it."));
        }

        if (helperCpu > 20 && !LiveEncodingSettings.Current.Video.Encoder.Hardware)
        {
            advice.Add(new PerformanceAdvice(AdviceLevel.Info, "Live page channels are encoding on the CPU",
                $"SchedularrTV's helpers are averaging {helperCpu:0}% CPU. Live page channels can encode on the graphics card instead: App settings › Hardware and video encoding."));
        }

        if (alwaysOn.Channels.Count > 0)
        {
            advice.Add(new PerformanceAdvice(AdviceLevel.Info, $"{alwaysOn.Channels.Count} channel{(alwaysOn.Channels.Count == 1 ? " plays" : "s play")} 24/7",
                "Those are transcoded all the time, watched or not, so their cost above is constant. Every other channel only costs anything while someone's tuned in."));
        }

        foreach (GpuUsage gpu in latest.Gpus.Where(gpu => gpu.Encoder is > 85))
        {
            advice.Add(new PerformanceAdvice(AdviceLevel.Warning, $"{gpu.Name}'s video encoder is nearly full",
                $"It's at {gpu.Encoder:0}%. More channels encoding on it at once will start to fall behind."));
        }

        return advice;
    }

    private string ChannelsFolder() => Path.Combine(Path.GetFullPath(ersatzOptions.Value.ConfigRoot), "channels");

    /// <summary>How many channel.json files leave ErsatzTV encoding in software, of how many; read at most once a minute.</summary>
    private (int Software, int Total) SoftwareEncodedChannels(DateTimeOffset now)
    {
        if (now - _encoders.At < TimeSpan.FromMinutes(1))
        {
            return (_encoders.Software, _encoders.Total);
        }

        int software = 0, total = 0;
        try
        {
            string folder = ChannelsFolder();
            if (Directory.Exists(folder))
            {
                foreach (string channel in Directory.EnumerateDirectories(folder))
                {
                    string path = Path.Combine(channel, ersatzOptions.Value.Lineup.ChannelConfigFileName);
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    total++;
                    if (IsSoftwareEncoded(File.ReadAllText(path)))
                    {
                        software++;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        _encoders = (now, software, total);
        return (software, total);
    }

    /// <summary>Whether a channel.json leaves video acceleration unset (null, missing or blank), so ErsatzTV encodes on the CPU.</summary>
    public static bool IsSoftwareEncoded(string channelJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(channelJson, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return !(document.RootElement.TryGetProperty("normalization", out JsonElement normalization)
                     && normalization.TryGetProperty("video", out JsonElement video)
                     && video.TryGetProperty("accel", out JsonElement accel)
                     && accel.ValueKind == JsonValueKind.String
                     && !string.IsNullOrWhiteSpace(accel.GetString()));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static double? Average(IEnumerable<double?> values)
    {
        double[] known = [.. values.Where(value => value is not null).Select(value => value!.Value)];
        return known.Length > 0 ? known.Average() : null;
    }

    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{bytes / 1024.0:0} KB"
    };
}
