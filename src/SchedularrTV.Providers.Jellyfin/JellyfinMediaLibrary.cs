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
    TimeProvider? timeProvider = null,
    SchedularrTV.Plugins.IMediaLibraryNotifier? notifier = null) : IMediaLibrary, IMediaSourceInfo
{
    public string Name => "Jellyfin";

    /// <summary>Jellyfin's own library scan while it runs.</summary>
    public async ValueTask<MediaSourceActivity?> GetActivityAsync(CancellationToken cancellationToken = default) =>
        IsConfigured && await client.GetLibraryScanAsync(cancellationToken).ConfigureAwait(false) is { } scan
            ? new MediaSourceActivity($"Jellyfin is scanning its library ({scan.Name})", scan.Percent)
            : null;

    /// <summary>Jellyfin's libraries (its "virtual folders"), which can be left out of the schedule.</summary>
    public async ValueTask<IReadOnlyList<MediaSourceLibrary>> GetLibrariesAsync(CancellationToken cancellationToken = default) =>
        IsConfigured
            ? [.. (await client.GetLibrariesAsync(cancellationToken).ConfigureAwait(false))
                .Select(library => new MediaSourceLibrary(library.Name, library.Locations.Count > 0 ? string.Join(" · ", library.Locations) : null))]
            : [];

    // Read on each use: settings saved in the app apply straight away.
    private JellyfinOptions _options => options.Value;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    // Kept and served while the next one is read, so a periodic re-read never leaves the library empty.
    private RefreshingSnapshot<IReadOnlyList<MediaItem>>? _cache;

    private RefreshingSnapshot<IReadOnlyList<MediaItem>> Cache => LazyInitializer.EnsureInitialized(ref _cache, () =>
    {
        var cache = new RefreshingSnapshot<IReadOnlyList<MediaItem>>(ReadAsync, Name, _time, logger);
        // The library pages are built from this, so they're told when a newer read lands.
        cache.RefreshedInBackground += () => notifier?.LibraryChanged();
        return cache;
    });

    private const char KeySeparator = '\u001f';

    private volatile MediaSourceProgress? _loading;

    /// <summary>Set while the library is being read from Jellyfin; null otherwise.</summary>
    public MediaSourceProgress? Loading => _loading;

    /// <summary>Raised from the loading thread whenever <see cref="Loading"/> changes.</summary>
    public event Action? LoadingChanged;

    private void Report(string stage, int items = 0, int? total = null)
    {
        _loading = new MediaSourceProgress(stage, items, total, _loading?.StartedAt ?? _time.GetUtcNow());
        LoadingChanged?.Invoke();
    }

    public async ValueTask<IReadOnlyList<MediaItem>> SearchAsync(QueryExpression expression, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MediaItem> snapshot = await EnsureSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return QueryEvaluator.Filter(snapshot, expression);
    }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsConfigured)
        {
            await Cache.RefreshAsync(ScopeKey(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Whether a server address and key are set. Without them this source is simply empty: shows and movies can come from local folders instead.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.BaseUrl) && !string.IsNullOrWhiteSpace(_options.ApiKey);

    /// <summary>
    /// What the snapshot is read from. The server and user are part of it, so pointing App settings at another server
    /// reloads at once; so are the excluded libraries, or excluded content would keep airing until the cache expired.
    /// </summary>
    private string ScopeKey() =>
        string.Join(KeySeparator, new[] { _options.BaseUrl,_options.UserId ?? string.Empty }.Concat(_options.LibraryExclusions(scope)));

    private async ValueTask<IReadOnlyList<MediaItem>> EnsureSnapshotAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return [];
        }

        // Once read, the snapshot is served straight away; when it's old it's re-read in the background.
        return await Cache.GetAsync(ScopeKey(), _options.CacheDuration, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<MediaItem>> ReadAsync(string scopeKey, CancellationToken cancellationToken)
    {
        string[] excluded = scopeKey.Split(KeySeparator).Skip(2).ToArray();

        try
        {
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

            logger.LogInformation("Jellyfin snapshot refreshed: {Count} items, {Collections} collections.", projected.Count, collections.Count);

            return projected;
        }
        finally
        {
            _loading = null;
            LoadingChanged?.Invoke();
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
            // Episodes are seldom given genres of their own: the series' stand for them.
            Genres = item.Genres is { Count: > 0 } genres ? genres : info?.Genres ?? [],
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
    public static TimeSpan? CreditsChapter(TimeSpan runtime, IEnumerable<(TimeSpan Start, string? Name)> chapters) =>
        ChapterMarkers.CreditsStart(runtime, chapters);

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
