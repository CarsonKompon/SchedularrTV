namespace SchedularrTV.CreditSqueeze;

/// <summary>Conservative agreement between observed readings of the same moving row. Never synthesizes wording.</summary>
public static class CreditsTextAgreement
{
    public static string Identity(string text) => string.Concat(text.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    public static double? Shift(IReadOnlyList<CreditsOcrLine> before, IReadOnlyList<CreditsOcrLine> after)
    {
        List<double> shifts = [];
        foreach (var line in after)
        {
            var previous = before.FirstOrDefault(other => Identity(other.Text) == Identity(line.Text));
            if (line.Box is { IsValid: true } box && previous?.Box is { IsValid: true } old && Math.Abs(box.CenterX - old.CenterX) < .04)
                shifts.Add(old.Top - box.Top);
        }
        return shifts.Count >= 2 && shifts.Max() - shifts.Min() < .035 ? shifts.Average() : null;
    }

    public static bool SameMovingRow(CreditsOcrLine before, CreditsOcrLine after, double? shift)
    {
        string left = Identity(before.Text), right = Identity(after.Text);
        return shift is { } movement && Math.Abs(movement) > .0015 && Math.Min(before.Confidence, after.Confidence) < 85
            && Math.Max(before.Confidence, after.Confidence) >= 85 && left.Length >= 8 && right.Length >= 8
            && before.Box is { IsValid: true } a && after.Box is { IsValid: true } b
            && Math.Abs(a.Top - b.Top - movement) < .02 && Math.Abs(a.CenterX - b.CenterX) < .04
            && Math.Abs(a.Width - b.Width) <= Math.Max(a.Width, b.Width) * .25 && OneEdit(left, right);
    }

    public static (IReadOnlyList<CreditsOcrSample> Samples, int Corrections) Refine(IReadOnlyList<CreditsOcrSample> samples)
    {
        int corrections = 0;
        var result = samples.ToArray();
        for (int index = 0; index < samples.Count; index++)
        {
            var sample = samples[index];
            if (sample.Layout != CreditsLayoutKind.Scroll) continue;
            var lines = sample.Lines.ToArray();
            for (int row = 0; row < lines.Length; row++)
            {
                if (lines[row].Confidence >= 85) continue;
                List<CreditsOcrLine> observations = [];
                for (int neighbour = Math.Max(0, index - 2); neighbour <= Math.Min(samples.Count - 1, index + 2); neighbour++)
                {
                    if (neighbour == index || samples[neighbour].Layout != CreditsLayoutKind.Scroll) continue;
                    double? shift = Shift(sample.Lines, samples[neighbour].Lines);
                    observations.AddRange(samples[neighbour].Lines.Where(other => other.Confidence >= 85 && SameMovingRow(lines[row], other, shift)));
                }
                // Conflicting strong observations remain uncertain rather than picking a plausible name.
                if (observations.Count == 0 || observations.Select(line => Identity(line.Text)).Distinct().Count() != 1) continue;
                var best = observations.MaxBy(line => line.Confidence)!;
                lines[row] = lines[row] with { Text = best.Text, Confidence = best.Confidence };
                corrections++;
            }
            result[index] = sample with { Lines = lines };
        }
        return (result, corrections);
    }

    private static bool OneEdit(string left, string right)
    {
        if (Math.Abs(left.Length - right.Length) > 1) return false;
        int a = 0, b = 0, edits = 0;
        while (a < left.Length && b < right.Length)
        {
            if (left[a] == right[b]) { a++; b++; continue; }
            if (++edits > 1) return false;
            if (left.Length >= right.Length) a++;
            if (right.Length >= left.Length) b++;
        }
        return edits + (left.Length - a) + (right.Length - b) <= 1;
    }
}
