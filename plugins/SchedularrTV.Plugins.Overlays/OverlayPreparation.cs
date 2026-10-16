using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Hosting;
using SchedularrTV.Core.Publishing;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Plugins.Playout;

namespace SchedularrTV.Overlays;

/// <summary>Read on each pass so saving plugin settings takes effect without restarting.</summary>
public sealed class OverlayPreparationSettings(Func<string, string?> read)
{
    public bool WatchedOnly => !string.Equals(read("WatchedOnly"), "false", StringComparison.OrdinalIgnoreCase);
    public TimeSpan LeadTime => TimeSpan.FromMinutes(double.TryParse(read("LeadTimeMinutes"), NumberStyles.Float,
        CultureInfo.InvariantCulture, out double minutes) && double.IsFinite(minutes) && minutes >= 0 ? Math.Min(minutes, 525600) : 60);

    public bool MayPrepare(int channelNumber, ScheduleEntry entry, OverlayShowing showing, DateTimeOffset now, IChannelViewers? viewers)
    {
        if (WatchedOnly && viewers?.IsWatched(channelNumber) == false) return false;
        if (NextAppearance(entry, showing, now) is not { } due) return false;
        TimeSpan lead = LeadTime;
        return lead == TimeSpan.Zero || due - now <= lead;
    }

    /// <summary>The next visible instant, including an appearance already in progress, clipped to this schedule entry.</summary>
    public static DateTimeOffset? NextAppearance(ScheduleEntry entry, OverlayShowing showing, DateTimeOffset now)
    {
        if (entry.End <= now) return null;
        DateTimeOffset from = now > entry.Start ? now : entry.Start;
        if (showing.Timing is not { } timing) return from;
        if (timing.FrequencyMs <= 0 || timing.VisibleMs <= 0) return null;
        DateTimeOffset origin = timing.Wall ? DateTimeOffset.UnixEpoch : entry.Start;
        double elapsed = (from - origin).TotalMilliseconds;
        double cycle = timing.PhaseMs + Math.Floor((elapsed - timing.PhaseMs) / timing.FrequencyMs) * timing.FrequencyMs;
        if (cycle + timing.VisibleMs <= elapsed) cycle += timing.FrequencyMs;
        DateTimeOffset due = origin.AddMilliseconds(Math.Max(elapsed, cycle));
        return due < entry.End ? due : null;
    }
}

public sealed record DeferredOverlay(ScheduleEntry Entry, OverlayShowing Showing);

/// <summary>Rechecks deferred appearances every 30 seconds, so tuning in and entering the lead window trigger preparation.</summary>
public sealed class OverlayPreparationMonitor(OverlayPreparationSettings settings, IServiceProvider services, TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly ConcurrentDictionary<int, IReadOnlyList<DeferredOverlay>> _waiting = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public void Track(int channelNumber, IReadOnlyList<DeferredOverlay> waiting)
    {
        if (waiting.Count == 0) _waiting.TryRemove(channelNumber, out _);
        else _waiting[channelNumber] = waiting;
    }

    public void Check()
    {
        DateTimeOffset now = _time.GetUtcNow();
        IChannelViewers? viewers = services.GetService<IChannelViewers>();
        if (_waiting.Any(channel => channel.Value.Any(candidate => settings.MayPrepare(channel.Key, candidate.Entry, candidate.Showing, now, viewers))))
            services.GetService<IScheduleRepublisher>()?.RequestRepublish();
        foreach (var channel in _waiting)
            if (channel.Value.All(candidate => candidate.Entry.End <= now)) _waiting.TryRemove(channel.Key, out _);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) Check();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
