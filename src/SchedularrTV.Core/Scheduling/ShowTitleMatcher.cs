using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FuzzySharp;

namespace SchedularrTV.Core.Scheduling;

/// <summary>
/// Matches a typed show title or block name against the real one, forgiving how people write them: case,
/// punctuation, accents, "&amp;" / "+" / "n" for "and", "The" left out, and small typos.
/// </summary>
public static partial class ShowTitleMatcher
{
    /// <summary>Lowest fuzzy score (0-100) accepted for a typo, and how far ahead of the runner-up it must be.</summary>
    private const int MinimumScore = 85;
    private const int Margin = 5;
    /// <summary>Shorter names (letters and digits only) must match exactly once normalized; a typo there is too likely another show.</summary>
    private const int MinimumFuzzyLength = 6;

    /// <summary>
    /// The candidate whose title best matches <paramref name="name"/>: an exact (case-insensitive) title first, then
    /// one equal once normalized, then a close fuzzy match that isn't a near-tie. Null when nothing is close enough.
    /// </summary>
    public static T? Find<T>(string? name, IEnumerable<T> candidates, Func<T, string?> titleOf) where T : class
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string wanted = name.Trim();
        List<(T Item, string Title)> list = [.. candidates
            .Select(candidate => (Item: candidate, Title: titleOf(candidate)?.Trim() ?? string.Empty))
            .Where(candidate => candidate.Title.Length > 0)];
        if (list.Count == 0)
        {
            return null;
        }

        foreach ((T item, string title) in list)
        {
            if (string.Equals(title, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        string words = Words(wanted);
        string key = words.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (key.Length == 0)
        {
            return null;
        }

        List<(T Item, string Words)> normalized = [.. list.Select(candidate => (candidate.Item, Words: Words(candidate.Title)))];
        foreach ((T item, string candidateWords) in normalized)
        {
            if (candidateWords.Replace(" ", string.Empty, StringComparison.Ordinal) == key)
            {
                return item;
            }
        }

        if (key.Length < MinimumFuzzyLength)
        {
            return null;
        }

        // Numbers must agree, so "Ben 10" never stands in for "Ben 100" or a sequel.
        string numbers = Numbers(words);
        List<(T Item, int Score)> ranked = [.. normalized
            .Where(candidate => candidate.Words.Length > 0 && Numbers(candidate.Words) == numbers)
            .Select(candidate => (candidate.Item, Score: Math.Max(Fuzz.Ratio(words, candidate.Words), Fuzz.TokenSortRatio(words, candidate.Words))))
            .OrderByDescending(candidate => candidate.Score)];
        return ranked.Count > 0 && ranked[0].Score >= MinimumScore && (ranked.Count == 1 || ranked[0].Score - ranked[1].Score >= Margin)
            ? ranked[0].Item
            : null;
    }

    /// <summary>
    /// A title as lower-case words without accents or punctuation, with "&amp;", "+" and "n" read as "and", every
    /// "the" dropped, and a leading "a" / "an" dropped: "The Ed, Edd n Eddy" → "ed edd and eddy".
    /// </summary>
    public static string Words(string? title)
    {
        string decomposed = (title ?? string.Empty).Normalize(NormalizationForm.FormD);
        var plain = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                plain.Append(c);
            }
        }

        string spaced = plain.ToString().ToLowerInvariant().Replace("&", " and ", StringComparison.Ordinal).Replace("+", " and ", StringComparison.Ordinal);
        List<string> words = [.. NotWord().Split(spaced).Where(word => word.Length > 0).Select(word => word == "n" ? "and" : word)];
        if (words.Count > 1 && words[0] is "a" or "an")
        {
            words.RemoveAt(0);
        }

        if (words.Count(word => word != "the") > 0)
        {
            words.RemoveAll(word => word == "the");
        }

        return string.Join(' ', words);
    }

    private static string Numbers(string words) => string.Join(",", Digits().Matches(words).Select(match => match.Value.TrimStart('0')));

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NotWord();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}
