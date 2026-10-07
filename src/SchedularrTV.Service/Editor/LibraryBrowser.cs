using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;

namespace SchedularrTV.Service.Editor;

public sealed record LibraryShow(
    string Id,
    string Name,
    int? Year,
    int EpisodeCount,
    IReadOnlyList<int> Seasons,
    TimeSpan TotalRuntime,
    IReadOnlyList<string> Networks,
    int VersionedEpisodes,
    IReadOnlyList<string> Versions)
{
    public TimeSpan? AverageEpisodeDuration { get; init; }

    public string AverageEpisodeRuntime => AverageEpisodeDuration is { } duration
        ? $"{(int)duration.TotalMinutes}m {duration.Seconds:00}s" : "-";

    public string Runtime => TotalRuntime.TotalHours >= 1
        ? $"{(int)TotalRuntime.TotalHours}h {TotalRuntime.Minutes}m"
        : $"{TotalRuntime.Minutes}m";

    /// <summary>Explains the versions badge. Only meaningful when <see cref="VersionedEpisodes"/> is set.</summary>
    public string VersionHint =>
        Versions.Count > 0
            ? $"{VersionedEpisodes} episode(s) exist in more than one version. Only one ever airs. "
              + $"Versions here: {string.Join(", ", Versions)}. "
              + "Set which to prefer or avoid under a collection's Advanced section."
            : $"{VersionedEpisodes} episode(s) exist in more than one version. Only one ever airs. "
              + "These have no shared naming, so they cannot be told apart by name.";
}

public sealed record LibraryMovie(string Title, int? Year, TimeSpan Duration)
{
    public string? Id { get; init; }

    public string Runtime => $"{(int)Duration.TotalMinutes}m";
}

public sealed record LibraryEpisode(string Title, int Season, int Episode, TimeSpan Duration)
{
    public string Code => $"S{Season:D2}E{Episode:D2}";
}

/// <summary>
/// The folder levels of the commercials library, which the file-system provider records as ordered
/// tags: <c>&lt;root&gt;/&lt;brand&gt;/&lt;type&gt;/&lt;occasion&gt;</c>.
/// </summary>
public sealed record CommercialFolders(
    IReadOnlyList<string> Roots,
    IReadOnlyList<string> Brands,
    IReadOnlyList<string> Types,
    IReadOnlyList<string> Occasions);

