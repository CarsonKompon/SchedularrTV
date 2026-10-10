using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Publishing;
using SchedularrTV.Providers.ErsatzTv;

namespace SchedularrTV.Service;

/// <summary>How keeping one always-on channel playing is going.</summary>
/// <param name="LastPlaying">When ErsatzTV last answered for it; null if it never has since the app started.</param>
/// <param name="Problem">Why the last attempt failed; null when it worked.</param>
public sealed record AlwaysOnStatus(int Number, string Name, DateTimeOffset? LastPlaying, string? Problem);

/// <summary>
/// Keeps the channels set to play around the clock (<see cref="ChannelConfig.AlwaysOn"/>) running in ErsatzTV.
/// </summary>
/// <remarks>
/// ErsatzTV Next starts a channel when a player asks for its playlist and stops it 90 seconds after the last request,
/// and it has no setting to keep one going. So this asks for each always-on channel's playlists every
/// <see cref="Interval"/>, the way a player would, without downloading the video. ErsatzTV keeps transcoding in real
/// time while the session is alive, so tuning in finds the channel already playing, mid-programme.
/// It also keeps <see cref="AlwaysOnChannels"/> current, so the viewer sources answer from each channel's setting
/// rather than taking this for a viewer.
/// </remarks>
public sealed class AlwaysOnKeeper(
    SchedulerConfigProvider config,
    AlwaysOnChannels alwaysOn,
    IOptions<ErsatzTvOptions> options,
    IHttpClientFactory clients,
    TimeProvider clock,
    ILogger<AlwaysOnKeeper> logger) : BackgroundService
{
    /// <summary>Well inside ErsatzTV's 90-second reap, with room for a request that's slow while the channel starts.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    private readonly ConcurrentDictionary<int, AlwaysOnStatus> _status = new();

    /// <summary>Each always-on channel, by number.</summary>
    public IReadOnlyList<AlwaysOnStatus> Statuses => [.. _status.Values.OrderBy(status => status.Number)];

    /// <summary>The always-on channels in a schedule: number → counts as watched.</summary>
    public static IReadOnlyDictionary<int, bool> Wanted(SchedulerConfig schedule) =>
        schedule.Channels
            .Where(channel => channel.AlwaysOn == true)
            .GroupBy(channel => channel.Number)
            .ToDictionary(group => group.Key, group => group.First().AlwaysOnCountsAsWatched ?? true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The viewer sources follow a saved change at once rather than on the next round.
        alwaysOn.Set(Wanted(config.Current));
        config.ConfigChanged += OnConfigChanged;
        try
        {
            await RunAsync(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            config.ConfigChanged -= OnConfigChanged;
        }
    }

    private void OnConfigChanged(SchedulerConfig schedule) => alwaysOn.Set(Wanted(schedule));

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            DateTimeOffset started = clock.GetUtcNow();
            try
            {
                await KeepAllAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Couldn't keep the always-on channels playing this time round.");
            }

            TimeSpan wait = Interval - (clock.GetUtcNow() - started);
            try
            {
                await Task.Delay(wait > TimeSpan.FromSeconds(1) ? wait : TimeSpan.FromSeconds(1), clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task KeepAllAsync(CancellationToken cancellationToken)
    {
        // Read every time round: switching a channel on or off applies as soon as the schedule is saved.
        SchedulerConfig schedule = config.Current;
        List<ChannelConfig> channels = [.. schedule.Channels.Where(channel => channel.AlwaysOn == true)
            .GroupBy(channel => channel.Number).Select(group => group.First())];
        alwaysOn.Set(Wanted(schedule));

        foreach (int gone in _status.Keys.Except(channels.Select(channel => channel.Number)).ToList())
        {
            _status.TryRemove(gone, out _);
            _failingSince.TryRemove(gone, out _);
            _warned.TryRemove(gone, out _);
        }

        if (channels.Count == 0)
        {
            return;
        }

        string upstream = ErsatzTvPreviewProxy.Upstream(options.Value);
        await Task.WhenAll(channels.Select(channel => KeepAsync(upstream, channel, cancellationToken))).ConfigureAwait(false);
    }

    private async Task KeepAsync(string upstream, ChannelConfig channel, CancellationToken cancellationToken)
    {
        string? problem;
        try
        {
            using HttpClient http = clients.CreateClient(ErsatzTvPreviewProxy.ClientName);
            Uri channelPlaylist = new($"{upstream}/channel/{channel.Number.ToString(CultureInfo.InvariantCulture)}.m3u8");
            string playlist = await http.GetStringAsync(channelPlaylist, cancellationToken).ConfigureAwait(false);

            // The channel's playlist points at its session's; asking for that too is what a player does next.
            if (NextPlaylist(playlist, channelPlaylist) is { } session)
            {
                await http.GetStringAsync(session, cancellationToken).ConfigureAwait(false);
            }

            problem = null;
        }
        catch (HttpRequestException ex)
        {
            problem = ex.StatusCode is { } code ? $"ErsatzTV answered {(int)code} {code}" : $"ErsatzTV isn't answering at {upstream}: {ex.Message}";
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            problem = $"ErsatzTV at {upstream} didn't answer in time";
        }
        catch (UriFormatException)
        {
            problem = $"ErsatzTV's address ({upstream}) isn't valid; check its bind address and port in App settings";
        }

        DateTimeOffset now = clock.GetUtcNow();
        _status.TryGetValue(channel.Number, out AlwaysOnStatus? before);
        _failingSince.TryGetValue(channel.Number, out DateTimeOffset failingSince);
        _status[channel.Number] = new AlwaysOnStatus(channel.Number, channel.Name, problem is null ? now : before?.LastPlaying, problem);

        if (problem is null)
        {
            _failingSince.TryRemove(channel.Number, out _);
            if (_warned.TryRemove(channel.Number, out _) || before is null || before.Problem is not null)
            {
                logger.LogInformation("Channel {Number} ({Name}) is playing around the clock.", channel.Number, channel.Name);
            }

            return;
        }

        if (!_failingSince.ContainsKey(channel.Number))
        {
            _failingSince[channel.Number] = failingSince = now;
        }

        // ErsatzTV is still starting when the app does (and restarts when the channel list changes), so a failure is only
        // reported once it has lasted a while, and only once until the channel plays again.
        if (now - failingSince >= WarnAfter && _warned.TryAdd(channel.Number, 0))
        {
            logger.LogWarning("Couldn't keep channel {Number} ({Name}) playing: {Problem}. Still trying every {Interval} seconds.",
                channel.Number, channel.Name, problem, Interval.TotalSeconds);
        }
    }

    private static readonly TimeSpan WarnAfter = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<int, DateTimeOffset> _failingSince = new();
    private readonly ConcurrentDictionary<int, byte> _warned = new();

    /// <summary>The first playlist a master playlist names (a session's, or a variant's), resolved against where it came from.</summary>
    public static Uri? NextPlaylist(string playlist, Uri from)
    {
        foreach (string raw in playlist.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string path = line.Split('?', 2)[0];
            if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(from, line, out Uri? next))
            {
                return next;
            }
        }

        return null;
    }
}
