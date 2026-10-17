using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.Providers.ErsatzTv;

/// <summary>One of ErsatzTV's video accelerations, as channel.json names it, and what the UI calls it.</summary>
public sealed record ErsatzTvHardware(string Value, string Label, string Hint);

/// <summary>How a channel ends up encoded.</summary>
/// <param name="Hardware">An ErsatzTV acceleration, or <see cref="ErsatzTvEncoding.Software"/> for the processor.</param>
/// <param name="SourceWidth">
/// The width when the picture size comes from the channel's source (an IPTV stream, a streamer channel's picture), which
/// keeps its own shape; null for 16:9 at <paramref name="Height"/>.
/// </param>
public sealed record EffectiveEncoding(string Hardware, int Height, int BitrateKbps, string? VaapiDevice, string? VaapiDriver, int? SourceWidth = null)
{
    public int Width => SourceWidth ?? ErsatzTvEncoding.WidthFor(Height);
    public string ScalingMode { get; init; } = "scale_and_pad";
    public string? FrameRate { get; init; }
}

/// <summary>
/// ErsatzTV encoding chosen in SchedularrTV: app-wide defaults (App settings), which each channel can override field by
/// field. Applied to every channel as a channel config overlay that ErsatzTV merges over its channel.json, so the
/// encoding is always what SchedularrTV says, without rewriting that file.
/// </summary>
public static class ErsatzTvEncoding
{
    /// <summary>Channel annotation carrying a channel's own settings (<see cref="ChannelEncodingConfig"/> as JSON).</summary>
    public const string Annotation = "ersatztv.encoding";

    /// <summary>
    /// Channel annotation giving the picture size of what the channel plays, <c>1280x720</c>, set by channels made from
    /// one source (IPTV, streamers). Its picture is then kept at that size rather than scaled to the default; a size the
    /// channel chooses itself still wins.
    /// </summary>
    public const string PictureAnnotation = "ersatztv.picture";

    /// <summary>The value for encoding on the processor.</summary>
    public const string Software = "none";

    /// <summary>Overlays must name a channel config version ErsatzTV reads; every 0.1.x build reads 0.1.0.</summary>
    public const string OverlayVersion = "https://ersatztv.org/channel/version/0.1.0";

    /// <summary>What can be chosen, as ErsatzTV Next spells it in channel.json.</summary>
    public static readonly IReadOnlyList<ErsatzTvHardware> Hardware =
    [
        new(Software, "Processor (software)", "Works everywhere; the heaviest on the CPU."),
        new("cuda", "NVIDIA (CUDA / NVENC)", "GeForce, Quadro and Tesla cards, on Windows or Linux."),
        new("qsv", "Intel Quick Sync", "Intel processors with graphics, and Arc cards."),
        new("amf", "AMD (AMF)", "Radeon cards and Ryzen graphics, on Windows."),
        new("vaapi", "VA-API", "Intel or AMD graphics on Linux."),
        new("videotoolbox", "Apple VideoToolbox", "Any Mac."),
        new("rkmpp", "Rockchip (RKMPP)", "Rockchip boards such as the Orange Pi 5."),
        new("vulkan", "Vulkan", "Newer, and experimental in ErsatzTV.")
    ];

    public static readonly IReadOnlyList<int> Heights = [480, 540, 720, 1080, 1440, 2160];

    public static readonly IReadOnlyList<string> AspectRatios = ["16:9", "4:3", "16:10", "1:1", "21:9", "9:16"];
    public static readonly IReadOnlyList<string> FrameRates = ["24000/1001", "24", "25", "30000/1001", "30", "50", "60000/1001", "60"];

    public static int WidthFor(int height, string aspectRatio)
    {
        string[] parts = aspectRatio.Split(':');
        return (int)Math.Round(height * double.Parse(parts[0], CultureInfo.InvariantCulture)
            / double.Parse(parts[1], CultureInfo.InvariantCulture) / 2, MidpointRounding.AwayFromZero) * 2;
    }

