using System.Text.Json;
using System.Text.Json.Serialization;

namespace SchedularrTV.AutoChapters;

/// <summary>What was found in one file, or why it couldn't be analysed.</summary>
public sealed class ChapterRecord
{
    /// <summary>The path as the library reports it (the key).</summary>
    public string Path { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>File size and last write when analysed, so a replaced file is analysed again.</summary>
    public long Size { get; set; }

    public DateTime Modified { get; set; }

    /// <summary>Marker offsets in seconds.</summary>
    public List<double> Markers { get; set; } = [];

    public DateTimeOffset AnalysedAt { get; set; }

    public double Seconds { get; set; }

    /// <summary>Why it couldn't be analysed; null when it was.</summary>
    public string? Error { get; set; }

    [JsonIgnore]
    public IReadOnlyList<TimeSpan> MarkerTimes => [.. Markers.Select(TimeSpan.FromSeconds)];
}

/// <summary>
/// The markers found so far, kept in the plugin's data folder (<c>plugin-data/auto-chapters/chapters.json</c>) so
/// nothing is analysed twice. Lookups are by library path and cheap, since they run on every library search.
/// </summary>
public sealed class ChapterStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly string _path;
    private readonly Lock _gate = new();
    private Dictionary<string, ChapterRecord> _records;
    private int _version;

    public ChapterStore(string dataDirectory)
    {
        _path = System.IO.Path.Combine(dataDirectory, "chapters.json");
        _records = Load(_path);
    }

    /// <summary>Raised after a record is added or the store is cleared.</summary>
    public event Action? Changed;

    /// <summary>Raised after a programme's record is saved, with the record.</summary>
    public event Action<ChapterRecord>? Saved;

    /// <summary>Where the records are kept on disk.</summary>
    public string FilePath => _path;

    /// <summary>Goes up with every change, so caches built from the store know when to start over.</summary>
    public int Version => Volatile.Read(ref _version);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _records.Count;
            }
        }
    }

    public ChapterRecord? Find(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        lock (_gate)
        {
            return _records.GetValueOrDefault(path);
        }
    }

    public IReadOnlyList<ChapterRecord> Recent(int count)
    {
        lock (_gate)
        {
            return [.. _records.Values.OrderByDescending(record => record.AnalysedAt).Take(count)];
        }
    }

    public (int Found, int None, int Failed) Totals()
    {
        lock (_gate)
        {
            return (_records.Values.Count(record => record.Error is null && record.Markers.Count > 0),
                _records.Values.Count(record => record.Error is null && record.Markers.Count == 0),
                _records.Values.Count(record => record.Error is not null));
        }
    }

    public void Save(ChapterRecord record)
    {
        lock (_gate)
        {
            _records[record.Path] = record;
            Write();
        }

        Interlocked.Increment(ref _version);
        Changed?.Invoke();
        Saved?.Invoke(record);
    }

    /// <summary>Forgets everything (to analyse again after changing the detection settings).</summary>
    public void Clear(bool failedOnly = false)
    {
        lock (_gate)
        {
            _records = failedOnly
                ? _records.Where(pair => pair.Value.Error is null).ToDictionary(StringComparer.Ordinal)
                : new Dictionary<string, ChapterRecord>(StringComparer.Ordinal);
            Write();
        }

        Interlocked.Increment(ref _version);
        Changed?.Invoke();
    }

    // Called under the lock. Written whole each time: it's small (a few numbers per programme).
    private void Write()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_records.Values, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory; written again with the next record.
        }
    }

    private static Dictionary<string, ChapterRecord> Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<List<ChapterRecord>>(File.ReadAllText(path), Json) is { } records)
            {
                return records.Where(record => record.Path.Length > 0).DistinctBy(record => record.Path, StringComparer.Ordinal)
                    .ToDictionary(record => record.Path, StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }

        return new Dictionary<string, ChapterRecord>(StringComparer.Ordinal);
    }
}
