using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SchedularrTV.Plugins.Playout;

namespace SchedularrTV.Iptv;

/// <summary>
/// The picture size of each carried channel's stream, as the relay last saw it, kept in <c>iptv-pictures.json</c> so it
/// survives a restart. ErsatzTV is told it (the channel's picture annotation), so a channel it re-encodes keeps the
/// provider's size rather than being scaled to the app-wide default. A size first seen, or changed, republishes.
/// </summary>
public sealed partial class PictureSizes
{
    private readonly string _path;
    private readonly IScheduleRepublisher? _republisher;
    private readonly ILogger<PictureSizes>? _logger;
    private readonly Lock _gate = new();
    private Dictionary<string, string> _sizes;

    public const string FileName = "iptv-pictures.json";

    public PictureSizes(string path, IScheduleRepublisher? republisher = null, ILogger<PictureSizes>? logger = null)
    {
        _path = path;
        _republisher = republisher;
        _logger = logger;
        _sizes = Load(path);
    }

    /// <summary>A channel's size (<c>1280x720</c>) by its relay key (<c>source/number</c>); null before its stream has been seen.</summary>
    public string? For(string key)
    {
        lock (_gate)
        {
            return _sizes.TryGetValue(key, out string? size) ? size : null;
        }
    }

    /// <summary>
    /// Keeps a channel's size, read from the relay's output stream (<see cref="Parse"/>): the one stream copied out, not
    /// the several variants an HLS input can list.
    /// </summary>
    public void Note(string key, string size)
    {
        lock (_gate)
        {
            if (_sizes.TryGetValue(key, out string? known) && known == size)
            {
                return;
            }

            _sizes = new Dictionary<string, string>(_sizes, StringComparer.Ordinal) { [key] = size };
            Save();
        }

        _logger?.LogInformation("IPTV: {Channel}'s picture is {Size}.", key, size);
        _republisher?.RequestRepublish();
    }

    /// <summary>The size in an ffmpeg stream line such as <c>Stream #0:0: Video: h264 (High), yuv420p(tv), 1280x720 [SAR 1:1], 50 fps</c>.</summary>
    public static string? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.Contains("Video:", StringComparison.Ordinal))
        {
            return null;
        }

        Match match = SizePattern().Match(line[line.IndexOf("Video:", StringComparison.Ordinal)..]);
        return match.Success
               && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int width)
               && int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int height)
               && width >= 160 && height >= 120
            ? string.Create(CultureInfo.InvariantCulture, $"{width}x{height}")
            : null;
    }

    // A size stands alone between separators, which keeps codec tags like 0x1B out.
    [GeneratedRegex(@"(?:^|[\s,])(\d{3,5})x(\d{3,5})(?=[\s,\[]|$)")]
    private static partial Regex SizePattern();

    private static Dictionary<string, string> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) is { } loaded
                    ? new Dictionary<string, string>(loaded, StringComparer.Ordinal)
                    : new(StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_sizes));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(ex, "IPTV: couldn't keep the channels' picture sizes; they're known until the scheduler restarts.");
        }
    }
}
