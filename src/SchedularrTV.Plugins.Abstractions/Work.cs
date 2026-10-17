using System.Diagnostics;

namespace SchedularrTV.Plugins.Performance;

/// <summary>
/// Names what SchedularrTV and its plugins are busy doing, so the Performance page can show where CPU and memory go
/// ("Auto Chapters – Chapter detection", "Local AI – Whisper transcribing") rather than just "SchedularrTV" or "Helpers".
/// </summary>
/// <remarks>
/// <para>
/// Wrap a piece of work in a scope: <c>using var work = Work.Begin("Auto Chapters", "Chapter detection", episode);</c>.
/// The scope flows through <c>await</c>s like a logging scope, so work started inside it (another plugin's text
/// generator, say) nests under it and is shown as done "for" it.
/// </para>
/// <para>
/// Programs started for the work (ffmpeg, a browser, Piper…) are tied to it with <see cref="Track"/> straight after
/// <see cref="Process.Start()"/>; whatever they start in turn is counted with them. Work done in this process (Whisper,
/// the language model, image processing) is measured as the app's CPU above its usual idle level, shared between the
/// scopes running at the time.
/// </para>
/// <para>Everything here is cheap and safe to call from anywhere, scope or not.</para>
/// </remarks>
public static class Work
{
    /// <summary>How many finished scopes are remembered for the Performance page's recent work list.</summary>
    private const int FinishedKept = 400;

    private static readonly AsyncLocal<WorkScope?> Ambient = new();
    private static readonly Lock Gate = new();
    private static readonly List<WorkScope> Running = [];
    private static readonly Queue<WorkScope> Finished = new();
    private static readonly Dictionary<int, (WorkScope Owner, DateTimeOffset At)> Processes = [];
    private static long _nextId;

