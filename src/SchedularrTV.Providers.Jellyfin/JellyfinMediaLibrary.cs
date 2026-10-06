using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;

namespace SchedularrTV.Providers.Jellyfin;

/// <summary>
/// Snapshot-backed <see cref="IMediaLibrary"/>. The whole library is projected into
/// <see cref="MediaItem"/>s once per cache window and queries are evaluated in memory.
/// </summary>
/// <remarks>
/// Building a 48-hour, multi-channel schedule issues thousands of query evaluations; round-tripping
/// each of those to Jellyfin would be far slower than holding a few MB of metadata, and it also
/// guarantees every query operator behaves identically regardless of server-side search support.
/// </remarks>
public sealed class JellyfinMediaLibrary(
    JellyfinClient client,
    IOptions<JellyfinOptions> options,
    ILogger<JellyfinMediaLibrary> logger,
    ILibraryScope? scope = null,
    TimeProvider? timeProvider = null) : IMediaLibrary
{
    // Read on each use: settings saved in the app apply straight away.
    private JellyfinOptions _options => options.Value;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private IReadOnlyList<MediaItem> _snapshot = [];
    private DateTimeOffset _snapshotTakenAt = DateTimeOffset.MinValue;
    private string _snapshotScope = string.Empty;
    private volatile JellyfinLoadProgress? _loading;

    /// <summary>Set while the library is being read from Jellyfin; null otherwise.</summary>
    public JellyfinLoadProgress? Loading => _loading;

    /// <summary>Raised from the loading thread whenever <see cref="Loading"/> changes.</summary>
    public event Action? LoadingChanged;

    private void Report(string stage, int items = 0, int? total = null)
    {
        _loading = new JellyfinLoadProgress(stage, items, total, _loading?.StartedAt ?? _time.GetUtcNow());
        LoadingChanged?.Invoke();
    }

    public async ValueTask<IReadOnlyList<MediaItem>> SearchAsync(QueryExpression expression, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MediaItem> snapshot = await EnsureSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return QueryEvaluator.Filter(snapshot, expression);
    }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken = default)
    {
        _snapshotTakenAt = DateTimeOffset.MinValue;
        await EnsureSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<MediaItem>> EnsureSnapshotAsync(CancellationToken cancellationToken)
    {
        string[] excluded = [.. scope?.ExcludedLibraries ?? []];
        // The server and user are part of it too, so pointing App settings at another server reloads at once.
        string scopeKey = string.Join('\u001f', new[] { _options.BaseUrl, _options.UserId ?? string.Empty }.Concat(excluded));

        // Changing which libraries are in scope has to invalidate the snapshot, or the excluded
        // content would keep airing until the cache happened to expire.
        if (scopeKey == _snapshotScope && _time.GetUtcNow() - _snapshotTakenAt < _options.CacheDuration)
        {
            return _snapshot;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (scopeKey == _snapshotScope && _time.GetUtcNow() - _snapshotTakenAt < _options.CacheDuration)
            {
                return _snapshot;
            }

            Report("Connecting to Jellyfin");
            List<JellyfinItem> raw = await client.GetAllItemsAsync(cancellationToken, (items, total) => Report("Reading items", items, total))
                .ConfigureAwait(false);

            // Extras are absent from the main crawl, so they arrive separately and are merged in by
            // id; without them an intro configured on a show could never be resolved.
            Report("Reading extras", raw.Count);
            HashSet<string> known = [.. raw.Select(i => i.Id)];
            foreach (JellyfinItem extra in await client.GetExtrasAsync(cancellationToken).ConfigureAwait(false))
            {
                if (known.Add(extra.Id))
                {
                    raw.Add(extra);
                }
            }

            if (excluded.Length > 0)
            {
                IReadOnlyList<string> roots = await client.GetLibraryPathsAsync(excluded, cancellationToken).ConfigureAwait(false);
                int before = raw.Count;
                raw = [.. raw.Where(i => !IsUnder(i.Path, roots))];
                logger.LogInformation("Excluded {Count} item(s) from {Libraries}.", before - raw.Count, string.Join(", ", excluded));
            }

            Report("Reading collections");
            Dictionary<string, List<string>> collections = await client
                .GetCollectionMembershipAsync(cancellationToken, (done, total) => Report("Reading collections", done, total))
                .ConfigureAwait(false);
            Report("Reading series and seasons", raw.Count);
            Dictionary<string, JellyfinSeriesInfo> series = await client.GetSeriesInfoAsync(cancellationToken).ConfigureAwait(false);
            Dictionary<string, int> seasons = await client.GetSeasonNumbersAsync(cancellationToken).ConfigureAwait(false);

            string[] multiVersion = [.. raw.Where(i => i.MediaSourceCount > 1).Select(i => i.Id)];
            Report("Reading alternate versions", 0, multiVersion.Length);
            Dictionary<string, JellyfinVersion> versions = multiVersion.Length > 0
                ? await client.GetVersionsAsync(multiVersion, cancellationToken, (done, total) => Report("Reading alternate versions", done, total))
                    .ConfigureAwait(false)
                : [];
            Report("Putting the library together", raw.Count);

            Dictionary<string, List<string>> collectionsByItem = new(StringComparer.Ordinal);
            foreach ((string name, List<string> members) in collections)
            {
                foreach (string memberId in members)
                {
                    if (!collectionsByItem.TryGetValue(memberId, out List<string>? list))
                    {
                        list = [];
                        collectionsByItem[memberId] = list;
                    }

                    list.Add(name);
                }
            }

            // A series belonging to a collection implies all of its episodes belong to it too.
            Dictionary<string, List<string>> collectionsBySeries = collectionsByItem;

            List<MediaItem> projected = new(raw.Count);
            foreach (JellyfinItem item in raw)
            {
                projected.Add(Project(item, collectionsByItem, collectionsBySeries, series, seasons, versions));
            }

            _snapshot = projected;
            _snapshotTakenAt = _time.GetUtcNow();
            _snapshotScope = scopeKey;
            logger.LogInformation("Jellyfin snapshot refreshed: {Count} items, {Collections} collections.", projected.Count, collections.Count);

            return _snapshot;
        }
        finally
        {
            _loading = null;
            LoadingChanged?.Invoke();
            _refreshLock.Release();
        }
    }

    private MediaItem Project(
        JellyfinItem item,
        IReadOnlyDictionary<string, List<string>> collectionsByItem,
        IReadOnlyDictionary<string, List<string>> collectionsBySeries,
        IReadOnlyDictionary<string, JellyfinSeriesInfo> series,
        IReadOnlyDictionary<string, int> seasons,
        IReadOnlyDictionary<string, JellyfinVersion> versions)
    {
        List<string> tags = [];
        Dictionary<string, string> attributes = new(StringComparer.OrdinalIgnoreCase);

        foreach (string tag in item.Tags ?? [])
        {
            int separator = tag.IndexOf(':');
            if (separator > 0)
            {
                string prefix = tag[..separator].Trim();
                if (_options.AttributeTagPrefixes.Contains(prefix, StringComparer.OrdinalIgnoreCase))
                {
                    attributes[prefix] = tag[(separator + 1)..].Trim();
                    continue;
                }
            }

            tags.Add(tag);
        }

        List<string> itemCollections = [];
        if (collectionsByItem.TryGetValue(item.Id, out List<string>? direct))
        {
            itemCollections.AddRange(direct);
        }

        if (item.SeriesId is not null && collectionsBySeries.TryGetValue(item.SeriesId, out List<string>? inherited))
        {
            itemCollections.AddRange(inherited.Except(itemCollections, StringComparer.OrdinalIgnoreCase));
        }

        DateTimeOffset? premiere = item.PremiereDate ?? YearToDate(item.ProductionYear);

        JellyfinSeriesInfo? info = item.SeriesId is not null && series.TryGetValue(item.SeriesId, out JellyfinSeriesInfo? found)
            ? found
            : null;

        versions.TryGetValue(item.Id, out JellyfinVersion? version);

        // A chapter named for the credits marks where they start (in the second half, so "Opening Credits" doesn't count).
        if (CreditsChapter(item) is { } credits)
        {
            attributes[MediaItem.CreditsStartAttribute] = credits.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        return new MediaItem
        {
            Id = item.Id,
            Title = item.Name ?? item.Id,
            Kind = MapKind(item.Type, tags),
            Duration = TimeSpan.FromTicks(item.RunTimeTicks ?? 0),
            SeriesId = item.SeriesId,
            // Jellyfin copies the series name onto each episode and does not rewrite it when the
            // series is renamed, so the series item is the authoritative source.
            SeriesName = info?.Name ?? item.SeriesName,
            SeasonNumber = ResolveSeasonNumber(item, seasons),
            EpisodeNumber = item.IndexNumber,
            PremiereDate = premiere,
            Overview = item.Overview,

            // Episodes are often unrated themselves: the series' rating stands for them.
            Rating = string.IsNullOrWhiteSpace(item.OfficialRating) ? info?.Rating : item.OfficialRating.Trim(),
            Tags = tags,
            Collections = itemCollections,
            Networks = info?.Networks ?? [],
            SeriesYear = info?.Year,
            Chapters = item.Chapters?
                .Select(c => TimeSpan.FromTicks(c.StartPositionTicks))
                .Where(t => t >= TimeSpan.Zero)
                .Distinct()
                .OrderBy(t => t)
                .ToList() ?? [],
            Attributes = attributes.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            Path = item.Path,
            VersionGroupId = version?.GroupId,
            VersionName = version?.Name
        };
    }

    /// <summary>Where a chapter named for the end credits ("Credits", "End Credits", "Outro", "Ending") starts, if one does.</summary>
    internal static TimeSpan? CreditsChapter(JellyfinItem item) =>
        CreditsChapter(TimeSpan.FromTicks(item.RunTimeTicks ?? 0),
            (item.Chapters ?? []).Select(chapter => (TimeSpan.FromTicks(chapter.StartPositionTicks), chapter.Name)));

    /// <summary>Where the chapter named for the end credits starts, among chapters (start, name) of an item this long.</summary>
    public static TimeSpan? CreditsChapter(TimeSpan runtime, IEnumerable<(TimeSpan Start, string? Name)> chapters)
    {
        foreach ((TimeSpan start, string? named) in chapters)
        {
            string name = named?.Trim() ?? string.Empty;
            bool credits = name.Contains("credit", StringComparison.OrdinalIgnoreCase) || name.Contains("outro", StringComparison.OrdinalIgnoreCase)
                           || name.Equals("ending", StringComparison.OrdinalIgnoreCase) || name.Equals("ED", StringComparison.Ordinal);
            if (credits && !name.Contains("open", StringComparison.OrdinalIgnoreCase) && runtime > TimeSpan.Zero && start > runtime / 2 && start < runtime)
            {
                return start;
            }
        }

        return null;
    }

    internal static int? ResolveSeasonNumber(JellyfinItem item, IReadOnlyDictionary<string, int> seasons)
    {
        string[] path = item.Path?.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (item.Type == "Episode" && path.Length >= 2
            && string.Equals(path[^2], "Specials", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return item.SeasonId is not null && seasons.TryGetValue(item.SeasonId, out int index)
            ? index : item.ParentIndexNumber;
    }

    private static bool IsUnder(string? path, IReadOnlyList<string> roots) =>
        path is not null
        && roots.Any(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase));

    // Real libraries contain items with a ProductionYear of 0 or other nonsense, which would
    // otherwise throw while projecting the whole snapshot.
    internal static DateTimeOffset? YearToDate(int? year) =>
        year is >= 1 and <= 9999 ? new DateTimeOffset(year.Value, 1, 1, 0, 0, 0, TimeSpan.Zero) : null;

    private static MediaKind MapKind(string? type, List<string> tags)
    {
        if (tags.Any(t => t is "Commercials" or "Commercial" or "Bumper" or "Promo" or "Filler"))
        {
            return MediaKind.Filler;
        }

        return type switch
        {
            "Episode" => MediaKind.Episode,
            "Movie" => MediaKind.Movie,
            _ => MediaKind.Unknown
        };
    }
}
