using System.Text.Json.Nodes;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>The archive editor's file access: what it writes is what Archive Follow reads, and nothing else is lost.</summary>
public sealed class ArchiveDayFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"archive-edit-{Guid.NewGuid():N}");

    public ArchiveDayFileTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_day_built_by_hand_is_read_back_by_the_scheduler_and_its_labels_are_indexed()
    {
        var date = new DateOnly(2003, 7, 4);
        var document = new ArchiveDayDocument
        {
            Rows =
            [
                new ArchiveRowDraft { Time = "07:00", Show = "Doug", Episode = "Doug's Dog", Blocks = ["Morning"] },
                new ArchiveRowDraft { Time = "06:30", EndTime = "07:00", Show = "Rugrats", Blocks = ["Morning", "Nick Jr"] },
                new ArchiveRowDraft { Time = "23:30", EndTime = "24:00", Show = "Late Show" }
            ]
        };

        await ArchiveDayFile.WriteAsync(_folder, date, document);

        Assert.True(File.Exists(Path.Combine(_folder, "2003", "07", "2003-07-04.json")));
        Assert.Equal([date], ArchiveDayFile.ListDates(_folder));

        ArchiveDay day = await ArchiveReader.ReadDayAsync(ArchiveDayFile.PathFor(_folder, date), date);
        Assert.Equal(["Rugrats", "Doug", "Late Show"], day.Slots.Select(slot => slot.Show));
        Assert.Equal(TimeSpan.FromHours(6.5), day.Slots[0].Start);
        Assert.Equal(TimeSpan.FromHours(23.5), day.Slots[1].End);
        Assert.Equal(TimeSpan.FromDays(1), day.Slots[2].End);
        Assert.Equal("Doug's Dog", day.Slots[1].Episode);

        JsonObject index = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_folder, ArchiveDayFile.BlockIndexFile)))!;
        Assert.Equal(2, (int)index["Morning"]!["count"]!);
        Assert.Equal(1, (int)index["Nick Jr"]!["count"]!);
    }

    [Fact]
    public async Task Editing_an_archived_day_keeps_its_extra_fields_and_existing_index_counts()
    {
        var date = new DateOnly(1999, 12, 31);
        string path = ArchiveDayFile.PathFor(_folder, date);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """
            {"source":"TV Guide scan","schedule":[
              {"time":"20:00","end_time":null,"show":"Old Show","episode":"Finale","blocks":["Prime"],"network":"Fox"}
            ]}
            """);
        await File.WriteAllTextAsync(Path.Combine(_folder, ArchiveDayFile.BlockIndexFile), """{"Prime":{"count":412}}""");

        ArchiveDayDocument document = (await ArchiveDayFile.ReadAsync(_folder, date))!;
        ArchiveRowDraft row = Assert.Single(document.Rows);
        Assert.Equal(("20:00", "Old Show", "Finale"), (row.Time, row.Show, row.Episode));
        row.Show = "Corrected Show";
        document.Rows.Add(new ArchiveRowDraft { Time = "21:00", Show = "New Show", Blocks = ["Prime", "Late"] });

        await ArchiveDayFile.WriteAsync(_folder, date, document);

        JsonObject written = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.Equal("TV Guide scan", (string?)written["source"]);
        JsonObject first = (JsonObject)written["schedule"]![0]!;
        Assert.Equal("Corrected Show", (string?)first["show"]);
        Assert.Equal("Fox", (string?)first["network"]);

        JsonObject index = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_folder, ArchiveDayFile.BlockIndexFile)))!;
        Assert.Equal(412, (int)index["Prime"]!["count"]!);
        Assert.Equal(1, (int)index["Late"]!["count"]!);
    }

    [Fact]
    public async Task Bad_rows_are_refused_without_writing_anything()
    {
        ArchiveRowDraft[] rows =
        [
            new() { Time = "6pm", Show = "Not a time" },
            new() { Time = "08:00", EndTime = "07:00", Show = "Backwards" },
            new() { Time = "09:00", Show = "" },
            new() { Time = "10:00", Show = "First" },
            new() { Time = "10:00", Show = "Same start" }
        ];

        IReadOnlyList<string> problems = ArchiveDayFile.Problems(rows);
        Assert.Contains(problems, problem => problem.StartsWith("Row 1") && problem.Contains("06:30"));
        Assert.Contains(problems, problem => problem.StartsWith("Row 2") && problem.Contains("before it starts"));
        Assert.Contains(problems, problem => problem.StartsWith("Row 3") && problem.Contains("name the show"));
        Assert.Contains(problems, problem => problem.StartsWith("Row 5") && problem.Contains("also starts at 10:00"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ArchiveDayFile.WriteAsync(_folder, new DateOnly(2001, 1, 1), new ArchiveDayDocument { Rows = [.. rows] }));
        Assert.Empty(ArchiveDayFile.ListDates(_folder));
        Assert.False(File.Exists(Path.Combine(_folder, ArchiveDayFile.BlockIndexFile)));
    }

    [Fact]
    public async Task Deleting_a_day_removes_only_its_file()
    {
        var keep = new DateOnly(2002, 9, 1);
        var remove = new DateOnly(2002, 9, 2);
        foreach (DateOnly date in new[] { keep, remove })
        {
            await ArchiveDayFile.WriteAsync(_folder, date, new ArchiveDayDocument { Rows = [new ArchiveRowDraft { Time = "06:00", Show = "Show" }] });
        }

        ArchiveDayFile.Delete(_folder, remove);

        Assert.Equal([keep], ArchiveDayFile.ListDates(_folder));
        Assert.Null(await ArchiveDayFile.ReadAsync(_folder, remove));
    }
}
