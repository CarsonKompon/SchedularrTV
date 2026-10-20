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

        HashSet<string> encodingOverlays = new(StringComparer.OrdinalIgnoreCase);
        foreach (ChannelSchedule schedule in schedules.OrderBy(s => s.ChannelNumber))
        {
            string number = schedule.ChannelNumber.ToString(CultureInfo.InvariantCulture);
            string channelFolder = Path.Combine(root, "channels", number);
            Directory.CreateDirectory(Path.Combine(channelFolder, "playout"));

            string channelConfigPath = Path.Combine(channelFolder, _options.Lineup.ChannelConfigFileName);
            if (!File.Exists(channelConfigPath))
            {
                await File.WriteAllTextAsync(channelConfigPath, NewChannelConfig, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Created default channel config at {Path}.", channelConfigPath);
            }
            else
            {
                // Configs written before ErsatzTV versioned them are refused by current builds, and the channel
                // then never plays, so they're brought up to date (the rest of the file is left as it is).
                await ChannelConfigSchema.UpgradeFileAsync(channelConfigPath, logger, cancellationToken).ConfigureAwait(false);
            }

            // Keep audio passthrough, but explicit video settings must take precedence over copying video.
            List<string> overlays = [];
            if (IsPassthrough(schedule))
            {
                overlays.Add(await EnsureCopyOverlayAsync(root, cancellationToken).ConfigureAwait(false));
            }

            string encoding = ErsatzTvEncoding.Overlay(ErsatzTvEncoding.Resolve(_options.Encoding, schedule),
                copyVideo: IsPassthrough(schedule) && !ErsatzTvEncoding.HasOverrides(schedule),
                channelDisabledFilters: await ReadDisabledFiltersAsync(channelConfigPath, cancellationToken).ConfigureAwait(false),
                subtitleMode: ErsatzTvTracks.SubtitleMode(ErsatzTvTracks.For(schedule)),
                // Local tool paths are not valid for an unmanaged remote/container installation.
                ffmpegPath: _options.ManageProcess ? _options.FfmpegPath : null,
                ffprobePath: _options.ManageProcess ? _options.FfprobePath : null);
            string encodingPath = await EnsureOverlayAsync(root, ErsatzTvEncoding.FileName(encoding), encoding, cancellationToken).ConfigureAwait(false);
            encodingOverlays.Add(encodingPath);
            overlays.Add(encodingPath);

            lineup.Channels.Add(new LineupChannel
            {
                Number = number,
                Name = schedule.ChannelName,
                Config = channelConfigPath,
                Overlays = overlays,
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
        RemoveUnusedEncodingOverlays(root, encodingOverlays);

        // Everything ErsatzTV reads from the lineup belongs here: it loads the file once at startup,
        // so any field that changes without a restart would silently keep serving the old value.
        supervisor.NotifyLineupPublished(
            string.Join('|', lineup.Channels.Select(c =>
                $"{c.Number}:{c.TvgId}:{c.Config}:{string.Join(',', c.Overlays ?? [])}:{c.Name}:{c.Logo}:{c.Group}")));
    }

    /// <summary>
    /// Channel annotation asking for the channel's streams to be passed through (copied) rather than re-encoded where
    /// ErsatzTV can, as for IPTV channels that are already in a format players take.
    /// </summary>
    public const string CopyAnnotation = "ersatztv.copy";

    internal static bool IsPassthrough(ChannelSchedule schedule) =>
        schedule.Annotations?.TryGetValue(CopyAnnotation, out string? copy) == true && bool.TryParse(copy, out bool passthrough) && passthrough;

    /// <summary>
    /// Copying is an overlay on the channel's own channel.json, so the rest of its settings (and any edits to them)
    /// still apply to whatever can't be copied. Loudness normalisation needs re-encoding, so it's off.
    /// </summary>
    internal const string CopyOverlay = """
        {
          "version": "https://ersatztv.org/channel/version/0.1.0",
          "normalization": {
            "audio": { "mode": "copy", "normalize_loudness": false },
            "video": { "mode": "copy" }
          }
        }
        """;

    /// <summary>The <c>ffmpeg.disabled_filters</c> a channel.json sets; none when it sets none or can't be read.</summary>
    internal static async Task<IReadOnlyList<string>> ReadDisabledFiltersAsync(string channelConfigPath, CancellationToken cancellationToken)
    {
        try
        {
            string json = await File.ReadAllTextAsync(channelConfigPath, cancellationToken).ConfigureAwait(false);
            return System.Text.Json.Nodes.JsonNode.Parse(json)?["ffmpeg"]?["disabled_filters"] is System.Text.Json.Nodes.JsonArray filters
                ? filters.Select(f => f?.GetValueKind() == JsonValueKind.String ? f.GetValue<string>() : null)
                    .OfType<string>().ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    private static Task<string> EnsureCopyOverlayAsync(string root, CancellationToken cancellationToken) =>
        EnsureOverlayAsync(root, "copy.json", CopyOverlay, cancellationToken);

    /// <summary>Writes an overlay into the overlays folder unless it's already there as it should be; returns its path.</summary>
    private static async Task<string> EnsureOverlayAsync(string root, string name, string content, CancellationToken cancellationToken)
    {
        string folder = Path.Combine(root, "overlays");
        string path = Path.Combine(folder, name);
        if (!File.Exists(path) || await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) != content)
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
        }

        return path;
    }

    // The encoding overlays the previous lineup used, which the ErsatzTV still running may yet start a channel with.
    private HashSet<string> _previousEncodingOverlays = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Encoding overlays are named after their content, so a changed setting leaves the old file behind. It's removed
    /// once neither this lineup nor the one before uses it: until ErsatzTV restarts for the new lineup, it starts
    /// channels from the old one.
    /// </summary>
    private void RemoveUnusedEncodingOverlays(string root, HashSet<string> used)
    {
        HashSet<string> previous = _previousEncodingOverlays;
        _previousEncodingOverlays = used;
        string folder = Path.Combine(root, "overlays");
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(folder, "encoding-*.json"))
        {
            string full = Path.GetFullPath(file);
            if (used.Contains(full) || previous.Contains(full))
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Couldn't remove the unused encoding overlay {Path}.", file);
            }
        }
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
    internal const string DefaultChannelConfig = """
        {
          "version": "https://ersatztv.org/channel/version/0.1.0",
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

    /// <summary>
    /// What a new channel starts with on a Raspberry Pi (or another low-power computer): 720p at 3 Mbps and no loudness
    /// normalisation, since ErsatzTV encodes in software there (the Pi 5 has no H.264 encoder) and a 1080p encode with
    /// loudnorm is more than one Pi can do in real time. Like the standard one, written once and then left to the user.
    /// </summary>
    internal const string LowPowerChannelConfig = """
        {
          "version": "https://ersatztv.org/channel/version/0.1.0",
          "playout": { "folder": "./playout" },
          "ffmpeg": { "ffmpeg_path": "", "ffprobe_path": "", "disabled_filters": [] },
          "normalization": {
            "audio": {
              "format": "aac",
              "bitrate_kbps": 160,
              "buffer_kbps": 320,
              "channels": 2,
              "sample_rate_hz": 48000,
              "normalize_loudness": false,
              "loudness": { "integrated_target": -16, "range_target": 11, "true_peak": -1.5 }
            },
            "video": {
              "format": "h264",
              "bit_depth": 8,
              "width": 1280,
              "height": 720,
              "bitrate_kbps": 3000,
              "buffer_kbps": 6000,
              "accel": null,
              "tonemap_algorithm": "linear"
            },
            "subtitle": { "mode": "burn" }
          }
        }
        """;

    /// <summary>The channel.json a new channel gets on this computer.</summary>
    internal static string NewChannelConfig => SchedularrTV.Core.Platform.HardwareProfile.IsLowPower ? LowPowerChannelConfig : DefaultChannelConfig;
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

    /// <summary>Configs merged over <see cref="Config"/>, such as the copy overlay for passed-through channels.</summary>
    [JsonPropertyName("overlays")]
    public List<string>? Overlays { get; set; }

    [JsonPropertyName("tvg_id")]
    public string TvgId { get; set; } = string.Empty;

    [JsonPropertyName("logo")]
    public string? Logo { get; set; }

    [JsonPropertyName("group")]
    public string? Group { get; set; }
}
