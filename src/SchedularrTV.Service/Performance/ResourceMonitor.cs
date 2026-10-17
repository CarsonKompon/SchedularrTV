using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Platform;
using SchedularrTV.Core.Publishing;
using SchedularrTV.Plugins.Performance;
using SchedularrTV.Providers.ErsatzTv;

namespace SchedularrTV.Service.Performance;

/// <summary>One process's share of the computer at a sample.</summary>
/// <param name="Role">What it is, in words: "ffmpeg", "Browser", "Channel worker"…</param>
/// <param name="CpuPercent">Of the whole computer (every core together is 100).</param>
/// <param name="Source">The work it's running for ("Generated Promos – Generating promo"), when that's known.</param>
public sealed record ProcessUsage(int Pid, string Name, string Role, double CpuPercent, long MemoryBytes, string? Source = null);

/// <summary>What one part of the setup is using: SchedularrTV itself, ErsatzTV and its transcoders, or SchedularrTV's helpers.</summary>
public sealed record UsageGroup(string Key, string Name, string Description, double CpuPercent, long MemoryBytes, IReadOnlyList<ProcessUsage> Processes);

/// <summary>
/// What one named piece of work is using at a sample: a plugin and what it's doing ("Local AI – Whisper transcribing"),
/// ErsatzTV's parts, or SchedularrTV's own.
/// </summary>
/// <param name="Key">"Source – Action", the same from sample to sample.</param>
/// <param name="Via">The work it's being done for, when another plugin asked for it ("Generated Promos – Generating promo").</param>
/// <param name="CpuPercent">All of it: its processes' and its estimated share of SchedularrTV's own.</param>
/// <param name="InAppCpuPercent">The part estimated from SchedularrTV's own CPU (work done inside the app, like Whisper).</param>
/// <param name="MemoryBytes">Its processes', plus its estimated share of SchedularrTV's own above its usual.</param>
/// <param name="Processes">How many processes it has running.</param>
/// <param name="Running">How many scopes of it are running (several promos at once, say).</param>
public sealed record SourceUsage(
    string Key,
    string Source,
    string Action,
    string? Via,
    double CpuPercent,
    double InAppCpuPercent,
    long MemoryBytes,
    int Processes,
    int Running);

/// <summary>One piece of work that ran in the last <see cref="ResourceMonitor.HistoryLength"/>, with what it cost.</summary>
/// <param name="AverageCpuPercent">While it ran.</param>
/// <param name="PeakCpuPercent">Its highest at any sample.</param>
public sealed record WorkUsage(
    long Id,
    string Source,
    string Action,
    string? Subject,
    string? Via,
    DateTimeOffset Started,
    DateTimeOffset? Ended,
    double AverageCpuPercent,
    double PeakCpuPercent,
    long PeakMemoryBytes)
{
    public string Label => $"{Source} – {Action}";

    public TimeSpan Duration => (Ended ?? DateTimeOffset.UtcNow) - Started;
}

