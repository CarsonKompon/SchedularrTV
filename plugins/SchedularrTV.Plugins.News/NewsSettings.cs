namespace SchedularrTV.News;

/// <summary>
/// The plugin's own settings (its gear on the Plugins page), and the addresses it works out from the app's. What every
/// live page plugin has comes from <see cref="LivePageSettings"/>: captured 15 times a second (8 on a Raspberry Pi),
/// kept on 90 seconds after the last viewer.
/// </summary>
public sealed class NewsSettings(IPluginContext context) : LivePageSettings(context, RoutePrefix, 15, 8, 90)
{
    public const string RoutePrefix = "/plugins/news";

    /// <summary>How often stories and data are fetched again while a show is in use (5 to 240 minutes).</summary>
    public TimeSpan RefreshEvery => TimeSpan.FromMinutes(Number("RefreshMinutes", 15, 5, 240));

    /// <summary>A TomTom key, for traffic on the map; null for a map without traffic.</summary>
    public string? TomTomKey => Value("TomTomKey");

    /// <summary>The show's sound (its voice and music), made by the plugin; read by the show's own ffmpeg.</summary>
    public string SoundUrl(string showId) => $"{Local}{RoutePrefix}/sound/{Uri.EscapeDataString(showId)}.ts";

    /// <summary>
    /// What a show plays when it's on air: its page, with its sound. Both follow the show's settings by themselves (the
    /// page polls for them, the sound re-reads them), so changing a show doesn't start its stream again.
    /// </summary>
    public LiveStreamSpec Spec(NewsShow show) => BaseSpec(show.Id, show.Name, PageUrl(show.Id)) with
    {
        Audio = LiveAudio.Stream,
        AudioStreamUrl = SoundUrl(show.Id),
        Volume = 1.0
    };
}
