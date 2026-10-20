using System.Text;

namespace SchedularrTV.CreditSqueeze;

/// <summary>Text evidence in source-relative coordinates, independent of a show's layout.</summary>
public sealed record CreditsTextBox(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
    public double CenterX => Left + Width / 2;
    public bool IsValid => double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Width) && double.IsFinite(Height)
        && Left >= 0 && Top >= 0 && Width > 0 && Height > 0 && Right <= 1.001 && Bottom <= 1.001;
}

/// <summary>
/// Finds credit-like text groups independently in every frame. Adjacent frames can corroborate
/// moving or uncertain lines, but agreement is not required for a clear, briefly displayed card.
/// Does not invent/correct names, constrain credits to a fixed screen area, or choose just one column.
/// </summary>
public static class CreditsOcrAnalysis
{
    private sealed record Detection(int Frame, CreditsOcrLine Line, string Identity, bool Supported);

    public static IReadOnlyList<IReadOnlyList<CreditsOcrLine>> Clean(
        IReadOnlyList<IReadOnlyList<CreditsOcrLine>> frames, int minimumConfidence = 60)
    {
        var candidates = frames.Select(frame => frame.Where(line => CreditsOcrFiltering.Keep(line, minimumConfidence)).ToArray()).ToArray();
        var detections = candidates.Select((frame, index) => frame.Select(line => new Detection(index, line, Identity(line.Text),
            line.Box is { IsValid: true } box && frame.Any(other => !ReferenceEquals(line, other)
                && other.Confidence >= Math.Max(80, minimumConfidence) && other.Box is { IsValid: true } next && Neighbours(box, next))))
            .ToArray()).ToArray();
        var occurrences = detections.SelectMany(frame => frame).GroupBy(item => item.Identity)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        bool[] hasTextGroup = detections.Select(frame => frame.Any(item => item.Supported && item.Line.Confidence >= 80)).ToArray();
        bool changingGroups = detections.Select(frame => string.Join('\n', frame.Where(item => item.Supported).Select(item => item.Identity)))
            .Where(signature => signature.Length > 0).Distinct(StringComparer.Ordinal).Take(2).Count() > 1;

        return detections.Select(frame => (IReadOnlyList<CreditsOcrLine>)frame.Where(Keep).Select(item => item.Line).ToArray()).ToArray();

        bool Keep(Detection item)
        {
            // Alternative OCR engines and old transcripts without geometry retain confidence-only behaviour.
            if (item.Line.Box is not { IsValid: true } box) return true;
            if (item.Supported) return true;
            Detection[] sameText = occurrences[item.Identity];
            int stationaryFrames = sameText.Where(other => other.Line.Box is { IsValid: true } otherBox && Stationary(box, otherBox))
                .Select(other => other.Frame).Distinct().Count();
            // A fixed, isolated mark while other credit groups change elsewhere is usually a watermark.
            // A real heading beside scrolling names remains supported and is retained above.
            if (changingGroups && frames.Count >= 4 && stationaryFrames >= Math.Ceiling(frames.Count * .8)
                && sameText.Count(other => hasTextGroup[other.Frame] && !other.Supported) >= Math.Ceiling(frames.Count * .6)) return false;

            bool corroborated = sameText.Any(other => Math.Abs(other.Frame - item.Frame) is > 0 and <= 2
                && other.Line.Box is { IsValid: true } otherBox && Tracks(box, otherBox));
            if (corroborated) return true;

            // Keep a clear standalone name/card even if it only lasts one sample. Short transient
            // fragments need a neighbouring text line or a second observation, not a word dictionary.
            int letters = item.Line.Text.EnumerateRunes().Count(Rune.IsLetter);
            return item.Line.Confidence >= Math.Max(92, minimumConfidence)
                && (letters >= 4 || item.Line.Text.Contains('©')
                    || item.Line.Text.EnumerateRunes().Any(rune => rune.Value is >= 0x3400 and <= 0x9fff or >= 0xac00 and <= 0xd7af));
        }
    }

    private static string Identity(string text) => string.Join(' ', text.Normalize(NormalizationForm.FormC)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private static bool SimilarHeight(CreditsTextBox a, CreditsTextBox b) => Math.Min(a.Height, b.Height) / Math.Max(a.Height, b.Height) >= .45;

    private static bool Neighbours(CreditsTextBox a, CreditsTextBox b)
    {
        if (!SimilarHeight(a, b)) return false;
        double height = Math.Max(a.Height, b.Height);
        double verticalGap = Math.Max(0, Math.Max(a.Top - b.Bottom, b.Top - a.Bottom));
        bool aligned = Math.Abs(a.Left - b.Left) <= height * 2 || Math.Abs(a.CenterX - b.CenterX) <= height * 2
            || Math.Abs(a.Right - b.Right) <= height * 2;
        // Vertical paragraphs and multi-column role/name rows both provide useful evidence.
        if (verticalGap <= height * 2.5 && aligned) return true;
        double horizontalGap = Math.Max(0, Math.Max(a.Left - b.Right, b.Left - a.Right));
        return Math.Abs(a.Top - b.Top) <= height * .6 && horizontalGap <= Math.Max(.08, height * 5);
    }

    private static bool Tracks(CreditsTextBox a, CreditsTextBox b) => SimilarHeight(a, b)
        && Math.Abs(a.CenterX - b.CenterX) <= Math.Max(.08, Math.Max(a.Width, b.Width) * .4)
        && Math.Abs(a.Top - b.Top) <= .5;

    private static bool Stationary(CreditsTextBox a, CreditsTextBox b) => SimilarHeight(a, b)
        && Math.Abs(a.Left - b.Left) < .015 && Math.Abs(a.Top - b.Top) < .015
        && Math.Abs(a.Width - b.Width) < .025;
}
