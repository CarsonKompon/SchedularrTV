using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Service;
using SchedularrTV.Service.Editor;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class ConfigBackupsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"config-backups-{Guid.NewGuid():N}");

    public void Dispose() => TestFolders.Delete(_directory);

    private SchedulerConfigProvider Provider(int keep = 100) =>
        new(Options.Create(new SchedulerServiceOptions
        {
            ConfigPath = Path.Combine(_directory, "schedule.yaml"), WatchConfig = false, AutoRebuild = false, ConfigBackups = keep
        }), NullLogger<SchedulerConfigProvider>.Instance);

    private static string Yaml(string seed) => SchedulerConfigLoader.Serialize(new SchedulerConfig { Settings = { Seed = seed } });

    [Fact]
    public void Every_save_keeps_what_the_file_held_before_and_a_backup_can_be_restored()
    {
        SchedulerConfigProvider provider = Provider();
        Assert.True(provider.Save(Yaml("first")).IsValid);
        Assert.True(provider.Save(Yaml("second")).IsValid);
        Assert.True(provider.Save(Yaml("second")).IsValid); // Nothing changed: no backup.

        IReadOnlyList<ConfigBackup> backups = provider.Backups.List();
        Assert.Equal(Yaml("first"), provider.Backups.Read(backups[0].Id));
        Assert.All(backups, backup => Assert.Equal(ConfigBackups.BeforeSave, backup.Reason));
        Assert.Equal(Path.Combine(_directory, ConfigBackups.FolderName), provider.Backups.Folder);

        ConfigBackup first = backups[0];
        Assert.True(provider.Restore(first.Id)!.IsValid);
        Assert.Equal("first", provider.Current.Settings.Seed);
        Assert.Equal(Yaml("first"), provider.ReadRaw());

        // The restore is undoable: what it replaced is the newest backup.
        ConfigBackup newest = provider.Backups.List()[0];
        Assert.Equal(ConfigBackups.BeforeRestore, newest.Reason);
        Assert.Equal(Yaml("second"), provider.Backups.Read(newest.Id));

        Assert.Null(provider.Restore("schedule-20260101-000000-000.save"));
        Assert.Null(provider.Backups.Read("../schedule"));
    }

    [Fact]
    public void An_edit_made_outside_the_app_is_backed_up_when_it_is_read()
    {
        SchedulerConfigProvider provider = Provider();
        Assert.True(provider.Save(Yaml("mine")).IsValid);

        File.WriteAllText(provider.ConfigPath, Yaml("by hand"));
        provider.Reload();

        Assert.Equal("by hand", provider.Current.Settings.Seed);
        ConfigBackup newest = provider.Backups.List()[0];
        Assert.Equal(ConfigBackups.BeforeOutsideEdit, newest.Reason);
        Assert.Equal(Yaml("mine"), provider.Backups.Read(newest.Id));
    }

    [Fact]
    public void Only_the_newest_are_kept_and_none_when_turned_off()
    {
        var backups = new ConfigBackups(Path.Combine(_directory, "schedule.yaml"), keep: 3);
        DateTimeOffset at = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < 5; i++)
        {
            Assert.NotNull(backups.Snapshot($"seed: v{i}\n", ConfigBackups.Manual, at.AddMinutes(i)));
        }

        IReadOnlyList<ConfigBackup> kept = backups.List();
        Assert.Equal(3, kept.Count);
        Assert.Equal(new[] { "seed: v4\n", "seed: v3\n", "seed: v2\n" }, kept.Select(backup => backups.Read(backup.Id)));
        Assert.Equal(at.AddMinutes(4), kept[0].At);

        // The same again as the newest isn't kept twice.
        Assert.Null(backups.Snapshot("seed: v4\n", ConfigBackups.Manual, at.AddMinutes(9)));

        var off = new ConfigBackups(Path.Combine(_directory, "other", "schedule.yaml"), keep: 0);
        Assert.Null(off.Snapshot("seed: x\n", ConfigBackups.Manual));
        Assert.Empty(off.List());
    }

    [Fact]
    public void A_line_diff_marks_what_was_added_and_removed()
    {
        IReadOnlyList<DiffLine> diff = LineDiff.Compare("a\nb\nc\nd\n", "a\nc\nx\nd\n")!;

        Assert.Equal(new[] { "a", "-b", "c", "+x", "d" },
            diff.Select(line => line.Kind switch { DiffKind.Added => "+" + line.Text, DiffKind.Removed => "-" + line.Text, _ => line.Text }));
        Assert.Equal((2, (int?)null), (diff[1].OldNumber!.Value, diff[1].NewNumber));

        IReadOnlyList<DiffLine> longer = LineDiff.Compare(
            string.Join('\n', Enumerable.Range(0, 20)), string.Join('\n', Enumerable.Range(0, 20).Select(i => i == 10 ? "ten" : i.ToString())))!;
        DiffHunk hunk = Assert.Single(LineDiff.Hunks(longer, context: 2));
        Assert.Equal(8, hunk.SkippedBefore);
        Assert.Equal(6, hunk.Lines.Count);
    }
}
