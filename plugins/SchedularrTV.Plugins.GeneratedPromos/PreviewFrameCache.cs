namespace SchedularrTV.GeneratedPromos;

/// <summary>
/// Keeps the editor's extracted video frames (<c>preview-frames</c>) small: each entry (a single frame, or one
/// background's whole strip of frames) is dropped a day after it was last used, and the least recently used go first
/// once the folder is over its size budget. Entries are touched when reused, so what's being edited stays.
/// </summary>
public static class PreviewFrameCache
{
    public const string FolderName = "preview-frames";

    /// <summary>Unused this long, an entry goes.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    /// <summary>Over this, the least recently used entries go until it's back under <see cref="TrimTo"/>.</summary>
    public const long MaxBytes = 1024L * 1024 * 1024;

    public const long TrimTo = 512L * 1024 * 1024;

    /// <summary>Marks an entry (file or strip folder) as just used.</summary>
    public static void Touch(string path)
    {
        try
        {
            if (File.Exists(path)) File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            else if (Directory.Exists(path)) Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only bookkeeping; the entry may just go a little sooner.
        }
    }

    /// <summary>Removes old entries, then the least recently used while it's over budget. Returns the bytes freed.</summary>
    public static long Trim(string directory, DateTime now, TimeSpan? maxAge = null, long maxBytes = MaxBytes, long trimTo = TrimTo)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        List<(string Path, bool Folder, DateTime Used, long Bytes)> entries = [];
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            try
            {
                var info = new FileInfo(file);
                entries.Add((file, false, info.LastWriteTimeUtc, info.Length));
            }
            catch (IOException)
            {
            }
        }

        foreach (string folder in Directory.EnumerateDirectories(directory))
        {
            // A strip still being extracted (moved into place when it's done).
            if (folder.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) < TimeSpan.FromHours(1))
            {
                continue;
            }

            try
            {
                long bytes = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
                entries.Add((folder, true, Directory.GetLastWriteTimeUtc(folder), bytes));
            }
            catch (IOException)
            {
            }
        }

        long freed = 0;
        long total = entries.Sum(entry => entry.Bytes);
        TimeSpan age = maxAge ?? MaxAge;
        // Oldest first: everything past its age goes; then, only if it's over budget, the least recently used until
        // it's well under (so it isn't trimming again at every new frame).
        bool trimming = total > maxBytes;
        foreach ((string path, bool folder, DateTime used, long bytes) in entries.OrderBy(entry => entry.Used))
        {
            bool stale = now - used > age;
            if (!stale && (!trimming || total <= trimTo))
            {
                break;
            }

            if (Remove(path, folder))
            {
                freed += bytes;
                total -= bytes;
            }
        }

        return freed;
    }

    private static bool Remove(string path, bool folder)
    {
        try
        {
            if (folder) Directory.Delete(path, recursive: true);
            else File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use (being written or read right now): it's tried again next time.
            return false;
        }
    }
}
