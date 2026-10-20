namespace SchedularrTV.CreditSqueeze;

public enum CreditsLayoutKind { Unknown, Card, Scroll }

/// <summary>Layout is local to a scan, never a single assumption for the entire ending.</summary>
public static class CreditsLayouts
{
    public static bool Moving(IReadOnlyList<CreditsOcrLine> before, IReadOnlyList<CreditsOcrLine> after) => Movement(before, after) is not null;

    private static double? Movement(IReadOnlyList<CreditsOcrLine> before, IReadOnlyList<CreditsOcrLine> after)
    {
        List<double> deltas = [];
        foreach (var line in after)
        {
            var previous = before.FirstOrDefault(other => string.Equals(other.Text.Trim(), line.Text.Trim(), StringComparison.OrdinalIgnoreCase));
            if (line.Box is { IsValid: true } box && previous?.Box is { IsValid: true } old
                && Math.Abs(box.CenterX - old.CenterX) < .06 && Math.Abs(old.Top - box.Top) is > .0015 and < .5)
                deltas.Add(old.Top - box.Top);
        }
        if (deltas.Count < 2 || deltas.Max() - deltas.Min() >= .035) return null;
        double movement = deltas.Average();
        return Math.Abs(movement) > .0015 && deltas.All(delta => Math.Sign(delta) == Math.Sign(movement)) ? movement : null;
    }

    public static IReadOnlyList<CreditsOcrSample> Classify(IReadOnlyList<CreditsOcrSample> samples)
    {
        bool Stable(int from, int to)
        {
            var left = samples[from].Lines; var right = samples[to].Lines;
            if (left.Count == 0 || left.Count != right.Count) return false;
            return left.Zip(right).All(pair => string.Equals(pair.First.Text.Trim(), pair.Second.Text.Trim(), StringComparison.OrdinalIgnoreCase)
                && (pair.First.Box is null && pair.Second.Box is null || pair.First.Box is { IsValid: true } a && pair.Second.Box is { IsValid: true } b
                    && Math.Abs(a.Left - b.Left) < .004 && Math.Abs(a.Top - b.Top) < .0015 && Math.Abs(a.Width - b.Width) < .01));
        }
        bool[] stationary = samples.Select((sample, index) => sample.StaticFrame || index > 0 && Stable(index - 1, index)
            || index + 1 < samples.Count && Stable(index, index + 1)).ToArray();
        var movements = Enumerable.Range(0, Math.Max(0, samples.Count - 1)).Select(index => stationary[index] && stationary[index + 1]
            ? null : Movement(samples[index].Lines, samples[index + 1].Lines)).ToArray();
        bool Motion(int edge)
        {
            if (edge < 0 || edge >= movements.Length || movements[edge] is not { } movement) return false;
            bool Consistent(int other) => other >= 0 && other < movements.Length && movements[other] is { } next
                && Math.Sign(movement) == Math.Sign(next) && Math.Abs(movement / next) is > .35 and < 3;
            return samples.Count == 2 || Consistent(edge - 1) || Consistent(edge + 1);
        }
        return samples.Select((sample, index) => sample with
        {
            Layout = sample.Lines.Count == 0 ? CreditsLayoutKind.Unknown
                : Motion(index - 1) || Motion(index) ? CreditsLayoutKind.Scroll
                : stationary[index] ? CreditsLayoutKind.Card : CreditsLayoutKind.Unknown
        }).ToArray();
    }

    public static bool NameCard(CreditsOcrSample sample)
    {
        if ((!sample.StaticFrame && sample.Layout != CreditsLayoutKind.Card) || sample.Layout == CreditsLayoutKind.Scroll) return false;
        return sample.Lines.Count(line => line.Confidence >= 85 && NameLike(line.Text)) >= 2;
    }

    private static bool NameLike(string text)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 2 or > 4) return false;
        return words.Count(word => word.Length > 0 && char.IsUpper(word[0]) && word.Any(char.IsLetter)) >= 2;
    }
}
