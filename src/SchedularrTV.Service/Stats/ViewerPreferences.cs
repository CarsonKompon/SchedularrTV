using System.Globalization;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.Service.Stats;

/// <summary>One of viewer-aware scheduling's thresholds: where the channel is, and what it needs.</summary>
public sealed record ViewerAwareCheck(string Label, string Current, string Needed, bool Met);

/// <summary>How viewer-aware scheduling treats one show.</summary>
/// <param name="Share">The share of its airtime someone watched, 0 to 1.</param>
/// <param name="Factor">How much more (above 1) or less (below 1) often it comes round.</param>
public sealed record ShowFactor(string Show, int AiredMinutes, double Share, double Factor);

/// <summary>Where viewer-aware scheduling stands on a channel.</summary>
public sealed record ViewerAwareStatus(
    bool Active,
    IReadOnlyList<ViewerAwareCheck> Checks,
    IReadOnlyList<ShowFactor> Shows,
    double ChannelShare)
{
    /// <summary>The factors the planner uses, by show; only shows that move. Empty while not active.</summary>
    public IReadOnlyDictionary<string, double> Factors { get; } = Active
        ? Shows.Where(show => Math.Abs(show.Factor - 1) > ViewerWeights.Neutral)
            .GroupBy(show => show.Show, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Factor, StringComparer.OrdinalIgnoreCase)
        : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Viewer-aware scheduling, from the viewing history: each show's factor is how much more (or less) of its airtime
/// gets watched than the channel's average, trusted more the longer it has aired, and kept within the strength's
/// bounds. Nothing moves until the channel passes every threshold in <see cref="ViewerAwareConfig"/>.
/// </summary>
public sealed class ViewerPreferences(ViewingStore store, TimeProvider clock) : IViewerPreference
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DateTimeOffset At, string Settings, ViewerAwareStatus Status)> _cache = new(StringComparer.Ordinal);

    /// <summary>Airtime, in minutes, a show needs before its own viewing is trusted half way over the channel average.</summary>
    public const double ConfidenceMinutes = 180;

    public IReadOnlyDictionary<string, double>? TitleFactors(ChannelConfig channel)
    {
        if (channel.ViewerAware is not { Enabled: true })
        {
            return null;
        }

        ViewerAwareStatus status = Status(channel);
        return status.Active && status.Factors.Count > 0 ? status.Factors : null;
    }

    /// <summary>Where viewer-aware scheduling stands on a channel (worked out even while it's switched off, for the Stats tab).</summary>
    public ViewerAwareStatus Status(ChannelConfig channel, bool fresh = false)
    {
        ViewerAwareConfig settings = channel.ViewerAware ?? new ViewerAwareConfig();
        string key = Fingerprint(settings);
        DateTimeOffset now = clock.GetUtcNow();
        lock (_gate)
        {
            if (!fresh && _cache.TryGetValue(channel.Id, out var cached) && cached.Settings == key && now - cached.At < CacheFor)
            {
                return cached.Status;
            }
        }

        long nowMinute = ViewingStore.ToMinute(now);
        long from = nowMinute - Math.Clamp(settings.LookbackDays, 1, 3650) * 24L * 60;
        ViewerAwareStatus status = Evaluate(settings, store.Airings(channel.Id, from), store.Viewers(channel.Id, from), store.FirstMinute(channel.Id), nowMinute);
        lock (_gate)
        {
            _cache[channel.Id] = (now, key, status);
        }

        return status;
    }

    /// <summary>The factors' bounds for a strength: (most often, least often).</summary>
    public static (double Boost, double Floor) Bounds(ViewerAwareStrength strength) => strength switch
    {
        ViewerAwareStrength.Gentle => (1.5, 2 / 3.0),
        ViewerAwareStrength.Strong => (4, 0.2),
        _ => (2.5, 0.4)
    };

    public static ViewerAwareStatus Evaluate(ViewerAwareConfig settings, IReadOnlyList<AiringRow> airings, IReadOnlyList<ViewerMinute> viewers,
        long? firstMinute, long nowMinute)
    {
        int lookback = Math.Clamp(settings.LookbackDays, 1, 3650);
        long from = nowMinute - lookback * 24L * 60;
        double historyDays = firstMinute is { } first ? Math.Max(0, nowMinute - first) / (24.0 * 60) : 0;

        // Average different viewers a day, over the days of the lookback the channel has history for.
        long counted = Math.Max(from, firstMinute ?? nowMinute);
        int days = Math.Max(1, (int)Math.Ceiling((nowMinute - counted) / (24.0 * 60)));
        double dailyViewers = viewers.Where(viewer => viewer.Minute >= counted)
            .GroupBy(viewer => (viewer.Minute - counted) / (24 * 60))
            .Sum(day => day.Select(viewer => (viewer.Name, viewer.Via)).Distinct().Count()) / (double)days;

        List<AiringRow> programmes = [.. airings.Where(airing => airing.Kind != "break" && airing.Start / 60 >= from)];
        double watchedHours = airings.Where(airing => airing.Start / 60 >= from).Sum(airing => airing.WatchedMinutes) / 60.0;
        int aired = programmes.Sum(airing => airing.AiredMinutes);
        double channelShare = aired == 0 ? 0 : programmes.Sum(airing => airing.WatchedMinutes) / (double)aired;

        List<ViewerAwareCheck> checks =
        [
            new("History", Days(historyDays), $"{settings.MinHistoryDays} days", historyDays >= settings.MinHistoryDays),
            new("Average daily viewers", dailyViewers.ToString("0.#", CultureInfo.CurrentCulture),
                settings.MinAverageDailyViewers.ToString("0.#", CultureInfo.CurrentCulture), dailyViewers >= settings.MinAverageDailyViewers),
            new("Hours watched", watchedHours.ToString("0.#", CultureInfo.CurrentCulture),
                settings.MinWatchedHours.ToString("0.#", CultureInfo.CurrentCulture), watchedHours >= settings.MinWatchedHours)
        ];

        (double boost, double rest) = Bounds(settings.Strength);
        List<ShowFactor> shows = [];
        foreach (IGrouping<string, AiringRow> show in programmes.GroupBy(airing => airing.Show, StringComparer.OrdinalIgnoreCase))
        {
            int showAired = show.Sum(airing => airing.AiredMinutes);
            if (showAired < Math.Max(1, settings.MinShowMinutes))
            {
                continue;
            }

            double share = show.Sum(airing => airing.WatchedMinutes) / (double)showAired;
            shows.Add(new ShowFactor(show.First().Show, showAired, share, Factor(share, channelShare, showAired, boost, rest)));
        }

        bool active = channelShare > 0 && checks.All(check => check.Met);
        return new ViewerAwareStatus(active, checks, [.. shows.OrderByDescending(show => show.Factor).ThenBy(show => show.Show, StringComparer.OrdinalIgnoreCase)], channelShare);
    }

    /// <summary>
    /// A show's factor: its watched share against the channel's, as a ratio, raised to how far its own airtime can
    /// be trusted (so a show seen briefly stays near 1), then kept within the bounds.
    /// </summary>
    public static double Factor(double share, double channelShare, int airedMinutes, double boost, double rest)
    {
        if (channelShare <= 0)
        {
            return 1;
        }

        double ratio = Math.Max(0.01, share / channelShare);
        double confidence = airedMinutes / (airedMinutes + ConfidenceMinutes);
        return Math.Clamp(Math.Pow(ratio, confidence), rest, boost);
    }

    private static string Days(double days) => days < 1 ? $"{days * 24:0} hours" : $"{days:0.#} days";

    private static string Fingerprint(ViewerAwareConfig settings) => string.Join('|', settings.Enabled, settings.Strength, settings.LookbackDays,
        settings.MinHistoryDays, settings.MinAverageDailyViewers, settings.MinWatchedHours, settings.MinShowMinutes);
}
