using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;

namespace SchedularrTV.Streamers;

/// <summary>What a channel is airing: the streamer shown (the highest-priority one live) and the address of their video.</summary>
/// <remarks>
/// While nobody is live it can be a replay instead: <see cref="ReplayKey"/>'s recording <see cref="Replay"/>, playing as
/// if it had started at <see cref="ReplayFrom"/> (so a restart picks up where it was), with its sound at
/// <see cref="AudioUrl"/> when that's separate. <see cref="Round"/> counts the times it has started over.
/// </remarks>
public sealed record ChannelOnAir(string? ActiveKey, string? StreamUrl, DateTimeOffset? ResolvedAt, string? Problem, DateTimeOffset? TriedAt = null)
{
    public string? ReplayKey { get; init; }

    public StreamSession? Replay { get; init; }

    public DateTimeOffset ReplayFrom { get; init; }

    public string? AudioUrl { get; init; }

    public int Round { get; init; }

    /// <summary>How far into the replay it is at <paramref name="now"/>.</summary>
    public TimeSpan ReplayPosition(DateTimeOffset now) =>
        Replay is { Length.Ticks: > 0 } replay && now > ReplayFrom ? TimeSpan.FromTicks((now - ReplayFrom).Ticks % replay.Length.Ticks) : TimeSpan.Zero;
}

