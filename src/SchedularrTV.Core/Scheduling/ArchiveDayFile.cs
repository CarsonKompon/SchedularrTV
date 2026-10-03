using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SchedularrTV.Core.Scheduling;

/// <summary>One row of an archived day as written in its file, for editing: times as text, end optional.</summary>
public sealed class ArchiveRowDraft
{
    /// <summary><c>HH:mm</c>.</summary>
    public string Time { get; set; } = string.Empty;

    /// <summary><c>HH:mm</c>, <c>24:00</c> for midnight, or empty to run until the next row starts.</summary>
    public string? EndTime { get; set; }

    public string Show { get; set; } = string.Empty;

    public string? Episode { get; set; }

    public List<string> Blocks { get; set; } = [];

    // Whatever else the row had in the file (archives often carry extra fields), written back untouched.
    internal JsonObject? Original { get; set; }

    public ArchiveRowDraft Copy() => new()
    {
        Time = Time,
        EndTime = EndTime,
        Show = Show,
        Episode = Episode,
        Blocks = [.. Blocks],
        Original = Original?.DeepClone() as JsonObject
    };
}

/// <summary>An archived day's file, open for editing.</summary>
public sealed class ArchiveDayDocument
{
    public List<ArchiveRowDraft> Rows { get; set; } = [];

    // The file's other top-level fields, kept when it's saved.
    internal JsonObject? Root { get; set; }

    /// <summary>An independent copy, for editing without touching what was read.</summary>
    public ArchiveDayDocument Copy() => new()
    {
        Rows = [.. Rows.Select(row => row.Copy())],
        Root = Root?.DeepClone() as JsonObject
    };
}

/// <summary>
/// Reads and writes an Archive Follow folder's day files (<c>YYYY/MM/YYYY-MM-DD.json</c>, each
/// <c>{"schedule":[{"time","end_time","show","episode","blocks"}]}</c>) and keeps <c>_block_index.json</c> listing their
/// block labels. <see cref="ArchiveReader"/> is what schedules from them.
/// </summary>
public static class ArchiveDayFile
{
    public const string BlockIndexFile = "_block_index.json";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static string PathFor(string directory, DateOnly date) => Path.Combine(directory,
        date.Year.ToString("D4", CultureInfo.InvariantCulture), date.Month.ToString("D2", CultureInfo.InvariantCulture), $"{date:yyyy-MM-dd}.json");