/// <summary>
/// Read-only, aggregated view of the media library for the editor's browse pages.
/// </summary>
/// <remarks>
/// Cached because the browse pages re-query on every keystroke, and grouping 17k items per
/// keystroke would make the UI crawl.
/// </remarks>
public sealed class LibraryBrowser(IMediaLibrary library, TimeProvider timeProvider, ILogger<LibraryBrowser>? logger = null)
    : SchedularrTV.Plugins.IMediaLibraryNotifier
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyList<LibraryShow> _shows = [];
    private IReadOnlyList<LibraryMovie> _movies = [];
    private IReadOnlyList<MediaItem> _programmes = [];
    private IReadOnlyList<string> _collections = [];
    private IReadOnlyList<string> _versionLabels = [];
    private CommercialFolders _folders = new([], [], [], []);
    private Dictionary<string, List<LibraryEpisode>> _episodesByShow = new(StringComparer.Ordinal);
    private DateTimeOffset _builtAt = DateTimeOffset.MinValue;

    public async Task<IReadOnlyList<LibraryShow>> GetShowsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        return _shows;
    }

    public async Task<IReadOnlyList<LibraryMovie>> GetMoviesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        return _movies;
    }

    public async Task<IReadOnlyList<MediaItem>> GetProgrammesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        return _programmes;
    }

    /// <summary>The collections items belong to, from any source (Jellyfin's BoxSets, the <c>set</c> in a local movie's .nfo…).</summary>
    public async Task<IReadOnlyList<string>> GetLibraryCollectionsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        return _collections;
    }

    public async Task<CommercialFolders> GetCommercialFoldersAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        return _folders;
    }

    /// <summary>
    /// The part of each alternate version's name that actually distinguishes it, e.g. <c>Web Release</c>.
    /// Offered as ready-made choices so a version rule does not have to be typed from memory.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetVersionLabelsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        return _versionLabels;
    }

    /// <summary>Episodes of one series, keyed by series id rather than name.</summary>
    public async Task<IReadOnlyList<LibraryEpisode>> GetEpisodesAsync(string seriesId, CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        return _episodesByShow.TryGetValue(seriesId, out List<LibraryEpisode>? episodes) ? episodes : [];
    }

    /// <summary>
    /// Raised when what the browse pages show may have changed (the library was re-read, or is stale), so open pages
    /// load it again.
    /// </summary>
    public event Action? Changed;

    /// <summary>True while the library is being re-read after a plugin said it changed, for a "updating" hint.</summary>
    public bool Updating => Volatile.Read(ref _updating) == 1;

    private int _updating;
    private int _changePending;
    private long _generation;

    /// <summary>Re-reads the library on next use: a media source was switched on or off, or its settings changed.</summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _builtAt = DateTimeOffset.MinValue;
        Changed?.Invoke();
    }

    /// <summary>
    /// A plugin's media source changed (a web channel, guide or station saved): re-read straight away, in the
    /// background, then tell the open pages. Saves close together become one re-read.
    /// </summary>
    public void LibraryChanged()
    {
        Interlocked.Increment(ref _generation);
        _builtAt = DateTimeOffset.MinValue;
        if (Interlocked.Exchange(ref _changePending, 1) == 1)
        {
            return;
        }

        Interlocked.Exchange(ref _updating, 1);
        Changed?.Invoke();
        _ = Task.Run(async () =>
        {
            // A moment for the saves of one edit to land together.
            await Task.Delay(TimeSpan.FromMilliseconds(300)).ConfigureAwait(false);
            Interlocked.Exchange(ref _changePending, 0);
            try
            {
                await EnsureAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Couldn't re-read the library after a plugin changed it.");
            }
            finally
            {
                Interlocked.Exchange(ref _updating, 0);
                Changed?.Invoke();
            }
        });
    }

    /// <summary>True from <see cref="WarmUp"/> until the library has been read (or failed to be).</summary>
    public bool Warming => Volatile.Read(ref _warming) == 1;

    private int _warming;

    /// <summary>
    /// Reads the library in the background if it isn't already in hand, so the library pages and the channel
    /// wizard open on it straight away. Open pages are told when it's ready. Never throws.
    /// </summary>
    public void WarmUp()
    {
        if (Interlocked.Exchange(ref _warming, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await EnsureAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Not set up yet, or the server is down: the pages say so when they ask.
                logger?.LogInformation("The library couldn't be read in the background: {Message}", ex.GetBaseException().Message);
            }
            finally
            {
                Interlocked.Exchange(ref _warming, 0);
                Changed?.Invoke();
            }
        });
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await library.RefreshAsync(cancellationToken).ConfigureAwait(false);
        _builtAt = DateTimeOffset.MinValue;
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke();
    }

    private async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (timeProvider.GetUtcNow() - _builtAt < CacheDuration)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (timeProvider.GetUtcNow() - _builtAt < CacheDuration)
            {
                return;
            }

            long generation = Interlocked.Read(ref _generation);
            IReadOnlyList<MediaItem> all = await library
                .SearchAsync(QueryExpression.All, cancellationToken)
                .ConfigureAwait(false);

            // Alternate versions are separate provider items, so counting them would report an
            // episode twice. The browser shows what a collection would actually schedule, but the
            // per-show tally is taken first so the UI can still point out where versions exist.
            Dictionary<string, ShowVersions> versionsByShow = VersionsByShow(all);
            _versionLabels = [.. versionsByShow.Values
                .SelectMany(v => v.Labels)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)];

            all = VersionCollapser.Collapse(all, null);
            _programmes = all.Where(item => item.Kind is MediaKind.Episode or MediaKind.Movie).ToArray();

            // Grouped by series id, not name: a library can hold two distinct series with identical
            // names, and merging them would hide one of them entirely.
            _shows = all
                .Where(i => i.Kind == MediaKind.Episode && !string.IsNullOrWhiteSpace(i.SeriesName))
                .GroupBy(i => i.SeriesId ?? i.SeriesName!, StringComparer.Ordinal)
                .Select(g => new LibraryShow(
                    g.Key,
                    g.First().SeriesName!,
                    g.First().SeriesYear,
                    g.Count(),
                    g.Select(i => i.SeasonNumber ?? 0).Distinct().OrderBy(s => s).ToList(),
                    g.Aggregate(TimeSpan.Zero, (sum, i) => sum + i.Duration),
                    [.. g.SelectMany(i => i.Networks).Distinct(StringComparer.OrdinalIgnoreCase)],
                    versionsByShow.TryGetValue(g.Key, out ShowVersions? v) ? v.EpisodeCount : 0,
                    v?.Labels ?? [])
                {
                    AverageEpisodeDuration = g.Where(item => item.Duration > TimeSpan.Zero)
                        .Select(item => (double?)item.Duration.Ticks).Average() is { } averageTicks
                        ? TimeSpan.FromTicks((long)Math.Round(averageTicks)) : null
                })
                .OrderBy(s => TitleSortKey(s.Name), StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(s => s.EpisodeCount)
                .ToList();

            _movies = all
                .Where(i => i.Kind == MediaKind.Movie)
                .Select(i => new LibraryMovie(i.Title, i.PremiereDate?.Year, i.Duration) { Id = i.Id })
                .DistinctBy(m => m.Title, StringComparer.OrdinalIgnoreCase)
                .OrderBy(m => TitleSortKey(m.Title), StringComparer.OrdinalIgnoreCase)
                .ToList();

            _collections = all
                .SelectMany(i => i.Collections)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();

            List<MediaItem> clips = all.Where(i => i.Kind == MediaKind.Filler && i.Tags.Count > 0).ToList();
            _folders = new CommercialFolders(
                Level(clips, 0),
                Level(clips, 1),
                Level(clips, 2),
                Level(clips, 3));

            _episodesByShow = all
                .Where(i => i.Kind == MediaKind.Episode && !string.IsNullOrWhiteSpace(i.SeriesName))
                .GroupBy(i => i.SeriesId ?? i.SeriesName!, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g
                        .Select(i => new LibraryEpisode(i.Title, i.SeasonNumber ?? 0, i.EpisodeNumber ?? 0, i.Duration))
                        .OrderBy(e => e.Season)
                        .ThenBy(e => e.Episode)
                        .ToList(),
                    StringComparer.Ordinal);

            // Changed while it was being read: what was read may already be out of date, so the next use reads again.
            _builtAt = Interlocked.Read(ref _generation) == generation ? timeProvider.GetUtcNow() : DateTimeOffset.MinValue;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static readonly string[] LeadingArticles = ["The ", "An ", "A "];

    /// <summary>How many of a show's episodes exist in several versions, and what those are called.</summary>
    private sealed record ShowVersions(int EpisodeCount, IReadOnlyList<string> Labels);

    /// <summary>
    /// Groups alternate versions by the show they belong to, reducing each version name to the
    /// release it describes: "S01E01 The Beginning - DVD Release [2009-03-15]" becomes "DVD Release".
    /// </summary>
    /// <remarks>
    /// A label is the part where a group's names diverge, which keeps episode titles out: providers
    /// also merge unrelated files whose metadata collided, and those diverge from the first word.
    /// Trailing "[...]" is dropped first so dated variants of one release collapse to a single label
    /// instead of each becoming its own.
    /// </remarks>
    private static Dictionary<string, ShowVersions> VersionsByShow(IReadOnlyList<MediaItem> items)
    {
        const int maxLabelLength = 40;
        char[] separators = [' ', '-', '_', '.', '[', ']', '(', ')'];

        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        Dictionary<string, List<string>> labels = new(StringComparer.Ordinal);

        foreach (IGrouping<string, MediaItem> group in items
            .Where(i => i.VersionGroupId is not null && !string.IsNullOrWhiteSpace(i.SeriesName))
            .GroupBy(i => i.VersionGroupId!, StringComparer.Ordinal))
        {
            MediaItem first = group.First();
            string show = first.SeriesId ?? first.SeriesName!;

            counts[show] = counts.GetValueOrDefault(show) + 1;

            string[] names = [.. group
                .Select(i => VersionCollapser.WithoutTrailingBrackets(i.VersionName))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .Distinct(StringComparer.OrdinalIgnoreCase)];

            foreach (string label in GroupLabels(names, separators))
            {
                if (label.Length > maxLabelLength)
                {
                    continue;
                }

                if (!labels.TryGetValue(show, out List<string>? list))
                {
                    labels[show] = list = [];
                }

                list.Add(label);
            }
        }

        return counts.ToDictionary(
            c => c.Key,
            c => new ShowVersions(
                c.Value,
                labels.TryGetValue(c.Key, out List<string>? l)
                    ? [.. l.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)]
                    : []),
            StringComparer.Ordinal);
    }

    private static IEnumerable<string> GroupLabels(string[] names, char[] separators)
    {
        // Every version of this episode is the same release, differing only by a bracketed suffix
        // that has already been stripped, so the release is whatever follows the last " - ".
        if (names.Length == 1)
        {
            if (VersionCollapser.ReleaseLabel(names[0]) is { } single)
            {
                yield return single;
            }

            yield break;
        }

        if (names.Length == 0)
        {
            yield break;
        }

        int start = CommonPrefix(names);

        // The shared part has to end at an explicit qualifier separator, otherwise the names are
        // simply different files rather than one episode released twice.
        if (!names[0][..start].TrimEnd().EndsWith('-'))
        {
            yield break;
        }

        foreach (string name in names)
        {
            string label = name[start..].Trim(separators);

            if (label.Length > 0 && label.Any(char.IsLetter))
            {
                yield return label;
            }
        }
    }

    private static int CommonPrefix(string[] names)
    {
        int limit = names.Min(n => n.Length);

        for (int length = 0; length < limit; length++)
        {
            char c = char.ToUpperInvariant(names[0][length]);

            foreach (string name in names)
            {
                if (char.ToUpperInvariant(name[length]) != c)
                {
                    return length;
                }
            }
        }

        return limit;
    }

    /// <summary>Files "The Eric Andre Show" under E, the way a printed listings index reads.</summary>
    public static string TitleSortKey(string title)
    {
        foreach (string article in LeadingArticles)
        {
            if (title.StartsWith(article, StringComparison.OrdinalIgnoreCase))
            {
                return title[article.Length..];
            }
        }

        return title;
    }

    private static IReadOnlyList<string> Level(IEnumerable<MediaItem> clips, int depth) =>
        [.. clips
            .Where(i => i.Tags.Count > depth)
            .Select(i => i.Tags[depth])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)];
}
