using Microsoft.Extensions.Options;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Providers.FileSystem;

namespace SchedularrTV.Service.Editor;

/// <summary>One folder of a commercials library, for the ad break editor's folder trees.</summary>
public sealed class CommercialFolder
{
    public required string Name { get; init; }

    /// <summary>Full path, as the library reports its clips' paths.</summary>
    public required string Path { get; init; }

    public CommercialFolder? Parent { get; init; }

    public List<CommercialFolder> Children { get; } = [];

    /// <summary>Clips directly in this folder.</summary>
    public int Clips { get; set; }

    /// <summary>Clips in this folder and every folder inside it.</summary>
    public int TotalClips { get; set; }

    /// <summary>Folders from the root down to this one.</summary>
    public IEnumerable<CommercialFolder> Ancestors()
    {
        for (CommercialFolder? folder = Parent; folder is not null; folder = folder.Parent)
        {
            yield return folder;
        }
    }

    /// <summary>This folder and every folder inside it.</summary>
    public IEnumerable<CommercialFolder> Descendants()
    {
        yield return this;
        foreach (CommercialFolder child in Children)
        {
            foreach (CommercialFolder descendant in child.Descendants())
            {
                yield return descendant;
            }
        }
    }
}

/// <summary>
/// The folder trees of the commercials libraries: each local commercials folder (App settings) as its own root, read
/// from disk so empty folders show too, plus one root for clips from elsewhere (a media server's) by their paths.
/// </summary>
public sealed class CommercialTree(LibraryBrowser browser, IOptions<FileSystemLibraryOptions> options, ILogger<CommercialTree> logger)
{
    private const int MaxFolders = 20_000;

    public async Task<IReadOnlyList<CommercialFolder>> GetRootsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MediaItem> clips;
        try
        {
            clips = await browser.GetClipsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation("The commercials couldn't be read for the folder tree: {Message}", ex.GetBaseException().Message);
            clips = [];
        }

        return await Task.Run(() => Build(clips), cancellationToken);
    }

    private List<CommercialFolder> Build(IReadOnlyList<MediaItem> clips)
    {
        List<CommercialFolder> roots = [];
        Dictionary<string, CommercialFolder> byKey = new(StringComparer.OrdinalIgnoreCase);

        foreach (MediaFolderOptions folder in options.Value.Folders.Where(folder => !folder.IsLibrary && !string.IsNullOrWhiteSpace(folder.Path)))
        {
            string path;
            try
            {
                path = System.IO.Path.GetFullPath(folder.Path).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (byKey.ContainsKey(AdBreakPaths.Key(path)))
            {
                continue;
            }

            CommercialFolder root = new() { Name = AdBreakPaths.Name(path), Path = path };
            roots.Add(root);
            byKey[AdBreakPaths.Key(path)] = root;
            ReadDisk(root, byKey);
        }

        // Clips from elsewhere: grouped under the folder they all share (or one per drive when they share none).
        List<MediaItem> elsewhere = [];
        foreach (MediaItem clip in clips)
        {
            if (Folder(clip.Path!) is not { } directory)
            {
                continue;
            }

            if (roots.FirstOrDefault(root => AdBreakPaths.IsUnder(clip.Path, root.Path)) is { } owner)
            {
                Count(owner, directory, byKey);
            }
            else
            {
                elsewhere.Add(clip);
            }
        }

        foreach (IGrouping<string, MediaItem> group in elsewhere.GroupBy(clip => FirstSegment(clip.Path!), StringComparer.OrdinalIgnoreCase))
        {
            string common = CommonFolder([.. group.Select(clip => Folder(clip.Path!)!)]);
            CommercialFolder root = new() { Name = AdBreakPaths.Name(common) is { Length: > 0 } name ? name : common, Path = common };
            roots.Add(root);
            byKey[AdBreakPaths.Key(common)] = root;
            foreach (MediaItem clip in group)
            {
                Count(root, Folder(clip.Path!)!, byKey);
            }
        }

        foreach (CommercialFolder root in roots)
        {
            Total(root);
            Sort(root);
        }

        return roots;
    }

    private void ReadDisk(CommercialFolder root, Dictionary<string, CommercialFolder> byKey)
    {
        if (!Directory.Exists(root.Path))
        {
            return;
        }

        try
        {
            int read = 0;
            foreach (string directory in Directory.EnumerateDirectories(root.Path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                         .OrderBy(directory => directory.Length))
            {
                if (++read > MaxFolders)
                {
                    break;
                }

                if (AdBreakPaths.Key(directory)[(AdBreakPaths.Key(root.Path).Length)..].Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part.StartsWith('.')))
                {
                    continue;
                }

                Ensure(root, directory, byKey);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogInformation("Couldn't read the folders under {Path}: {Message}", root.Path, ex.Message);
        }
    }

    /// <summary>The node for a folder under <paramref name="root"/>, adding it (and the folders above it) if need be.</summary>
    private static CommercialFolder Ensure(CommercialFolder root, string directory, Dictionary<string, CommercialFolder> byKey)
    {
        string key = AdBreakPaths.Key(directory);
        if (byKey.TryGetValue(key, out CommercialFolder? existing))
        {
            return existing;
        }

        if (!AdBreakPaths.IsUnder(directory, root.Path))
        {
            return root;
        }

        string parentPath = directory[..Math.Max(0, LastSeparator(directory))];
        CommercialFolder parent = parentPath.Length == 0 || AdBreakPaths.Same(parentPath, root.Path) ? root : Ensure(root, parentPath, byKey);
        CommercialFolder node = new() { Name = AdBreakPaths.Name(directory), Path = directory.TrimEnd('/', '\\'), Parent = parent };
        parent.Children.Add(node);
        byKey[key] = node;
        return node;
    }

    private static void Count(CommercialFolder root, string directory, Dictionary<string, CommercialFolder> byKey)
    {
        CommercialFolder folder = AdBreakPaths.Same(directory, root.Path) ? root : Ensure(root, directory, byKey);
        folder.Clips++;
    }

    private static int Total(CommercialFolder folder)
    {
        folder.TotalClips = folder.Clips + folder.Children.Sum(Total);
        return folder.TotalClips;
    }

    private static void Sort(CommercialFolder folder)
    {
        folder.Children.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (CommercialFolder child in folder.Children)
        {
            Sort(child);
        }
    }

    private static int LastSeparator(string path) => Math.Max(path.TrimEnd('/', '\\').LastIndexOf('/'), path.TrimEnd('/', '\\').LastIndexOf('\\'));

    private static string? Folder(string path)
    {
        int separator = LastSeparator(path);
        return separator <= 0 ? null : path[..separator];
    }

    private static string FirstSegment(string path)
    {
        string key = AdBreakPaths.Key(path);
        int slash = key.IndexOf('/', key.StartsWith('/') ? 1 : 0);
        return slash < 0 ? key : key[..slash];
    }

    /// <summary>The deepest folder every one of <paramref name="folders"/> is in (or is).</summary>
    private static string CommonFolder(IReadOnlyList<string> folders)
    {
        string common = folders[0];
        foreach (string folder in folders.Skip(1))
        {
            while (common.Length > 0 && !AdBreakPaths.Same(folder, common) && !AdBreakPaths.IsUnder(folder, common))
            {
                int separator = LastSeparator(common);
                common = separator <= 0 ? string.Empty : common[..separator];
            }
        }

        return common.Length == 0 ? folders[0][..Math.Max(1, folders[0].IndexOfAny(['/', '\\']) + 1)] : common;
    }
}
