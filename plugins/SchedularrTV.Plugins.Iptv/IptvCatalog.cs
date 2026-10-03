using System.Collections.Concurrent;
using System.IO.Compression;
using Microsoft.Extensions.Hosting;

namespace SchedularrTV.Iptv;

/// <summary>What was last read of one provider: its playlist and the guide for the channels carried.</summary>
public sealed record SourceCatalog(Playlist Playlist, XmltvGuide? Guide, DateTimeOffset LoadedAt, string? Problem = null, string? GuideProblem = null)
{
    public static SourceCatalog Failed(string problem, DateTimeOffset at, SourceCatalog? previous) =>
        previous is null
            ? new SourceCatalog(new Playlist([], []), null, at, problem)
            : previous with { Problem = problem };
}

/// <summary>Every provider's last-read playlist and guide, kept in memory between refreshes.</summary>
public sealed class IptvCatalog
{
    private readonly ConcurrentDictionary<string, SourceCatalog> _sources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised after a provider's playlist or guide was read again.</summary>
    public event Action? Changed;

    public SourceCatalog? Get(string sourceId) => _sources.GetValueOrDefault(sourceId);

    public void Set(string sourceId, SourceCatalog catalog)
    {
        _sources[sourceId] = catalog;
        Changed?.Invoke();
    }

    public void Retain(IEnumerable<string> sourceIds)
    {
        HashSet<string> keep = new(sourceIds, StringComparer.OrdinalIgnoreCase);
        foreach (string id in _sources.Keys.Where(id => !keep.Contains(id)).ToList())
        {
            _sources.TryRemove(id, out _);
        }
    }
}

/// <summary>Downloads playlists and guides, from the web or from files on this computer.</summary>
public sealed class IptvFetcher(IHttpClientFactory httpClients)
{
    public const string ClientName = "iptv";

    /// <summary>How far back and ahead guide listings are kept.</summary>
    public static readonly TimeSpan GuideBehind = TimeSpan.FromDays(1);
    public static readonly TimeSpan GuideAhead = TimeSpan.FromDays(15);

