namespace SchedularrTV.Service;

/// <summary>Reads <c>KEY=value</c> lines from a <c>.env</c> file so local secrets need no system variables.</summary>
public static class DotEnvFile
{
    /// <summary>The nearest <c>.env</c> in any start directory or its parents.</summary>
    public static string? Find(params string?[] startDirectories)
    {
        foreach (string? start in startDirectories)
        {
            for (DirectoryInfo? directory = string.IsNullOrWhiteSpace(start) ? null : new DirectoryInfo(start);
                 directory is not null;
                 directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, ".env");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public static IReadOnlyDictionary<string, string> Parse(IEnumerable<string> lines)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line[7..].TrimStart();
            }

            int equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            string value = line[(equals + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            {
                value = value[1..^1];
            }
            else if (value.IndexOf(" #", StringComparison.Ordinal) is var comment and >= 0)
            {
                value = value[..comment].TrimEnd();
            }

            values[line[..equals].Trim()] = value;
        }

        return values;
    }
}
