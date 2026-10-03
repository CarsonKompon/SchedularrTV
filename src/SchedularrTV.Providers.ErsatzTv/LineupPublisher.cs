using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Publishing;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.Providers.ErsatzTv;

/// <summary>
/// Regenerates ErsatzTV Next's lineup.json from the scheduler's channel definitions.
/// </summary>
/// <remarks>
/// Opt-in (<c>WriteLineup</c>). A mismatch between lineup <c>tvg_id</c> and the XMLTV fragment file
/// name silently produces an empty guide, so generating both from one source removes the most common
/// setup failure. Per-channel <c>channel.json</c> files are left alone — those hold transcoding
/// settings that belong to ErsatzTV, not to the scheduler.
/// </remarks>
public sealed class LineupPublisher(
    IOptions<ErsatzTvOptions> options,
    ErsatzTvProcessSupervisor supervisor,
    ILogger<LineupPublisher> logger) : IPlayoutPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Read on each use: settings saved in the app apply straight away.
    private ErsatzTvOptions _options => options.Value;

    public string Name => "ersatztv-lineup";

    public async Task PublishAsync(IReadOnlyList<ChannelSchedule> schedules, CancellationToken cancellationToken = default)
    {
        if (!_options.WriteLineup || string.IsNullOrWhiteSpace(_options.ConfigRoot))
        {
            return;
        }

        string root = Path.GetFullPath(_options.ConfigRoot);
        Directory.CreateDirectory(root);

        // Next resolves output/xmltv folders against the process working directory, not the lineup
        // file, so emit absolute paths and the server runs correctly from anywhere.
        string outputFolder = Resolve(root, _options.Lineup.OutputFolder);
        string xmltvFolder = Resolve(root, _options.XmltvFolder);
        Directory.CreateDirectory(outputFolder);
        Directory.CreateDirectory(xmltvFolder);

        var lineup = new LineupDocument
        {
            Server = new LineupServer { BindAddress = _options.Lineup.BindAddress, Port = _options.Lineup.Port },
            Output = new LineupFolder { Folder = outputFolder },
            Xmltv = new LineupFolder { Folder = xmltvFolder }
        };

        foreach (ChannelSchedule schedule in schedules.OrderBy(s => s.ChannelNumber))
        {
            string number = schedule.ChannelNumber.ToString(CultureInfo.InvariantCulture);
            string channelFolder = Path.Combine(root, "channels", number);
            Directory.CreateDirectory(Path.Combine(channelFolder, "playout"));

            string channelConfigPath = Path.Combine(channelFolder, _options.Lineup.ChannelConfigFileName);
            if (!File.Exists(channelConfigPath))
            {
                await File.WriteAllTextAsync(channelConfigPath, DefaultChannelConfig, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Created default channel config at {Path}.", channelConfigPath);
            }

            lineup.Channels.Add(new LineupChannel
            {
                Number = number,
                Name = schedule.ChannelName,
                Config = channelConfigPath,
                TvgId = schedule.TvgId,
                Logo = schedule.ChannelLogo,
                Group = schedule.ChannelGroup
            });
        }

        string lineupPath = Path.Combine(root, "lineup.json");
        string temp = lineupPath + ".tmp";

        await using (FileStream stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, lineup, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, lineupPath, overwrite: true);
        logger.LogInformation("Wrote lineup with {Count} channel(s) to {Path}.", lineup.Channels.Count, lineupPath);

        WarnAboutOrphanedChannels(root, lineup.Channels.Select(c => c.Number));

        // Everything ErsatzTV reads from the lineup belongs here: it loads the file once at startup,
        // so any field that changes without a restart would silently keep serving the old value.
        supervisor.NotifyLineupPublished(
            string.Join('|', lineup.Channels.Select(c => $"{c.Number}:{c.TvgId}:{c.Config}:{c.Name}:{c.Logo}:{c.Group}")));
    }

    /// <summary>
    /// Renumbering a channel leaves its old folder behind. ErsatzTV ignores anything absent from the
    /// lineup, but a stale playout is confusing enough to be worth pointing at.
    /// </summary>
    private void WarnAboutOrphanedChannels(string root, IEnumerable<string> activeNumbers)
    {
        string channelsRoot = Path.Combine(root, "channels");
        if (!Directory.Exists(channelsRoot))
        {
            return;
        }

        HashSet<string> active = new(activeNumbers, StringComparer.OrdinalIgnoreCase);

        foreach (string folder in Directory.EnumerateDirectories(channelsRoot))
        {
            string name = Path.GetFileName(folder);
            if (!active.Contains(name))
            {
                logger.LogWarning("Channel folder {Path} is no longer in the lineup and can be deleted.", folder);
            }
        }
    }

    /// <summary>A lineup folder setting (absolute, or relative to the lineup folder) as a full path.</summary>
    public static string Resolve(string root, string folder) =>
        Path.GetFullPath(Path.IsPathRooted(folder) ? folder : Path.Combine(root, folder.TrimStart('.', '/', '\\')));

    // Written once and never touched again, so every knob worth changing is spelled out rather
    // than left to defaults the user would have to discover in ErsatzTV's schema.
    private const string DefaultChannelConfig = """
        {
          "playout": { "folder": "./playout" },
          "ffmpeg": { "ffmpeg_path": "", "ffprobe_path": "", "disabled_filters": [] },
          "normalization": {
            "audio": {
              "format": "aac",
              "bitrate_kbps": 192,
              "buffer_kbps": 384,
              "channels": 2,
              "sample_rate_hz": 48000,
              "normalize_loudness": true,
              "loudness": { "integrated_target": -16, "range_target": 11, "true_peak": -1.5 }
            },
            "video": {
              "format": "h264",
              "bit_depth": 8,
              "width": 1920,
              "height": 1080,
              "bitrate_kbps": 6000,
              "buffer_kbps": 12000,
              "accel": null,
              "tonemap_algorithm": "linear"
            },
            "subtitle": { "mode": "burn" }
          }
        }
        """;
}

internal sealed class LineupDocument
{
    [JsonPropertyName("server")]
    public LineupServer Server { get; set; } = new();

    [JsonPropertyName("output")]
    public LineupFolder Output { get; set; } = new();

    [JsonPropertyName("xmltv")]
    public LineupFolder Xmltv { get; set; } = new();

    [JsonPropertyName("channels")]
    public List<LineupChannel> Channels { get; set; } = [];
}

internal sealed class LineupServer
{
    [JsonPropertyName("bind_address")]
    public string BindAddress { get; set; } = "0.0.0.0";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 8409;
}

internal sealed class LineupFolder
{
    [JsonPropertyName("folder")]
    public string Folder { get; set; } = string.Empty;
}

internal sealed class LineupChannel
{
    [JsonPropertyName("number")]
    public string Number { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("config")]
    public string Config { get; set; } = string.Empty;

    [JsonPropertyName("tvg_id")]
    public string TvgId { get; set; } = string.Empty;

    [JsonPropertyName("logo")]
    public string? Logo { get; set; }

    [JsonPropertyName("group")]
    public string? Group { get; set; }
}
