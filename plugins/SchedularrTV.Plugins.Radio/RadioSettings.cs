using SchedularrTV.Core.Platform;

namespace SchedularrTV.Radio;

/// <summary>
/// The plugin's own settings (its gear on the Plugins page), and the addresses it works out from the app's. What every
/// live page plugin has comes from <see cref="LivePageSettings"/>: captured 20 times a second (10 on a Raspberry Pi),
/// kept on 90 seconds after the last viewer.
/// </summary>
public sealed class RadioSettings(IPluginContext context) : LivePageSettings(context, RoutePrefix, 20, 10, 90)
{
    public const string RoutePrefix = "/plugins/radio";

    /// <summary>The station's sound, sequenced by the plugin; read by the station's own ffmpeg.</summary>
    public string SoundUrl(string stationId) => $"{Local}{RoutePrefix}/sound/{Uri.EscapeDataString(stationId)}.ts";

    /// <summary>What a station plays when it's on air: its page, its sound, and the visualizer drawn from that sound.</summary>
    public LiveStreamSpec Spec(RadioStation station)
    {
        FrameRect? rect = station.VisualizerRect;
        LiveEncodingSettings encoding = LiveEncodingSettings.Current;
        return BaseSpec(station.Id, station.Name, PageUrl(station.Id)) with
        {
            Encoding = encoding,
            Audio = LiveAudio.Stream,
            AudioStreamUrl = SoundUrl(station.Id),
            Volume = station.Volume,
            Visualizer = rect is null ? null : RadioLayout.Filter(station.Visualizer, rect, station.VisualizerColor ?? station.AccentColor,
                encoding.FrameRate, station.VisualizerIntensity),
            VisualizerRect = rect
        };
    }
}
