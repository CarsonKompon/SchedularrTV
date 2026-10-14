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
/// <para>
/// Only the very first read waits. After that the view in hand is always served, and is rebuilt in the background when
/// it's old, when a source was reconfigured, or when a source finished re-reading its library; open pages are told
/// (<see cref="Changed"/>) once the new view is in. A periodic re-read of a big library takes minutes, and pages, tag
/// pickers and autocompletes used to come up empty for all of it.
/// </para>
/// </remarks>
public sealed class LibraryBrowser(IMediaLibrary library, TimeProvider timeProvider, ILogger<LibraryBrowser>? logger = null)
    : SchedularrTV.Plugins.IMediaLibraryNotifier
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    /// <summary>How long to wait before trying again after a background rebuild failed.</summary>
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);

    /// <summary>Everything the pages read, built together and swapped in whole.</summary>
    private sealed record View(
        long Generation,
        DateTimeOffset BuiltAt,
        IReadOnlyList<LibraryShow> Shows,
        IReadOnlyList<LibraryMovie> Movies,
        IReadOnlyList<MediaItem> Programmes,
        IReadOnlyList<string> Collections,
        IReadOnlyList<string> VersionLabels,
        CommercialFolders Folders,
        IReadOnlyDictionary<string, List<LibraryEpisode>> EpisodesByShow,
        IReadOnlyList<MediaItem> Clips);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile View? _view;

    public async Task<IReadOnlyList<LibraryShow>> GetShowsAsync(CancellationToken cancellationToken = default) =>
        (await ViewAsync(cancellationToken).ConfigureAwait(false)).Shows;

    public async Task<IReadOnlyList<LibraryMovie>> GetMoviesAsync(CancellationToken cancellationToken = default) =>
        (await ViewAsync(cancellationToken).ConfigureAwait(false)).Movies;

    public async Task<IReadOnlyList<MediaItem>> GetProgrammesAsync(CancellationToken cancellationToken = default) =>
        (await ViewAsync(cancellationToken).ConfigureAwait(false)).Programmes;

    /// <summary>The collections items belong to, from any source (Jellyfin's BoxSets, the <c>set</c> in a local movie's .nfo…).</summary>
    public async Task<IReadOnlyList<string>> GetLibraryCollectionsAsync(CancellationToken cancellationToken = default) =>
        (await ViewAsync(cancellationToken).ConfigureAwait(false)).Collections;

    public async Task<CommercialFolders> GetCommercialFoldersAsync(CancellationToken cancellationToken = default) =>
        (await ViewAsync(cancellationToken).ConfigureAwait(false)).Folders;

    /// <summary>Every commercial, bumper and promo clip that has a file path (the commercials libraries' clips).</summary>
    public async Task<IReadOnlyList<MediaItem>> GetClipsAsync(CancellationToken cancellationToken = default) =>
        (await ViewAsync(cancellationToken).ConfigureAwait(false)).Clips;

    /// <summary>
    /// The part of each alternate version's name that actually distinguishes it, e.g. <c>Web Release</c>.
    /// Offered as ready-made choices so a version rule does not have to be typed from memory.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetVersionLabelsAsync(CancellationToken cancellationToken = default) =>
        (await ViewAsync(cancellationToken).ConfigureAwait(false)).VersionLabels;

    /// <summary>Episodes of one series, keyed by series id rather than name.</summary>
    public async Task<IReadOnlyList<LibraryEpisode>> GetEpisodesAsync(string seriesId, CancellationToken cancellationToken = default) =>
        (await ViewAsync(cancellationToken).ConfigureAwait(false)).EpisodesByShow.TryGetValue(seriesId, out List<LibraryEpisode>? episodes)
            ? episodes : [];

    /// <summary>
    /// Raised when what the browse pages show may have changed (the library was re-read, or is stale), so open pages
    /// load it again.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// True while the library is being re-read because a source changed, for an "updating" hint. What the pages show
    /// stays in place meanwhile.
    /// </summary>
    public bool Updating => Volatile.Read(ref _updating) == 1;

    private int _updating;
    private int _rebuildRunning;
    private long _generation;
    private long _retryAtTicks;

    /// <summary>
    /// A media source was switched on or off, or its settings changed: re-read in the background, keeping what the
    /// pages show until the new view is in.
    /// </summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        if (_view is null)
        {
            // Nothing read yet: the first read (or WarmUp) picks it up.
            Changed?.Invoke();
            return;
        }

        RebuildInBackground(TimeSpan.Zero, announce: true);
    }

    /// <summary>
    /// A media source's library changed (a plugin's web channel, guide or station saved, or Jellyfin or the local
    /// folders finished re-reading): re-read straight away, in the background, then tell the open pages. Changes close
    /// together become one re-read.
    /// </summary>
    public void LibraryChanged()
    {
        Interlocked.Increment(ref _generation);
        // A moment for the saves of one edit to land together.
        RebuildInBackground(TimeSpan.FromMilliseconds(300), announce: true);
    }

    /// <summary>
    /// Rebuilds the view behind the pages. One runs at a time; it keeps going until the view is up to date, so a change
    /// arriving mid-rebuild isn't lost. <paramref name="announce"/> shows the "updating" hint while it runs (a source
    /// changed); a rebuild because the view is merely old runs quietly. Open pages are told when it's done.
    /// </summary>
    private void RebuildInBackground(TimeSpan delay, bool announce)
    {
        if (announce)
        {
            Interlocked.Exchange(ref _updating, 1);
            Changed?.Invoke();
        }

        if (Interlocked.Exchange(ref _rebuildRunning, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            bool failed = false;
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                }

                while (_view is not { } view || IsStale(view))
                {
                    await BuildAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // What the pages show stays: an unreachable server shouldn't empty them.
                failed = true;
                Interlocked.Exchange(ref _retryAtTicks, (timeProvider.GetUtcNow() + RetryAfterFailure).UtcTicks);
                logger?.LogWarning("Couldn't re-read the library; the pages keep showing what was read before. {Message}",
                    ex.GetBaseException().Message);
            }
            finally
            {
                Interlocked.Exchange(ref _updating, 0);
                Volatile.Write(ref _rebuildRunning, 0);
            }

            // A change that came in just as this finished, while it still counted as running.
            if (!failed && _view is { } latest && latest.Generation != Interlocked.Read(ref _generation))
            {
                RebuildInBackground(TimeSpan.Zero, announce: true);
                return;
            }

            Changed?.Invoke();
        });
    }

    /// <summary>Whether a view was built before the latest change, or long enough ago to read again.</summary>
    private bool IsStale(View view) =>
        view.Generation != Interlocked.Read(ref _generation) || timeProvider.GetUtcNow() - view.BuiltAt >= CacheDuration;

    /// <summary>
    /// The view in hand, straight away, starting a background rebuild when it's stale. Only the very first read waits.
    /// </summary>
    private async ValueTask<View> ViewAsync(CancellationToken cancellationToken)
    {
        if (_view is not { } view)
        {
            return await BuildAsync(cancellationToken).ConfigureAwait(false);
        }

        if (IsStale(view) && timeProvider.GetUtcNow().UtcTicks >= Interlocked.Read(ref _retryAtTicks))
        {
            RebuildInBackground(TimeSpan.Zero, announce: false);
        }

        return view;
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
                await ViewAsync(CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>
    /// Re-reads every source now and waits for it (the Rescan button). The pages keep what they show until it's done.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await library.RefreshAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _generation);
        await BuildAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>Builds the view from the library (unless another caller just did) and swaps it in.</summary>
    private async Task<View> BuildAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_view is { } current && !IsStale(current))
            {
                return current;
            }

            // A change while this reads leaves the view a generation behind, so it's read again (see IsStale).
            long generation = Interlocked.Read(ref _generation);
            IReadOnlyList<MediaItem> all = await library
                .SearchAsync(QueryExpression.All, cancellationToken)
                .ConfigureAwait(false);

            // Alternate versions are separate provider items, so counting them would report an
            // episode twice. The browser shows what a collection would actually schedule, but the
            // per-show tally is taken first so the UI can still point out where versions exist.
            Dictionary<string, ShowVersions> versionsByShow = VersionsByShow(all);
            IReadOnlyList<string> versionLabels = [.. versionsByShow.Values
                .SelectMany(v => v.Labels)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)];

            all = VersionCollapser.Collapse(all, null);
            IReadOnlyList<MediaItem> programmes = all.Where(item => item.Kind is MediaKind.Episode or MediaKind.Movie).ToArray();

            // Grouped by series id, not name: a library can hold two distinct series with identical
            // names, and merging them would hide one of them entirely.
            IReadOnlyList<LibraryShow> shows = all
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

            IReadOnlyList<LibraryMovie> movies = all
                .Where(i => i.Kind == MediaKind.Movie)
                .Select(i => new LibraryMovie(i.Title, i.PremiereDate?.Year, i.Duration) { Id = i.Id })
                .DistinctBy(m => m.Title, StringComparer.OrdinalIgnoreCase)
                .OrderBy(m => TitleSortKey(m.Title), StringComparer.OrdinalIgnoreCase)
                .ToList();

            IReadOnlyList<string> collections = all
                .SelectMany(i => i.Collections)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();

            List<MediaItem> clips = all.Where(i => i.Kind == MediaKind.Filler && i.Tags.Count > 0).ToList();
            CommercialFolders folders = new(
                Level(clips, 0),
                Level(clips, 1),
                Level(clips, 2),
                Level(clips, 3));

            Dictionary<string, List<LibraryEpisode>> episodesByShow = all
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

            // Every commercial, bumper and promo clip with a path, for the ad break folder trees.
            IReadOnlyList<MediaItem> clipFiles = [.. all.Where(i => i.Kind == MediaKind.Filler && !string.IsNullOrWhiteSpace(i.Path))];

            View built = new(generation, timeProvider.GetUtcNow(), shows, movies, programmes, collections, versionLabels, folders, episodesByShow, clipFiles);
            _view = built;
            Interlocked.Exchange(ref _retryAtTicks, 0);
            return built;
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
