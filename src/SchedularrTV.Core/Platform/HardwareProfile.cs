using System.Globalization;
using System.Runtime.InteropServices;

namespace SchedularrTV.Core.Platform;

/// <summary>How the app treats the computer it runs on.</summary>
public enum HardwareMode
{
    /// <summary>Decided from the hardware: a Raspberry Pi or another small ARM board is <see cref="LowPower"/>.</summary>
    Auto,

    /// <summary>A Raspberry Pi or similar: lighter defaults (smaller, faster encodes, fewer things at once) and warnings.</summary>
    LowPower,

    /// <summary>An ordinary desktop or server: the full defaults, and no low-power warnings.</summary>
    Standard
}

/// <summary>What was found out about this computer.</summary>
public sealed record HardwareInfo(
    string? Model,
    bool IsRaspberryPi,
    int? PiGeneration,
    int Cores,
    long MemoryBytes,
    Architecture Architecture,
    bool HasV4l2H264Encoder)
{
    public double MemoryGb => MemoryBytes / 1024d / 1024d / 1024d;

    /// <summary>A short description for the UI: "Raspberry Pi 5 Model B · 4 cores · 8 GB", say.</summary>
    public string Summary
    {
        get
        {
            string name = Model ?? $"{RuntimeInformation.OSDescription.Split(' ', 2)[0]} {Architecture.ToString().ToLowerInvariant()}";
            string memory = MemoryBytes > 0 ? $" · {Math.Round(MemoryGb, MemoryGb < 3 ? 1 : 0).ToString(CultureInfo.InvariantCulture)} GB" : "";
            return $"{name} · {Cores} core{(Cores == 1 ? "" : "s")}{memory}";
        }
    }

    /// <summary>Whether the rules in <see cref="HardwareProfile"/> call this computer low-power.</summary>
    public bool LooksLowPower =>
        IsRaspberryPi
        || (OperatingSystem.IsLinux() && Architecture is Architecture.Arm64 or Architecture.Arm && (Cores <= 4 || MemoryGb <= 4.5))
        || Cores <= 2;
}

/// <summary>
/// The app settings that hardware and encoding choices are read from (<see cref="HardwareProfile.SettingKey"/>,
/// <see cref="LiveEncodingSettings.Section"/>, <see cref="RenderEncodingSettings.Section"/>). The host hands over a
/// reader for its live configuration, so the scheduler and every plugin see the same values, and a saved change
/// applies at once. Without one (tests, tools) everything reads as unset, so the defaults apply.
/// </summary>
public static class PlatformSettings
{
    private static Func<string, string?> _read = _ => null;

    public static void Use(Func<string, string?> read) => _read = read ?? (_ => null);

    public static string? Read(string key)
    {
        try
        {
            return _read(key);
        }
        catch (ObjectDisposedException)
        {
            // A host restarted in place: the old configuration is gone until the new host hands over its own.
            return null;
        }
    }
}

/// <summary>
/// Whether this computer is a Raspberry Pi (or something like it), so the app and its plugins can pick lighter defaults
/// and warn about settings that are known to be too heavy. Read <see cref="IsLowPower"/> each time it's needed: the
/// <c>Hardware:Profile</c> app setting (<see cref="HardwareMode"/>) can change while the app runs.
/// </summary>
/// <remarks>
/// Defaults that follow this are only ever defaults: a value the user set is always kept, even if it's heavy; the UI
/// warns about it instead.
/// </remarks>
public static class HardwareProfile
{
    /// <summary>The app setting that overrides detection: <c>Auto</c>, <c>LowPower</c> or <c>Standard</c>.</summary>
    public const string SettingKey = "Hardware:Profile";

    private static readonly Lazy<HardwareInfo> _detected = new(Detect);

    /// <summary>What detection found (worked out once).</summary>
    public static HardwareInfo Detected => _detected.Value;

    /// <summary>The mode the user chose (Auto unless set).</summary>
    public static HardwareMode Mode =>
        Enum.TryParse(PlatformSettings.Read(SettingKey)?.Trim().Trim('"').Replace("-", "", StringComparison.Ordinal), ignoreCase: true,
            out HardwareMode mode) ? mode : HardwareMode.Auto;

    /// <summary>True when the app should use its low-power defaults and show low-power warnings.</summary>
    public static bool IsLowPower => Mode switch
    {
        HardwareMode.LowPower => true,
        HardwareMode.Standard => false,
        _ => Detected.LooksLowPower
    };

    /// <summary>A Raspberry Pi 4 (or anything with the Pi's V4L2 H.264 encoder), where <c>h264_v4l2m2m</c> can take encoding off the CPU.</summary>
    public static bool HasV4l2H264Encoder => Detected.HasV4l2H264Encoder;

    /// <summary>
    /// Picks the low-power or standard value. For defaults only: never apply it over something the user chose.
    /// </summary>
    public static T Pick<T>(T standard, T lowPower) => IsLowPower ? lowPower : standard;

    /// <summary>A friendly name for the hardware, for warnings: "this Raspberry Pi 5", "this low-power device".</summary>
    public static string DeviceName => Detected switch
    {
        { IsRaspberryPi: true, PiGeneration: { } generation } => $"this Raspberry Pi {generation}",
        { IsRaspberryPi: true } => "this Raspberry Pi",
        _ => "this low-power device"
    };

    private static HardwareInfo Detect()
    {
        string? model = null;
        bool v4l2 = false;
        if (OperatingSystem.IsLinux())
        {
            // The device tree names the board on a Pi (and most other ARM boards); it ends with a NUL.
            foreach (string path in new[] { "/proc/device-tree/model", "/sys/firmware/devicetree/base/model" })
            {
                try
                {
                    if (File.Exists(path))
                    {
                        model = File.ReadAllText(path).Trim('\0', ' ', '\n', '\r');
                        if (model.Length > 0)
                        {
                            break;
                        }

                        model = null;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            // The Pi 4's (and 3's) hardware H.264 encoder is the bcm2835-codec's /dev/video11. The Pi 5 has none.
            v4l2 = File.Exists("/dev/video11");
        }

        bool pi = model?.Contains("Raspberry Pi", StringComparison.OrdinalIgnoreCase) == true;
        int? generation = null;
        if (pi && model is not null)
        {
            // "Raspberry Pi 5 Model B Rev 1.0", "Raspberry Pi 4 Model B Rev 1.4", "Raspberry Pi Compute Module 4 Rev 1.0"
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(model, @"(?:Pi|Module)\s+(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            {
                generation = number;
            }
        }

        long memory = 0;
        try
        {
            memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
        }

        return new HardwareInfo(model, pi, generation, Environment.ProcessorCount, memory, RuntimeInformation.OSArchitecture,
            v4l2 && (generation is null or < 5));
    }
}
