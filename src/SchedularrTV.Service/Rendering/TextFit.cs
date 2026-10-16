using SkiaSharp;

namespace SchedularrTV.Service.Rendering;

/// <summary>
/// Fits text layers' lines into their boxes, for both generated promos and overlays: the text shrinks until it fits,
/// and with word wrap its lines break between words to fill the box's width first.
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
    public static IReadOnlyList<string> Fit(IReadOnlyList<string> lines, SKRect bounds, SKFont font, SKPaint paint, bool wrap,
        float heightAllowance = 1f)
    {
        while (true)
        {
            IReadOnlyList<string> laid = wrap ? Wrap(lines, bounds.Width, font, paint) : lines;
            bool fits = laid.Count == 0
                || (laid.Max(line => font.MeasureText(line, paint)) <= bounds.Width
                    && font.Spacing * laid.Count <= bounds.Height * heightAllowance);
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
    public static IReadOnlyList<string> Wrap(IEnumerable<string> lines, float width, SKFont font, SKPaint paint)
    {
        List<string> wrapped = [];
        foreach (string line in lines)
        {
            if (line.Length == 0 || font.MeasureText(line, paint) <= width)
            {
                wrapped.Add(line);
                continue;
            }

            string current = string.Empty;
            foreach (string word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = current.Length == 0 ? word : $"{current} {word}";
                if (font.MeasureText(candidate, paint) <= width)
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
                while (current.Length > 1 && font.MeasureText(current, paint) > width)
                {
                    int take = current.Length - 1;
                    while (take > 1 && font.MeasureText(current[..take], paint) > width)
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
}
