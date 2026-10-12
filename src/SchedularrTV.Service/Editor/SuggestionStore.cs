using System.Text.Json;

namespace SchedularrTV.Service.Editor;

/// <summary>
/// Remembers which suggested channels (by network name) the user has chosen to ignore.
/// </summary>
/// <remarks>
/// Kept in its own small file beside the schedule rather than in the schedule itself, so ignoring a
/// suggestion doesn't leave the editor with unsaved changes or show up in the YAML.
/// </remarks>
public sealed class SuggestionStore(SchedulerConfigProvider config, ILogger<SuggestionStore>? logger = null)
{
    public const string FileName = "ignored-suggestions.json";

    private readonly Lock _gate = new();
    private HashSet<string>? _ignored;

    /// <summary>Raised after a suggestion is ignored or restored, so other open tabs update too.</summary>
    public event Action? Changed;

    private string FilePath => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(config.ConfigPath)) ?? ".", FileName);

    public bool IsIgnored(string network)
    {
        lock (_gate)
        {
            return Load().Contains(network.Trim());
        }
    }

    public void Ignore(string network) => Update(set => set.Add(network.Trim()));

    public void Restore(string network) => Update(set => set.Remove(network.Trim()));

    private void Update(Func<HashSet<string>, bool> change)
    {
        lock (_gate)
        {
            if (!change(Load()))
            {
                return;
            }

            Save();
        }

        Changed?.Invoke();
    }

    private HashSet<string> Load()
    {
        if (_ignored is not null)
        {
            return _ignored;
        }

        _ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) is { } saved)
            {
                _ignored.UnionWith(saved.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged file just means every suggestion shows again; it's rewritten on the next change.
            logger?.LogWarning(ex, "Couldn't read {Path}; ignored suggestions are reset", FilePath);
        }

        return _ignored;
    }

    private void Save()
    {
        try
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_ignored!.Order(StringComparer.OrdinalIgnoreCase).ToList(),
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still applied for this session; it just won't survive a restart.
            logger?.LogWarning(ex, "Couldn't save {Path}", FilePath);
        }
    }
}
