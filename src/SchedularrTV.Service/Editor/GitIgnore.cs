using System.Text;
using System.Text.RegularExpressions;

namespace SchedularrTV.Service.Editor;

/// <summary>
/// Lists a folder's files the way git would see them: anything its <c>.gitignore</c> files (the top one and any in
/// subfolders) exclude is left out, and so is the <c>.git</c> folder. Without a <c>.gitignore</c>, everything is listed.
/// </summary>
public static class GitIgnore
{
    private sealed record Rule(string Base, Regex Pattern, bool Negate, bool DirectoryOnly);

    /// <summary>Paths relative to <paramref name="root"/>, with <c>/</c> between folders.</summary>
    public static IEnumerable<string> Files(string root) => Walk(Path.GetFullPath(root), string.Empty, []);

    private static IEnumerable<string> Walk(string root, string relative, IReadOnlyList<Rule> inherited)
    {
        string directory = relative.Length == 0 ? root : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        IReadOnlyList<Rule> rules = inherited;
        string ignoreFile = Path.Combine(directory, ".gitignore");
        if (File.Exists(ignoreFile))
        {
            rules = [.. inherited, .. Parse(File.ReadAllLines(ignoreFile), relative)];
        }

        foreach (string folder in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(folder);
            // Linked folders could loop back on themselves (or reach far outside), so they're skipped.
            if (name == ".git" || new DirectoryInfo(folder).LinkTarget is not null)
            {
                continue;
            }

            string path = Join(relative, name);
            if (Matches(rules, path, isDirectory: true))
            {
                continue;
            }

            foreach (string file in Walk(root, path, rules))
            {
                yield return file;
            }
        }

        foreach (string file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
        {
            string path = Join(relative, Path.GetFileName(file));
            if (!Matches(rules, path, isDirectory: false))
            {
                yield return path;
            }
        }
    }

    /// <summary>Whether a path (relative to the top folder, <c>/</c>-separated) is ignored by <paramref name="lines"/> of a top-level .gitignore.</summary>
    public static bool IsIgnored(IEnumerable<string> lines, string path, bool isDirectory) =>
        Matches(Parse(lines, string.Empty).ToList(), path, isDirectory);

    private static bool Matches(IReadOnlyList<Rule> rules, string path, bool isDirectory)
    {
        // The last rule that matches decides, as in git.
        bool ignored = false;
        foreach (Rule rule in rules)
        {
            if (rule.DirectoryOnly && !isDirectory)
            {
                continue;
            }

            string local;
            if (rule.Base.Length == 0)
            {
                local = path;
            }
            else if (path.StartsWith(rule.Base + "/", StringComparison.Ordinal))
            {
                local = path[(rule.Base.Length + 1)..];
            }
            else
            {
                continue;
            }

            if (rule.Pattern.IsMatch(local))
            {
                ignored = !rule.Negate;
            }
        }

        return ignored;
    }

    private static IEnumerable<Rule> Parse(IEnumerable<string> lines, string relative)
    {
        RegexOptions options = RegexOptions.CultureInvariant | (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? RegexOptions.IgnoreCase : RegexOptions.None);
        foreach (string raw in lines)
        {
            string line = TrimTrailingSpaces(raw.TrimEnd('\r'));
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            bool negate = false;
            if (line[0] == '!')
            {
                negate = true;
                line = line[1..];
            }
            else if (line.StartsWith("\\!", StringComparison.Ordinal) || line.StartsWith("\\#", StringComparison.Ordinal))
            {
                line = line[1..];
            }

            bool directoryOnly = false;
            if (line.EndsWith('/'))
            {
                directoryOnly = true;
                line = line.TrimEnd('/');
            }

            if (line.Length == 0)
            {
                continue;
            }

            // A slash anywhere but the end ties the pattern to this .gitignore's folder; otherwise it matches at any depth.
            bool anchored = line.Contains('/');
            line = line.TrimStart('/');
            if (line.Length == 0)
            {
                continue;
            }

            string pattern = "^" + (anchored ? string.Empty : "(?:.*/)?") + Translate(line) + "$";
            yield return new Rule(relative, new Regex(pattern, options), negate, directoryOnly);
        }
    }

    private static string TrimTrailingSpaces(string line)
    {
        int end = line.Length;
        while (end > 0 && line[end - 1] == ' ' && !(end > 1 && line[end - 2] == '\\'))
        {
            end--;
        }

        return line[..end];
    }

    private static string Translate(string glob)
    {
        var regex = new StringBuilder();
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            switch (c)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                {
                    bool atStart = i == 0 || glob[i - 1] == '/';
                    int after = i + 2;
                    if (atStart && after < glob.Length && glob[after] == '/')
                    {
                        // "**/": any number of folders, including none.
                        regex.Append("(?:.*/)?");
                        i = after;
                    }
                    else if (atStart && after == glob.Length)
                    {
                        // A trailing "/**": everything inside.
                        regex.Append(".*");
                        i = after - 1;
                    }
                    else
                    {
                        regex.Append("[^/]*");
                        i = after - 1;
                    }

                    break;
                }
                case '*':
                    regex.Append("[^/]*");
                    break;
                case '?':
                    regex.Append("[^/]");
                    break;
                case '[':
                {
                    int start = i + 1;
                    if (start < glob.Length && glob[start] is '!' or '^') start++;
                    if (start < glob.Length && glob[start] == ']') start++;
                    int close = start < glob.Length ? glob.IndexOf(']', start) : -1;
                    if (close < 0)
                    {
                        regex.Append("\\[");
                        break;
                    }

                    string set = glob[(i + 1)..close];
                    if (set.StartsWith('!')) set = "^" + set[1..];
                    regex.Append('[').Append(set.Replace("\\", "\\\\").Replace("[", "\\[")).Append(']');
                    i = close;
                    break;
                }
                case '\\' when i + 1 < glob.Length:
                    regex.Append(Regex.Escape(glob[i + 1].ToString()));
                    i++;
                    break;
                default:
                    regex.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        return regex.ToString();
    }

    private static string Join(string relative, string name) => relative.Length == 0 ? name : $"{relative}/{name}";
}