/// <summary>Everything measured at one moment.</summary>
/// <param name="Transcodes">ffmpeg processes ErsatzTV is running: about one per channel that's playing.</param>
/// <param name="Sources">What each named piece of work used, most CPU first.</param>
public sealed record ResourceSample(
    DateTimeOffset At,
    int Cores,
    double? SystemCpuPercent,
    long? SystemMemoryUsedBytes,
    long? SystemMemoryTotalBytes,
    IReadOnlyList<UsageGroup> Groups,
    IReadOnlyList<GpuUsage> Gpus,
    int Transcodes,
    IReadOnlyList<SourceUsage> Sources)
{
    public UsageGroup? Group(string key) => Groups.FirstOrDefault(group => group.Key == key);

    public SourceUsage? Source(string key) => Sources.FirstOrDefault(source => source.Key == key);

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
/// <para>
/// Processes are found by walking down from this one and from ErsatzTV (the supervised process, or any running
/// <c>ersatztv</c> program when it's started some other way), so a transcoder is counted under whichever started it.
/// </para>
/// <para>
/// Each is then put down to the work that started it (<see cref="Work"/>): a plugin and what it was doing. Work done
/// inside this process (Whisper, the language model) can't be measured on its own, so SchedularrTV's CPU and memory
/// above their usual idle level are shared between the work running at the time, by how long each ran.
/// </para>
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

    /// <summary>The name SchedularrTV's own work goes under.</summary>
    public const string AppSource = "SchedularrTV";

    /// <summary>What's left of SchedularrTV's own CPU once named work has had its share.</summary>
    public const string AppAction = "App (web pages, scheduling, API)";

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan HistoryLength = TimeSpan.FromMinutes(15);

    private const int RecentWorkKept = 300;

    // Kept in a field rather than captured, as it also goes to the GPU reader (CS9124 otherwise).
    private readonly ILogger _logger = logger;
    private readonly SystemUsage _system = new();
    private readonly GpuReader _gpu = new(logger);
    private readonly Dictionary<int, (DateTime Started, TimeSpan Cpu, DateTimeOffset At)> _cpu = [];
    private readonly Queue<ResourceSample> _history = new();
    private readonly Dictionary<long, TimeSpan> _ownTime = [];
    private readonly Dictionary<long, WorkTally> _tallies = [];
    private readonly Lock _gate = new();

    private (DateTimeOffset At, int Software, int Total) _encoders = (DateTimeOffset.MinValue, 0, 0);
    private double? _idleCpu;
    private double? _idleMemory;
    private DateTimeOffset? _lastWorkCheck;
    private DateTimeOffset? _lastSampleAt;

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

    /// <summary>The work that's run in the last <see cref="HistoryLength"/>, newest first.</summary>
    public IReadOnlyList<WorkUsage> RecentWork
    {
        get
        {
            lock (_gate)
            {
                return [.. _tallies.Values.Select(tally => tally.ToUsage()).OrderByDescending(work => work.Started)];
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
        DateTimeOffset tableAt = DateTimeOffset.UtcNow;
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

            // The work each helper was started for: its own, or the nearest process above it that has one.
            WorkSnapshot work = Work.Snapshot(_lastWorkCheck ?? tableAt - Interval);
            _lastWorkCheck = tableAt;
            Dictionary<long, WorkInfo> workById = work.Items.ToDictionary(item => item.Id);
            Dictionary<int, WorkInfo?> owners = [];

            WorkInfo? OwnerOf(int pid)
            {
                if (owners.TryGetValue(pid, out WorkInfo? known))
                {
                    return known;
                }

                WorkInfo? found = null;
                int current = pid;
                for (int depth = 0; depth < 32 && current != self; depth++)
                {
                    if (work.Processes.TryGetValue(current, out long id) && workById.TryGetValue(id, out WorkInfo? owner))
                    {
                        found = owner;
                        break;
                    }

                    if (!byPid.TryGetValue(current, out ProcessEntry? entry) || entry.ParentPid == current)
                    {
                        break;
                    }

                    current = entry.ParentPid;
                }

                owners[pid] = found;
                return found;
            }

            UsageGroup setupGroup = Measure(SetupKey, "SchedularrTV", "The app itself: scheduling, the web pages, the API, and work plugins do inside it (Whisper, the AI writer).",
                setup, Open, now, cores, entry => entry.Pid == self ? $"{AppSource} – {AppAction}" : $"{AppSource} – Tray app");
            UsageGroup ersatzGroup = Measure(ErsatzKey, "ErsatzTV", "The streaming server and the ffmpeg transcoder for each channel that's playing.",
                ersatz, Open, now, cores, entry => $"ErsatzTV – {ErsatzRole(entry.Name)}");
            UsageGroup helperGroup = Measure(HelpersKey, "Helpers", "What SchedularrTV and its plugins start: hidden browsers and ffmpeg for live page channels, IPTV relays, promo and credit renders.",
                helpers, Open, now, cores, entry => OwnerOf(entry.Pid)?.Label);

            // Forget processes that have gone, so a reused id starts afresh.
            HashSet<int> alive = [.. setup.Concat(ersatz).Concat(helpers).Select(entry => entry.Pid)];
            foreach (int gone in _cpu.Keys.Where(pid => !alive.Contains(pid)).ToList())
            {
                _cpu.Remove(gone);
            }

            if (table.Count > 0)
            {
                Work.ForgetProcessesExcept(table.Select(entry => entry.Pid).ToHashSet(), tableAt);
            }

            IReadOnlyList<SourceUsage> sources = Attribute(setupGroup, ersatzGroup, helperGroup, self, work, OwnerOf, now);

            (long? used, long? total) = SystemUsage.Memory();
            IReadOnlyList<GpuUsage> gpus = await _gpu.ReadAsync(now, cancellationToken).ConfigureAwait(false);

            return new ResourceSample(now, cores, _system.CpuPercent(), used, total, [setupGroup, ersatzGroup, helperGroup], gpus,
                ersatz.Count(entry => entry.Name == "ffmpeg"), sources);
        }
        finally
        {
            foreach (Process? process in opened.Values)
            {
                process?.Dispose();
            }
        }
    }

    /// <summary>
    /// Puts this sample's CPU and memory down to the work that used it, and adds it to each piece of work's running total.
    /// </summary>
    private List<SourceUsage> Attribute(UsageGroup setup, UsageGroup ersatz, UsageGroup helpers, int self, WorkSnapshot work,
        Func<int, WorkInfo?> ownerOf, DateTimeOffset now)
    {
        double seconds = _lastSampleAt is { } last && now > last ? Math.Min((now - last).TotalSeconds, Interval.TotalSeconds * 3) : Interval.TotalSeconds;
        _lastSampleAt = now;

        Dictionary<string, SourceTally> sources = [];
        Dictionary<long, (double Cpu, long Memory)> byWork = [];

        SourceTally Tally(string source, string action, string? via = null)
        {
            string key = $"{source} – {action}";
            if (!sources.TryGetValue(key, out SourceTally? tally))
            {
                sources[key] = tally = new SourceTally(key, source, action);
            }

            if (via is not null)
            {
                tally.Via.Add(via);
            }

            return tally;
        }

        void AddToWork(long id, double cpu, long memory)
        {
            (double Cpu, long Memory) sum = byWork.GetValueOrDefault(id);
            byWork[id] = (sum.Cpu + cpu, sum.Memory + memory);
        }

        foreach (ProcessUsage process in ersatz.Processes)
        {
            SourceTally tally = Tally("ErsatzTV", ErsatzRole(process.Name));
            tally.Cpu += process.CpuPercent;
            tally.Memory += process.MemoryBytes;
            tally.Processes++;
        }

        // Helpers: under the work that started them, else as one of SchedularrTV's own, by what they are.
        HashSet<long> withProcesses = [];
        foreach (ProcessUsage process in helpers.Processes)
        {
            if (ownerOf(process.Pid) is { } owner)
            {
                SourceTally tally = Tally(owner.Source, owner.Action, owner.Via);
                tally.Cpu += process.CpuPercent;
                tally.Memory += process.MemoryBytes;
                tally.Processes++;
                withProcesses.Add(owner.Id);
                AddToWork(owner.Id, process.CpuPercent, process.MemoryBytes);
            }
            else
            {
                SourceTally tally = Tally(AppSource, $"{process.Role}, unnamed");
                tally.Cpu += process.CpuPercent;
                tally.Memory += process.MemoryBytes;
                tally.Processes++;
            }
        }

        // This process: what's above its usual, shared by how long each piece of work ran in it since the last sample.
        double appCpu = 0;
        long appMemory = 0;
        foreach (ProcessUsage process in setup.Processes)
        {
            if (process.Pid == self)
            {
                appCpu += process.CpuPercent;
                appMemory += process.MemoryBytes;
            }
            else
            {
                SourceTally tally = Tally(AppSource, "Tray app");
                tally.Cpu += process.CpuPercent;
                tally.Memory += process.MemoryBytes;
                tally.Processes++;
            }
        }

        Dictionary<long, double> ran = [];
        foreach (WorkInfo item in work.Items.Where(item => !item.ForProcess))
        {
            TimeSpan before = _ownTime.GetValueOrDefault(item.Id);
            _ownTime[item.Id] = item.OwnTime;
            double delta = (item.OwnTime - before).TotalSeconds;
            if (delta > 0.01)
            {
                ran[item.Id] = delta;
            }
        }

        foreach (long gone in _ownTime.Keys.Where(id => !work.Items.Any(item => item.Id == id)).ToList())
        {
            _ownTime.Remove(gone);
        }

        // Work with a process of its own running is waiting on it (Auto Chapters on its ffmpeg, say): that process is its
        // cost, measured exactly, so it takes no share of the app's. Work that draws frames in the app while ffmpeg
        // encodes them names the drawing as a narrower scope of its own, which does.
        Dictionary<long, double> weights = ran.Where(pair => !withProcesses.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);

        double totalWeight = weights.Values.Sum();
        double cpuLeft = appCpu;
        long memoryLeft = appMemory;
        if (totalWeight <= 0)
        {
            // Idle: learn the app's usual level, slowly so one busy moment doesn't skew it.
            _idleCpu = _idleCpu is { } cpu ? cpu * 0.8 + appCpu * 0.2 : appCpu;
            _idleMemory = _idleMemory is { } memory ? memory * 0.9 + appMemory * 0.1 : appMemory;
        }
        else
        {
            double cpuExtra = Math.Max(0, appCpu - (_idleCpu ?? 0));
            double memoryExtra = Math.Max(0, appMemory - (_idleMemory ?? appMemory));
            Dictionary<long, WorkInfo> items = work.Items.ToDictionary(item => item.Id);
            foreach ((long id, double weight) in weights)
            {
                if (items.TryGetValue(id, out WorkInfo? item))
                {
                    double share = weight / totalWeight;
                    double cpu = cpuExtra * share;
                    long memory = (long)(memoryExtra * share);
                    SourceTally tally = Tally(item.Source, item.Action, item.Via);
                    tally.Cpu += cpu;
                    tally.InAppCpu += cpu;
                    tally.Memory += memory;
                    AddToWork(id, cpu, memory);
                    cpuLeft -= cpu;
                    memoryLeft -= memory;
                }
            }
        }

        SourceTally app = Tally(AppSource, AppAction);
        app.Cpu += Math.Max(0, cpuLeft);
        app.InAppCpu += Math.Max(0, cpuLeft);
        app.Memory += Math.Max(0, memoryLeft);
        app.Processes++;

        foreach (WorkInfo item in work.Items.Where(item => item.Ended is null && !item.Hidden))
        {
            Tally(item.Source, item.Action, item.Via).Running++;
        }

        // Each piece of work's own running total, for the recent work list.
        lock (_gate)
        {
            foreach (WorkInfo item in work.Items.Where(item => !item.Hidden))
            {
                if (!_tallies.TryGetValue(item.Id, out WorkTally? tally))
                {
                    _tallies[item.Id] = tally = new WorkTally();
                }

                tally.Info = item;
                if (byWork.TryGetValue(item.Id, out (double Cpu, long Memory) used))
                {
                    tally.CpuPercentSeconds += used.Cpu * seconds;
                    tally.PeakCpu = Math.Max(tally.PeakCpu, used.Cpu);
                    tally.PeakMemory = Math.Max(tally.PeakMemory, used.Memory);
                }
            }

            DateTimeOffset cutoff = DateTimeOffset.UtcNow - HistoryLength;
            foreach (long old in _tallies.Where(pair => pair.Value.Info.Ended is { } ended && ended < cutoff).Select(pair => pair.Key).ToList())
            {
                _tallies.Remove(old);
            }

            if (_tallies.Count > RecentWorkKept)
            {
                foreach (long extra in _tallies.Values.Where(tally => tally.Info.Ended is not null)
                             .OrderBy(tally => tally.Info.Started).Take(_tallies.Count - RecentWorkKept).Select(tally => tally.Info.Id).ToList())
                {
                    _tallies.Remove(extra);
                }
            }
        }

        return [.. sources.Values
            .Select(tally => new SourceUsage(tally.Key, tally.Source, tally.Action, tally.Via.Count > 0 ? string.Join(", ", tally.Via.Order()) : null,
                tally.Cpu, tally.InAppCpu, tally.Memory, tally.Processes, tally.Running))
            .OrderByDescending(source => source.CpuPercent)];
    }

    private UsageGroup Measure(string key, string name, string description, IEnumerable<ProcessEntry> entries, Func<int, Process?> open, DateTimeOffset now, int cores,
        Func<ProcessEntry, string?> source)
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
                processes.Add(new ProcessUsage(entry.Pid, entry.Name, Role(entry.Name), percent, memory, source(entry)));
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

    /// <summary>What an ErsatzTV process is doing, as a source's action.</summary>
    public static string ErsatzRole(string name) => Role(name) switch
    {
        "ErsatzTV" => "Streaming server",
        "Transcoder (ffmpeg)" => "Channel transcoding (ffmpeg)",
        var role => role
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
                + "Turn off 24/7 on channels nobody needs instantly, lower the picture size or bitrate, or move encoding to the graphics card (App settings › Hardware and video encoding)."));
        }

        // The heaviest named work over the last minute (not ErsatzTV's channels, nor the app's own idling): the likeliest cause of a hitch.
        var heaviest = recent
            .SelectMany(sample => sample.Sources)
            .Where(source => source.Source != "ErsatzTV" && !(source.Source == AppSource && source.Action == AppAction))
            .GroupBy(source => source.Key)
            .Select(group => (Key: group.Key, Average: group.Sum(source => source.CpuPercent) / recent.Length, Peak: group.Max(source => source.CpuPercent)))
            .OrderByDescending(source => source.Average)
            .FirstOrDefault();
        if (heaviest.Key is not null && heaviest.Average > 15)
        {
            advice.Add(new PerformanceAdvice(heaviest.Average > 40 ? AdviceLevel.Warning : AdviceLevel.Info, $"{heaviest.Key} is the busiest right now",
                $"It has averaged {heaviest.Average:0}% CPU over the last minute (peaking at {heaviest.Peak:0}%). If channels are stuttering, that's the first place to look: "
                + "its plugin's settings may let it use fewer threads, run less often, or use the graphics card."));
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
                $"{software} of {channels} channel{(channels == 1 ? " is" : "s are")} encoded on the processor. Choosing your graphics card under "
                + "App settings › Hardware and video encoding › ErsatzTV channels (or on a channel's General tab, for just that one) usually cuts a channel's CPU by most of it."));
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

    /// <summary>
    /// How many channels ErsatzTV encodes on the processor, of how many: by the channel's own setting, else the app-wide
    /// default. Worked out at most every 15 seconds.
    /// </summary>
    private (int Software, int Total) SoftwareEncodedChannels(DateTimeOffset now)
    {
        if (now - _encoders.At < TimeSpan.FromSeconds(15))
        {
            return (_encoders.Software, _encoders.Total);
        }

        int software = 0, total = 0;
        ErsatzTvEncodingOptions global = ersatzOptions.Value.Encoding;
        Dictionary<int, ChannelEncodingConfig?> own = [];
        foreach (ChannelConfig configured in services.GetService<SchedulerConfigProvider>()?.Current.Channels ?? [])
        {
            own.TryAdd(configured.Number, configured.Encoding);
        }

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
                    ChannelEncodingConfig? channelEncoding = int.TryParse(Path.GetFileName(channel), out int number)
                        ? own.GetValueOrDefault(number)
                        : null;
                    if (ErsatzTvEncoding.Resolve(global, channelEncoding).Hardware == ErsatzTvEncoding.Software)
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

    /// <summary>A source's numbers as they're added up for one sample.</summary>
    private sealed class SourceTally(string key, string source, string action)
    {
        public string Key { get; } = key;

        public string Source { get; } = source;

        public string Action { get; } = action;

        public HashSet<string> Via { get; } = [];

        public double Cpu { get; set; }

        public double InAppCpu { get; set; }

        public long Memory { get; set; }

        public int Processes { get; set; }

        public int Running { get; set; }
    }

    /// <summary>One piece of work's running total.</summary>
    private sealed class WorkTally
    {
        public WorkInfo Info { get; set; } = null!;

        public double CpuPercentSeconds { get; set; }

        public double PeakCpu { get; set; }

        public long PeakMemory { get; set; }

        public WorkUsage ToUsage()
        {
            double seconds = Math.Max(1, ((Info.Ended ?? DateTimeOffset.UtcNow) - Info.Started).TotalSeconds);
            return new WorkUsage(Info.Id, Info.Source, Info.Action, Info.Subject, Info.Via, Info.Started, Info.Ended,
                Math.Min(100, CpuPercentSeconds / seconds), PeakCpu, PeakMemory);
        }
    }
}
