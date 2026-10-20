using SkiaSharp;

namespace SchedularrTV.CreditSqueeze;

/// <summary>Alternate input for outlined/condensed credit fonts, without changing recognized wording.</summary>
public static class CreditsOcrPreprocessing
{
    public static SKBitmap Contrast(SKBitmap source)
    {
        int[] histogram = new int[256];
        byte[] gray = new byte[source.Width * source.Height];
        for (int y = 0; y < source.Height; y++)
        for (int x = 0; x < source.Width; x++)
        {
            var pixel = source.GetPixel(x, y);
            byte value = (byte)((54 * pixel.Red + 183 * pixel.Green + 19 * pixel.Blue) >> 8);
            gray[y * source.Width + x] = value;
            histogram[value]++;
        }
        int threshold = Threshold(histogram);
        bool invert = histogram.Take(threshold + 1).Sum() > gray.Length * .55;
        using var binary = new SKBitmap(source.Width, source.Height);
        for (int y = 0; y < source.Height; y++)
        for (int x = 0; x < source.Width; x++)
        {
            bool dark = gray[y * source.Width + x] <= threshold;
            binary.SetPixel(x, y, dark != invert ? SKColors.Black : SKColors.White);
        }
        // Widen narrow display fonts; page-relative TSV coordinates still map to the same source.
        int width = Math.Min(2880, (int)Math.Round(source.Width * 1.5));
        return binary.Resize(new SKImageInfo(width, source.Height), new SKSamplingOptions(SKFilterMode.Linear))
            ?? binary.Copy();
    }

    private static int Threshold(int[] histogram)
    {
        long count = histogram.Sum(value => (long)value);
        double total = histogram.Select((value, index) => (double)value * index).Sum();
        long leftCount = 0;
        double leftSum = 0, best = -1;
        int threshold = 127;
        for (int value = 0; value < 255; value++)
        {
            leftCount += histogram[value]; leftSum += (double)histogram[value] * value;
            long rightCount = count - leftCount;
            if (leftCount == 0 || rightCount == 0) continue;
            double delta = leftSum / leftCount - (total - leftSum) / rightCount;
            double variance = leftCount * (double)rightCount * delta * delta;
            if (variance > best) { best = variance; threshold = value; }
        }
        return threshold;
    }

    public static double Score(IReadOnlyList<CreditsOcrLine> lines, int confidence) =>
        CreditsOcrAnalysis.Clean([lines], confidence)[0].Sum(line => Math.Min(64, line.Text.Count(char.IsLetter)) * line.Confidence / 100);

    public static bool NeedsRetry(IReadOnlyList<CreditsOcrLine> lines, int confidence)
    {
        var useful = lines.Where(line => CreditsOcrFiltering.Keep(line, confidence)).ToArray();
        return Score(lines, confidence) < 80 || useful.Length == 0 || useful.Average(line => line.Confidence) < 85;
    }

    public static IReadOnlyList<CreditsOcrLine> Prefer(IReadOnlyList<CreditsOcrLine> current, IReadOnlyList<CreditsOcrLine> alternative, int confidence) =>
        Score(alternative, confidence) > Score(current, confidence) * 1.1 + 4 ? alternative : current;
}
