using System.Globalization;
using SkiaSharp;

namespace SchedularrTV.Service.Rendering;

/// <summary>
/// Fits text layers' lines into their boxes, for both generated promos and overlays: the text shrinks until it fits,
/// and with word wrap its lines break between words to fill the box's width first. Letter and line spacing are part of
/// the fit, so spaced-out text still fits its box.
/// </summary>
public static class TextFit
{
    /// <summary>The smallest size text shrinks to.</summary>
    public const float SmallestSize = 8;

    /// <summary>
    /// Shrinks <paramref name="font"/> until <paramref name="lines"/> fit in <paramref name="bounds"/> and returns the lines
    /// as they're drawn: wrapped to the box's width at that size when <paramref name="wrap"/> is set, else as given.
    /// <paramref name="heightAllowance"/> lets single-line text use a little more than the box's height (overlays always have).
    /// </summary>
    /// <param name="letterSpacing">Extra space between letters, as a fraction of the font size (0 is the font's own; negative tightens).</param>
    /// <param name="lineSpacing">Distance from one line to the next, as a multiple of the font's own (1 is the font's own).</param>
    public static IReadOnlyList<string> Fit(IReadOnlyList<string> lines, SKRect bounds, SKFont font, SKPaint paint, bool wrap,
        float heightAllowance = 1f, float letterSpacing = 0, float lineSpacing = 1)
    {
        while (true)
        {
            IReadOnlyList<string> laid = wrap ? Wrap(lines, bounds.Width, font, paint, letterSpacing) : lines;
            bool fits = laid.Count == 0
                || (laid.Max(line => Measure(line, font, paint, letterSpacing)) <= bounds.Width
                    && Block(font, laid.Count, lineSpacing) <= bounds.Height * heightAllowance);
            if (fits || font.Size <= SmallestSize)
            {
                return laid;
            }

            font.Size *= 0.94f;
        }
    }

    /// <summary>
    /// Each line broken between words so none is wider than <paramref name="width"/>. A word wider than the box on its
    /// own is broken between letters. Blank lines are kept.
    /// </summary>
    public static IReadOnlyList<string> Wrap(IEnumerable<string> lines, float width, SKFont font, SKPaint paint, float letterSpacing = 0)
    {
        List<string> wrapped = [];
        foreach (string line in lines)
        {
            if (line.Length == 0 || Measure(line, font, paint, letterSpacing) <= width)
            {
                wrapped.Add(line);
                continue;
            }

            string current = string.Empty;
            foreach (string word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = current.Length == 0 ? word : $"{current} {word}";
                if (Measure(candidate, font, paint, letterSpacing) <= width)
                {
                    current = candidate;
                    continue;
                }

                if (current.Length > 0)
                {
                    wrapped.Add(current);
                }

                current = word;

                // A word too long for a line of its own is split between letters.
                while (current.Length > 1 && Measure(current, font, paint, letterSpacing) > width)
                {
                    int take = current.Length - 1;
                    while (take > 1 && Measure(current[..take], font, paint, letterSpacing) > width)
                    {
                        take--;
                    }

                    wrapped.Add(current[..take]);
                    current = current[take..];
                }
            }

            if (current.Length > 0)
            {
                wrapped.Add(current);
            }
        }

        return wrapped;
    }

    /// <summary>How wide <paramref name="line"/> draws, with <paramref name="letterSpacing"/> between its letters.</summary>
    public static float Measure(string line, SKFont font, SKPaint paint, float letterSpacing = 0)
    {
        float width = font.MeasureText(line, paint);
        if (letterSpacing == 0)
        {
            return width;
        }

        int letters = Letters(line).Count;
        return letters < 2 ? width : width + letterSpacing * font.Size * (letters - 1);
    }

    /// <summary>The distance from one line's baseline to the next.</summary>
    public static float LineHeight(SKFont font, float lineSpacing) => font.Spacing * Clean(lineSpacing, 1);

    /// <summary>How tall <paramref name="count"/> lines are, from the first's top to the last's bottom.</summary>
    public static float Block(SKFont font, int count, float lineSpacing) =>
        count <= 0 ? 0 : font.Spacing + LineHeight(font, lineSpacing) * (count - 1);

    /// <summary>
    /// Draws <paramref name="line"/> like <see cref="SKCanvas.DrawText(string, float, float, SKTextAlign, SKFont, SKPaint)"/>,
    /// with <paramref name="letterSpacing"/> between its letters (each placed where the font's own spacing, kerning
    /// included, puts it, plus the extra).
    /// </summary>
    public static void Draw(SKCanvas canvas, string line, float x, float y, SKTextAlign align, SKFont font, SKPaint paint, float letterSpacing = 0)
    {
        if (letterSpacing == 0 || line.Length < 2)
        {
            canvas.DrawText(line, x, y, align, font, paint);
            return;
        }

        float width = Measure(line, font, paint, letterSpacing);
        float left = align switch
        {
            SKTextAlign.Center => x - width / 2,
            SKTextAlign.Right => x - width,
            _ => x
        };
        float extra = letterSpacing * font.Size;
        IReadOnlyList<(int Start, string Letter)> letters = Letters(line);
        for (int index = 0; index < letters.Count; index++)
        {
            (int start, string letter) = letters[index];
            float offset = start == 0 ? 0 : font.MeasureText(line[..start], paint);
            canvas.DrawText(letter, left + offset + extra * index, y, SKTextAlign.Left, font, paint);
        }
    }

    /// <summary>A spacing setting fit to use: finite and within reason, else <paramref name="fallback"/>.</summary>
    public static float Clean(float value, float fallback) => float.IsFinite(value) && value > 0 ? Math.Clamp(value, 0.3f, 5f) : fallback;

    /// <summary>A letter-spacing setting as a fraction of the font size, from a percentage (null or nonsense is 0).</summary>
    public static float LetterSpacing(float? percent) => percent is { } value && float.IsFinite(value) ? Math.Clamp(value, -50, 200) / 100 : 0;

    /// <summary>A line-spacing setting as a multiple (null or nonsense is 1).</summary>
    public static float LineSpacing(float? multiple) => multiple is { } value ? Clean(value, 1) : 1;

    // Letters as people see them: a letter with its accents, or an emoji, stays together.
    private static IReadOnlyList<(int Start, string Letter)> Letters(string line)
    {
        List<(int, string)> letters = [];
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(line);
        while (elements.MoveNext())
        {
            letters.Add((elements.ElementIndex, elements.GetTextElement()));
        }

        return letters;
    }
}