    public async Task<Playlist> LoadPlaylistAsync(IptvSource source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.PlaylistUrl))
        {
            throw new InvalidOperationException("There's no playlist address.");
        }

        string path = await DownloadAsync(source.PlaylistUrl, source.UserAgent, cancellationToken).ConfigureAwait(false);
        try
        {
            await using Stream stream = OpenMaybeCompressed(path);
            using var reader = new StreamReader(stream);
            Playlist playlist = M3uParser.Parse(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
            return playlist.Channels.Count == 0
                ? throw new InvalidOperationException("The playlist has no channels in it (is it an M3U playlist?).")
                : playlist;
        }
        finally
        {
            Delete(path, source.PlaylistUrl);
        }
    }

    /// <summary>The guide's listings for the given channels, or null when there's no guide to read.</summary>
    public async Task<XmltvGuide?> LoadGuideAsync(IptvSource source, Playlist playlist, IReadOnlyList<PlaylistChannel> carried, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // With the iptv-org guide, only an address set here fills in; the playlist's own (often long gone) isn't used.
        string? location = source.IptvOrgGuide
            ? string.IsNullOrWhiteSpace(source.GuideUrl) ? null : source.GuideUrl.Trim()
            : source.EffectiveGuideUrl(playlist.GuideUrls);
        if (location is null)
        {
            return null;
        }

        string path = await DownloadAsync(location, source.UserAgent, cancellationToken).ConfigureAwait(false);
        try
        {
            await using Stream stream = OpenMaybeCompressed(path);
            return await Task.Run(() => XmltvReader.Read(stream, guide =>
            {
                HashSet<string> wanted = new(StringComparer.OrdinalIgnoreCase);
                foreach (PlaylistChannel channel in carried)
                {
                    if (guide.Match(channel) is { } id)
                    {
                        wanted.Add(id);
                    }
                    else if (channel.TvgId is { } listed)
                    {
                        // Matched later, if the guide lists programmes for it without a <channel> entry.
                        wanted.Add(listed);
                    }
                }

                return wanted;
            }, now - GuideBehind, now + GuideAhead), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Delete(path, location);
        }
    }

    /// <summary>
    /// A guide grabbed with iptv-org/epg. Its channels are listed under the playlist's own <c>tvg-id</c>s; ones found by
    /// name (<paramref name="names"/>: guide id → playlist name) are given that name, so they match too. Channels it has
    /// no listings for are left out, so a provider's guide can fill them in.
    /// </summary>
    public static async Task<XmltvGuide> LoadIptvOrgGuideAsync(string file, IReadOnlyDictionary<string, string> names, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(file);
        XmltvGuide guide = await Task.Run(() => XmltvReader.Read(stream,
            read => new HashSet<string>(read.Channels.Keys, StringComparer.OrdinalIgnoreCase), now - GuideBehind, now + GuideAhead), cancellationToken)
            .ConfigureAwait(false);

        foreach ((string id, string name) in names)
        {
            if (!guide.Channels.TryGetValue(id, out List<string>? known))
            {
                guide.Channels[id] = known = [];
            }

            known.Add(name);
        }

        foreach (string id in guide.Channels.Keys.Where(id => !guide.Programmes.TryGetValue(id, out List<GuideProgramme>? listings) || listings.Count == 0).ToList())
        {
            guide.Channels.Remove(id);
        }

        return guide;
    }

    /// <summary>A local copy of a playlist or guide: a temporary download of a web address, or the file itself.</summary>
    private async Task<string> DownloadAsync(string location, string? userAgent, CancellationToken cancellationToken)
    {
        location = location.Trim().Trim('"');
        if (!IsWeb(location))
        {
            string local = location.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? new Uri(location).LocalPath : location;
            return File.Exists(local) ? local : throw new FileNotFoundException($"There's no file at {local}.");
        }

        using HttpClient http = httpClients.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, location);
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }

        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase} from {Redact(location)}");
        }

        string temp = Path.Combine(Path.GetTempPath(), $"schedularrtv-iptv-{Guid.NewGuid():N}.tmp");
        await using (FileStream file = File.Create(temp))
        {
            await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }

        return temp;
    }

    /// <summary>Opens a file, through gzip when it starts like a gzip file (guides are often .xml.gz).</summary>
    private static Stream OpenMaybeCompressed(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        int first = file.ReadByte();
        int second = file.ReadByte();
        file.Position = 0;
        return first == 0x1f && second == 0x8b ? new GZipStream(file, CompressionMode.Decompress) : file;
    }

    private static void Delete(string path, string location)
    {
        if (!IsWeb(location.Trim().Trim('"')))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static bool IsWeb(string location) =>
        location.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || location.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>A provider address without its query (where the username and password usually are), for messages.</summary>
    public static string Redact(string location) =>
        Uri.TryCreate(location, UriKind.Absolute, out Uri? uri) ? $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}" : location;
}

