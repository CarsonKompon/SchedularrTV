using System.Globalization;
using System.Text.RegularExpressions;

namespace SchedularrTV.Service;

/// <summary>One earlier version of <c>schedule.yaml</c>.</summary>
/// <param name="Id">The file's name without <c>.yaml</c>; what the API and the Backups page refer to it by.</param>
/// <param name="At">When it was put aside (just before the change that replaced it).</param>
/// <param name="Reason">Why: one of the <see cref="ConfigBackups"/> reason constants.</param>
public sealed record ConfigBackup(string Id, DateTimeOffset At, long Bytes, string Reason)
{
    public string Describe() => Reason switch
    {
        ConfigBackups.BeforeSave => "Before a save",
        ConfigBackups.BeforeOutsideEdit => "Before the file was changed outside SchedularrTV",
        ConfigBackups.BeforeRestore => "Before restoring an earlier backup",
        ConfigBackups.BeforeMigration => "Before the plugins layout update",
        ConfigBackups.Manual => "Backed up by hand",
        _ => Reason
    };
}

/// <summary>
/// Earlier versions of the schedule, kept automatically: every time <c>schedule.yaml</c> is about to be replaced (by the
/// editor, the API, a restore, or an edit made to the file outside the app), what it held is copied into a
/// <c>backups</c> folder beside it first. The newest <see cref="Keep"/> are kept.
/// </summary>
/// <remarks>
/// The folder sits beside the file but the config watcher only watches the file itself, so writing here never
/// triggers a reload. Backing up never stops a save: a failure is logged and the save goes ahead.
/// </remarks>
public sealed partial class ConfigBackups(string configPath, int keep, ILogger? logger = null)
{
    public const string FolderName = "backups";

    public const string BeforeSave = "save";
    public const string BeforeOutsideEdit = "outside";
    public const string BeforeRestore = "restore";
    public const string BeforeMigration = "migrate";
    public const string Manual = "manual";

    private const string Prefix = "schedule-";
    private const string StampFormat = "yyyyMMdd-HHmmss-fff";

    private readonly Lock _gate = new();

    /// <summary>How many are kept; 0 turns backups off.</summary>
    public int Keep => Math.Max(keep, 0);

    public bool Enabled => Keep > 0;

    public string Folder => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".", FolderName);

    /// <summary>Every backup, newest first.</summary>
    public IReadOnlyList<ConfigBackup> List()
    {
        if (!Directory.Exists(Folder))
        {
            return [];
        }

        List<ConfigBackup> backups = [];
        foreach (string path in Directory.EnumerateFiles(Folder, Prefix + "*.yaml"))
        {
            if (Parse(Path.GetFileNameWithoutExtension(path)) is { } backup)
            {
                long bytes;
                try
                {
                    bytes = new FileInfo(path).Length;
                }
                catch (IOException)
                {
                    continue;
                }

                backups.Add(backup with { Bytes = bytes });
            }
        }

        return [.. backups.OrderByDescending(backup => backup.At).ThenByDescending(backup => backup.Id, StringComparer.Ordinal)];
    }

    public ConfigBackup? Find(string id) => Parse(id) is not null ? List().FirstOrDefault(backup => backup.Id == id) : null;

    /// <summary>A backup's YAML, or null when there's no such backup.</summary>
    public string? Read(string id)
    {
        if (Parse(id) is null)
        {
            return null;
        }

        string path = Path.Combine(Folder, id + ".yaml");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>
    /// Puts <paramref name="yaml"/> aside as a backup, unless backups are off, it's empty, or it's the same as the newest
    /// one already kept. Returns the new backup, or null when none was needed or it couldn't be written.
    /// </summary>
    public ConfigBackup? Snapshot(string yaml, string reason, DateTimeOffset? at = null)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(yaml))
        {
            return null;
        }

        lock (_gate)
        {
            try
            {
                ConfigBackup? newest = List().FirstOrDefault();
                if (newest is not null && Read(newest.Id) == yaml)
                {
                    return null;
                }

                Directory.CreateDirectory(Folder);
                DateTimeOffset stamp = (at ?? DateTimeOffset.UtcNow).ToUniversalTime();

                // Always after the newest (two in the same millisecond, or a clock set back), so the order is kept.
                if (newest is not null && stamp <= newest.At)
                {
                    stamp = newest.At.AddMilliseconds(1);
                }

                string id = $"{Prefix}{stamp.ToString(StampFormat, CultureInfo.InvariantCulture)}.{Tidy(reason)}";

                string path = Path.Combine(Folder, id + ".yaml");
                string temp = path + ".tmp";
                File.WriteAllText(temp, yaml);
                File.Move(temp, path, overwrite: true);
                Prune();
                return new ConfigBackup(id, stamp, new FileInfo(path).Length, Tidy(reason));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(ex, "Couldn't back up the schedule into {Folder}; the change goes ahead without a backup.", Folder);
                return null;
            }
        }
    }

    public bool Delete(string id)
    {
        if (Parse(id) is null)
        {
            return false;
        }

        string path = Path.Combine(Folder, id + ".yaml");
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    private void Prune()
    {
        foreach (ConfigBackup old in List().Skip(Keep))
        {
            try
            {
                File.Delete(Path.Combine(Folder, old.Id + ".yaml"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogDebug(ex, "Couldn't remove the old schedule backup {Id}.", old.Id);
            }
        }
    }

    private static string Tidy(string reason) =>
        reason is { Length: > 0 } && ReasonPattern().IsMatch(reason) ? reason : BeforeSave;

    /// <summary>A backup's id read back into its time and reason; null for anything that isn't one (so no path escapes the folder).</summary>
    private static ConfigBackup? Parse(string id)
    {
        Match match = IdPattern().Match(id);
        if (!match.Success
            || !DateTimeOffset.TryParseExact(match.Groups["stamp"].Value, StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset at))
        {
            return null;
        }

        return new ConfigBackup(id, at, 0, match.Groups["reason"].Value);
    }

    [GeneratedRegex(@"^schedule-(?<stamp>\d{8}-\d{6}-\d{3})\.(?<reason>[a-z]{1,20})$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[a-z]{1,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReasonPattern();
}
