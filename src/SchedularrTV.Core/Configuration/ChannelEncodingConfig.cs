namespace SchedularrTV.Core.Configuration;

/// <summary>
/// One channel's own ErsatzTV encoding settings, over the app-wide ones. Every field left unset inherits.
/// </summary>
public sealed class ChannelEncodingConfig
{
    /// <summary>
    /// Where the video is encoded: <c>none</c> for the processor, or one of ErsatzTV's accelerations (<c>cuda</c>,
    /// <c>qsv</c>, <c>amf</c>, <c>vaapi</c>, <c>videotoolbox</c>, <c>rkmpp</c>, <c>vulkan</c>). Null inherits.
    /// </summary>
    public string? Hardware { get; set; }

    /// <summary>Picture height (the width follows at 16:9). Null inherits.</summary>
    public int? Height { get; set; }

    /// <summary>Video bitrate in kbps. Null inherits.</summary>
    public int? BitrateKbps { get; set; }

    /// <summary>True when nothing is set, so the section can be left out of the YAML.</summary>
    [YamlDotNet.Serialization.YamlIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Hardware) && Height is null && BitrateKbps is null;
}