/// <summary>
/// Reads every provider's playlist and guide when the plugin starts, when the sources are saved, and on the refresh
/// interval; gives new channels their numbers; then asks the scheduler to publish so they reach ErsatzTV.
/// </summary>
public sealed class IptvRefresher(
    SourceStore store,
    IptvCatalog catalog,
    IptvFetcher fetcher,
    IptvSettings settings,
    IScheduleRepublisher republisher,
    TimeProvider clock,
    ILogger<IptvRefresher> logger,
    IptvOrg.IptvOrgGuides? iptvOrg = null) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0, 1);
    private volatile bool _refreshing;

    /// <summary>True while playlists and guides are being read.</summary>
    public bool Refreshing => _refreshing;

    /// <summary>Raised when a refresh starts or ends.</summary>
    public event Action? StateChanged;

    /// <summary>Reads everything again now (soon, if a refresh is already running).</summary>
    public void RequestRefresh()
    {
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        store.Changed += RequestRefresh;
        if (iptvOrg is not null)
        {
            iptvOrg.Grabbed += RequestRefresh;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await RefreshAllAsync(stoppingToken).ConfigureAwait(false);

                using var interval = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                interval.CancelAfter(settings.RefreshInterval);
                try
                {
                    await _wake.WaitAsync(interval.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // The interval passed.
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            store.Changed -= RequestRefresh;
            if (iptvOrg is not null)
            {
                iptvOrg.Grabbed -= RequestRefresh;
            }
        }
    }

    private async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        _refreshing = true;
        StateChanged?.Invoke();
        try
        {
            IReadOnlyList<IptvSource> sources = store.Sources;
            catalog.Retain(sources.Select(source => source.Id));
            foreach (IptvSource source in sources.Where(source => source.Enabled))
            {
                await RefreshAsync(source, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _refreshing = false;
            StateChanged?.Invoke();
        }

        republisher.RequestRepublish();
    }

    private async Task RefreshAsync(IptvSource source, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        SourceCatalog? previous = catalog.Get(source.Id);
        Playlist playlist;
        try
        {
            playlist = await fetcher.LoadPlaylistAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("IPTV {Source}: couldn't read the playlist: {Message}", source.Name, ex.Message);
            catalog.Set(source.Id, SourceCatalog.Failed($"Couldn't read the playlist: {ex.Message}", now, previous));
            return;
        }

        List<PlaylistChannel> carried = [.. playlist.Channels.Where(channel => source.Includes(channel.Key))];
        if (store.AssignNumbers(source.Id, carried.Select(channel => channel.Key)))
        {
            logger.LogInformation("IPTV {Source}: numbered its new channels.", source.Name);
        }

        XmltvGuide? guide = null;
        string? guideProblem = null;
        if (carried.Count > 0)
        {
            try
            {
                guide = await fetcher.LoadGuideAsync(source, playlist, carried, now, cancellationToken).ConfigureAwait(false);
                if (guide is null && !source.IptvOrgGuide)
                {
                    guideProblem = "No guide: set a guide address (the playlist doesn't name one), or make one with iptv-org/epg.";
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("IPTV {Source}: couldn't read the guide: {Message}", source.Name, ex.Message);
                guideProblem = $"Couldn't read the guide: {ex.Message}";

                // The last guide read is better than none until the provider's is back.
                guide = previous?.Guide;
            }

            // The iptv-org guide comes first; the provider's (if any) fills in the channels it has nothing for.
            if (source.IptvOrgGuide && iptvOrg?.GuideFile(source.Id) is { } grabbed)
            {
                try
                {
                    XmltvGuide own = await IptvFetcher.LoadIptvOrgGuideAsync(grabbed, iptvOrg.State(source.Id).Names, now, cancellationToken)
                        .ConfigureAwait(false);
                    guide = XmltvGuide.Merge(own, guide);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
                {
                    logger.LogWarning("IPTV {Source}: couldn't read the iptv-org guide: {Message}", source.Name, ex.Message);
                    guideProblem = $"Couldn't read the iptv-org guide: {ex.Message}";
                }
            }
            else if (source.IptvOrgGuide && guide is null)
            {
                guideProblem = "The iptv-org guide is being made; listings appear once the first grab finishes.";
            }
        }

        catalog.Set(source.Id, new SourceCatalog(playlist, guide, now, null, guideProblem));
        logger.LogInformation("IPTV {Source}: {Channels} channel(s) in the playlist, {Carried} carried, guide listings for {Listed}.",
            source.Name, playlist.Channels.Count, carried.Count, carried.Count(channel => guide?.Match(channel) is { } id && guide.Programmes.ContainsKey(id)));
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }
}
