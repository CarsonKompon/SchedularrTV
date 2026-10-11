namespace SchedularrTV.Providers.Jellyfin;

/// <summary>Configuration problem the operator must fix; not worth a stack trace in the log.</summary>
public sealed class JellyfinConfigurationException(string message) : SchedularrTV.Core.Media.MediaSourceConfigurationException(message);

public sealed class JellyfinOptions
{
    public const string SectionName = "Jellyfin";

    /// <summary>Base URL of the Jellyfin server, e.g. <c>http://jellyfin.local:8096</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>API key created in Jellyfin's dashboard. Never log this value.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Optional user id used to scope library visibility.</summary>
    public string? UserId { get; set; }

    /// <summary>Explicit plugin selection; false preserves legacy schedule.yaml exclusions.</summary>
    public bool LibrarySelectionConfigured { get; set; }

    public List<string> ExcludedLibraries { get; set; } = [];

    public IReadOnlyList<string> LibraryExclusions(SchedularrTV.Core.Media.ILibraryScope? legacyScope) =>
        LibrarySelectionConfigured ? ExcludedLibraries : legacyScope?.ExcludedLibraries ?? [];

    public int PageSize { get; set; } = 500;

    /// <summary>
    /// How long a library snapshot is reused before a fresh crawl. Unset: 30 minutes, or 2 hours on a Raspberry Pi,
    /// where re-reading a large library at every hourly rebuild costs seconds of CPU and a few hundred MB.
    /// </summary>
    public TimeSpan CacheDuration { get; set; } = SchedularrTV.Core.Platform.HardwareProfile.Pick(TimeSpan.FromMinutes(30), TimeSpan.FromHours(2));

    public string[] IncludeItemTypes { get; set; } = ["Episode", "Movie", "Video", "MusicVideo"];

    /// <summary>
    /// Tag prefixes that are lifted into <c>MediaItem.Attributes</c>. A Jellyfin tag of
    /// <c>promo_type:later_today</c> becomes the attribute <c>promo_type = later_today</c>,
    /// which is how promo targeting metadata is expressed without custom Jellyfin fields.
    /// </summary>
    public string[] AttributeTagPrefixes { get; set; } = ["promo_type", "show_id", "channel", "brand", "trimmable", "stop_before_end"];

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Whether a server address and key are set.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);
}
