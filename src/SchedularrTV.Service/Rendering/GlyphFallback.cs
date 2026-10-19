using System.Globalization;
using System.Text;
using SkiaSharp;

namespace SchedularrTV.Service.Rendering;

/// <summary>
/// Swaps characters a font can't draw for ones it can, so text never shows empty boxes: "&amp;" becomes "and", an accented
/// letter its plain one ("Pokémon" → "Pokemon"), curly quotes straight ones, and so on. Characters the font has are left
/// alone, so a font with "é" keeps it.
/// </summary>
public static class GlyphFallback
{
    // What to say instead, best first; the first whose every character the font has is used. Words are written in
    // lower case and match the text's case when it's all capitals.
    private static readonly Dictionary<int, string[]> Replacements = new()
    {
        ['&'] = ["and", "+"],
        ['@'] = ["at"],
        ['%'] = ["percent"],
        ['+'] = ["plus"],
        ['#'] = ["No.", "number"],
        ['‘'] = ["'"], ['’'] = ["'"], ['‚'] = [","], ['‛'] = ["'"], ['′'] = ["'"],
        ['“'] = ["\""], ['”'] = ["\""], ['„'] = ["\""], ['″'] = ["\""],
        ['«'] = ["\"", "<<"], ['»'] = ["\"", ">>"], ['‹'] = ["'", "<"], ['›'] = ["'", ">"],
        ['–'] = ["-"], ['—'] = ["–", "-"], ['‒'] = ["-"], ['―'] = ["-"], ['−'] = ["-"],
        ['‐'] = ["-"], ['‑'] = ["-"],
        ['…'] = ["..."],
        ['•'] = ["·", "-"], ['·'] = ["-"],
        ['™'] = ["TM"], ['©'] = ["(C)"], ['®'] = ["(R)"],
        ['½'] = ["1/2"], ['¼'] = ["1/4"], ['¾'] = ["3/4"],
        ['×'] = ["x"], ['÷'] = ["/"],
        ['°'] = [" degrees"],
        ['ß'] = ["ss"], ['ẞ'] = ["SS"],
        ['æ'] = ["ae"], ['Æ'] = ["AE"], ['œ'] = ["oe"], ['Œ'] = ["OE"],
        ['ø'] = ["o"], ['Ø'] = ["O"], ['ł'] = ["l"], ['Ł'] = ["L"],
        ['đ'] = ["d"], ['Đ'] = ["D"], ['ð'] = ["d"], ['Ð'] = ["D"],
        ['þ'] = ["th"], ['Þ'] = ["Th"], ['ı'] = ["i"],
        ['¡'] = ["!"], ['¿'] = ["?"],
        ['€'] = ["EUR"], ['£'] = ["GBP"], ['¥'] = ["JPY"],
        [' '] = [" "], [' '] = [" "], [' '] = [" "], [' '] = [" "], [' '] = [" "],
        ['⁄'] = ["/"]
    };

    // Said as a word, so it gets a space either side when it's against other characters ("R&B" → "R and B").
    private static readonly HashSet<int> Words = ['&', '@', '+'];

    // Nothing to draw: dropped when the font hasn't got them (rather than drawn as a box).
    private static readonly HashSet<int> Invisible = ['​', '‌', '‍', '⁠', '﻿', '­'];

    /// <summary>
    /// <paramref name="text"/> with every character <paramref name="typeface"/> can't draw swapped for what it can: a
    /// listed stand-in, or the letter without its accents (or another compatible form, like "ﬁ" → "fi"). A character
    /// with no stand-in the font has is left as it is.
    /// </summary>
    public static string Apply(string text, SKTypeface typeface)
    {
        using var font = new SKFont(typeface);
        return Apply(text, codepoint => font.ContainsGlyph(codepoint));
    }

    /// <summary>As <see cref="Apply(string, SKTypeface)"/>, with whether the font has a character given by <paramref name="has"/>.</summary>
    public static string Apply(string text, Func<int, bool> has)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // Nearly always nothing's missing, so the text is only rebuilt when something is.
        bool missing = false;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (!IsControl(rune) && !has(rune.Value))
            {
                missing = true;
                break;
            }
        }

        if (!missing)
        {
            return text;
        }

        bool capitals = IsAllCapitals(text);
        StringBuilder result = new(text.Length + 8);
        Rune[] runes = [.. text.EnumerateRunes()];
        for (int index = 0; index < runes.Length; index++)
        {
            Rune rune = runes[index];
            if (IsControl(rune) || has(rune.Value))
            {
                result.Append(rune.ToString());
                continue;
            }

            if (Invisible.Contains(rune.Value))
            {
                continue;
            }

            string? instead = StandIn(rune, has, capitals);
            if (instead is null)
            {
                result.Append(rune.ToString()); // nothing better; the font's own missing-glyph mark
                continue;
            }

            if (Words.Contains(rune.Value) && instead.Any(char.IsLetter))
            {
                if (result.Length > 0 && !char.IsWhiteSpace(result[^1])) result.Append(' ');
                result.Append(instead);
                if (index + 1 < runes.Length && !Rune.IsWhiteSpace(runes[index + 1])) result.Append(' ');
                continue;
            }

            result.Append(instead);
        }

        return result.ToString();
    }

    /// <summary>What to draw for a character the font hasn't got, or null when there's nothing it can draw instead.</summary>
    private static string? StandIn(Rune rune, Func<int, bool> has, bool capitals)
    {
        bool Drawable(string candidate) => candidate.EnumerateRunes().All(part => IsControl(part) || has(part.Value));

        if (Replacements.TryGetValue(rune.Value, out string[]? options))
        {
            foreach (string option in options)
            {
                string cased = capitals ? option.ToUpperInvariant() : option;
                if (Drawable(cased)) return cased;
                if (Drawable(option)) return option;
            }
        }

        // An accented letter without its accents, or a compatibility form ("ﬁ" → "fi", "²" → "2", fullwidth → plain).
        string plain = Plain(rune.ToString());
        if (plain.Length > 0 && plain != rune.ToString())
        {
            if (Drawable(plain))
            {
                return plain;
            }

            // A decomposed letter may have its own stand-in ("ǣ" → "æ" → "ae").
            StringBuilder built = new();
            foreach (Rune part in plain.EnumerateRunes())
            {
                if (has(part.Value)) built.Append(part.ToString());
                else if (part != rune && StandIn(part, has, capitals) is { } inner) built.Append(inner);
                else return null;
            }

            return built.ToString();
        }

        return null;
    }

    /// <summary>The text decomposed (compatibility forms too) with its accents and other combining marks taken off.</summary>
    public static string Plain(string text)
    {
        string decomposed = text.Normalize(NormalizationForm.FormKD);
        StringBuilder plain = new(decomposed.Length);
        foreach (char c in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
            {
                plain.Append(c);
            }
        }

        return plain.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Whether the text is written in capitals ("TOM &amp; JERRY"): no lower-case letters, and at least three capitals in
    /// a row, so initials like "R&amp;B" or "AT&amp;T" on their own don't count.
    /// </summary>
    private static bool IsAllCapitals(string text)
    {
        if (text.Any(char.IsLower))
        {
            return false;
        }

        int run = 0;
        foreach (char c in text)
        {
            run = char.IsUpper(c) ? run + 1 : 0;
            if (run >= 3)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsControl(Rune rune) => Rune.IsControl(rune) || rune.Value is '\n' or '\r' or '\t';
}
