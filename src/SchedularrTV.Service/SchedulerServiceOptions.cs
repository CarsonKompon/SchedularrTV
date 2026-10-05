namespace SchedularrTV.Service;

public sealed class SchedulerServiceOptions
{
    public const string SectionName = "Scheduler";

    /// <summary>Path to the YAML schedule definition.</summary>
    public string ConfigPath { get; set; } = "config/schedule.yaml";

    /// <summary>Path to the persisted playback state (rotation cursors and shuffle bags).</summary>
    public string StatePath { get; set; } = "state/playback-state.json";

    /// <summary>
    /// Folder holding uploaded channel logos, served over HTTP for the guide. Relative paths are
    /// resolved next to <see cref="ConfigPath"/> so logos travel with the schedule that names them.
    /// </summary>
    public string LogoPath { get; set; } = "logos";

    /// <summary>Rebuild automatically on startup and on the configured interval.</summary>
    public bool AutoRebuild { get; set; } = true;

    /// <summary>Reload the YAML file when it changes on disk.</summary>
    public bool WatchConfig { get; set; } = true;

    /// <summary>
    /// How many earlier versions of the YAML file to keep in the <c>backups</c> folder beside it, one put aside before
    /// every change (see the Backups page). 0 turns them off.
    /// </summary>
    public int ConfigBackups { get; set; } = 100;

    /// <summary>
    /// Optional shared secret required by mutating API endpoints. When null, mutating endpoints are
    /// disabled so an unauthenticated deployment cannot have its schedule rewritten.
    /// </summary>
    public string? ApiKey { get; set; }
}
