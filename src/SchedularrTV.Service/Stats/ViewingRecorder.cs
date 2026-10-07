using System.Globalization;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.Service.Stats;

/// <summary>
/// Once a minute, notes what each channel is airing and who's watching it (<see cref="AudienceReader"/>), into the
/// viewing history (<see cref="ViewingStore"/>). Once a day, history older than <see cref="StatsOptions.KeepDays"/> goes.
/// </summary>
public sealed class ViewingRecorder(
    ViewingStore store,
    AudienceReader audience,
    ScheduleCoordinator coordinator,
    IOptionsMonitor<StatsOptions> options,
    TimeProvider clock,
    ILogger<ViewingRecorder> logger) : BackgroundService
{
    private volatile IReadOnlyDictionary<string, IReadOnlyList<Viewer>> _now = new Dictionary<string, IReadOnlyList<Viewer>>();
    private DateTimeOffset _pruned = DateTimeOffset.MinValue;

    /// <summary>Who was watching each channel at the last sample, by channel id.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Viewer>> Now => _now;

    /// <summary>When the last sample was taken; null before the first.</summary>
    public DateTimeOffset? LastSample { get; private set; }

    /// <summary>Raised after each sample, for open Stats tabs.</summary>
    public event Action? Sampled;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // A few seconds into each minute, so a programme that's just started is the one counted.
            DateTimeOffset now = clock.GetUtcNow();
            TimeSpan wait = TimeSpan.FromSeconds(65 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond);
            try
            {
                await Task.Delay(wait > TimeSpan.FromSeconds(60) ? wait - TimeSpan.FromSeconds(60) : wait, clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!options.CurrentValue.Enabled)
            {
                continue;
            }

            try
            {
                await SampleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Viewing stats: couldn't record this minute.");
            }
        }
    }

    public async Task SampleAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        IReadOnlyList<ChannelSchedule> schedules = coordinator.Latest;
        if (schedules.Count == 0)
        {
            return;
        }

        IReadOnlyList<AudienceChannel> channels = [.. schedules.Select(schedule => new AudienceChannel(schedule.ChannelId, schedule.ChannelNumber, schedule.ChannelName))];
        IReadOnlyDictionary<string, IReadOnlyList<Viewer>> watching = await audience.ReadAsync(channels, cancellationToken).ConfigureAwait(false);
        _now = watching;
        LastSample = now;

        long minute = ViewingStore.ToMinute(now);
        List<MinuteSample> samples = [];
        foreach (ChannelSchedule schedule in schedules)
        {
            if (Airing(schedule, now) is not { } entry)
            {
                continue;
            }

            IReadOnlyList<Viewer> who = watching.TryGetValue(schedule.ChannelId, out IReadOnlyList<Viewer>? found) ? found : [];
            (string kind, string show, string? title) = Describe(entry);
            samples.Add(new MinuteSample(minute, schedule.ChannelId, entry.Start.ToUnixTimeSeconds(), kind, show, title, entry.Item?.Id, who.Count, who));
        }

        store.Record(samples);
        Sampled?.Invoke();

        if (now - _pruned > TimeSpan.FromDays(1))
        {
            _pruned = now;
            store.Prune(ViewingStore.ToMinute(now - TimeSpan.FromDays(Math.Clamp(options.CurrentValue.KeepDays, 7, 3650))));
        }
    }

    /// <summary>What a channel is airing at <paramref name="at"/>, from its published schedule.</summary>
    public static ScheduleEntry? Airing(ChannelSchedule schedule, DateTimeOffset at)
    {
        foreach (ScheduleEntry entry in schedule.Entries)
        {
            if (entry.Start <= at && at < entry.End)
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// How an airing is recorded: its kind (a programme, a short show between programmes, or a break of ads and promos),
    /// the show (a series by its name, a movie by its title) and, for an episode, its title.
    /// </summary>
    public static (string Kind, string Show, string? Title) Describe(ScheduleEntry entry)
    {
        string kind = entry.Kind switch
        {
            ScheduleEntryKind.Program => "program",
            ScheduleEntryKind.Interstitial => "interstitial",
            _ => "break"
        };
        var item = entry.Item;
        if (kind == "break")
        {
            return (kind, "Breaks", item?.Title ?? entry.Kind.ToString());
        }

        string show = item?.SeriesName is { Length: > 0 } series ? series
            : item?.Title is { Length: > 0 } itemTitle ? itemTitle
            : entry.Source is { Length: > 0 } source ? source : "Unknown";
        if (item?.SeriesName is not { Length: > 0 } || item.Title is not { Length: > 0 } episode || string.Equals(episode, show, StringComparison.Ordinal))
        {
            return (kind, show, null);
        }

        string? code = item.SeasonNumber is { } season && item.EpisodeNumber is { } number
            ? string.Create(CultureInfo.InvariantCulture, $"S{season:00}E{number:00}")
            : null;
        return (kind, show, code is null ? episode : $"{code} {episode}");
    }
}
