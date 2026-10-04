using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using SchedularrTV.Core.Media;
using SchedularrTV.Providers.Jellyfin;

namespace SchedularrTV.CreditSqueeze;

/// <summary>Where looking up credits stands, for the Credit Squeeze tab.</summary>
/// <param name="Waiting">Episodes still to be asked about.</param>
/// <param name="Remaining">Roughly how long they'll take.</param>
/// <param name="CanAsk">Jellyfin is set up and asking it is on.</param>
public sealed record CreditsLookupStatus(int Waiting, TimeSpan Remaining, int Marked, int Asked, bool CanAsk, string? Problem);

/// <summary>
/// Where each episode's end credits start. A chapter named for them (read with the library) comes first; otherwise
/// Jellyfin's media segments are asked, its "Outro" segment (Jellyfin 10.10 and newer, filled in by a segment provider
/// such as Intro Skipper). Those are looked up in the background and kept in <c>plugin-data/credit-squeeze/credits.json</c>,
/// so publishing never waits on Jellyfin; an episode is squeezed from the next publish after its answer is in.
/// </summary>
public sealed class CreditsMarkers(IServiceProvider services, CreditSqueezeSettings settings, TimeProvider clock, ILogger<CreditsMarkers> logger)
    : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    // Episodes without a segment are asked again now and then, in case one has been added since.
    private static readonly TimeSpan AskAgainAfter = TimeSpan.FromDays(3);

    private readonly ConcurrentDictionary<string, Marker> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _wanted = new();
    private readonly ConcurrentDictionary<string, bool> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private bool _loaded;

    /// <summary>Raised when new answers are in, so schedules are published again with them.</summary>
    public event Action? Found;

    /// <param name="Start">Seconds into the episode, or null when it has no credits segment.</param>
    public sealed record Marker(double? Start, DateTimeOffset CheckedAt);

    private string StorePath => Path.Combine(settings.DataDirectory, "credits.json");

    /// <summary>Where an item's credits start: its credits chapter, else a known segment. Unknown ones are looked up for next time.</summary>
    public TimeSpan? CreditsOf(MediaItem item)
    {
        if (item.CreditsStart is { } chapter)
        {
            return chapter;
        }

        if (!settings.UseSegments || !Guid.TryParse(item.Id, out _))
        {
            return null;
        }

        Load();
        if (_known.TryGetValue(item.Id, out Marker? marker))
        {
            if (marker.Start is null && clock.GetUtcNow() - marker.CheckedAt > AskAgainAfter)
            {
                Want(item.Id);
            }

            return marker.Start is { } seconds ? TimeSpan.FromSeconds(seconds) : null;
        }

        Want(item.Id);
        return null;
    }

    private void Want(string itemId)
    {
        if (_queued.TryAdd(itemId, true))
        {
            _wanted.Enqueue(itemId);
            _wake.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Load();
        int found = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_wanted.TryDequeue(out string? itemId))
            {
                if (found > 0)
                {
                    Save();
                    found = 0;
                    Found?.Invoke();
                }

                try
                {
                    await _wake.WaitAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            _queued.TryRemove(itemId, out _);
            if (services.GetService<JellyfinClient>() is not { } jellyfin)
            {
                continue;
            }

            try
            {
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                (TimeSpan Start, TimeSpan End)? segment = await jellyfin.GetCreditsSegmentAsync(itemId, stoppingToken).ConfigureAwait(false);
                _askedTicks += System.Diagnostics.Stopwatch.GetElapsedTime(started).Ticks;
                _askedThisRun++;
                _lastProblem = null;
                _known[itemId] = new Marker(segment?.Start.TotalSeconds, clock.GetUtcNow());
                if (segment is not null)
                {
                    found++;
                }

                // Every so often, so a long first round of lookups starts paying off before it's over.
                if (found >= 25)
                {
                    Save();
                    found = 0;
                    Found?.Invoke();
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !stoppingToken.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Credit squeeze: couldn't ask Jellyfin for the credits of {Item}.", itemId);
                _lastProblem = $"Couldn't reach Jellyfin: {ex.Message}";
                _known[itemId] = new Marker(null, clock.GetUtcNow());
            }
        }
    }

    private void Load()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            if (File.Exists(StorePath) && JsonSerializer.Deserialize<Dictionary<string, Marker>>(File.ReadAllText(StorePath), Json) is { } stored)
            {
                foreach ((string id, Marker marker) in stored)
                {
                    _known.TryAdd(id, marker);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(settings.DataDirectory);
            File.WriteAllText(StorePath + ".tmp", JsonSerializer.Serialize(_known.ToDictionary(pair => pair.Key, pair => pair.Value), Json));
            File.Move(StorePath + ".tmp", StorePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>How many episodes have known credits, of how many were asked about.</summary>
    public (int Marked, int Asked) Coverage()
    {
        Load();
        return (_known.Values.Count(marker => marker.Start is not null), _known.Count);
    }

    private long _askedTicks;
    private int _askedThisRun;

    /// <summary>
    /// What's going on: how many episodes are waiting to be asked about, and roughly how long that will take (from how
    /// long the ones so far took); whether Jellyfin can be asked at all; and the last thing that went wrong.
    /// </summary>
    public CreditsLookupStatus Status()
    {
        Load();
        int waiting = _queued.Count;
        TimeSpan each = _askedThisRun > 0 ? TimeSpan.FromTicks(_askedTicks / _askedThisRun) : TimeSpan.FromMilliseconds(150);
        bool jellyfin = services.GetService<JellyfinClient>() is not null;
        (int marked, int asked) = Coverage();
        return new CreditsLookupStatus(waiting, waiting * each, marked, asked, settings.UseSegments && jellyfin, _lastProblem);
    }

    private string? _lastProblem;

    /// <summary>Asks about these items now (the ones not known yet), for a preview.</summary>
    public void LookUp(IEnumerable<MediaItem> items)
    {
        foreach (MediaItem item in items)
        {
            CreditsOf(item);
        }
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }
}
