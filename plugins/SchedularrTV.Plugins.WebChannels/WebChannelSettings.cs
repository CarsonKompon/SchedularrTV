namespace SchedularrTV.WebChannels;

/// <summary>
/// The plugin's own settings (its gear on the Plugins page), and the addresses it works out from the app's. What every
/// live page plugin has comes from <see cref="LivePageSettings"/>: captured 15 times a second (8 on a Raspberry Pi),
/// kept on 90 seconds after the last viewer.
/// </summary>
public sealed class WebChannelSettings(IPluginContext context) : LivePageSettings(context, RoutePrefix, 15, 8, 90)
{
    public const string RoutePrefix = "/plugins/web-channels";

    /// <summary>The plugin this one replaces; its settings are used until this one's are set.</summary>
    public const string LegacyPluginId = "ws4kp";

    protected override string? Value(string key)
    {
        foreach (string? raw in new[] { Context.Configuration[key], Context.AppConfiguration[$"Plugins:{LegacyPluginId}:{key}"] })
        {
            if (raw?.Trim().Trim('"') is { Length: > 0 } value)
            {
                return value;
            }
        }

        return null;
    }

    public string WeatherStarSite => Value("WeatherStarUrl") ?? WebPresets.DefaultWeatherStarSite;

    /// <summary>Where pages are for the hidden browser, which runs on this machine, so the plugin's own pages are local.</summary>
    public PageAddresses ForBrowser => new(WeatherStarSite, Local);

    /// <summary>Where pages are for previews in the app: the plugin's own pages relative to it.</summary>
    public PageAddresses ForPreview => new(WeatherStarSite, null);

    public string PageUrl(WebChannel channel) => channel.PresetInfo.PageUrl(channel, ForBrowser);

    /// <summary>What a channel plays when it's on air.</summary>
    public LiveStreamSpec Spec(WebChannel channel) => BaseSpec(channel.Id, channel.Name, PageUrl(channel)) with
    {
        Audio = string.IsNullOrWhiteSpace(channel.MusicFolder) ? LiveAudio.Silence : LiveAudio.Music,
        MusicFolder = channel.MusicFolder,
        Volume = channel.MusicVolume
    };
}