    /// <summary>Every day with a file in the folder, in any year.</summary>
    public static IReadOnlyList<DateOnly> ListDates(string directory)
    {
        SortedSet<DateOnly> dates = [];
        if (!Directory.Exists(directory))
        {
            return [];
        }

        foreach (string yearFolder in Directory.EnumerateDirectories(directory))
        {
            if (!int.TryParse(Path.GetFileName(yearFolder), NumberStyles.None, CultureInfo.InvariantCulture, out int year))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(yearFolder, "*.json", SearchOption.AllDirectories))
            {
                if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateOnly date) && date.Year == year && path == PathFor(directory, date))
                {
                    dates.Add(date);
                }
            }
        }

        return [.. dates];
    }

    /// <summary>The day's file, or null when there isn't one. Throws when it can't be read or parsed.</summary>
    public static async Task<ArchiveDayDocument?> ReadAsync(string directory, DateOnly date, CancellationToken cancellationToken = default)
    {
        string path = PathFor(directory, date);
        if (!File.Exists(path))
        {
            return null;
        }

        await using FileStream stream = File.OpenRead(path);
        JsonNode? node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        JsonObject root = node as JsonObject ?? throw new JsonException($"{Path.GetFileName(path)} isn't a JSON object.");

        List<ArchiveRowDraft> rows = [];
        if (root["schedule"] is JsonArray schedule)
        {
            foreach (JsonObject row in schedule.OfType<JsonObject>())
            {
                rows.Add(new ArchiveRowDraft
                {
                    Time = Text(row, "time") ?? string.Empty,
                    EndTime = Text(row, "end_time"),
                    Show = Text(row, "show") ?? string.Empty,
                    Episode = Text(row, "episode"),
                    Blocks = row["blocks"] is JsonArray blocks
                        ? [.. blocks.OfType<JsonValue>().Where(block => block.GetValueKind() == JsonValueKind.String).Select(block => block.GetValue<string>())]
                        : [],
                    Original = (JsonObject)row.DeepClone()
                });
            }
        }

        root.Remove("schedule");
        return new ArchiveDayDocument { Rows = rows, Root = root };
    }

    /// <summary>What's wrong with a day's rows, if anything; nothing is written while there are problems.</summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<ArchiveRowDraft> rows)
    {
        List<string> problems = [];
        HashSet<TimeSpan> starts = [];
        for (int index = 0; index < rows.Count; index++)
        {
            ArchiveRowDraft row = rows[index];
            string where = $"Row {index + 1}";
            if (ParseTime(row.Time, allowMidnight: false) is not { } start)
            {
                problems.Add($"{where}: the start time must look like 06:30.");
                continue;
            }

            if (!starts.Add(start))
            {
                problems.Add($"{where}: another row also starts at {row.Time}.");
            }

            if (!string.IsNullOrWhiteSpace(row.EndTime))
            {
                if (ParseTime(row.EndTime, allowMidnight: true) is not { } end)
                {
                    problems.Add($"{where}: the end time must look like 07:00 (24:00 for midnight), or be left empty.");
                }
                else if (end <= start && end != TimeSpan.Zero)
                {
                    problems.Add($"{where}: it ends before it starts.");
                }
            }

            if (string.IsNullOrWhiteSpace(row.Show))
            {
                problems.Add($"{where}: name the show.");
            }
        }

        return problems;
    }

    /// <summary>
    /// Writes the day (rows sorted by start), creating its folders, and adds any new block labels to the folder's
    /// <c>_block_index.json</c> (creating it for a new archive). Existing index entries are left as they are.
    /// </summary>
    public static async Task WriteAsync(string directory, DateOnly date, ArchiveDayDocument document, CancellationToken cancellationToken = default)
    {
        if (Problems(document.Rows) is { Count: > 0 } problems)
        {
            throw new InvalidOperationException(string.Join(" ", problems));
        }

        JsonObject root = document.Root?.DeepClone() as JsonObject ?? new JsonObject();
        var schedule = new JsonArray();
        foreach (ArchiveRowDraft row in document.Rows.OrderBy(row => ParseTime(row.Time, allowMidnight: false)))
        {
            JsonObject written = row.Original?.DeepClone() as JsonObject ?? new JsonObject();
            written["time"] = row.Time.Trim();
            written["end_time"] = string.IsNullOrWhiteSpace(row.EndTime) ? null : row.EndTime.Trim();
            written["show"] = row.Show.Trim();
            written["episode"] = string.IsNullOrWhiteSpace(row.Episode) ? null : row.Episode.Trim();
            var blocks = new JsonArray();
            foreach (string block in row.Blocks.Select(block => block.Trim()).Where(block => block.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                blocks.Add(block);
            }

            written["blocks"] = blocks;
            schedule.Add(written);
        }

        root["schedule"] = schedule;

        string path = PathFor(directory, date);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await WriteAtomicallyAsync(path, root.ToJsonString(WriteOptions), cancellationToken).ConfigureAwait(false);
        await AddBlockLabelsAsync(directory, document.Rows.SelectMany(row => row.Blocks), cancellationToken).ConfigureAwait(false);
    }

    public static void Delete(string directory, DateOnly date)
    {
        string path = PathFor(directory, date);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary><c>HH:mm</c>; <c>24:00</c> (midnight at the end of the day) only where <paramref name="allowMidnight"/>.</summary>
    public static TimeSpan? ParseTime(string? value, bool allowMidnight) =>
        allowMidnight && value?.Trim() == "24:00" ? TimeSpan.FromDays(1)
        : TimeSpan.TryParseExact(value?.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out TimeSpan parsed) ? parsed : null;

    private static async Task AddBlockLabelsAsync(string directory, IEnumerable<string> labels, CancellationToken cancellationToken)
    {
        string path = Path.Combine(directory, BlockIndexFile);
        var index = new JsonObject();
        if (File.Exists(path))
        {
            await using FileStream stream = File.OpenRead(path);
            index = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false) as JsonObject ?? new JsonObject();
        }

        bool changed = !File.Exists(path);
        foreach (IGrouping<string, string> label in labels.Select(label => label.Trim()).Where(label => label.Length > 0)
                     .GroupBy(label => label, StringComparer.OrdinalIgnoreCase))
        {
            if (!index.Any(pair => string.Equals(pair.Key, label.Key, StringComparison.OrdinalIgnoreCase)))
            {
                index[label.Key] = new JsonObject { ["count"] = label.Count() };
                changed = true;
            }
        }

        if (changed)
        {
            await WriteAtomicallyAsync(path, index.ToJsonString(WriteOptions), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        string temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, content, cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    private static string? Text(JsonObject row, string name) =>
        row[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
}
