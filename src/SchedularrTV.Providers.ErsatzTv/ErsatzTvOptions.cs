namespace SchedularrTV.Providers.ErsatzTv;

/// <summary>
/// ErsatzTV Next is file-driven: it has no management API. The scheduler writes playout JSON into
/// each channel's playout folder and XMLTV programme fragments into the lineup's xmltv folder,
/// and Next serves <c>/channels.m3u</c>, <c>/channel/{n}.m3u8</c> and <c>/xmltv.xml</c> from those.
/// </summary>
public sealed class ErsatzTvOptions
{
    public const string SectionName = "ErsatzTv";

    /// <summary>
    /// Folder containing <c>lineup.json</c>. Scaffold it with
    /// <c>ersatztv add-lineup config/lineup.json --channels 1</c>.
    /// </summary>
    public string ConfigRoot { get; set; } = "etv/config";

    /// <summary>
    /// Playout folder for a channel, relative to <see cref="ConfigRoot"/>.
    /// <c>{channelNumber}</c> is substituted. Must match <c>playout.folder</c> in channel.json.
    /// </summary>
    public string PlayoutFolderTemplate { get; set; } = "channels/{channelNumber}/playout";

    /// <summary>
    /// Folder for per-channel XMLTV fragments, relative to <see cref="ConfigRoot"/>.
    /// Must match <c>xmltv.folder</c> in lineup.json. Files are named <c>{tvgId}.xml</c>.
    /// </summary>
    public string XmltvFolder { get; set; } = "xmltv";

    /// <summary>
    /// Rewrites media paths from how Jellyfin reports them to how ErsatzTV sees them.
    /// Required whenever the two run in different containers or on different hosts.
    /// </summary>
    public List<PathMapping> PathMappings { get; set; } = [];

    /// <summary>
    /// Writes <c>lineup.json</c> from the scheduler's channel definitions on every publish. Keeps
    /// <c>tvg_id</c> in sync with the XMLTV fragments, which is the usual cause of an empty guide.
    /// </summary>
    public bool WriteLineup { get; set; }

    public LineupServerOptions Lineup { get; set; } = new();

    /// <summary>
    /// Launch and supervise ErsatzTV Next as a child process: started after the first playout is
    /// written, restarted if it dies or the channel list changes, killed on shutdown.
    /// </summary>
    public bool ManageProcess { get; set; }

    /// <summary>Full path to <c>ersatztv.exe</c> (or <c>ersatztv</c>), e.g. the cargo release build.</summary>
    public string? ExecutablePath { get; set; }

    public TimeSpan RestartDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Emitted for gaps the scheduler could not fill, so the timeline has no holes.</summary>
    public string PaddingVideoLavfi { get; set; } = "color=c=black:s=1280x720:r=30";

    public string PaddingAudioLavfi { get; set; } = "anullsrc=channel_layout=stereo:sample_rate=48000";

    /// <summary>
    /// The program ErsatzTV runs to read a live stream (a plugin's live feed, such as a web page channel), given the
    /// stream's address as its last argument; it must write the MPEG-TS to its output. ErsatzTV treats what it
    /// reads this way as live: joined where it is rather than skipped through to the scheduled position, which
    /// an endless stream would never reach. curl comes with Windows 10 and later, macOS and most Linux systems.
    /// </summary>
    public string LiveStreamCommand { get; set; } = "curl";

    /// <summary>Arguments for <see cref="LiveStreamCommand"/> before the stream's address.</summary>
    public string[] LiveStreamArguments { get; set; } = ["--silent", "--show-error", "--fail", "--no-buffer"];

    /// <summary>
    /// <see cref="GuideMode.Programs"/> lists only real programmes and stretches each one over the
    /// following ad break, which is what a viewer expects to see in a guide.
    /// </summary>
    public GuideMode GuideMode { get; set; } = GuideMode.Programs;

    /// <summary>
    /// Where guide artwork is served from, normally the media server itself. Guide clients fetch
    /// these URLs directly, so it has to be reachable from them rather than only from this service.
    /// Defaults to the Jellyfin address; empty writes no artwork at all.
    /// </summary>
    public string? ArtworkBaseUrl { get; set; }

    /// <summary>
    /// Where guide clients fetch the pictures of local TV shows and movies from: this scheduler, which serves the
    /// pictures beside their files. Defaults to <c>http://localhost:</c> and the scheduler's port, which suits a guide read on
    /// this computer (Jellyfin's Live TV here, say); set it to the address other devices reach the scheduler at when
    /// their guide should show them.
    /// </summary>
    public string? LocalArtworkBaseUrl { get; set; }
}

public sealed class PathMapping
{
    public required string From { get; set; }

    public required string To { get; set; }
}

/// <summary>Values written into a generated lineup.json.</summary>
public sealed class LineupServerOptions
{
    public string BindAddress { get; set; } = "0.0.0.0";

    public int Port { get; set; } = 8409;

    /// <summary>Where Next writes HLS segments.</summary>
    public string OutputFolder { get; set; } = "hls";

    /// <summary>Per-channel normalization config, relative to the channel folder.</summary>
    public string ChannelConfigFileName { get; set; } = "channel.json";
}

public enum GuideMode
{
    /// <summary>Only programmes; interstitials are absorbed into the preceding programme.</summary>
    Programs,

    /// <summary>Every entry, including each commercial and bumper. Useful for debugging.</summary>
    All
}
