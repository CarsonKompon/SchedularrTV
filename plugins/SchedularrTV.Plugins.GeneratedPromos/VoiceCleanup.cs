using System.Globalization;
using SchedularrTV.Core.Configuration;

namespace SchedularrTV.GeneratedPromos;

/// <summary>
/// Cleaning a voice pack's takes before they're used: the ffmpeg filter for its settings, and finding the pitch of a
/// mic's whine or squeal from recordings.
/// </summary>
public static class VoiceCleanup
{
    /// <summary>The sample rate takes are analysed at.</summary>
    public const int Rate = 48000;

    // Each analysed frame: about 0.17 s, fine enough to tell 6 Hz apart.
    private const int FrameSize = 8192;

    // A peak counts as a whine when it stands this many times above the spectrum around it (about 12 dB).
    private const double ProminenceNeeded = 16;

    private const double NyquistLimit = 23500;

    /// <summary>
    /// The ffmpeg audio filter for <paramref name="cleanup"/> (comma-separated, to put in a chain), or null when it changes
    /// nothing: notches at the whine and its harmonics, then the low-pass, then the noise reduction.
    /// </summary>
    public static string? Filter(GeneratedVoiceCleanup? cleanup)
    {
        if (cleanup is not { Active: true })
        {
            return null;
        }

        List<string> filters = [];
        if (cleanup.Whine is { } whine && double.IsFinite(whine))
        {
            double pitch = Math.Clamp(whine, GeneratedVoiceCleanup.MinWhine, GeneratedVoiceCleanup.MaxWhine);
            double q = Math.Clamp(double.IsFinite(cleanup.WhineSharpness) ? cleanup.WhineSharpness : 8,
                GeneratedVoiceCleanup.MinSharpness, GeneratedVoiceCleanup.MaxSharpness);
            for (int harmonic = 1; harmonic <= 3 && pitch * harmonic < NyquistLimit; harmonic++)
            {
                // Twice for each, so the cut is deep enough to make a loud squeal inaudible.
                string notch = $"bandreject=f={Number(pitch * harmonic)}:width_type=q:w={Number(q)}";
                filters.Add(notch);
                filters.Add(notch);
            }
        }

        if (cleanup.LowPass is { } lowPass && double.IsFinite(lowPass))
        {
            // Two in a row: a steeper slope, so the voice keeps its clarity just under the cut.
            string cut = $"lowpass=f={Number(Math.Clamp(lowPass, GeneratedVoiceCleanup.MinLowPass, GeneratedVoiceCleanup.MaxLowPass))}";
            filters.Add(cut);
            filters.Add(cut);
        }

        if (cleanup.Denoise is { } denoise && double.IsFinite(denoise))
        {
            // Tracks the noise as it goes, so a steady hiss or hum is learned from the take itself.
            filters.Add($"afftdn=nr={Number(Math.Clamp(denoise, GeneratedVoiceCleanup.MinDenoise, GeneratedVoiceCleanup.MaxDenoise))}:nf=-50:tn=1");
        }

        return filters.Count == 0 ? null : string.Join(',', filters);
    }

    /// <summary>
    /// The pitch of a steady whine in <paramref name="takes"/> (mono samples at <see cref="Rate"/>), in Hz: the strongest
    /// narrow peak from 1 to 20 kHz that stands well above the spectrum around it, averaged over the quieter half of the
    /// recordings (between words, where a whine is plainest). Null when there's no clear one.
    /// </summary>
    public static double? FindWhine(IEnumerable<float[]> takes)
    {
        double[] window = new double[FrameSize];
        for (int index = 0; index < FrameSize; index++)
        {
            window[index] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * index / (FrameSize - 1));
        }

        // Every frame's loudness and spectrum; only the quieter half is averaged.
        List<(double Loudness, double[] Power)> frames = [];
        foreach (float[] samples in takes)
        {
            for (int start = 0; start + FrameSize <= samples.Length; start += FrameSize / 2)
            {
                double[] real = new double[FrameSize];
                double[] imaginary = new double[FrameSize];
                double energy = 0;
                for (int index = 0; index < FrameSize; index++)
                {
                    double sample = samples[start + index];
                    energy += sample * sample;
                    real[index] = sample * window[index];
                }

                if (energy <= 0)
                {
                    continue; // digital silence says nothing about the mic
                }

                Fft(real, imaginary);
                double[] power = new double[FrameSize / 2];
                for (int bin = 0; bin < power.Length; bin++)
                {
                    power[bin] = real[bin] * real[bin] + imaginary[bin] * imaginary[bin];
                }

                frames.Add((energy, power));
            }
        }

