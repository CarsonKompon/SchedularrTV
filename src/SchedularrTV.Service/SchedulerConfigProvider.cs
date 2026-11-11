using Microsoft.Extensions.Options;
using SchedularrTV.Core.Configuration;

namespace SchedularrTV.Service;

/// <summary>
/// Owns the in-memory schedule configuration and keeps it in sync with the YAML file on disk.
/// A single source of truth here means the API, the background builder and a future visual editor
/// all observe the same config instance.
/// </summary>
public sealed class SchedulerConfigProvider : IDisposable
{
    private readonly SchedulerServiceOptions _options;
    private readonly ILogger<SchedulerConfigProvider> _logger;
    private readonly Lock _gate = new();
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounce;

    private SchedulerConfig _config = new();
    private ConfigValidationResult _validation = new(true, [], []);

    // The file's text as last loaded or written here, so an edit made to it elsewhere can be backed up as it was before.
    private string? _lastRaw;

    public SchedulerConfigProvider(IOptions<SchedulerServiceOptions> options, ILogger<SchedulerConfigProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
        Backups = new ConfigBackups(ConfigPath, _options.ConfigBackups, logger);

        // A fresh install has no schedule yet; start it with an empty one rather than an error.
        try
        {
            Hosting.StarterConfig.WriteIfMissing(ConfigPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not create a starter schedule at {Path}.", ConfigPath);
        }

        Reload();
        StartWatching();
    }

    public event Action<SchedulerConfig>? ConfigChanged;

    /// <summary>Channels whose id was changed (old id to new), raised just before <see cref="ConfigChanged"/>.</summary>
    public event Action<IReadOnlyDictionary<string, string>>? ChannelIdsChanged;

    public string ConfigPath => Path.GetFullPath(_options.ConfigPath);

    public SchedulerConfig Current
    {
        get
        {
            lock (_gate)
            {
                return _config;
            }
        }
    }

    public ConfigValidationResult Validation
    {
        get
        {
            lock (_gate)
            {
                return _validation;
            }
        }
    }

    public string ReadRaw() => File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : string.Empty;

    /// <summary>Earlier versions of the file, one put aside before every change.</summary>
    public ConfigBackups Backups { get; }

    /// <summary>
    /// Validates and atomically replaces the YAML file, then reloads. What the file held is backed up first (as
    /// <paramref name="backupReason"/>), so the change can be undone from the Backups page.
    /// </summary>
    public ConfigValidationResult Save(string yaml, string backupReason = ConfigBackups.BeforeSave)
    {
        SchedulerConfig parsed = SchedulerConfigLoader.Parse(yaml);

        // Tag-based break settings (from an import, the API or pasted YAML) are moved into ad breaks as they're saved.
        bool rewritten = SchedularrTV.Core.Scheduling.AdBreakMigration.Migrate(parsed);

        // Every channel and block keeps a uid, and (unless a backup is being put back, which is whole in itself) what still
        // names a channel or block the way it was called before follows the rename.
        ConfigIdentity known = ConfigIdentity.Of(Current);
        rewritten |= ConfigIdentity.EnsureUids(parsed, known);
        if (backupReason != ConfigBackups.BeforeRestore)
        {
            rewritten |= ConfigIdentity.FollowRenames(parsed, known);
        }

        if (rewritten)
        {
            yaml = YamlCommentMerger.Merge(yaml, SchedulerConfigLoader.Serialize(parsed));
        }

        ConfigValidationResult validation = SchedulerConfigLoader.Validate(parsed);

        if (!validation.IsValid)
        {
            return validation;
        }

        string directory = Path.GetDirectoryName(ConfigPath) ?? ".";
        Directory.CreateDirectory(directory);

        string before = ReadRaw();
        if (!string.Equals(before, yaml, StringComparison.Ordinal))
        {
            Backups.Snapshot(before, backupReason);
        }

        string temp = ConfigPath + ".tmp";
        File.WriteAllText(temp, yaml);
        File.Move(temp, ConfigPath, overwrite: true);
        lock (_gate)
        {
            _lastRaw = yaml;
        }

        Apply(parsed, validation);
        return validation;
    }

    /// <summary>
    /// Puts a backup's YAML back as the schedule (backing up what's there now first, so the restore can be undone too).
    /// Null when there's no such backup.
    /// </summary>
    public ConfigValidationResult? Restore(string backupId) =>
        Backups.Read(backupId) is { } yaml ? Save(yaml, ConfigBackups.BeforeRestore) : null;

    public void Reload()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                _logger.LogWarning("Schedule config not found at {Path}; starting with an empty configuration.", ConfigPath);
                Apply(new SchedulerConfig(), new ConfigValidationResult(false, [$"Config file not found: {ConfigPath}"], []));
                return;
            }

            string raw = File.ReadAllText(ConfigPath);

            // Changed by something other than this app (a text editor, a sync tool): what it was before is kept.
            string? previous;
            lock (_gate)
            {
                previous = _lastRaw;
                _lastRaw = raw;
            }

            if (previous is not null && !string.Equals(previous, raw, StringComparison.Ordinal))
            {
                Backups.Snapshot(previous, ConfigBackups.BeforeOutsideEdit);
            }

            SchedulerConfig parsed = SchedulerConfigLoader.Parse(raw);

            // A schedule from before ad breaks has its break and promo settings moved into them, once.
            bool movedToAdBreaks = SchedularrTV.Core.Scheduling.AdBreakMigration.Migrate(parsed);

