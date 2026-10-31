using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SchedularrTV.Core.Media;

namespace SchedularrTV.Service.Editor;

/// <summary>One metadata change written to a source, with the values from before it so it can be undone.</summary>
/// <param name="Source">The source it was written to ("Jellyfin", "Local folders").</param>
/// <param name="Fields">The fields that changed.</param>
/// <param name="Note">What it was, when not an ordinary edit: "Reverted the change from 31 Oct, 9:41 pm".</param>
public sealed record MetadataHistoryEntry(
    string Id,
    DateTimeOffset At,
    string Source,
    MetadataFields Fields,
    EditableMetadata Before,
    EditableMetadata After,
    string? Note = null);

/// <summary>
/// The last few metadata changes made to each show and movie, kept in the data folder (<c>metadata-history/</c>, a file
/// per item) so they survive restarts and a wrong change can be reverted.
/// </summary>
public sealed class MetadataHistory(string dataDirectory)
{
    /// <summary>How many changes are kept per item; older ones are dropped.</summary>
    public const int Limit = 20;

    public const string FolderName = "metadata-history";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Lock _lock = new();

    public string Folder { get; } = Path.Combine(Path.GetFullPath(dataDirectory), FolderName);

    private sealed record Document(string ItemId, List<MetadataHistoryEntry> Entries);

    /// <summary>An item's changes, newest first; empty when it has none.</summary>
    public IReadOnlyList<MetadataHistoryEntry> Read(string itemId)
    {
        lock (_lock)
        {
            return Newest(Load(itemId).Entries);
        }
    }

    /// <summary>Adds a change written to the source, keeping the newest <see cref="Limit"/>.</summary>
    public MetadataHistoryEntry Record(string itemId, string source, EditableMetadata before, EditableMetadata after, MetadataFields fields,
        string? note = null, DateTimeOffset? at = null)
    {
        var entry = new MetadataHistoryEntry(Guid.NewGuid().ToString("N"), at ?? DateTimeOffset.Now, source, fields, before, after, note);
        lock (_lock)
        {
            Document document = Load(itemId);
            document.Entries.Add(entry);
            // Kept oldest first on disk, the order they were recorded in.
            Save(document with { Entries = [.. Newest(document.Entries).Take(Limit).Reverse()] });
        }

        return entry;
    }

    // Newest first; of two at the same moment, the one recorded later first (the sort keeps the reversed order for ties).
    private static List<MetadataHistoryEntry> Newest(IEnumerable<MetadataHistoryEntry> entries) =>
        [.. entries.Reverse().OrderByDescending(entry => entry.At)];

    private string FileFor(string itemId) =>
        Path.Combine(Folder, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(itemId)))[..24] + ".json");

    private Document Load(string itemId)
    {
        string path = FileFor(itemId);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Document>(File.ReadAllText(path), Json) is { } document
                && document.ItemId == itemId)
            {
                return document with { Entries = document.Entries ?? [] };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable history starts again rather than stopping edits.
        }

        return new Document(itemId, []);
    }

    private void Save(Document document)
    {
        Directory.CreateDirectory(Folder);
        string path = FileFor(document.ItemId);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(document, Json));
        File.Move(temp, path, overwrite: true);
    }
}
