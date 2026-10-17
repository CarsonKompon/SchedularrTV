using System.Text.Json.Serialization;

namespace SchedularrTV.Providers.ErsatzTv;

/// <summary>
/// Mirrors https://ersatztv.org/playout/version/0.0.3 (schema/playout.json in ErsatzTV/next).
/// </summary>
internal sealed class PlayoutDocument
{
    public const string SchemaVersion = "https://ersatztv.org/playout/version/0.0.3";

    [JsonPropertyName("version")]
    public string Version { get; set; } = SchemaVersion;

    [JsonPropertyName("generated_at")]
    public string GeneratedAt { get; set; } = string.Empty;

    [JsonPropertyName("items")]
    public List<PlayoutItem> Items { get; set; } = [];
}

internal sealed class PlayoutItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("start")]
    public string Start { get; set; } = string.Empty;

    [JsonPropertyName("finish")]
    public string Finish { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public PlayoutSource? Source { get; set; }

    [JsonPropertyName("tracks")]
    public PlayoutTracks? Tracks { get; set; }

    /// <summary>
    /// Graphics layers (screen bugs, lower thirds) composited over the item, bottom to top, in ErsatzTV's own
    /// format. Passed through from the entry's <c>ersatztv.graphics</c> annotation, as a plugin wrote it.
    /// </summary>
    [JsonPropertyName("graphics")]
    public System.Text.Json.Nodes.JsonArray? Graphics { get; set; }
}

internal sealed class PlayoutTracks
{
    [JsonPropertyName("video")]
    public PlayoutTrack? Video { get; set; }

    [JsonPropertyName("audio")]
    public PlayoutTrack? Audio { get; set; }

    /// <summary>The subtitles to include; how they're shown (burnt in or a track) is the channel's subtitle mode.</summary>
    [JsonPropertyName("subtitle")]
    public PlayoutTrack? Subtitle { get; set; }
}

/// <summary>
/// One track of an item: from <see cref="Source"/>, else the item's own source; the stream at
/// <see cref="StreamIndex"/>, else the first of its kind.
/// </summary>
internal sealed class PlayoutTrack
{
    [JsonPropertyName("source")]
    public PlayoutSource? Source { get; set; }

    [JsonPropertyName("stream_index")]
    public int? StreamIndex { get; set; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "source_type")]
[JsonDerivedType(typeof(LocalSource), "local")]
[JsonDerivedType(typeof(LavfiSource), "lavfi")]
[JsonDerivedType(typeof(HttpSource), "http")]
[JsonDerivedType(typeof(ScriptSource), "script")]
internal abstract class PlayoutSource;

/// <summary>
/// A program whose output is an MPEG-TS stream. Used for live feeds: with <see cref="IsLive"/> ErsatzTV joins the
/// stream where it is, where an http source would be read up to the scheduled offset first (never, for an endless stream).
/// </summary>
internal sealed class ScriptSource : PlayoutSource
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("args")]
    public List<string> Args { get; set; } = [];

    [JsonPropertyName("is_live")]
    public bool? IsLive { get; set; }

    [JsonPropertyName("probe_hint")]
    public System.Text.Json.Nodes.JsonNode? ProbeHint { get; set; }
}

/// <summary>A stream or file fetched over HTTP(S), such as a plugin's live feed.</summary>
internal sealed class HttpSource : PlayoutSource
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;

    [JsonPropertyName("in_point_ms")]
    public long? InPointMs { get; set; }

    [JsonPropertyName("out_point_ms")]
    public long? OutPointMs { get; set; }

    /// <summary>Lets ErsatzTV skip probing, so a live stream is opened once rather than twice.</summary>
    [JsonPropertyName("probe_hint")]
    public System.Text.Json.Nodes.JsonNode? ProbeHint { get; set; }
}

internal sealed class LocalSource : PlayoutSource
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("in_point_ms")]
    public long? InPointMs { get; set; }

    /// <summary>Set for trimmed filler so playback stops on the boundary instead of running long.</summary>
    [JsonPropertyName("out_point_ms")]
    public long? OutPointMs { get; set; }
}

internal sealed class LavfiSource : PlayoutSource
{
    [JsonPropertyName("params")]
    public string Params { get; set; } = string.Empty;
}
