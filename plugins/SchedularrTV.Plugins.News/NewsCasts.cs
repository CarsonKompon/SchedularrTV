using System.Collections.Concurrent;
using System.Text.Json;
using SchedularrTV.Plugins.Speech;

namespace SchedularrTV.News;

/// <summary>
/// Where a show is in its rundown, by the clock: the rundown loops from <c>start</c>, and a newer one (from the next
/// refresh) takes over at the end of the current loop, so a story is never cut off. The page and the sound both work
/// out what's on from this, which keeps the picture and the voice together.
/// </summary>
public sealed class NewsCast
{
    private readonly Lock _gate = new();
    private Rundown? _current;
    private Rundown? _pending;
    private long _start;

    /// <summary>The rundown on air at <paramref name="nowMs"/> (Unix ms), when its current loop started, and whether another is waiting.</summary>
    public (Rundown? Rundown, long StartMs, bool Waiting) At(long nowMs)
    {
        lock (_gate)
        {
            Roll(nowMs);
            return (_current, _start, _pending is not null);
        }
    }

    /// <summary>
    /// A new rundown: on at once when <paramref name="immediately"/> (the show's settings changed) or there's none yet,
    /// else from the end of the current loop.
    /// </summary>
    public void Publish(Rundown rundown, long nowMs, bool immediately)
    {
        lock (_gate)
        {
            if (_current is null || immediately || _current.LengthMs <= 0)
            {
                _current = rundown;
                _pending = null;
                _start = nowMs;
                return;
            }

            Roll(nowMs);
            _pending = rundown;
        }
    }

    /// <summary>Back to the top (the newest rundown), for a break that's just gone on air.</summary>
    public void Restart(long nowMs)
    {
        lock (_gate)
        {
            if (_pending is not null)
            {
                _current = _pending;
                _pending = null;
            }

            _start = nowMs;
        }
    }

    private void Roll(long nowMs)
    {
        if (_current is null || _current.LengthMs <= 0)
        {
            return;
        }

        if (nowMs >= _start + _current.LengthMs && _pending is not null)
        {
            _start += _current.LengthMs;
            _current = _pending;
            _pending = null;
        }

        if (_current.LengthMs > 0 && nowMs >= _start + _current.LengthMs)
        {
            // Whole loops of the same rundown.
            _start += (nowMs - _start) / _current.LengthMs * _current.LengthMs;
        }
    }
}

/// <summary>What the page polls for: its settings, the rundown and its data, and where it is.</summary>
public sealed record NewsState(
    NewsPageConfig Config,
    long Version,
    long StartedAt,
    long ServerTime,
    bool Loops,
    bool Building,
    IReadOnlyList<string> Problems,
    IReadOnlyList<RundownItem> Items,
    NewsSnapshot? Data);

/// <summary>
/// Every show's rundown: fetched, written and voiced in the background whenever it's asked for and is older than the
/// refresh time (or the show's settings have changed), and kept on its clock (<see cref="NewsCast"/>).
/// </summary>
public sealed class NewsCasts(NewsSources sources, NewsVoice voice, NewsSettings settings, TimeProvider clock, ILogger<NewsCasts> logger)
{
    private sealed class Entry
    {
        public readonly NewsCast Cast = new();
        public string? Fingerprint;
        public DateTimeOffset BuiltAt = DateTimeOffset.MinValue;
        public Task? Building;
        public IReadOnlyList<string> Problems = [];
        public int Listeners;
        public DateTimeOffset LastUsed;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private long _version;
    private DateTimeOffset _tidied = DateTimeOffset.MinValue;

    private static readonly JsonSerializerOptions FingerprintJson = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    /// <summary>The key a show's rundown is kept under: its own, or the editor's preview of it (which isn't voiced).</summary>
    public static string Key(string id, bool preview) => preview ? "preview|" + id : id;

