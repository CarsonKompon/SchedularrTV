using System.Text.Json.Nodes;
using System.Xml.Linq;
using SchedularrTV.Core.Media;
using SchedularrTV.Providers.FileSystem;
using SchedularrTV.Providers.Jellyfin;
using SchedularrTV.Service.Editor;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class MetadataEditingTests
{
    private static readonly EditableMetadata Edited = new()
    {
        Title = "Rugrats", Year = 1991, Overview = "Babies on adventures.", Rating = "TV-Y",
        Genres = ["Animation", "Comedy"], Studios = ["Nickelodeon"], Tags = []
    };

    // ---- NFO files ----

    [Fact]
    public void An_nfo_keeps_everything_it_wasnt_asked_to_change()
    {
        const string existing = """
            <?xml version="1.0" encoding="utf-8"?>
            <tvshow>
              <title>Rugrats (1991)</title>
              <uniqueid type="tvdb" default="true">76219</uniqueid>
              <genre>Kids</genre>
              <genre>Family</genre>
              <thumb aspect="poster">https://example.org/poster.jpg</thumb>
              <tag>old tag</tag>
            </tvshow>
            https://thetvdb.com/series/rugrats
            """;

        string updated = NfoWriter.Update(existing, "tvshow", Edited, MetadataFields.Title | MetadataFields.Genres | MetadataFields.Tags);
        XElement show = XElement.Parse(updated[(updated.IndexOf("<tvshow", StringComparison.Ordinal))..(updated.IndexOf("</tvshow>", StringComparison.Ordinal) + 9)]);

        Assert.Equal("Rugrats", show.Element("title")!.Value);
        Assert.Equal(new[] { "Animation", "Comedy" }, show.Elements("genre").Select(genre => genre.Value));
        // The new genres sit where the old ones were, after the id.
        Assert.Equal("uniqueid", show.Elements("genre").First().PreviousNode is XElement before ? before.Name.LocalName : null);
        Assert.Empty(show.Elements("tag"));
        // Not asked to change: kept, attributes and all.
        Assert.Equal("76219", show.Element("uniqueid")!.Value);
        Assert.Equal("poster", show.Element("thumb")!.Attribute("aspect")!.Value);
        Assert.Null(show.Element("plot"));
        // The scraper link after the XML survives.
        Assert.Contains("https://thetvdb.com/series/rugrats", updated);
    }

    [Fact]
    public void A_missing_nfo_is_written_new_and_read_back_the_same()
    {
        string written = NfoWriter.Update(null, "movie", Edited, MetadataFields.All);
        XElement movie = XElement.Parse(written[written.IndexOf("<movie", StringComparison.Ordinal)..]);

        Assert.Equal("Rugrats", movie.Element("title")!.Value);
        Assert.Equal("1991", movie.Element("year")!.Value);
        Assert.Equal("Babies on adventures.", movie.Element("plot")!.Value);
        Assert.Equal("TV-Y", movie.Element("mpaa")!.Value);
        Assert.Equal("Nickelodeon", movie.Element("studio")!.Value);
        Assert.StartsWith("<?xml", written, StringComparison.Ordinal);
    }

    // ---- Jellyfin ----

    [Fact]
    public void A_jellyfin_item_is_changed_only_where_edited_and_the_changes_are_locked()
    {
        var item = new JsonObject
        {
            ["Id"] = "abc",
            ["Name"] = "Rugrats (1991)",
            ["Overview"] = "Old overview",
            ["ProviderIds"] = new JsonObject { ["Tvdb"] = "76219" },
            ["Genres"] = new JsonArray("Kids"),
            ["Studios"] = new JsonArray(new JsonObject { ["Name"] = "Nick", ["Id"] = "1" }),
            ["LockedFields"] = new JsonArray("Cast")
        };

        JellyfinItemMetadata.Apply(item, Edited, MetadataFields.Title | MetadataFields.Genres | MetadataFields.Studios | MetadataFields.Year);

        Assert.Equal("Rugrats", item["Name"]!.GetValue<string>());
        Assert.Equal(1991, item["ProductionYear"]!.GetValue<int>());
        Assert.Equal(new[] { "Animation", "Comedy" }, item["Genres"]!.AsArray().Select(genre => genre!.GetValue<string>()));
        Assert.Equal("Nickelodeon", item["Studios"]![0]!["Name"]!.GetValue<string>());
        // Untouched, including what this app knows nothing about (the update replaces the whole item).
        Assert.Equal("Old overview", item["Overview"]!.GetValue<string>());
        Assert.Equal("76219", item["ProviderIds"]!["Tvdb"]!.GetValue<string>());
        Assert.Equal("abc", item["Id"]!.GetValue<string>());
        // Locked so a metadata refresh doesn't undo it; the year can't be locked, and what was locked stays locked.
        Assert.Equal(new[] { "Cast", "Name", "Genres", "Studios" }, item["LockedFields"]!.AsArray().Select(field => field!.GetValue<string>()));

        EditableMetadata read = JellyfinItemMetadata.Read(item);
        Assert.Equal(("Rugrats", 1991, "Old overview"), (read.Title, read.Year, read.Overview));
        Assert.Equal(new[] { "Nickelodeon" }, read.Studios);
    }

    // ---- History and reverting ----

    private sealed class FakeEditor : IMetadataEditor
    {
        public EditableMetadata Stored { get; set; } = new() { Title = "Rugrats (1991)", Year = 1991, Genres = ["Kids"] };
        public int Writes { get; private set; }

        public string Name => "Test source";
        public string WriteLabel => "Write it";
        public MetadataFields Supported => MetadataFields.All;
        public bool Owns(string id) => id.StartsWith("test:", StringComparison.Ordinal);
        public ValueTask<EditableMetadata?> ReadAsync(string id, MetadataItemKind kind, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<EditableMetadata?>(Stored);
        public ValueTask<string?> DescribeTargetAsync(string id, MetadataItemKind kind, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>("test");

        public ValueTask WriteAsync(string id, MetadataItemKind kind, EditableMetadata metadata, MetadataFields fields, CancellationToken cancellationToken = default)
        {
            Stored = LibraryMetadata.Merge(Stored, metadata, fields).Normalized();
            Writes++;
            return ValueTask.CompletedTask;
        }
    }

    private static string TempFolder() => Directory.CreateTempSubdirectory("schedularrtv-metadata-").FullName;

    [Fact]
    public async Task A_change_is_written_recorded_and_can_be_reverted()
    {
        string folder = TempFolder();
        try
        {
            var editor = new FakeEditor();
            var metadata = new LibraryMetadata([editor], new MetadataHistory(folder));
            Assert.Same(editor, metadata.EditorFor("test:1"));
            Assert.Null(metadata.EditorFor("other:1"));

            EditableMetadata before = editor.Stored;
            MetadataHistoryEntry? entry = await metadata.WriteAsync(editor, "test:1", MetadataItemKind.Show, before,
                before with { Title = "Rugrats", Genres = ["Animation"] });

            Assert.NotNull(entry);
            Assert.Equal(MetadataFields.Title | MetadataFields.Genres, entry.Fields);
            Assert.Equal("Rugrats", editor.Stored.Title);

            // Nothing changed: nothing written or recorded.
            Assert.Null(await metadata.WriteAsync(editor, "test:1", MetadataItemKind.Show, editor.Stored, editor.Stored));

            // Something else changes the year meanwhile; reverting the edit puts back only its own fields.
            editor.Stored = editor.Stored with { Year = 1990 };
            MetadataHistoryEntry? reverted = await metadata.RevertAsync(editor, "test:1", MetadataItemKind.Show, entry);
            Assert.NotNull(reverted);
            Assert.Equal(("Rugrats (1991)", 1990), (editor.Stored.Title, editor.Stored.Year));
            Assert.Equal(new[] { "Kids" }, editor.Stored.Genres);
            Assert.StartsWith("Reverted", reverted.Note, StringComparison.Ordinal);

            // Kept on disk, newest first.
            IReadOnlyList<MetadataHistoryEntry> history = new MetadataHistory(folder).Read("test:1");
            Assert.Equal(new[] { reverted.Id, entry.Id }, history.Select(item => item.Id));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void History_keeps_only_the_newest_changes()
    {
        string folder = TempFolder();
        try
        {
            var history = new MetadataHistory(folder);
            DateTimeOffset start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
            for (int change = 0; change < MetadataHistory.Limit + 5; change++)
            {
                history.Record("test:1", "Test source", new EditableMetadata { Year = change }, new EditableMetadata { Year = change + 1 },
                    MetadataFields.Year, at: start.AddMinutes(change));
            }

            IReadOnlyList<MetadataHistoryEntry> kept = history.Read("test:1");
            Assert.Equal(MetadataHistory.Limit, kept.Count);
            Assert.Equal(start.AddMinutes(MetadataHistory.Limit + 4), kept[0].At);
            Assert.Empty(history.Read("test:2"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Differences_ignore_spacing_case_in_lists_and_blank_values()
    {
        var left = new EditableMetadata { Title = " Rugrats ", Overview = "", Genres = ["Comedy", "animation"] };
        var right = new EditableMetadata { Title = "Rugrats", Overview = null, Genres = ["comedy", "Animation", " "] };
        Assert.Equal(MetadataFields.None, left.Differences(right));
        Assert.Equal(MetadataFields.Genres, left.Differences(right with { Genres = ["Animation", "Comedy"] }));
    }
}
