using System.Text.Json;

namespace SchedularrTV.Iptv;

/// <summary>The IPTV providers, kept in the plugin's data folder (<c>plugin-data/iptv/sources.json</c>).</summary>
public sealed class SourceStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly string _path;
    private readonly Lock _gate = new();
    private IReadOnlyList<IptvSource> _sources;

    public SourceStore(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "sources.json");
        _sources = Load(_path);
    }

    /// <summary>Raised after the sources are saved.</summary>
    public event Action? Changed;

    public IReadOnlyList<IptvSource> Sources
    {
        get
        {
            lock (_gate)
            {
                return _sources;
            }
        }
    }

    public IptvSource? Find(string id) =>
        Sources.FirstOrDefault(source => string.Equals(source.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Replaces every source; ids are made unique and values tidied. Throws if the file can't be written.</summary>
    public void Save(IEnumerable<IptvSource> sources)
    {
        List<IptvSource> saved = [];
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (IptvSource source in sources)
        {
            IptvSource copy = source.Copy();
            copy.Name = string.IsNullOrWhiteSpace(copy.Name) ? "IPTV" : copy.Name.Trim();
            copy.PlaylistUrl = copy.PlaylistUrl.Trim().Trim('"');
            copy.GuideUrl = string.IsNullOrWhiteSpace(copy.GuideUrl) ? null : copy.GuideUrl.Trim().Trim('"');
            copy.UserAgent = string.IsNullOrWhiteSpace(copy.UserAgent) ? null : copy.UserAgent.Trim();
            copy.Group = string.IsNullOrWhiteSpace(copy.Group) ? null : copy.Group.Trim();
            copy.FirstNumber = Math.Clamp(copy.FirstNumber, 1, 99999);
            copy.Selected = [.. copy.Selected.Distinct(StringComparer.Ordinal)];

            string id = string.IsNullOrWhiteSpace(copy.Id) ? IptvSource.Slug(copy.Name, 24) : copy.Id;
            string unique = id;
            for (int suffix = 2; !ids.Add(unique); suffix++)
            {
                unique = $"{id}-{suffix}";
            }

            copy.Id = unique;

            // Numbers given since the editor read the sources are kept, not lost with its older copy.
            if (Find(copy.Id) is { } stored)
            {
                foreach ((string key, int number) in stored.Numbers)
                {
                    copy.Numbers.TryAdd(key, number);
                }
            }

            saved.Add(copy);
        }

        Write(saved);
        Changed?.Invoke();
    }

    /// <summary>
    /// Gives a number to each carried channel that hasn't one: the next free one from the source's first number, free
    /// meaning not used by any source. Numbers already given are kept. Saves (without raising <see cref="Changed"/>)
    /// when anything was given.
    /// </summary>
    /// <param name="keys">The source's carried channels, in playlist order.</param>
    public bool AssignNumbers(string sourceId, IEnumerable<string> keys)
    {
        lock (_gate)
        {
            List<IptvSource> sources = [.. _sources.Select(source => source.Copy())];
            IptvSource? target = sources.FirstOrDefault(source => string.Equals(source.Id, sourceId, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                return false;
            }

            HashSet<int> used = [.. sources.SelectMany(source => source.Numbers.Values)];
            int next = target.FirstNumber;
            bool changed = false;
            foreach (string key in keys)
            {
                if (target.Numbers.ContainsKey(key))
                {
                    continue;
                }

                while (used.Contains(next))
                {
                    next++;
                }

                target.Numbers[key] = next;
                used.Add(next);
                changed = true;
            }

            if (changed)
            {
                WriteLocked(sources);
            }

            return changed;
        }
    }

    private void Write(List<IptvSource> sources)
    {
        lock (_gate)
        {
            WriteLocked(sources);
        }
    }

    private void WriteLocked(List<IptvSource> sources)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(sources, Json));
        File.Move(temp, _path, overwrite: true);
        _sources = sources;
    }

    private static IReadOnlyList<IptvSource> Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<IptvSource>>(File.ReadAllText(path), Json) ?? [] : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
