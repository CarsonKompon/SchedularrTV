namespace SchedularrTV.Service.Editor;

public sealed record FolderListing(string? Folder, string? Parent, IReadOnlyList<string> Folders, IReadOnlyList<string> Files, string? Error);

/// <summary>Folder listings for the editor's file pickers; the scheduler reads these paths itself, so they are the server's.</summary>
public static class FileBrowsing
{
    public static readonly string[] Audio = [".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma"];
    public static readonly string[] Images = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"];
    public static readonly string[] Videos = [".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".mpg", ".mpeg", ".ts"];

    /// <summary>Label for the top level, which lists drives on Windows and places on macOS and Linux.</summary>
    public static string RootLabel => OperatingSystem.IsWindows() ? "This PC" : "Locations";

    /// <summary>
    /// A folder's sub-folders and matching files; a null folder lists the drives. No extensions lists every
    /// file, which is how programs without an extension (ffmpeg on macOS and Linux) are picked.
    /// </summary>
    public static FolderListing List(string? folder, IReadOnlyCollection<string> extensions)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return new FolderListing(null, null, Roots(), [], null);
        }

        var directory = new DirectoryInfo(folder);
        string? parent = directory.Parent?.FullName;
        try
        {
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
            return new FolderListing(
                directory.FullName,
                parent,
                [.. directory.EnumerateDirectories("*", options).Select(entry => entry.FullName).Order(StringComparer.OrdinalIgnoreCase)],
                [.. directory.EnumerateFiles("*", options)
                    .Where(file => extensions.Count == 0 || extensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
                    .Select(file => file.FullName)
                    .Order(StringComparer.OrdinalIgnoreCase)],
                null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new FolderListing(directory.FullName, parent, [], [], ex.Message);
        }
    }

    private static readonly string[] MountFolders = ["/Volumes/", "/media/", "/mnt/", "/run/media/"];

    /// <summary>
    /// Drives on Windows. Elsewhere every mount point is a "drive", including /proc, /sys and snap images,
    /// so only home, the root and real removable or network mounts are offered.
    /// </summary>
    private static List<string> Roots()
    {
        IEnumerable<DriveInfo> ready = DriveInfo.GetDrives().Where(drive =>
        {
            try
            {
                return drive.IsReady;
            }
            catch (IOException)
            {
                return false;
            }
        });

        if (OperatingSystem.IsWindows())
        {
            return [.. ready.Select(drive => drive.RootDirectory.FullName)];
        }

        List<string> roots = [];
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Directory.Exists(home))
        {
            roots.Add(home);
        }

        roots.Add("/");
        roots.AddRange(ready
            .Select(drive => drive.RootDirectory.FullName)
            .Where(path => MountFolders.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal));
        return [.. roots.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Where a picker opens: beside the current file, else the last folder used, else a sensible default.</summary>
    public static string? StartFolder(string? current, string? last, Environment.SpecialFolder fallback)
    {
        if (!string.IsNullOrWhiteSpace(current) && Path.IsPathFullyQualified(current))
        {
            string? beside = Directory.Exists(current) ? current : Path.GetDirectoryName(current);
            if (beside is not null && Directory.Exists(beside))
            {
                return beside;
            }
        }

        if (last is not null && Directory.Exists(last))
        {
            return last;
        }

        string special = Environment.GetFolderPath(fallback);
        return Directory.Exists(special) ? special : null;
    }
}
