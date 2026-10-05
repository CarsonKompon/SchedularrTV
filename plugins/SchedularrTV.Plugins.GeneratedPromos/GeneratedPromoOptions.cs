namespace SchedularrTV.GeneratedPromos;

public sealed class GeneratedPromoOptions
{
    public const string SectionName = "GeneratedPromos";

    /// <summary>Rendered promos and cached voice-overs. Must be readable by the playout engine.</summary>
    public string OutputPath { get; set; } = "state/generated-promos";

    public string FfmpegPath { get; set; } = "ffmpeg";

    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>Promos rendered at once; each render also keeps ffmpeg busy. Unset: 2, or 1 on a Raspberry Pi.</summary>
    public int Concurrency { get; set; } = HardwareProfile.Pick(2, 1);

    /// <summary>A failed render is not retried until this has passed.</summary>
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Unreferenced renders older than this are deleted when the schedule settings don't say; also clears abandoned writes.</summary>
    public TimeSpan KeepFor { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// The folder chosen in the plugin's settings on the Plugins page (<c>Plugins:generated-promos:SaveFolder</c>); it
    /// wins over the older schedule-file setting. Null when none was chosen.
    /// </summary>
    public string? SaveFolder { get; set; }

    // The rest of the plugin's own settings (Plugins page). Null when not set there: the older schedule-file setting
    // (settings.plugins.generated-promos) applies, else its default.

    /// <summary>Promos are only rendered once they air within this; zero renders everything ahead.</summary>
    public TimeSpan? LeadTime { get; set; }

    /// <summary>Renders only for channels someone is watching.</summary>
    public bool? WatchedOnly { get; set; }

    /// <summary>Unused videos are deleted after this; zero keeps them.</summary>
    public TimeSpan? KeepVideos { get; set; }

    /// <summary>Voice-overs not reused for this long are deleted; zero keeps them.</summary>
    public TimeSpan? KeepAudio { get; set; }
}