            // Channels and blocks get their uids (written into the file, so they last), and a channel id or block name
            // changed in the file by hand is followed by what refers to it, as in the editor.
            ConfigIdentity before = ConfigIdentity.Of(Current);
            bool identified = ConfigIdentity.EnsureUids(parsed, before);
            bool followed = ConfigIdentity.FollowRenames(parsed, before);
            ConfigValidationResult validation = SchedulerConfigLoader.Validate(parsed);

            if ((parsed.UsesOldLayout || movedToAdBreaks) && validation.IsValid)
            {
                MigrateLayout(raw, parsed, movedToAdBreaks);
            }
            else if ((identified || followed) && validation.IsValid)
            {
                WriteBack(raw, parsed);
            }

            foreach (string warning in validation.Warnings)
            {
                _logger.LogWarning("Config warning: {Warning}", warning);
            }

            foreach (string error in validation.Errors)
            {
                _logger.LogError("Config error: {Error}", error);
            }

            Apply(parsed, validation);
            _logger.LogInformation("Loaded {Channels} channel(s) from {Path}.", parsed.Channels.Count, ConfigPath);
        }
        catch (Exception ex) when (ex is SchedulerConfigException or YamlDotNet.Core.YamlException or IOException)
        {
            // Keep serving the previously valid config rather than dropping to nothing.
            _logger.LogError(ex, "Failed to reload schedule config; keeping the previous configuration.");
            lock (_gate)
            {
                _validation = new ConfigValidationResult(false, [ex.Message], _validation.Warnings);
            }
        }
    }

    /// <summary>
    /// Rewrites a schedule that still has promos, generated promos or Archive Follow at their old keys so they
    /// sit under <c>plugins:</c>, keeping the original beside it. Nothing about the schedule changes.
    /// </summary>
    private void MigrateLayout(string raw, SchedulerConfig parsed, bool movedToAdBreaks = false)
    {
        try
        {
            string backup = ConfigPath + (movedToAdBreaks ? ".before-ad-breaks.bak" : ".before-plugins.bak");
            if (!File.Exists(backup))
            {
                File.WriteAllText(backup, raw);
            }

            Backups.Snapshot(raw, ConfigBackups.BeforeMigration);
            string migrated = YamlCommentMerger.Merge(raw, SchedulerConfigLoader.Serialize(parsed));
            string temp = ConfigPath + ".tmp";
            File.WriteAllText(temp, migrated);
            File.Move(temp, ConfigPath, overwrite: true);
            lock (_gate)
            {
                _lastRaw = migrated;
            }

            _logger.LogInformation(
                movedToAdBreaks
                    ? "Moved the channels' break and promo settings into ad breaks in {Path}; the original is kept as {Backup}."
                    : "Moved promos, generated promos and Archive Follow settings under 'plugins:' in {Path}; the original is kept as {Backup}.",
                ConfigPath, backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The old layout still loads, so this is retried on the next start rather than treated as an error.
            _logger.LogWarning(ex, "Couldn't update {Path} to the new plugins layout; it still loads as it is.", ConfigPath);
        }
    }

    /// <summary>
    /// Writes channel and block uids (and references that followed a rename) into the file, keeping its comments; the file
    /// as it was is backed up first. A failure leaves the file as it is: it still loads, and this is tried again next time.
    /// </summary>
    private void WriteBack(string raw, SchedulerConfig parsed)
    {
        try
        {
            string updated = YamlCommentMerger.Merge(raw, SchedulerConfigLoader.Serialize(parsed));
            if (string.Equals(updated, raw, StringComparison.Ordinal))
            {
                return;
            }

            Backups.Snapshot(raw, ConfigBackups.BeforeSave);
            string temp = ConfigPath + ".tmp";
            File.WriteAllText(temp, updated);
            File.Move(temp, ConfigPath, overwrite: true);
            lock (_gate)
            {
                _lastRaw = updated;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't write channel and block ids into {Path}; it still loads as it is.", ConfigPath);
        }
    }

    private void Apply(SchedulerConfig config, ConfigValidationResult validation)
    {
        SchedulerConfig previous;
        lock (_gate)
        {
            previous = _config;
            _config = config;
            _validation = validation;
        }

        // Told first, so what's kept by channel id (where each rotation is up to) moves before the channel is rebuilt.
        if (ChannelIdsChanged is not null && ConfigIdentity.ChannelRenames(config, ConfigIdentity.Of(previous)) is { Count: > 0 } renamed)
        {
            try
            {
                ChannelIdsChanged.Invoke(renamed);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Couldn't carry everything kept for the renamed channel(s) over to their new ids.");
            }
        }

        ConfigChanged?.Invoke(config);
    }

    private void StartWatching()
    {
        if (!_options.WatchConfig)
        {
            return;
        }

        string directory = Path.GetDirectoryName(ConfigPath) ?? ".";
        if (!Directory.Exists(directory))
        {
            return;
        }

        _watcher = new FileSystemWatcher(directory, Path.GetFileName(ConfigPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };

        _watcher.Changed += (_, _) => ScheduleReload();
        _watcher.Created += (_, _) => ScheduleReload();
        _watcher.Renamed += (_, _) => ScheduleReload();
    }

    /// <summary>
    /// A single save raises several watcher events (editors write, truncate, then rename), so the
    /// reload is deferred and each new event resets the timer instead of queueing another reload.
    /// </summary>
    private void ScheduleReload()
    {
        lock (_gate)
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = new CancellationTokenSource();

            CancellationToken token = _debounce.Token;
            _ = Task.Delay(TimeSpan.FromMilliseconds(400), token)
                .ContinueWith(
                    _ => Reload(),
                    token,
                    TaskContinuationOptions.OnlyOnRanToCompletion,
                    TaskScheduler.Default);
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
