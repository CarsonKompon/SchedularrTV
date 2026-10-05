namespace SchedularrTV.Service.Editor;

public enum DiffKind
{
    Same,
    Removed,
    Added
}

/// <summary>One line of a diff, with its line number on the side(s) it's on.</summary>
public sealed record DiffLine(DiffKind Kind, string Text, int? OldNumber, int? NewNumber);

/// <summary>A run of lines to show: changes with a little of what's around them, or a gap of unchanged lines left out.</summary>
public sealed record DiffHunk(IReadOnlyList<DiffLine> Lines, int SkippedBefore);

/// <summary>A line-by-line diff of two texts (longest common subsequence), for comparing schedule backups.</summary>
public static class LineDiff
{
    /// <summary>Larger than this (lines × lines, after the common start and end are set aside) isn't compared.</summary>
    public const long MaxCells = 4_000_000;

    /// <summary>Every line of both texts in order, marked same, removed (only in <paramref name="before"/>) or added; null when they're too big to compare.</summary>
    public static IReadOnlyList<DiffLine>? Compare(string before, string after)
    {
        string[] a = Lines(before);
        string[] b = Lines(after);

        int head = 0;
        while (head < a.Length && head < b.Length && a[head] == b[head])
        {
            head++;
        }

        int tail = 0;
        while (tail < a.Length - head && tail < b.Length - head && a[a.Length - 1 - tail] == b[b.Length - 1 - tail])
        {
            tail++;
        }

        int n = a.Length - head - tail;
        int m = b.Length - head - tail;
        if ((long)(n + 1) * (m + 1) > MaxCells)
        {
            return null;
        }

        List<DiffLine> lines = new(a.Length + b.Length);
        for (int i = 0; i < head; i++)
        {
            lines.Add(new DiffLine(DiffKind.Same, a[i], i + 1, i + 1));
        }

        // lengths[i, j]: the longest common run of a[head + i..] and b[head + j..] (the middle parts).
        int width = m + 1;
        int[] lengths = new int[(n + 1) * width];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                lengths[i * width + j] = a[head + i] == b[head + j]
                    ? lengths[(i + 1) * width + j + 1] + 1
                    : Math.Max(lengths[(i + 1) * width + j], lengths[i * width + j + 1]);
            }
        }

        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && a[head + x] == b[head + y])
            {
                lines.Add(new DiffLine(DiffKind.Same, a[head + x], head + x + 1, head + y + 1));
                x++;
                y++;
            }
            // Where a line was replaced, what went comes before what came, as diffs usually read.
            else if (y < m && (x == n || lengths[x * width + y + 1] > lengths[(x + 1) * width + y]))
            {
                lines.Add(new DiffLine(DiffKind.Added, b[head + y], null, head + y + 1));
                y++;
            }
            else
            {
                lines.Add(new DiffLine(DiffKind.Removed, a[head + x], head + x + 1, null));
                x++;
            }
        }

        for (int i = 0; i < tail; i++)
        {
            int ai = a.Length - tail + i, bi = b.Length - tail + i;
            lines.Add(new DiffLine(DiffKind.Same, a[ai], ai + 1, bi + 1));
        }

        return lines;
    }

    /// <summary>The changed lines with <paramref name="context"/> unchanged ones either side; the rest is left out.</summary>
    public static IReadOnlyList<DiffHunk> Hunks(IReadOnlyList<DiffLine> lines, int context = 3)
    {
        List<DiffHunk> hunks = [];
        bool[] shown = new bool[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind == DiffKind.Same)
            {
                continue;
            }

            for (int j = Math.Max(0, i - context); j <= Math.Min(lines.Count - 1, i + context); j++)
            {
                shown[j] = true;
            }
        }

        int skipped = 0;
        List<DiffLine>? current = null;
        for (int i = 0; i < lines.Count; i++)
        {
            if (!shown[i])
            {
                if (current is not null)
                {
                    current = null;
                }

                skipped++;
                continue;
            }

            if (current is null)
            {
                current = [];
                hunks.Add(new DiffHunk(current, skipped));
                skipped = 0;
            }

            current.Add(lines[i]);
        }

        return hunks;
    }

    private static string[] Lines(string text) =>
        text.Length == 0 ? [] : text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
}
