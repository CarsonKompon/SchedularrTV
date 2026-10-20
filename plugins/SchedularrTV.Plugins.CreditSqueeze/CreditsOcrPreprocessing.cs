using SkiaSharp;

namespace SchedularrTV.CreditSqueeze;

/// <summary>Alternate input for outlined/condensed credit fonts, without changing recognized wording.</summary>
public static class CreditsOcrPreprocessing
{
    public static bool IsBlank(SKBitmap source)
    {
        var pixels = source.Pixels;
        if (pixels.Length == 0) return true;
        var first = pixels[0];
        return pixels.All(pixel => Math.Abs(pixel.Red - first.Red) <= 3 && Math.Abs(pixel.Green - first.Green) <= 3 && Math.Abs(pixel.Blue - first.Blue) <= 3);
    }

    public static SKBitmap Contrast(SKBitmap source)
    {
        int[] histogram = new int[256];
        byte[] gray = new byte[source.Width * source.Height];
        var pixels = source.Pixels;
        for (int index = 0; index < pixels.Length; index++)
        {
            var pixel = pixels[index];
            byte value = (byte)((54 * pixel.Red + 183 * pixel.Green + 19 * pixel.Blue) >> 8);
            gray[index] = value;
            histogram[value]++;
        }
        int threshold = Threshold(histogram);
        bool invert = histogram.Take(threshold + 1).Sum() > gray.Length * .55;
        using var binary = new SKBitmap(source.Width, source.Height);
        for (int index = 0; index < pixels.Length; index++)
        {
            bool dark = gray[index] <= threshold;
            pixels[index] = dark != invert ? SKColors.Black : SKColors.White;
        }
        binary.Pixels = pixels;
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
        if (useful.Length == 0) return true;
        double mean = useful.Average(line => line.Confidence);
        bool clearShortCard = useful.Sum(line => line.Text.Count(char.IsLetter)) >= 8 && mean >= 90;
        return (!CreditsOcrFiltering.Usable(useful) && !clearShortCard) || useful.Length < lines.Count * .6 || mean < 85
            || useful.Any(line => line.Confidence < 75 && line.Text.Count(char.IsLetter) >= 8);
    }

    public static IReadOnlyList<CreditsOcrLine> Prefer(IReadOnlyList<CreditsOcrLine> current, IReadOnlyList<CreditsOcrLine> alternative, int confidence)
    {
        double Mean(IReadOnlyList<CreditsOcrLine> lines) => lines.Where(line => CreditsOcrFiltering.Keep(line, confidence)).Select(line => line.Confidence).DefaultIfEmpty(0).Average();
        double before = Score(current, confidence), after = Score(alternative, confidence);
        bool moreEvidence = after > before * 1.1 + 4 && Mean(alternative) >= Mean(current) - 3;
        bool clearer = Mean(alternative) > Mean(current) + 8 && after >= before * .75;
        return moreEvidence || clearer ? alternative : current;
    }
}
