using SchedularrTV.Core.Configuration;

namespace SchedularrTV.Providers.ErsatzTv;

public enum MediaStreamKind
{
    Audio,
    Subtitle
}

/// <summary>An audio or subtitle stream in a media file, or a subtitle file beside it.</summary>
/// <param name="Index">ffprobe's stream index within its file (what ErsatzTV calls <c>stream_index</c>); 0 for a sidecar.</param>
/// <param name="ExternalPath">For a subtitle file beside the video (<c>Movie.en.srt</c>): its path; null for an embedded stream.</param>
public sealed record MediaStream(
    int Index,
    MediaStreamKind Kind,
    string? Codec,
    string? Language,
    string? Title = null,
    bool IsDefault = false,
    bool IsForced = false,
    bool IsHearingImpaired = false,
    string? ExternalPath = null)
{
    // Picture-based subtitle codecs: ErsatzTV can only burn these in.
    private static readonly HashSet<string> PictureCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "hdmv_pgs_subtitle", "pgssub", "dvd_subtitle", "dvdsub", "dvb_subtitle", "dvbsub", "xsub", "vobsub"
    };

    /// <summary>Text subtitles (SRT, ASS, WebVTT…), which can be a selectable track as well as burnt in.</summary>
    public bool IsTextSubtitle => Kind == MediaStreamKind.Subtitle && !PictureCodecs.Contains(Codec ?? string.Empty);
}

/// <summary>The streams chosen for one programme; null leaves ErsatzTV's own choice (audio) or none (subtitles).</summary>
public sealed record TrackChoice(MediaStream? Audio, MediaStream? Subtitle)
{
    public static readonly TrackChoice None = new(null, null);
}

/// <summary>Picks a programme's audio and subtitle streams from a channel's preferences.</summary>
public static class TrackChooser
{
    public static TrackChoice Choose(IReadOnlyList<MediaStream> streams, ChannelTracksConfig? preferences)
    {
        if (preferences is null || preferences.IsEmpty || streams.Count == 0)
        {
            return TrackChoice.None;
        }

        List<string> audioLanguages = Languages(preferences.AudioLanguages);
        List<MediaStream> audio = [.. streams.Where(stream => stream.Kind == MediaStreamKind.Audio && stream.ExternalPath is null)];
        MediaStream? chosenAudio = ChooseAudio(audio, audioLanguages);

        MediaStream? subtitle = null;
        if (preferences.EffectiveSubtitles != SubtitleMode.Off)
        {
            List<string> subtitleLanguages = Languages(preferences.SubtitleLanguages);
            if (subtitleLanguages.Count == 0)
            {
                subtitleLanguages = audioLanguages;
            }

            // A selectable track must be text; picture subtitles can only be burnt in.
            List<MediaStream> candidates = [.. streams.Where(stream => stream.Kind == MediaStreamKind.Subtitle
                && (preferences.EffectiveSubtitles == SubtitleMode.BurnIn || stream.IsTextSubtitle))];

            // The audio this programme ends up with: the chosen stream, else the file's default (ErsatzTV plays the first).
            string? playingLanguage = (chosenAudio ?? audio.FirstOrDefault())?.Language;
            bool understood = playingLanguage is not null && subtitleLanguages.Count > 0
                              && subtitleLanguages.Contains(LanguageCodes.Normalize(playingLanguage) ?? string.Empty);
            bool forcedOnly = preferences.OnlyForForeignAudio == true && understood;

            subtitle = ChooseSubtitle(candidates, subtitleLanguages, forcedOnly, preferences.AvoidHearingImpaired == true);
        }

        return new TrackChoice(chosenAudio, subtitle);
    }

    private static MediaStream? ChooseAudio(List<MediaStream> audio, List<string> languages)
    {
        if (audio.Count <= 1)
        {
            return null;
        }

        foreach (string language in languages)
        {
            // Within a language: the file's default first, then a commentary-free one, in file order.
            MediaStream? match = audio
                .Where(stream => LanguageCodes.Normalize(stream.Language) == language)
                .OrderByDescending(stream => stream.IsDefault)
                .ThenBy(stream => IsCommentary(stream))
                .FirstOrDefault();
            if (match is not null)
            {
                return match;
            }
        }

        // None of the languages: the file's own default (ErsatzTV on its own would play the first).
        return audio.FirstOrDefault(stream => stream.IsDefault);
    }

    private static MediaStream? ChooseSubtitle(List<MediaStream> subtitles, List<string> languages, bool forcedOnly, bool avoidHearingImpaired)
    {
        if (subtitles.Count == 0)
        {
            return null;
        }

        IEnumerable<MediaStream> Ranked(IEnumerable<MediaStream> streams) => streams
            .OrderBy(stream => forcedOnly ? 0 : stream.IsForced ? 1 : 0)   // full subtitles before signs-only, unless only forced is wanted
            .ThenBy(stream => avoidHearingImpaired && stream.IsHearingImpaired ? 1 : 0)
            .ThenByDescending(stream => stream.IsDefault)
            .ThenBy(stream => stream.IsTextSubtitle ? 0 : 1)                // text burns in more crisply than pictures
            .ThenBy(stream => stream.ExternalPath is null ? 0 : 1);

        if (forcedOnly)
        {
            // The programme is already in a language the channel wants: only signs and foreign dialogue.
            foreach (string language in languages)
            {
                if (Ranked(subtitles.Where(stream => stream.IsForced && LanguageCodes.Normalize(stream.Language) == language)).FirstOrDefault() is { } forced)
                {
                    return forced;
                }
            }

            return null;
        }

        foreach (string language in languages)
        {
            if (Ranked(subtitles.Where(stream => LanguageCodes.Normalize(stream.Language) == language)).FirstOrDefault() is { } match)
            {
                return match;
            }
        }

        // No language asked for: the file's default subtitles, else the first full ones.
        return languages.Count == 0
            ? Ranked(subtitles).FirstOrDefault(stream => stream.IsDefault) ?? Ranked(subtitles.Where(stream => !stream.IsForced)).FirstOrDefault()
            : null;
    }

    private static bool IsCommentary(MediaStream stream) =>
        stream.Title?.Contains("commentary", StringComparison.OrdinalIgnoreCase) == true;

    private static List<string> Languages(IEnumerable<string> configured) =>
        [.. configured.Select(LanguageCodes.Normalize).OfType<string>().Distinct(StringComparer.Ordinal)];
}
