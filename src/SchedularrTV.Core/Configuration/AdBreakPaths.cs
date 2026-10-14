using System.Text;
using System.Text.RegularExpressions;

namespace SchedularrTV.Core.Configuration;

/// <summary>
/// Folder paths as ad breaks compare them: either slash, any case, no trailing separator. Libraries on other machines
/// (a media server's) report their own paths, so nothing here touches the disk.
/// </summary>
public static class AdBreakPaths
{
    /// <summary>A comparable form of a folder path: forward slashes, no trailing slash.</summary>
    public static string Key(string path)
    {
        string normalized = path.Trim().Replace('\\', '/');
        while (normalized.Length > 1 && normalized.EndsWith('/'))
        {
            normalized = normalized[..^1];
        }

        return normalized;
    }

    /// <summary>Whether <paramref name="path"/> (a file or folder) is inside <paramref name="folder"/>.</summary>
    public static bool IsUnder(string? path, string folder)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        string root = Key(folder);
        string candidate = Key(path);
        return candidate.Length > root.Length
            && candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && candidate[root.Length] == '/';
    }

    /// <summary>Whether two folder paths are the same folder.</summary>
    public static bool Same(string left, string right) => string.Equals(Key(left), Key(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>The folders between <paramref name="folder"/> and the file at <paramref name="path"/>, outermost first.</summary>
    public static IReadOnlyList<string> FoldersBetween(string folder, string path)
    {
        string root = Key(folder);
        string candidate = Key(path);
        if (candidate.Length <= root.Length + 1)
        {
            return [];
        }

        string[] parts = candidate[(root.Length + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length <= 1 ? [] : parts[..^1];
    }

    /// <summary>The last folder name of a path.</summary>
    public static string Name(string path)
    {
        string key = Key(path);
        int slash = key.LastIndexOf('/');
        return slash < 0 ? key : key[(slash + 1)..];
    }

    /// <summary>A regex fragment matching the folder itself, with either slash as the separator.</summary>
    public static string Pattern(string folder)
    {
        StringBuilder pattern = new();
        bool separator = false;
        foreach (char c in Key(folder))
        {
            if (c == '/')
            {
                if (!separator)
                {
                    pattern.Append(Separator).Append('+');
                }

                separator = true;
                continue;
            }

            separator = false;
            pattern.Append(Regex.Escape(c.ToString()));
        }

        return pattern.ToString();
    }

    /// <summary>A regex character class for either slash.</summary>
    public const string Separator = @"[\\/]";

    /// <summary>A regex matching any path inside the folder.</summary>
    public static string Inside(string folder) => $"^{Pattern(folder)}{Separator}";
}