        if (frames.Count == 0)
        {
            return null;
        }

        List<(double Loudness, double[] Power)> quiet = frames.Count >= 4
            ? [.. frames.OrderBy(frame => frame.Loudness).Take((frames.Count + 1) / 2)]
            : frames;
        double[] average = new double[FrameSize / 2];
        foreach ((_, double[] power) in quiet)
        {
            for (int bin = 0; bin < average.Length; bin++)
            {
                average[bin] += power[bin] / quiet.Count;
            }
        }

        return Peak(average, Rate);
    }

    /// <summary>
    /// The frequency of the most prominent narrow peak from 1 to 20 kHz in a power spectrum (bins 0 to Nyquist), refined
    /// between bins; null when none stands <see cref="ProminenceNeeded"/> times above the median around it.
    /// </summary>
    public static double? Peak(double[] power, int rate)
    {
        double binWidth = rate / 2.0 / power.Length;
        int first = (int)Math.Ceiling(GeneratedVoiceCleanup.MinWhine / binWidth);
        int last = Math.Min(power.Length - 2, (int)(GeneratedVoiceCleanup.MaxWhine / binWidth));
        int around = Math.Max(8, (int)(400 / binWidth)); // compared with ±400 Hz either side
        int best = -1;
        double bestProminence = ProminenceNeeded;
        for (int bin = Math.Max(first, 1); bin <= last; bin++)
        {
            if (power[bin] < power[bin - 1] || power[bin] < power[bin + 1])
            {
                continue;
            }

            List<double> neighbours = [];
            for (int other = Math.Max(0, bin - around); other <= Math.Min(power.Length - 1, bin + around); other++)
            {
                if (Math.Abs(other - bin) > 3) neighbours.Add(power[other]);
            }

            neighbours.Sort();
            double median = neighbours.Count == 0 ? 0 : neighbours[neighbours.Count / 2];
            double prominence = median <= 0 ? double.PositiveInfinity : power[bin] / median;
            if (power[bin] > 0 && prominence > bestProminence)
            {
                bestProminence = prominence;
                best = bin;
            }
        }

        if (best < 0)
        {
            return null;
        }

        // Where the peak really is, between its bin and its neighbours (a parabola through the three, in dB).
        double left = Math.Log(Math.Max(power[best - 1], 1e-30));
        double middle = Math.Log(Math.Max(power[best], 1e-30));
        double right = Math.Log(Math.Max(power[best + 1], 1e-30));
        double denominator = left - 2 * middle + right;
        double shift = denominator == 0 ? 0 : Math.Clamp(0.5 * (left - right) / denominator, -0.5, 0.5);
        return Math.Round((best + shift) * binWidth);
    }

    /// <summary>An in-place radix-2 FFT (the length must be a power of two).</summary>
    private static void Fft(double[] real, double[] imaginary)
    {
        int count = real.Length;
        for (int index = 1, swap = 0; index < count; index++)
        {
            int bit = count >> 1;
            for (; (swap & bit) != 0; bit >>= 1) swap ^= bit;
            swap ^= bit;
            if (index < swap)
            {
                (real[index], real[swap]) = (real[swap], real[index]);
                (imaginary[index], imaginary[swap]) = (imaginary[swap], imaginary[index]);
            }
        }

        for (int length = 2; length <= count; length <<= 1)
        {
            double angle = -2 * Math.PI / length;
            double stepReal = Math.Cos(angle), stepImaginary = Math.Sin(angle);
            for (int start = 0; start < count; start += length)
            {
                double turnReal = 1, turnImaginary = 0;
                for (int offset = 0; offset < length / 2; offset++)
                {
                    int even = start + offset, odd = even + length / 2;
                    double oddReal = real[odd] * turnReal - imaginary[odd] * turnImaginary;
                    double oddImaginary = real[odd] * turnImaginary + imaginary[odd] * turnReal;
                    real[odd] = real[even] - oddReal;
                    imaginary[odd] = imaginary[even] - oddImaginary;
                    real[even] += oddReal;
                    imaginary[even] += oddImaginary;
                    double nextReal = turnReal * stepReal - turnImaginary * stepImaginary;
                    turnImaginary = turnReal * stepImaginary + turnImaginary * stepReal;
                    turnReal = nextReal;
                }
            }
        }
    }

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
