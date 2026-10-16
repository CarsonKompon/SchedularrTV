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

    /// <summary>Picture height in pixels. Null inherits.</summary>
    public int? Height { get; set; }

    /// <summary>Picture width in pixels. Null inherits; without a width the picture is 16:9.</summary>
    public int? Width { get; set; }

    /// <summary>Preset picture shape; explicit Width takes precedence. Null inherits.</summary>
    public string? AspectRatio { get; set; }

    /// <summary>source, or a positive frame rate (N or N/D). Null inherits.</summary>
    public string? FrameRate { get; set; }

    /// <summary>scale_and_pad (fit) or crop (fill). Null inherits.</summary>
    public string? ScalingMode { get; set; }

    /// <summary>Video bitrate in kbps. Null inherits.</summary>
    public int? BitrateKbps { get; set; }

    /// <summary>True when nothing is set, so the section can be left out of the YAML.</summary>
    [YamlDotNet.Serialization.YamlIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Hardware) && Height is null && Width is null
                           && string.IsNullOrWhiteSpace(AspectRatio) && string.IsNullOrWhiteSpace(FrameRate)
                           && string.IsNullOrWhiteSpace(ScalingMode) && BitrateKbps is null;
}