/// <summary>
/// Checks every streamer on the channels every minute or so: who's live (kept in their history, so predictions improve
/// over time), their past streams now and then, and which streamer each channel should show. When that changes, the
/// stream's address is looked up with yt-dlp and the running channel is restarted on it.
/// </summary>
public sealed class StreamerMonitor(
    StreamerStore store,
    StreamerSettings settings,
    TwitchClient twitch,
    YouTubeClient youtube,
    YtDlp ytdlp,
    StreamerChat chat,
    IServiceProvider services,
    TimeProvider clock,
    ILogger<StreamerMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan HistoryEvery = TimeSpan.FromHours(6);
    private static readonly TimeSpan RetryOpening = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, StreamerState> _streamers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ChannelOnAir> _onAir = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _guides = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>Raised when a channel's streamer or stream address changes, so running channels are restarted on it.</summary>
    public event Action? OnAirChanged;

    /// <summary>Raised after every round of checks, for the Streamers page.</summary>
    public event Action? Checked;

    public DateTimeOffset? LastChecked { get; private set; }

    public StreamerState? State(string key) => _streamers.TryGetValue(key, out StreamerState? state) ? state : null;

    public ChannelOnAir? OnAir(string channelId) => _onAir.TryGetValue(channelId, out ChannelOnAir? onAir) ? onAir : null;

    /// <summary>Checks again now (after channels are saved, say) rather than at the next round.</summary>
    public void Poke()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    /// <summary>The streamer a channel shows now (the first in its list that's live), or null when nobody is.</summary>
    public StreamerEntry? Active(StreamerChannel channel) =>
        channel.Streamers.FirstOrDefault(streamer => State(streamer.Key) is { Live: true });

    /// <summary>Everyone on the channel who's live, highest priority first.</summary>
    public IReadOnlyList<StreamerEntry> Live(StreamerChannel channel) =>
        [.. channel.Streamers.Where(streamer => State(streamer.Key) is { Live: true })];

    /// <summary>A streamer's next expected stream.</summary>
    public StreamPrediction? Prediction(StreamerEntry streamer, DateTimeOffset? from = null) =>
        StreamPredictor.Next(store.History(streamer.Key), from ?? clock.GetUtcNow(), settings.TimeZone, State(streamer.Key)?.Latest?.Scheduled);

    /// <summary>Everyone on the channel who isn't live, by when they're expected next (soonest first); unpredictable ones last.</summary>
    public IReadOnlyList<(StreamerEntry Streamer, StreamPrediction? Prediction)> Upcoming(StreamerChannel channel) =>
        [.. channel.Streamers
            .Where(streamer => State(streamer.Key) is not { Live: true })
            .Select(streamer => (streamer, Prediction(streamer)))
            .OrderBy(pair => pair.Item2?.At ?? DateTimeOffset.MaxValue)];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        store.Changed += Poke;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckAsync(stoppingToken).ConfigureAwait(false);
                    await chat.SweepAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Streamers: a round of checks failed.");
                }

                try
                {
                    await _wake.WaitAsync(settings.PollInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
        finally
        {
            store.Changed -= Poke;
        }
    }

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<StreamerChannel> channels = [.. store.Channels.Where(channel => channel.Enabled)];
        List<StreamerEntry> everyone = [.. channels.SelectMany(channel => channel.Streamers).DistinctBy(streamer => streamer.Key, StringComparer.OrdinalIgnoreCase)];

        using var parallel = new SemaphoreSlim(4);
        await Task.WhenAll(everyone.Select(async streamer =>
        {
            await parallel.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await CheckStreamerAsync(streamer, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                parallel.Release();
            }
        })).ConfigureAwait(false);

        // Streamers no longer on any channel are forgotten (their history stays).
        foreach (string key in _streamers.Keys.Where(key => everyone.All(streamer => !string.Equals(streamer.Key, key, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            _streamers.TryRemove(key, out _);
        }

        bool restart = false;
        foreach (StreamerChannel channel in channels)
        {
            restart |= await UpdateChannelAsync(channel, cancellationToken).ConfigureAwait(false);
        }

        foreach (string id in _onAir.Keys.Where(id => channels.All(channel => !string.Equals(channel.Id, id, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            _onAir.TryRemove(id, out _);
            restart = true;
        }

        if (restart)
        {
            OnAirChanged?.Invoke();
        }

        // The guide follows who's live and the predictions; republished only when it would read differently.
        bool guideChanged = false;
        foreach (StreamerChannel channel in channels)
        {
            string summary = GuideSummary(channel);
            if (!_guides.TryGetValue(channel.Id, out string? before) || before != summary)
            {
                _guides[channel.Id] = summary;
                guideChanged = true;
            }
        }

        if (restart || guideChanged)
        {
            services.GetService<IScheduleRepublisher>()?.RequestRepublish();
        }

        LastChecked = clock.GetUtcNow();
        Checked?.Invoke();
    }

    private async Task CheckStreamerAsync(StreamerEntry streamer, CancellationToken cancellationToken)
    {
        StreamerState state = _streamers.GetOrAdd(streamer.Key, _ => new StreamerState { Key = streamer.Key, Platform = streamer.Platform, Handle = streamer.Handle });
        DateTimeOffset now = clock.GetUtcNow();
        try
        {
            StreamerSnapshot snapshot = streamer.Platform == StreamPlatform.Twitch
                ? await twitch.StatusAsync(streamer.Handle, cancellationToken).ConfigureAwait(false)
                : await youtube.StatusAsync(streamer.Handle, cancellationToken).ConfigureAwait(false);

            // A page that leaves out the picture or name (YouTube while live, say) keeps the last ones known.
            state.Latest = snapshot with
            {
                Avatar = snapshot.Avatar ?? state.Latest?.Avatar,
                DisplayName = snapshot.DisplayName ?? state.Latest?.DisplayName
            };
            state.CheckedAt = now;
            state.Problem = null;

            if (snapshot.Live)
            {
                state.Live = true;
                state.OfflineChecks = 0;
                DateTimeOffset start = snapshot.StartedAt ?? now;
                store.Record(streamer.Key, [new StreamSession(start, now - start, snapshot.Title)], now);
            }
            else if (state.Live && ++state.OfflineChecks >= 2)
            {
                state.Live = false;
            }
            else if (!state.Live && streamer.Platform == StreamPlatform.Twitch && snapshot.StartedAt is { } last)
            {
                // Twitch says when the last broadcast started even when it keeps no recordings.
                store.Record(streamer.Key, [new StreamSession(last, TimeSpan.Zero)], now);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            state.Problem = ex is HttpRequestException or TaskCanceledException ? $"Couldn't reach {StreamerHandles.PlatformName(streamer.Platform)}: {ex.Message}" : ex.Message;
            logger.LogDebug(ex, "Streamers: couldn't check {Key}.", streamer.Key);
        }

        if (state.HistoryAt is null || now - state.HistoryAt > HistoryEvery)
        {
            state.HistoryAt = now;
            try
            {
                IReadOnlyList<StreamSession> past = streamer.Platform == StreamPlatform.Twitch
                    ? await twitch.PastStreamsAsync(streamer.Handle, cancellationToken).ConfigureAwait(false)
                    : await youtube.PastStreamsAsync(streamer.Handle, cancellationToken).ConfigureAwait(false);
                store.Record(streamer.Key, past, now);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Streamers: couldn't read the past streams of {Key}.", streamer.Key);
            }
        }
    }

    /// <summary>Works out who the channel shows and opens their stream; true when the running channel needs restarting.</summary>
    private async Task<bool> UpdateChannelAsync(StreamerChannel channel, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        StreamerEntry? active = Active(channel);
        ChannelOnAir? current = OnAir(channel.Id);

        if (active is null)
        {
            return await UpdateReplayAsync(channel, current, cancellationToken).ConfigureAwait(false);
        }

        bool switched = !string.Equals(current?.ActiveKey, active.Key, StringComparison.OrdinalIgnoreCase);
        bool stale = current?.ResolvedAt is { } resolved && now - resolved > RefreshAfter(active.Platform);
        bool retry = current is { StreamUrl: null, TriedAt: { } tried } && now - tried > RetryOpening;
        if (!switched && !stale && !retry)
        {
            return false;
        }

        StreamerState? state = State(active.Key);
        string page = active.Platform == StreamPlatform.YouTube && state?.Latest?.VideoId is { } video
            ? $"https://www.youtube.com/watch?v={video}"
            : StreamerHandles.Url(active.Platform, active.Handle) + (active.Platform == StreamPlatform.YouTube ? "/live" : "");
        try
        {
            string url = await ytdlp.ResolveAsync(page, cancellationToken).ConfigureAwait(false);
            _onAir[channel.Id] = new ChannelOnAir(active.Key, url, now, null, now);
            logger.LogInformation("Streamers: {Channel} is showing {Streamer}.", channel.Name, state?.Name(active) ?? active.Handle);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Streamers: {Channel} couldn't open {Streamer}'s stream: {Message}", channel.Name, active.Handle, ex.Message);

            // A stream that was working is kept until it's replaced; a new one waits for the next try.
            if (stale && current?.StreamUrl is not null && !switched)
            {
                _onAir[channel.Id] = current with { TriedAt = now, Problem = ex.Message };
                return false;
            }

            _onAir[channel.Id] = new ChannelOnAir(active.Key, null, null, ex.Message, now);
            return switched || current?.StreamUrl is not null;
        }
    }

    // Recordings that couldn't be opened (deleted, subscriber-only…), passed over from then on.
    private readonly ConcurrentDictionary<string, bool> _unplayable = new(StringComparer.Ordinal);

    /// <summary>
    /// The newest recording among the channel's streamers (at least ten minutes long, and openable), with its streamer;
    /// null when there's none.
    /// </summary>
    public (StreamerEntry Streamer, StreamSession Recording)? LatestRecording(StreamerChannel channel) =>
        channel.Streamers
            .SelectMany(streamer => store.History(streamer.Key)
                .Where(session => session.VideoId is { } id && session.Length >= TimeSpan.FromMinutes(10) && !_unplayable.ContainsKey(id))
                .Select(session => (streamer, session)))
            .OrderByDescending(pair => pair.session.Start)
            .Select(pair => ((StreamerEntry, StreamSession)?)(pair.streamer, pair.session))
            .FirstOrDefault();

    /// <summary>
    /// Nobody's live: the replay of whoever streamed last, when the channel wants one, else the countdown. A replay keeps
    /// its place across restarts, starts over when it ends, and moves to a newer recording as soon as there is one.
    /// True when the running channel needs restarting.
    /// </summary>
    private async Task<bool> UpdateReplayAsync(StreamerChannel channel, ChannelOnAir? current, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        bool wasShowing = current?.StreamUrl is not null;
        if (channel.Offline.Replay == ReplayStyle.Off || LatestRecording(channel) is not { } latest)
        {
            _onAir[channel.Id] = new ChannelOnAir(null, null, null, null);
            return wasShowing;
        }

        (StreamerEntry streamer, StreamSession recording) = latest;
        bool same = current is { ActiveKey: null, StreamUrl: not null } && current.Replay?.VideoId == recording.VideoId;
        bool ended = same && now - current!.ReplayFrom >= recording.Length;
        bool stale = same && current!.ResolvedAt is { } resolved && now - resolved > RefreshAfter(streamer.Platform);
        if (same && !ended && !stale)
        {
            return false;
        }

        // Starting over is a restart too, so the recording plays from its beginning again.
        DateTimeOffset from = same && !ended ? current!.ReplayFrom : now;
        int round = same && ended ? current!.Round + 1 : current?.Round ?? 0;
        string page = streamer.Platform == StreamPlatform.Twitch
            ? $"https://www.twitch.tv/videos/{Uri.EscapeDataString(recording.VideoId!)}"
            : $"https://www.youtube.com/watch?v={Uri.EscapeDataString(recording.VideoId!)}";
        try
        {
            (string video, string? audio) = await ytdlp.ResolveRecordingAsync(page, streamer.Platform, cancellationToken).ConfigureAwait(false);
            _onAir[channel.Id] = new ChannelOnAir(null, video, now, null, now)
            {
                ReplayKey = streamer.Key,
                Replay = recording,
                ReplayFrom = from,
                AudioUrl = audio,
                Round = round
            };
            if (!same)
            {
                logger.LogInformation("Streamers: {Channel} is replaying {Streamer}'s stream from {Start}.", channel.Name,
                    State(streamer.Key)?.Name(streamer) ?? streamer.Handle, recording.Start);
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Streamers: {Channel} couldn't open the replay {Page}: {Message}", channel.Name, page, ex.Message);
            _unplayable[recording.VideoId!] = true;

            // The next round tries the next newest recording.
            _onAir[channel.Id] = new ChannelOnAir(null, null, null, null);
            return wasShowing;
        }
    }

    /// <summary>Stream addresses expire (YouTube's after about six hours), so a long stream is reopened before then.</summary>
    private static TimeSpan RefreshAfter(StreamPlatform platform) => platform == StreamPlatform.YouTube ? TimeSpan.FromHours(4) : TimeSpan.FromHours(10);

    private string GuideSummary(StreamerChannel channel)
    {
        StreamerEntry? active = Active(channel);
        string live = active is null ? "-" : $"{active.Key}|{State(active.Key)?.Latest?.Title}";
        IEnumerable<string> next = Upcoming(channel).Select(pair => pair.Prediction is { } prediction
            ? $"{pair.Streamer.Key}@{prediction.At.ToUnixTimeSeconds() / 600}"
            : pair.Streamer.Key);
        // The re-run on air (and its pass, which moves the guide's times when it starts over) is in the guide too.
        string replay = OnAir(channel.Id) is { ActiveKey: null, StreamUrl: not null, Replay: { } recording } onAir
            ? $"{recording.VideoId}#{onAir.Round}@{onAir.ReplayFrom.ToUnixTimeSeconds()}"
            : "-";
        return $"{channel.Number}|{channel.Name}|{channel.Logo}|{live}|{replay}|{string.Join(',', next)}";
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }
}