    public NewsCast Use(string key, NewsShow show, bool speak)
    {
        DateTimeOffset now = clock.GetUtcNow();
        Entry entry = _entries.GetOrAdd(key, _ => new Entry());
        entry.LastUsed = now;

        // Settings that change what's fetched or said; the look is sent with each answer, so it needn't rebuild.
        NewsShow content = show.Copy();
        content.Theme = NewsTheme.NewsDesk;
        content.AccentColor = "";
        content.ShowSidebar = content.ShowTicker = content.ShowImages = content.TwelveHourClock = false;
        content.TickerMessage = content.HighlightColor = content.LiveLabel = null;
        content.Font = NewsFont.Theme;
        content.TextScale = 1;
        content.TickerSize = content.SidePanelSize = NewsSize.Normal;
        content.TickerSpeed = 0;
        content.ScanLines = content.ShowClock = content.MapDrift = content.MapMarker = false;
        content.MapStyle = MapStyle.Auto;
        content.MapRadarOpacity = 0;
        content.Name = speak ? show.Name : "";
        string fingerprint = JsonSerializer.Serialize(content, FingerprintJson) + (speak ? "|voiced" : "");

        lock (entry)
        {
            bool changed = entry.Fingerprint != fingerprint;
            bool stale = now - entry.BuiltAt > settings.RefreshEvery;
            if ((changed || stale) && entry.Building is not { IsCompleted: false })
            {
                bool immediately = changed;
                NewsShow copy = show.Copy();
                entry.Building = Task.Run(() => BuildAsync(key, entry, copy, speak, fingerprint, immediately));
            }
        }

        Prune(now);
        return entry.Cast;
    }

    /// <summary>The page's answer for a show.</summary>
    public NewsState State(string key, NewsShow show, bool speak, bool preview)
    {
        NewsCast cast = Use(key, show, speak);
        Entry entry = _entries[key];
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        (Rundown? rundown, long start, bool waiting) = cast.At(now);
        List<string> problems = [.. entry.Problems];
        if (speak && voice.Problem(show) is { } voiceProblem)
        {
            problems.Add(voiceProblem);
        }

        return new NewsState(
            NewsPageConfig.For(show, settings.TomTomKey is not null, preview),
            rundown?.Version ?? 0,
            start,
            now,
            !waiting,
            entry.Building is { IsCompleted: false },
            problems,
            rundown?.Items ?? [],
            rundown?.Snapshot);
    }

    /// <summary>
    /// The show's sound has started listening: the first listener (a break going on air) starts the rundown from the
    /// top. Dispose when it stops.
    /// </summary>
    public IDisposable Listen(NewsShow show)
    {
        NewsCast cast = Use(show.Id, show, speak: true);
        Entry entry = _entries[show.Id];
        if (Interlocked.Increment(ref entry.Listeners) == 1)
        {
            cast.Restart(clock.GetUtcNow().ToUnixTimeMilliseconds());
        }

        return new Leaving(() => Interlocked.Decrement(ref entry.Listeners));
    }