    /// <summary>FFmpeg/ErsatzTV accepts rational frame rates; source removes any inherited fixed rate.</summary>
    public static string? NormalizeFrameRate(string? value)
    {
        string? text = Clean(value);
        if (text == "source") return text;
        string[] parts = (text ?? "").Split('/');
        if (parts.Length is < 1 or > 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int numerator) || numerator <= 0)
            return null;
        int denominator = 1;
        if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out denominator) || denominator <= 0))
            return null;
        return (double)numerator / denominator is >= 1 and <= 240 ? text : null;
    }

    public static readonly IReadOnlyList<string> VaapiDrivers = ["ihd", "i965", "radeonsi"];

    /// <summary>Where the video is encoded until App settings says otherwise: the processor, which works everywhere.</summary>
    public const string DefaultHardware = Software;

    /// <summary>The picture height until App settings says otherwise: as a new channel.json has it (720p on a Raspberry Pi).</summary>
    public static int DefaultHeight => SchedularrTV.Core.Platform.HardwareProfile.IsLowPower ? 720 : 1080;

    /// <summary>The bitrate until App settings says otherwise, as a new channel.json has it.</summary>
    public static int DefaultBitrateKbps => SchedularrTV.Core.Platform.HardwareProfile.IsLowPower ? 3000 : 6000;

    public static string Label(string? hardware) =>
        Hardware.FirstOrDefault(choice => string.Equals(choice.Value, hardware, StringComparison.OrdinalIgnoreCase))?.Label ?? hardware ?? string.Empty;

    /// <summary>The app-wide settings with the defaults filled in, as every channel inherits them.</summary>
    public static EffectiveEncoding Defaults(ErsatzTvEncodingOptions global) => Resolve(global, (ChannelEncodingConfig?)null);

    /// <summary>The 16:9 width for a height, rounded to an even number.</summary>
    public static int WidthFor(int height) => (height * 16 / 9 + 1) / 2 * 2;

    /// <summary>A channel's own settings as the annotation carries them; null when it has none.</summary>
    public static string? ToAnnotation(ChannelEncodingConfig? channel) =>
        channel is null || channel.IsEmpty
            ? null
            : JsonSerializer.Serialize(new { hardware = Clean(channel.Hardware), height = channel.Height, width = channel.Width,
                aspect_ratio = channel.AspectRatio, frame_rate = channel.FrameRate,
                scaling_mode = Clean(channel.ScalingMode), bitrate_kbps = channel.BitrateKbps });

    /// <summary>Explicit channel settings must not be bypassed by a source's copy-mode overlay.</summary>
    public static bool HasOverrides(ChannelSchedule schedule) =>
        schedule.Annotations?.TryGetValue(Annotation, out string? json) == true
        && FromAnnotation(json) is { IsEmpty: false };

    /// <summary>
    /// The settings a channel ends up with: its own (from its annotation), else its source's picture size (from its
    /// picture annotation), else the app-wide defaults.
    /// </summary>
    public static EffectiveEncoding Resolve(ErsatzTvEncodingOptions global, ChannelSchedule schedule)
    {
        ChannelEncodingConfig? own = schedule.Annotations?.TryGetValue(Annotation, out string? json) == true ? FromAnnotation(json) : null;
        EffectiveEncoding resolved = Resolve(global, own);
        return Valid(own?.Height) is null && ValidWidth(own?.Width) is null
               && !AspectRatios.Contains(own?.AspectRatio ?? "")
               && schedule.Annotations?.TryGetValue(PictureAnnotation, out string? picture) == true
               && ParsePicture(picture) is { } size
            ? resolved with { Height = size.Height, SourceWidth = size.Width }
            : resolved;
    }

    /// <summary>A picture annotation's value, for a channel source to set.</summary>
    public static string Picture(int width, int height) =>
        string.Create(CultureInfo.InvariantCulture, $"{width}x{height}");

    /// <summary>A <c>1280x720</c> size, made even (encoders need it); null when it isn't one or is out of range.</summary>
    public static (int Width, int Height)? ParsePicture(string? text)
    {
        string[] parts = (text ?? string.Empty).Split('x', 'X');
        return parts.Length == 2
               && int.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int width)
               && int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int height)
               && width is >= 160 and <= 7680 && height is >= 120 and <= 4320
            ? (width / 2 * 2, height / 2 * 2)
            : null;
    }

    /// <summary>A channel's settings: each field its own where it sets one, else the app-wide setting, else the default.</summary>
    public static EffectiveEncoding Resolve(ErsatzTvEncodingOptions global, ChannelEncodingConfig? channel)
    {
        string hardware = Known(channel?.Hardware) ?? Known(global.Hardware) ?? DefaultHardware;
        int height = Valid(channel?.Height) ?? Valid(Number(global.Height)) ?? DefaultHeight;
        int bitrate = Bitrate(channel?.BitrateKbps) ?? Bitrate(Number(global.BitrateKbps)) ?? DefaultBitrateKbps;
        bool vaapi = hardware == "vaapi";
        string? aspect = AspectRatios.Contains(channel?.AspectRatio ?? "") ? channel!.AspectRatio
            : AspectRatios.Contains(global.AspectRatio ?? "") ? global.AspectRatio : null;
        int? width = ValidWidth(channel?.Width)
            ?? (aspect is not null ? WidthFor(height, aspect) : ValidWidth(Number(global.Width)));
        string? frameRate = NormalizeFrameRate(channel?.FrameRate) ?? NormalizeFrameRate(global.FrameRate);
        return new EffectiveEncoding(hardware, height, bitrate,
            vaapi ? Clean(global.VaapiDevice) : null,
            vaapi && Clean(global.VaapiDriver) is { } driver && VaapiDrivers.Contains(driver) ? driver : null,
            width)
        {
            ScalingMode = Scaling(channel?.ScalingMode) ?? Scaling(global.ScalingMode) ?? "scale_and_pad",
            FrameRate = frameRate == "source" ? null : frameRate
        };
    }

    /// <summary>
    /// ffmpeg filters a hardware acceleration must not use. <c>pad_cuda</c> leaves rows of the padded picture
    /// unwritten, which show as a green line along the bottom of letterboxed or pillarboxed programmes (anything not
    /// already the channel's size; ads usually are, so they look fine). Without it ErsatzTV pads in software, with real
    /// black, and keeps decoding, scaling and encoding on the card.
    /// </summary>
    public static IReadOnlyList<string> DisabledFilters(string hardware) => hardware switch
    {
        "cuda" => ["pad_cuda"],
        _ => []
    };

    /// <summary>The overlay that gives a channel <paramref name="encoding"/>, whatever its channel.json says.</summary>
    /// <param name="channelDisabledFilters">
    /// The filters the channel's own channel.json disables. An overlay replaces the list rather than adding to it, so
    /// they're carried over whenever the overlay sets one.
    /// </param>
    public static string Overlay(EffectiveEncoding encoding, bool copyVideo = false, IEnumerable<string>? channelDisabledFilters = null)
    {
        var video = new JsonObject
        {
            ["mode"] = copyVideo ? "copy" : "transcode",
            ["scaling_mode"] = encoding.ScalingMode,
            ["frame_rate"] = encoding.FrameRate,
            // Null in an overlay removes the key, so any acceleration in channel.json gives way to the processor.
            ["accel"] = encoding.Hardware == Software ? null : encoding.Hardware,
            ["width"] = encoding.Width,
            ["height"] = encoding.Height,
            ["bitrate_kbps"] = encoding.BitrateKbps,
            ["buffer_kbps"] = encoding.BitrateKbps * 2
        };

        if (encoding.VaapiDevice is { } device)
        {
            video["vaapi_device"] = device;
        }

        if (encoding.VaapiDriver is { } driver)
        {
            video["vaapi_driver"] = driver;
        }

        var overlay = new JsonObject
        {
            ["version"] = OverlayVersion,
            ["normalization"] = new JsonObject { ["video"] = video }
        };

        IReadOnlyList<string> disabled = DisabledFilters(encoding.Hardware);
        if (disabled.Count > 0)
        {
            var filters = new JsonArray();
            foreach (string filter in (channelDisabledFilters ?? Enumerable.Empty<string>()).Select(f => f.Trim()).Where(f => f.Length > 0)
                         .Concat(disabled).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                filters.Add(filter);
            }

            overlay["ffmpeg"] = new JsonObject { ["disabled_filters"] = filters };
        }

        return overlay.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>A file name that changes with the overlay's content, so ErsatzTV is restarted to pick a new one up.</summary>
    public static string FileName(string overlay) =>
        $"encoding-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(overlay)))[..12]}.json";

    internal static ChannelEncodingConfig? FromAnnotation(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            return new ChannelEncodingConfig
            {
                Hardware = root.TryGetProperty("hardware", out JsonElement hardware) && hardware.ValueKind == JsonValueKind.String ? hardware.GetString() : null,
                Height = root.TryGetProperty("height", out JsonElement height) && height.ValueKind == JsonValueKind.Number ? height.GetInt32() : null,
                Width = root.TryGetProperty("width", out JsonElement width) && width.ValueKind == JsonValueKind.Number ? width.GetInt32() : null,
                AspectRatio = root.TryGetProperty("aspect_ratio", out JsonElement aspect) && aspect.ValueKind == JsonValueKind.String ? aspect.GetString() : null,
                FrameRate = root.TryGetProperty("frame_rate", out JsonElement rate) && rate.ValueKind == JsonValueKind.String ? rate.GetString() : null,
                ScalingMode = root.TryGetProperty("scaling_mode", out JsonElement scaling) && scaling.ValueKind == JsonValueKind.String ? scaling.GetString() : null,
                BitrateKbps = root.TryGetProperty("bitrate_kbps", out JsonElement bitrate) && bitrate.ValueKind == JsonValueKind.Number ? bitrate.GetInt32() : null
            };
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    // A choice ErsatzTV understands; anything else (a typo in the YAML) is ignored rather than stopping the channel.
    private static string? Known(string? value) =>
        Clean(value) is { } hardware && Hardware.Any(choice => choice.Value == hardware) ? hardware : null;

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static int? Number(string? text) =>
        int.TryParse(text?.Trim().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;

    private static int? Valid(int? height) => height is >= 240 and <= 4320 ? height / 2 * 2 : null;

    private static int? ValidWidth(int? width) => width is >= 160 and <= 7680 ? width / 2 * 2 : null;

    private static string? Scaling(string? value) => Clean(value) is "scale_and_pad" or "crop" ? Clean(value) : null;

    private static int? Bitrate(int? kbps) => kbps is >= 300 and <= 100_000 ? kbps : null;
}
