using System.Text.Json;
using System.Text.Json.Serialization;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.Providers.ErsatzTv;

/// <summary>
/// A channel's audio and subtitle choices (<see cref="ChannelTracksConfig"/>) on their way to ErsatzTV: carried on the
/// channel's schedule as an annotation, turned into per-programme track selections in the playout, and into the
/// channel's subtitle mode in its encoding overlay.
/// </summary>
public static class ErsatzTvTracks
{
    /// <summary>Channel annotation carrying the channel's <see cref="ChannelTracksConfig"/> as JSON.</summary>
    public const string Annotation = "ersatztv.tracks";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public static string? ToAnnotation(ChannelTracksConfig? tracks) =>
        tracks is null || tracks.IsEmpty ? null : JsonSerializer.Serialize(tracks, Json);

    public static ChannelTracksConfig? FromAnnotation(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ChannelTracksConfig>(json, Json) is { IsEmpty: false } tracks ? tracks : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ChannelTracksConfig? For(ChannelSchedule schedule) =>
        schedule.Annotations?.TryGetValue(Annotation, out string? json) == true ? FromAnnotation(json) : null;

    /// <summary>
    /// ErsatzTV's <c>normalization.subtitle.mode</c> for the channel: <c>burn</c> or <c>convert</c> (a WebVTT track in the
    /// stream); null when the channel takes no subtitles, which leaves channel.json's own setting.
    /// </summary>
    public static string? SubtitleMode(ChannelTracksConfig? tracks) => tracks?.EffectiveSubtitles switch
    {
        Core.Configuration.SubtitleMode.BurnIn => "burn",
        Core.Configuration.SubtitleMode.Selectable => "convert",
        _ => null
    };
}
