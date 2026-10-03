namespace SchedularrTV.GeneratedPromos;

public sealed class GeneratedPromoOptions
{
    public const string SectionName = "GeneratedPromos";

    /// <summary>Rendered promos and cached voice-overs. Must be readable by the playout engine.</summary>
    public string OutputPath { get; set; } = "state/generated-promos";

    public string FfmpegPath { get; set; } = "ffmpeg";

    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>Promos rendered at once; each render also keeps ffmpeg busy.</summary>
    public int Concurrency { get; set; } = 2;

    /// <summary>A failed render is not retried until this has passed.</summary>
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Unreferenced renders older than this are deleted when the schedule settings don't say; also clears abandoned writes.</summary>
    public TimeSpan KeepFor { get; set; } = TimeSpan.FromDays(7);
}