    private sealed class Leaving(Action leave) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                leave();
            }
        }
    }

    private async Task BuildAsync(string key, Entry entry, NewsShow show, bool speak, string fingerprint, bool immediately)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            CancellationToken cancellationToken = timeout.Token;
            NewsSnapshot snapshot = await CompileAsync(show, cancellationToken).ConfigureAwait(false);
            List<string> problems = [.. snapshot.Problems];
            IReadOnlyList<RundownItem> items = NewsRundown.Compose(show, snapshot);

            if (speak && show.Speaks && voice.Find(show.VoiceProvider) is { SetupProblem: null } provider)
            {
                items = await VoiceAsync(provider, show.Voice!, items, problems, cancellationToken).ConfigureAwait(false);
            }

            var rundown = new Rundown(Interlocked.Increment(ref _version), snapshot, NewsRundown.Time(show, items));
            entry.Cast.Publish(rundown, clock.GetUtcNow().ToUnixTimeMilliseconds(), immediately);
            entry.Problems = problems;
            logger.LogDebug("News {Show}: {Count} segments, {Seconds:0} s a loop.", show.Name, rundown.Items.Count, rundown.LengthMs / 1000.0);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "News {Show}: couldn't make the rundown.", show.Name);
            entry.Problems = [$"Couldn't make the rundown: {ex.Message}"];
        }
        finally
        {
            // Settled either way, so a failure isn't retried on every poll; it's tried again at the next refresh.
            entry.Fingerprint = fingerprint;
            entry.BuiltAt = clock.GetUtcNow();
        }

        if (clock.GetUtcNow() - _tidied > TimeSpan.FromHours(1))
        {
            _tidied = clock.GetUtcNow();
            voice.Tidy();
        }
    }

    private async Task<IReadOnlyList<RundownItem>> VoiceAsync(ITextToSpeechProvider provider, string voiceId, IReadOnlyList<RundownItem> items,
        List<string> problems, CancellationToken cancellationToken)
    {
        List<RundownItem> voiced = [];
        int failed = 0;
        string? firstError = null;
        foreach (RundownItem item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Speech))
            {
                voiced.Add(item);
                continue;
            }

            try
            {
                (string path, long ms) = await voice.ClipAsync(provider, voiceId, item.Speech, cancellationToken).ConfigureAwait(false);
                voiced.Add(item with { Clip = path, ClipMs = ms });
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                failed++;
                firstError ??= ex.Message;
                voiced.Add(item);
            }
        }

        if (failed > 0)
        {
            problems.Add($"{failed} voice-over(s) couldn't be made ({provider.DisplayName}): {firstError}");
        }

        return voiced;
    }

    /// <summary>Fetches everything the show uses at once; whatever fails is left out and said why.</summary>
    public async Task<NewsSnapshot> CompileAsync(NewsShow show, CancellationToken cancellationToken)
    {
        List<string> problems = [];
        bool Wants(NewsDesk desk) => show.Desks.Contains(desk);

        async Task<T?> Try<T>(string what, Func<Task<T>> fetch) where T : class
        {
            try
            {
                return await fetch().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                lock (problems)
                {
                    problems.Add($"Couldn't get {what}: {Reason(ex)}");
                }

                return null;
            }
        }

        Task<FeedContent?[]> feeds = Task.WhenAll(show.ValidFeeds.Select(feed => Try(feed.Label ?? feed.Url, () => sources.FeedAsync(feed, cancellationToken))));

        Task<WeatherReport?> weather = Wants(NewsDesk.Weather) && show.HasPlace
            ? Try("the weather", () => sources.WeatherAsync(show, cancellationToken))
            : Task.FromResult<WeatherReport?>(null);

        Task<Quote?[]> quotes = Wants(NewsDesk.Markets)
            ? Task.WhenAll(show.Symbols.Select(symbol => Try(symbol, () => sources.QuoteAsync(symbol, cancellationToken))))
            : Task.FromResult(Array.Empty<Quote?>());

        Task<IReadOnlyList<ScoreGame>?[]> scores = Wants(NewsDesk.Sports)
            ? Task.WhenAll(show.Leagues.Select(league => Try(SportsLeagues.NameOf(league) + " scores", () => sources.ScoresAsync(league, cancellationToken))))
            : Task.FromResult(Array.Empty<IReadOnlyList<ScoreGame>?>());

        Task<LotteryDraw?[]> lottery = Wants(NewsDesk.Lottery)
            ? Task.WhenAll(show.Lotteries.Select(LotteryGames.Find).OfType<LotteryGames.Game>()
                .Select(game => Try(game.Name + " results", () => sources.LotteryAsync(game, cancellationToken))))
            : Task.FromResult(Array.Empty<LotteryDraw?>());

        string? key = settings.TomTomKey;
        bool mapping = Wants(NewsDesk.Map) && show.HasPlace;
        Task<IReadOnlyList<TrafficIncident>?> incidents = mapping && key is not null && show.MapIncidents
            ? Try("traffic incidents", () => sources.IncidentsAsync(key, show.Latitude, show.Longitude, show.MapZoom, cancellationToken))
            : Task.FromResult<IReadOnlyList<TrafficIncident>?>(null);

        Task<IReadOnlyList<NewsSources.RadarFrame>?> radar = mapping && show.MapRadar
            ? Try("the radar", () => sources.RadarFramesAsync(cancellationToken))
            : Task.FromResult<IReadOnlyList<NewsSources.RadarFrame>?>(null);

        await Task.WhenAll(feeds, weather, quotes, scores, lottery, incidents, radar).ConfigureAwait(false);

        if ((Wants(NewsDesk.Weather) || Wants(NewsDesk.Map)) && !show.HasPlace)
        {
            problems.Add("Choose a place for the weather and the map.");
        }

        if (show.ValidFeeds.Count == 0)
        {
            problems.Add("Add a news feed (an RSS or Atom address) for stories.");
        }

        IReadOnlyList<NewsSources.RadarFrame> frames = (await radar.ConfigureAwait(false)) ?? [];
        MapReport? map = mapping
            ? new MapReport(
                show.Location,
                show.Latitude,
                show.Longitude,
                show.MapZoom,
                show.MapRadar && frames.Count > 0,
                show.WantsTraffic(key is not null),
                show.MapTrafficFlow && key is not null,
                [.. ((await incidents.ConfigureAwait(false)) ?? []).Take(show.MapIncidentCount)],
                [.. (show.MapRadarLoop ? frames : frames.TakeLast(1)).Select(frame => frame.Time)])
            : null;

        return new NewsSnapshot(
            clock.GetUtcNow(),
            PickStories(show, (await feeds.ConfigureAwait(false)).OfType<FeedContent>(), clock.GetUtcNow()),
            await weather.ConfigureAwait(false),
            [.. (await quotes.ConfigureAwait(false)).OfType<Quote>()],
            [.. (await scores.ConfigureAwait(false)).OfType<IReadOnlyList<ScoreGame>>().SelectMany(games => games)],
            [.. (await lottery.ConfigureAwait(false)).OfType<LotteryDraw>()],
            map,
            problems);
    }

    /// <summary>
    /// The stories to run: recent enough, newest first from each feed, taken from the feeds in turn (so one busy feed
    /// doesn't crowd out the others), the same headline only once.
    /// </summary>
    public static IReadOnlyList<Story> PickStories(NewsShow show, IEnumerable<FeedContent> feeds, DateTimeOffset now)
    {
        List<Queue<Story>> queues = [.. feeds.Select(feed => new Queue<Story>(feed.Stories.Where(story =>
            show.MaxAgeHours == 0 || story.Published is null || now - story.Published <= TimeSpan.FromHours(show.MaxAgeHours))))];
        List<Story> picked = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        while (picked.Count < show.StoryCount && queues.Any(queue => queue.Count > 0))
        {
            foreach (Queue<Story> queue in queues)
            {
                while (queue.TryDequeue(out Story? story))
                {
                    if (seen.Add(new string([.. story.Title.Where(char.IsLetterOrDigit)])))
                    {
                        picked.Add(story);
                        break;
                    }
                }

                if (picked.Count >= show.StoryCount)
                {
                    break;
                }
            }
        }

        return picked;
    }

    private static string Reason(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } code } => $"the server answered {(int)code} {code}",
        TaskCanceledException => "it didn't answer in time (tried twice)",
        System.Xml.XmlException => "it isn't an RSS or Atom feed",
        _ => ex.GetBaseException().Message
    };

    private void Prune(DateTimeOffset now)
    {
        foreach (var stale in _entries.Where(pair => pair.Key.StartsWith("preview|", StringComparison.Ordinal) && now - pair.Value.LastUsed > TimeSpan.FromMinutes(15)).ToList())
        {
            _entries.TryRemove(stale.Key, out _);
        }
    }
}
