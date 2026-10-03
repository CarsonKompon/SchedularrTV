using Microsoft.Extensions.Hosting;
using SchedularrTV.Core.Query;

namespace SchedularrTV.AutoChapters;

/// <summary>One programme waiting to be analysed.</summary>
public sealed record ScanRequest(string Path, string Title, TimeSpan Duration, DateTimeOffset? AirsAt = null);

/// <summary>
/// Analyses programmes in the background, one (or a few) at a time: those in the schedule first, soonest first,
/// then, when switched on, the rest of the library.
/// </summary>
public sealed class ChapterScanner(
    ChapterStore store,
    AutoChaptersSettings settings,
    IServiceProvider services,
    ILogger<ChapterScanner> logger) : BackgroundService
{
    private readonly Lock _gate = new();
    private readonly List<ScanRequest> _soon = [];
    private readonly Queue<ScanRequest> _later = new();
    private readonly HashSet<string> _queued = new(StringComparer.Ordinal);
    private readonly List<string> _working = [];
    private readonly SemaphoreSlim _work = new(0);
    private DateTimeOffset _libraryScannedAt = DateTimeOffset.MinValue;

    /// <summary>Raised when the queue or what's being analysed changes, for the status page.</summary>
    public event Action? Changed;

    public string? LastError { get; private set; }

    public bool ScanningLibrary { get; private set; }

    public (int Soon, int Later) Queued
    {
        get
        {
            lock (_gate)
            {
                return (_soon.Count, _later.Count);
            }
        }
    }

    public IReadOnlyList<string> Working
    {
        get
        {
            lock (_gate)
            {
                return [.. _working];
            }
        }
    }

    /// <summary>Programmes about to air go first, in airing order; anything already analysed is skipped.</summary>
    public void QueueSoon(IEnumerable<(MediaItem Item, DateTimeOffset AirsAt)> upcoming)
    {
        int added = 0;
        lock (_gate)
        {
            foreach ((MediaItem item, DateTimeOffset airsAt) in upcoming)
            {
                if (ChapterEnricher.Wants(item) && store.Find(item.Path) is null && _queued.Add(item.Path!))
                {
                    _soon.Add(new ScanRequest(item.Path!, Name(item), item.Duration, airsAt));
                    added++;
                }
            }

            // Every channel publishes separately, so keep the whole list in airing order.
            _soon.Sort((left, right) => Nullable.Compare(left.AirsAt, right.AirsAt));
        }

        Signal(added);
    }

    /// <summary>Every episode and movie in the library without chapters, after anything airing soon.</summary>
    public async Task<int> QueueLibraryAsync(CancellationToken cancellationToken)
    {
        ScanningLibrary = true;
        Changed?.Invoke();
        try
        {
            IMediaLibrary library = services.GetRequiredService<IMediaLibrary>();
            IReadOnlyList<MediaItem> items = await library.SearchAsync(QueryParser.Parse("kind == 'Episode' OR kind == 'Movie'"), cancellationToken)
                .ConfigureAwait(false);
            int added = 0;
            lock (_gate)
            {
                foreach (MediaItem item in items.OrderBy(item => item.SortKey, StringComparer.Ordinal))
                {
                    if (ChapterEnricher.Wants(item) && store.Find(item.Path) is null && _queued.Add(item.Path!))
                    {
                        _later.Enqueue(new ScanRequest(item.Path!, Name(item), item.Duration));
                        added++;
                    }
                }
            }

            _libraryScannedAt = DateTimeOffset.UtcNow;
            logger.LogInformation("Auto Chapters: {Count} programme(s) in the library to analyse.", added);
            Signal(added);
            return added;
        }
        finally
        {
            ScanningLibrary = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Forgets what's waiting (what's being analysed now finishes).</summary>
    public void ClearQueue()
    {
        lock (_gate)
        {
            _soon.Clear();
            _later.Clear();
            _queued.Clear();
            _queued.UnionWith(_working);
        }

        Changed?.Invoke();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Task[] workers = [.. Enumerable.Range(0, settings.Concurrency).Select(_ => WorkAsync(stoppingToken))];

        // The whole library, when that's switched on: now (after a moment to start up) and every few hours for anything new.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (settings.ScanLibrary && DateTimeOffset.UtcNow - _libraryScannedAt > TimeSpan.FromHours(6))
                {
                    try
                    {
                        await QueueLibraryAsync(stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Auto Chapters couldn't read the library.");
                    }
                }

                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task WorkAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _work.WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            ScanRequest? next;
            lock (_gate)
            {
                if (_soon.Count > 0)
                {
                    next = _soon[0];
                    _soon.RemoveAt(0);
                }
                else
                {
                    _later.TryDequeue(out next);
                }

                if (next is not null)
                {
                    _working.Add(next.Title);
                }
            }

            if (next is null)
            {
                continue;
            }

            Changed?.Invoke();
            try
            {
                await AnalyseAsync(next, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                lock (_gate)
                {
                    _working.Remove(next.Title);
                    _queued.Remove(next.Path);
                }

                Changed?.Invoke();
            }
        }
    }

    private async Task AnalyseAsync(ScanRequest request, CancellationToken cancellationToken)
    {
        var record = new ChapterRecord { Path = request.Path, Title = request.Title, AnalysedAt = DateTimeOffset.UtcNow };
        string? file = settings.LocalPath(request.Path);
        if (file is null)
        {
            record.Error = "The file isn't reachable from this computer (check ErsatzTV's path mappings in settings.json).";
            store.Save(record);
            return;
        }

        var info = new FileInfo(file);
        record.Size = info.Length;
        record.Modified = info.LastWriteTimeUtc;
        if (store.Find(request.Path) is { Error: null } done && done.Size == record.Size && done.Modified == record.Modified)
        {
            return;
        }

        DateTimeOffset started = DateTimeOffset.UtcNow;
        try
        {
            DetectionOptions options = settings.Detection;
            Detection found;
            try
            {
                found = await BlackDetector.AnalyseAsync(settings.Ffmpeg, file, request.Duration, options, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException) when (options.RequireSilence)
            {
                // Some files (or ffmpeg builds) can't take the sound filter, a file with no sound especially:
                // go by the picture alone.
                found = await BlackDetector.AnalyseAsync(settings.Ffmpeg, file, request.Duration, options with { RequireSilence = false }, cancellationToken)
                    .ConfigureAwait(false);
            }
            record.Markers = [.. found.Markers.Select(marker => Math.Round(marker.TotalSeconds, 3))];
            record.Seconds = Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1);
            logger.LogInformation("Auto Chapters: {Title} has {Count} break point(s) ({Seconds:0}s to analyse).",
                request.Title, record.Markers.Count, record.Seconds);
            LastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            record.Error = ex.Message;
            LastError = $"{request.Title}: {ex.Message}";
            logger.LogWarning("Auto Chapters couldn't analyse {Title}: {Message}", request.Title, ex.Message);
        }

        store.Save(record);
    }

    private void Signal(int added)
    {
        if (added > 0)
        {
            _work.Release(added);
            Changed?.Invoke();
        }
    }

    private static string Name(MediaItem item) =>
        item.SeriesName is { Length: > 0 } series
            ? $"{series} S{item.SeasonNumber ?? 0:00}E{item.EpisodeNumber ?? 0:00} {item.Title}".Trim()
            : item.Title;

    public override void Dispose()
    {
        _work.Dispose();
        base.Dispose();
    }
}
