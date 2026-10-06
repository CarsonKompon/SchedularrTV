using SchedularrTV.Core.Platform;

namespace SchedularrTV.Streamers;

/// <summary>
/// The plugin's own settings (its gear on the Plugins page), and the addresses it works out from the app's. What every
/// live page plugin has comes from <see cref="LivePageSettings"/>: captured 15 times a second (8 on a Raspberry Pi, the
/// chat and pop-ups being see-through PNGs, the costliest kind), kept on a minute after the last viewer.
/// </summary>
public sealed class StreamerSettings(IPluginContext context) : LivePageSettings(context, RoutePrefix, 15, 8, 60)
{
    public const string RoutePrefix = "/plugins/streamers";

    public string DataDirectory => Context.DataDirectory;

    public string AppDataDirectory => Context.AppDataDirectory;

    public string? YtDlpPath => Value("YtDlpPath");

    /// <summary>How often each streamer is checked for being live: unset, every minute, or every two on a Raspberry Pi.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(Number("PollSeconds", HardwareProfile.Pick(60, 120), 20, 900));

    /// <summary>
    /// The tallest picture asked of a stream. Unset: 720, or 480 on a Raspberry Pi, where the stream is decoded in
    /// software and a 720p60 one alone can take most of the CPU.
    /// </summary>
    public int MaxHeight => Number("MaxHeight", HardwareProfile.Pick(720, 480), 360, 1080);

    /// <summary>The time zone predictions are worked out in: the schedule's, else this computer's.</summary>
    public TimeZoneInfo TimeZone
    {
        get
        {
            string? id = Value("TimeZone");
            try
            {
                return id is null ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return TimeZoneInfo.Local;
            }
        }
    }
}
