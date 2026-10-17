using System.Collections.Frozen;

namespace SchedularrTV.Providers.ErsatzTv;

/// <summary>
/// Compares languages however they're written: files tag streams with ISO 639-2 codes (<c>eng</c>, and for some
/// languages either the bibliographic <c>fre</c> or the terminology <c>fra</c>), people type <c>en</c> or <c>English</c>.
/// </summary>
public static class LanguageCodes
{
    // Two-letter code, three-letter codes (terminology first), English name.
    private static readonly (string Two, string[] Three, string Name)[] Known =
    [
        ("ar", ["ara"], "arabic"), ("bg", ["bul"], "bulgarian"), ("bn", ["ben"], "bengali"), ("ca", ["cat"], "catalan"),
        ("cs", ["ces", "cze"], "czech"), ("cy", ["cym", "wel"], "welsh"), ("da", ["dan"], "danish"), ("de", ["deu", "ger"], "german"),
        ("el", ["ell", "gre"], "greek"), ("en", ["eng"], "english"), ("es", ["spa"], "spanish"), ("et", ["est"], "estonian"),
        ("eu", ["eus", "baq"], "basque"), ("fa", ["fas", "per"], "persian"), ("fi", ["fin"], "finnish"), ("fil", ["fil"], "filipino"),
        ("fr", ["fra", "fre"], "french"), ("ga", ["gle"], "irish"), ("gl", ["glg"], "galician"), ("he", ["heb"], "hebrew"),
        ("hi", ["hin"], "hindi"), ("hr", ["hrv"], "croatian"), ("hu", ["hun"], "hungarian"), ("hy", ["hye", "arm"], "armenian"),
        ("id", ["ind"], "indonesian"), ("is", ["isl", "ice"], "icelandic"), ("it", ["ita"], "italian"), ("ja", ["jpn"], "japanese"),
        ("ka", ["kat", "geo"], "georgian"), ("kk", ["kaz"], "kazakh"), ("ko", ["kor"], "korean"), ("lt", ["lit"], "lithuanian"),
        ("lv", ["lav"], "latvian"), ("mk", ["mkd", "mac"], "macedonian"), ("ms", ["msa", "may"], "malay"), ("mt", ["mlt"], "maltese"),
        ("my", ["mya", "bur"], "burmese"), ("nb", ["nob"], "norwegian bokmal"), ("nl", ["nld", "dut"], "dutch"),
        ("no", ["nor"], "norwegian"), ("pl", ["pol"], "polish"), ("pt", ["por"], "portuguese"), ("ro", ["ron", "rum"], "romanian"),
        ("ru", ["rus"], "russian"), ("sk", ["slk", "slo"], "slovak"), ("sl", ["slv"], "slovenian"), ("sq", ["sqi", "alb"], "albanian"),
        ("sr", ["srp"], "serbian"), ("sv", ["swe"], "swedish"), ("sw", ["swa"], "swahili"), ("ta", ["tam"], "tamil"),
        ("te", ["tel"], "telugu"), ("th", ["tha"], "thai"), ("tl", ["tgl"], "tagalog"), ("tr", ["tur"], "turkish"),
        ("uk", ["ukr"], "ukrainian"), ("ur", ["urd"], "urdu"), ("vi", ["vie"], "vietnamese"), ("zh", ["zho", "chi"], "chinese"),
        ("yue", ["yue"], "cantonese"), ("cmn", ["cmn"], "mandarin")
    ];

    private static readonly FrozenDictionary<string, string> Canonical = BuildCanonical();

    /// <summary>The choices the channel settings offer: code and name.</summary>
    public static IReadOnlyList<(string Code, string Name)> Common { get; } =
    [
        .. Known.Select(language => (language.Three[0], char.ToUpperInvariant(language.Name[0]) + language.Name[1..]))
            .OrderBy(language => language.Item2, StringComparer.Ordinal)
    ];

    private static FrozenDictionary<string, string> BuildCanonical()
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string two, string[] three, string name) in Known)
        {
            string canonical = three[0];
            map.TryAdd(two, canonical);
            map.TryAdd(name, canonical);
            foreach (string code in three)
            {
                map.TryAdd(code, canonical);
            }
        }

        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One spelling for a language (<c>en</c>, <c>eng</c> and <c>English</c> are all <c>eng</c>); a region is dropped
    /// (<c>en-US</c> is <c>eng</c>). Unknown codes come back lower-cased; blank and "undetermined" are null.
    /// </summary>
    public static string? Normalize(string? language)
    {
        string? text = language?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (Canonical.TryGetValue(text, out string? known))
        {
            return known;
        }

        int region = text.IndexOfAny(['-', '_']);
        if (region > 0 && Canonical.TryGetValue(text[..region], out known))
        {
            return known;
        }

        string lower = text.ToLowerInvariant();
        return lower is "und" or "unknown" or "undetermined" or "zxx" or "mis" or "mul" ? null : lower;
    }

    public static bool Same(string? a, string? b) =>
        Normalize(a) is { } left && Normalize(b) is { } right && left == right;

    /// <summary>A display name for a code, or the code itself when it isn't one of the common languages.</summary>
    public static string Name(string code) =>
        Normalize(code) is { } canonical && Common.FirstOrDefault(language => language.Code == canonical) is { Name: { } name }
            ? name
            : code;
}
