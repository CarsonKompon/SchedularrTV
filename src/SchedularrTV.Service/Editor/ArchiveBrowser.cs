using System.Collections.Concurrent;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.Service.Editor;

public sealed class ArchiveBrowser
{
    private readonly ConcurrentDictionary<(string Directory, int Start, int End), ArchiveScan> _scans = new();

    public ArchiveScan? Cached(ArchiveFollowConfig follow) =>
        _scans.TryGetValue(Key(follow), out ArchiveScan? scan) ? scan : null;

    public Task<IReadOnlyList<DateOnly>> DatesAsync(ArchiveFollowConfig follow, CancellationToken cancellationToken) =>
        Task.Run(() => ArchiveReader.ListDates(follow, cancellationToken), cancellationToken);

    public async Task<ArchiveScan> ScanAsync(ArchiveFollowConfig follow, CancellationToken cancellationToken)
    {
        var key = Key(follow);
        ArchiveScan scan = await ArchiveReader.ScanAsync(follow, cancellationToken).ConfigureAwait(false);
        if (_scans.Count >= 8)
        {
            _scans.Clear();
        }
        _scans[key] = scan;
        return scan;
    }

    /// <summary>Forgets scans of a folder whose files were just edited, so the next scan reads them again.</summary>
    public void Invalidate(string directory)
    {
        foreach (var key in _scans.Keys.Where(key => string.Equals(
                     Path.GetFullPath(key.Directory), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase)))
        {
            _scans.TryRemove(key, out _);
        }
    }

    private static (string, int, int) Key(ArchiveFollowConfig follow) => (follow.Directory, follow.StartYear, follow.EndYear);
}