    /// <summary>
    /// The innermost scope running in this flow of work, if any. Background work handed off from a scope that has since
    /// ended (a <c>Task.Run</c>, say) falls back to the nearest one above it that's still running.
    /// </summary>
    public static WorkScope? Current
    {
        get
        {
            for (WorkScope? scope = Ambient.Value; scope is not null; scope = scope.Parent)
            {
                if (scope.IsRunning)
                {
                    return scope;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Starts naming the work done from here (and in everything it awaits) until the scope is disposed.
    /// </summary>
    /// <param name="source">Who's doing it: a plugin's name ("Generated Promos"), or "SchedularrTV" for the app itself.</param>
    /// <param name="action">What it's doing, in a few words ("Generating promo").</param>
    /// <param name="subject">What it's doing it to, if that helps ("Friends S01E02"); shown in the recent work list.</param>
    public static WorkScope Begin(string source, string action, string? subject = null)
    {
        WorkScope? parent = Current;
        var scope = new WorkScope(Interlocked.Increment(ref _nextId), source, action, subject, parent, processId: null);
        long now = Stopwatch.GetTimestamp();
        lock (Gate)
        {
            parent?.ChildStarted(now);
            scope.StartOwnTime(now);
            Running.Add(scope);
        }

        Ambient.Value = scope;
        return scope;
    }

    /// <summary>
    /// Like <see cref="Begin"/>, unless work from <paramref name="source"/> is already running here: then that goes on
    /// naming it and this returns null (a preview rendering a promo stays "Generating preview"). <c>using</c> takes null.
    /// </summary>
    public static WorkScope? BeginUnlessWithin(string source, string action, string? subject = null)
    {
        for (WorkScope? scope = Current; scope is not null; scope = scope.Parent)
        {
            if (scope.IsRunning && string.Equals(scope.Source, source, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return Begin(source, action, subject);
    }

    /// <summary>
    /// Ties <paramref name="process"/> (and whatever it starts) to the work running here, so its CPU and memory are shown
    /// under that work's name. Outside any scope, <paramref name="source"/> and <paramref name="action"/> name it
    /// instead, for as long as it runs; with neither, it's shown as one of SchedularrTV's own.
    /// </summary>
    /// <returns><paramref name="process"/>, so a call can wrap <c>Process.Start</c>.</returns>
    public static Process? Track(Process? process, string? source = null, string? action = null)
    {
        if (process is null)
        {
            return null;
        }

        int pid;
        try
        {
            pid = process.Id;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return process;
        }

        WorkScope? owner = Current;
        if (owner is null)
        {
            if (source is null)
            {
                return process;
            }

            // Its own scope, ended once the process is seen to have gone. Hidden from the recent work list: a
            // library scan's hundreds of quick ffprobes would crowd out everything else there.
            owner = new WorkScope(Interlocked.Increment(ref _nextId), source, action ?? "Running", subject: null, parent: null, processId: pid)
            {
                Hidden = true
            };
        }

        Tie(pid, owner);
        return process;
    }

    /// <summary>
    /// Records <paramref name="owner"/> as <paramref name="pid"/>'s, ending whatever stood in for an earlier process with
    /// that id. Without the Performance page's monitor to say which processes have gone, entries are let go after a day.
    /// </summary>
    private static void Tie(int pid, WorkScope owner)
    {
        DateTimeOffset at = DateTimeOffset.UtcNow;
        List<WorkScope> replaced = [];
        lock (Gate)
        {
            if (owner.ProcessId is not null && owner.IsRunning)
            {
                Running.Add(owner);
            }

            if (Processes.TryGetValue(pid, out (WorkScope Owner, DateTimeOffset At) earlier) && earlier.Owner.ProcessId == pid)
            {
                replaced.Add(earlier.Owner);
            }

            Processes[pid] = (owner, at);

            if (Processes.Count > 1000)
            {
                foreach (KeyValuePair<int, (WorkScope Owner, DateTimeOffset At)> stale in Processes.Where(pair => at - pair.Value.At > TimeSpan.FromDays(1)).ToList())
                {
                    Processes.Remove(stale.Key);
                    if (stale.Value.Owner.ProcessId == stale.Key)
                    {
                        replaced.Add(stale.Value.Owner);
                    }
                }
            }
        }

        foreach (WorkScope scope in replaced)
        {
            End(scope);
        }
    }

    /// <summary>
    /// Names <paramref name="process"/> (and whatever it starts) as <paramref name="source"/> – <paramref name="action"/>
    /// for as long as it runs, whatever work is running here: for long-lived processes (a live channel's browser and
    /// encoder, an IPTV relay) that outlast the work that started them.
    /// </summary>
    public static Process? TrackAs(Process? process, string source, string action)
    {
        if (process is null)
        {
            return null;
        }

        int pid;
        try
        {
            pid = process.Id;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return process;
        }

        Tie(pid, new WorkScope(Interlocked.Increment(ref _nextId), source, action, subject: null, parent: Current, processId: pid));
        return process;
    }

    /// <summary>Everything running now, and what finished since <paramref name="since"/>, with the processes tied to them.</summary>
    public static WorkSnapshot Snapshot(DateTimeOffset since)
    {
        long now = Stopwatch.GetTimestamp();
        lock (Gate)
        {
            Dictionary<long, WorkInfo> work = [];
            foreach (WorkScope scope in Running)
            {
                work[scope.Id] = scope.Info(now);
            }

            foreach (WorkScope scope in Finished.Where(scope => scope.Ended >= since))
            {
                work.TryAdd(scope.Id, scope.Info(now));
            }

            // Work that's finished but left a process running (a stream's ffmpeg, say) still names it.
            foreach ((WorkScope Owner, DateTimeOffset At) tracked in Processes.Values)
            {
                if (!work.ContainsKey(tracked.Owner.Id))
                {
                    work[tracked.Owner.Id] = tracked.Owner.Info(now);
                }
            }

            return new WorkSnapshot([.. work.Values], Processes.ToDictionary(pair => pair.Key, pair => pair.Value.Owner.Id));
        }
    }

    /// <summary>
    /// Forgets the processes that weren't in a list of running processes read at <paramref name="readAt"/> (one tied
    /// after that may simply not have been in it yet), ending the scopes that were only standing in for them.
    /// </summary>
    public static void ForgetProcessesExcept(IReadOnlySet<int> alive, DateTimeOffset readAt)
    {
        List<WorkScope> ended = [];
        lock (Gate)
        {
            foreach (KeyValuePair<int, (WorkScope Owner, DateTimeOffset At)> tracked in Processes.ToList())
            {
                if (tracked.Value.At < readAt && !alive.Contains(tracked.Key))
                {
                    Processes.Remove(tracked.Key);
                    if (tracked.Value.Owner.ProcessId == tracked.Key)
                    {
                        ended.Add(tracked.Value.Owner);
                    }
                }
            }
        }

        foreach (WorkScope scope in ended)
        {
            End(scope);
        }
    }

    internal static void End(WorkScope scope)
    {
        long now = Stopwatch.GetTimestamp();
        lock (Gate)
        {
            if (!scope.IsRunning)
            {
                return;
            }

            scope.Finish(now);
            Running.Remove(scope);
            if (!scope.Hidden)
            {
                Finished.Enqueue(scope);
                while (Finished.Count > FinishedKept)
                {
                    Finished.Dequeue();
                }
            }

            // A process's stand-in never counted as its parent's child (it isn't work done in this process).
            if (scope.ProcessId is null && scope.Parent is { IsRunning: true } parent)
            {
                parent.ChildEnded(now);
            }
        }

        if (scope.ProcessId is null && ReferenceEquals(Ambient.Value, scope))
        {
            Ambient.Value = scope.Parent;
        }
    }
}

/// <summary>
/// One piece of named work, from <see cref="Work.Begin"/> until it's disposed. Its own time is the time it ran with no
/// narrower scope inside it running: that's what in-process CPU is shared out by.
/// </summary>
public sealed class WorkScope : IDisposable
{
    private int _children;
    private long _ownSince;
    private long _ownTicks;
    private volatile bool _ended;

    internal WorkScope(long id, string source, string action, string? subject, WorkScope? parent, int? processId)
    {
        Id = id;
        Source = source;
        Action = action;
        Subject = subject;
        Parent = parent;
        ProcessId = processId;
        Started = DateTimeOffset.UtcNow;
    }

    public long Id { get; }

    public string Source { get; }

    public string Action { get; }

    public string? Subject { get; }

    public WorkScope? Parent { get; }

    public DateTimeOffset Started { get; }

    public DateTimeOffset? Ended { get; private set; }

    public bool IsRunning => !_ended;

    /// <summary>"Source – Action".</summary>
    public string Label => $"{Source} – {Action}";

    /// <summary>The process this scope stands in for, when it was made by <see cref="Work.Track"/> outside any scope.</summary>
    internal int? ProcessId { get; }

    /// <summary>Left out of the recent work list (it still names its process's use).</summary>
    internal bool Hidden { get; init; }

    public void Dispose() => Work.End(this);

    // The rest is called under Work's lock.

    internal void StartOwnTime(long now) => _ownSince = now;

    internal void ChildStarted(long now)
    {
        if (_children++ == 0)
        {
            StopOwnTime(now);
        }
    }

    internal void ChildEnded(long now)
    {
        if (_children > 0 && --_children == 0)
        {
            _ownSince = now;
        }
    }

    internal void Finish(long now)
    {
        StopOwnTime(now);
        Ended = DateTimeOffset.UtcNow;
        _ended = true;
    }

    internal WorkInfo Info(long now)
    {
        long own = _ownTicks + (_ownSince != 0 ? now - _ownSince : 0);
        string? via = null;
        for (WorkScope? parent = Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.Source != Source || parent.Action != Action)
            {
                via = parent.Label;
                break;
            }
        }

        return new WorkInfo(Id, Source, Action, Subject, via, Started, Ended, Stopwatch.GetElapsedTime(0, own), ProcessId is not null) { Hidden = Hidden };
    }

    private void StopOwnTime(long now)
    {
        if (_ownSince != 0)
        {
            _ownTicks += now - _ownSince;
            _ownSince = 0;
        }
    }
}

/// <summary>A scope as the Performance page sees it.</summary>
/// <param name="Via">The work it's being done for, when it was started inside another plugin's work.</param>
/// <param name="OwnTime">How long it has run with nothing narrower running inside it.</param>
/// <param name="ForProcess">Made by <see cref="Work.Track"/> to name a process started outside any scope.</param>
public sealed record WorkInfo(
    long Id,
    string Source,
    string Action,
    string? Subject,
    string? Via,
    DateTimeOffset Started,
    DateTimeOffset? Ended,
    TimeSpan OwnTime,
    bool ForProcess)
{
    public string Label => $"{Source} – {Action}";

    /// <summary>A quick process named outside any work: counted, but not listed as a job of its own.</summary>
    public bool Hidden { get; init; }
}

/// <summary>The work running and recently finished, and which scope each tracked process id belongs to.</summary>
public sealed record WorkSnapshot(IReadOnlyList<WorkInfo> Items, IReadOnlyDictionary<int, long> Processes);
