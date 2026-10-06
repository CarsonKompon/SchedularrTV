using SchedularrTV.Core.Media;

namespace SchedularrTV.Providers.FileSystem;

/// <summary>
/// Media that lives on disk rather than in a media server — commercials, bumpers, station IDs.
/// Tags come from the folder structure, so organising files <em>is</em> tagging them.
/// </summary>
public sealed class FileSystemLibraryOptions
{
    public const string SectionName = "FileSystemLibrary";

    public List<MediaFolderOptions> Folders { get; set; } = [];

    /// <summary>Path to ffprobe. Bare <c>ffprobe</c> resolves via PATH.</summary>
    public string FfprobePath { get; set; } = "ffprobe";

    public string[] Extensions { get; set; } = [".mp4", ".mkv", ".mpg", ".mpeg", ".avi", ".mov", ".m4v", ".webm", ".ts"];

    /// <summary>Durations are cached here so a rescan does not re-probe thousands of unchanged files.</summary>
    public string CachePath { get; set; } = "state/probe-cache.json";

    /// <summary>
    /// How long a scan is reused before the folders are walked again. Unset: 30 minutes, or 2 hours on a Raspberry Pi
    /// (each walk reads every file's size and date, and wakes a sleeping USB disk).
    /// </summary>
    public TimeSpan CacheDuration { get; set; } = SchedularrTV.Core.Platform.HardwareProfile.Pick(TimeSpan.FromMinutes(30), TimeSpan.FromHours(2));

    /// <summary>ffprobes run at once on new or changed files. Unset: 8, or 3 on a Raspberry Pi.</summary>
    public int ProbeConcurrency { get; set; } = SchedularrTV.Core.Platform.HardwareProfile.Pick(8, 3);

    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Files shorter than this are ignored; usually the result of a bad encode.</summary>
    public TimeSpan MinDuration { get; set; } = TimeSpan.FromSeconds(1);
}

public sealed class MediaFolderOptions
{
    /// <summary>Root folder, e.g. <c>D:\Media\Commercials</c>.</summary>
    public required string Path { get; set; }

    /// <summary>Tags applied to every file beneath the root, e.g. <c>["Commercials"]</c>.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// Turns each folder name between the root and the file into a tag, so
    /// <c>Adult Swim\Bumpers\Any Time\x.mp4</c> gains <c>Adult Swim</c>, <c>Bumpers</c> and <c>Any Time</c>.
    /// </summary>
    public bool TagFromFolders { get; set; } = true;

    /// <summary>Folder depth to stop tagging at. Null tags every level.</summary>
    public int? MaxTagDepth { get; set; }

    /// <summary>
    /// Folder that switches the levels beneath it to promo metadata:
    /// <c>&lt;promo folder&gt;/&lt;show&gt;/&lt;promo type&gt;</c>. The show name becomes <c>show_id</c> and
    /// the level below it <c>promo_type</c>, so "Up Next" reads as the folder name and matches
    /// <c>up_next</c>. Null disables it.
    /// </summary>
    public string? PromoFolder { get; set; } = "Promos";

    /// <summary>
    /// Escape hatch for metadata the folder layout cannot express positionally: a folder named
    /// <c>key=value</c> becomes an attribute rather than a tag. Jellyfin uses <c>key:value</c> for the
    /// same purpose; "=" is used here because ":" is illegal in a path.
    /// </summary>
    public string[] AttributeFolderPrefixes { get; set; } = ["promo_type", "show_id", "channel", "brand"];

    /// <summary>
    /// What the folder holds. <see cref="MediaKind.Filler"/> (the default): commercials, bumpers and promos, tagged by
    /// their folders. <see cref="MediaKind.Episode"/>: a TV library (show folders with season folders, as Sonarr lays
    /// them out). <see cref="MediaKind.Movie"/>: a movie library (a folder per movie, as Radarr lays them out). Both
    /// read .nfo files and the pictures beside the videos.
    /// </summary>
    public MediaKind Kind { get; set; } = MediaKind.Filler;

    /// <summary>A TV or movie library rather than commercials.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsLibrary => Kind is MediaKind.Episode or MediaKind.Movie;
}
