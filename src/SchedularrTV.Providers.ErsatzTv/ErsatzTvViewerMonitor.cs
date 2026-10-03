using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Publishing;

namespace SchedularrTV.Providers.ErsatzTv;

/// <summary>
/// Reads ErsatzTV Next's per-channel <c>.heartbeat</c> file, which it touches on every HLS request
/// for that channel and removes once the stream is reaped after 90 seconds without one.
/// </summary>
public sealed class ErsatzTvViewerMonitor(
    IOptions<ErsatzTvOptions> options,
    TimeProvider clock,
    ILogger<ErsatzTvViewerMonitor> logger) : IChannelViewers
{
    private const string HeartbeatFileName = ".heartbeat";

    // Next's own reap timeout plus slack for a player buffering between playlist requests.
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LineupCache = TimeSpan.FromMinutes(1);

    // Read on each use: settings saved in the app apply straight away.
    private ErsatzTvOptions _options => options.Value;
    private readonly Lock _gate = new();
    private string? _outputFolder;
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;

    public bool? IsWatched(int channelNumber)
    {
        if (OutputFolder() is not { } folder)
        {
            return null;
        }

        string heartbeat = Path.Combine(folder, channelNumber.ToString(CultureInfo.InvariantCulture), HeartbeatFileName);
        try
        {
            return File.Exists(heartbeat)
                && clock.GetUtcNow() - File.GetLastWriteTimeUtc(heartbeat) < HeartbeatTimeout;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>The HLS output folder named in lineup.json, which Next resolves against its working directory (the config root).</summary>
    private string? OutputFolder()
    {
        lock (_gate)
        {
            DateTimeOffset now = clock.GetUtcNow();
            if (now - _readAt < LineupCache)
            {
                return _outputFolder;
            }

            _readAt = now;
            _outputFolder = null;
            if (string.IsNullOrWhiteSpace(_options.ConfigRoot))
            {
                return null;
            }

            string root = Path.GetFullPath(_options.ConfigRoot);
            string lineup = Path.Combine(root, "lineup.json");
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(lineup));
                if (document.RootElement.TryGetProperty("output", out JsonElement output)
                    && output.TryGetProperty("folder", out JsonElement folder)
                    && folder.GetString() is { Length: > 0 } path)
                {
                    _outputFolder = Path.GetFullPath(Path.Combine(root, path));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                logger.LogDebug(ex, "Could not read the ErsatzTV lineup at {Path} to find who is watching.", lineup);
            }

            return _outputFolder;
        }
    }
}
