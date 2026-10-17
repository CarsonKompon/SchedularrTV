namespace SchedularrTV.Core.Configuration;

/// <summary>What a channel does with a programme's subtitles.</summary>
public enum SubtitleMode
{
    /// <summary>No subtitles (ErsatzTV's own behaviour).</summary>
    Off,

    /// <summary>
    /// A subtitle track viewers can switch on in their player (a WebVTT rendition in the HLS stream). Only text
    /// subtitles (SRT, ASS, WebVTT…) can be carried this way; a programme with only picture subtitles (PGS, DVD) has none.
    /// </summary>
    Selectable,

    /// <summary>Drawn into the picture, so every viewer sees them. Works with text and picture subtitles alike.</summary>
    BurnIn
}

/// <summary>
/// One channel's audio and subtitle choices (<c>tracks:</c> in the channel's YAML). Languages are ISO 639 codes or
/// English names (<c>en</c>, <c>eng</c>, <c>English</c>), tried in order.
/// </summary>
public sealed class ChannelTracksConfig
{
    /// <summary>
    /// Preferred audio languages, best first. A programme with none of them plays its default audio track. Empty plays
    /// the default track (the file's own choice, rather than ErsatzTV's first).
    /// </summary>
    public List<string> AudioLanguages { get; set; } = [];

    /// <summary>Whether subtitles are included, and how. Null is <see cref="SubtitleMode.Off"/>.</summary>
    public SubtitleMode? Subtitles { get; set; }

    /// <summary>
    /// Preferred subtitle languages, best first. Empty uses <see cref="AudioLanguages"/>, else the file's default (or
    /// first) subtitle track.
    /// </summary>
    public List<string> SubtitleLanguages { get; set; } = [];

    /// <summary>
    /// Subtitles only when the programme's audio isn't in a subtitle language (a foreign-language film, say). Programmes
    /// already in that language get only a forced track (signs and foreign dialogue), when they have one.
    /// </summary>
    public bool? OnlyForForeignAudio { get; set; }

    /// <summary>Leaves out subtitles for the deaf and hard of hearing (SDH) when another track in the language exists.</summary>
    public bool? AvoidHearingImpaired { get; set; }

    [YamlDotNet.Serialization.YamlIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public SubtitleMode EffectiveSubtitles => Subtitles ?? SubtitleMode.Off;

    /// <summary>True when nothing is set, so the section can be left out of the YAML.</summary>
    [YamlDotNet.Serialization.YamlIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => AudioLanguages.All(string.IsNullOrWhiteSpace) && EffectiveSubtitles == SubtitleMode.Off
                           && SubtitleLanguages.All(string.IsNullOrWhiteSpace) && OnlyForForeignAudio is null && AvoidHearingImpaired is null;
}
