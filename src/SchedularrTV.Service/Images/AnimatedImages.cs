using System.Collections.Concurrent;
using SkiaSharp;

namespace SchedularrTV.Service.Images;

/// <summary>
/// A picture with several frames (an animated GIF or WebP), decoded once: each frame as a whole picture (frames that
/// only draw what changed are put over the frame they build on), and when it shows. It loops.
/// </summary>
public sealed class AnimatedImage
{
    private readonly SKImage[] _frames;
    private readonly double[] _starts;

    public AnimatedImage(IReadOnlyList<SKImage> frames, IReadOnlyList<double> durations)
    {
        _frames = [.. frames];
        _starts = new double[_frames.Length];
        double at = 0;
        for (int index = 0; index < _frames.Length; index++)
        {
            _starts[index] = at;
            at += durations[index];
        }

        Duration = at;
    }

    public int Width => _frames[0].Width;

    public int Height => _frames[0].Height;

    public int FrameCount => _frames.Length;

    /// <summary>One loop, in seconds.</summary>
    public double Duration { get; }

    /// <summary>
    /// The frame showing <paramref name="seconds"/> after it starts playing (before it starts, the first): looping, or
    /// without <paramref name="loop"/>, the last frame once it has played through.
    /// </summary>
    public SKImage FrameAt(double seconds, bool loop = true)
    {
        if (_frames.Length == 1 || Duration <= 0 || seconds <= 0 || double.IsNaN(seconds))
        {
            return _frames[0];
        }

        if (!loop && seconds >= Duration)
        {
            return _frames[^1];
        }

        if (!double.IsFinite(seconds))
        {
            return _frames[0];
        }

        double into = seconds % Duration;
        int found = Array.BinarySearch(_starts, into);
        int index = found >= 0 ? found : ~found - 1;
        return _frames[Math.Clamp(index, 0, _frames.Length - 1)];
    }
}

/// <summary>
/// Animated pictures (GIF, WebP) for promos and overlays: whether a file is one, and its frames, kept for the files used
/// lately (by path and when the file last changed) so drawing a frame never decodes the file again.
/// </summary>
public static class AnimatedImages
{
    /// <summary>Frames a GIF says to show for 0 or 10 ms are shown for this long, as browsers do.</summary>
    private const double ShortestFrame = 0.1;

    /// <summary>At most this many frames are kept of one file; longer ones stop there.</summary>
    private const int MaxFrames = 900;

    /// <summary>How many files' frames are kept at once.</summary>
    private const int Kept = 12;

    private static readonly ConcurrentDictionary<string, bool> Probed = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Lazy<AnimatedImage?>> Decoded = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<string> Order = new();

    /// <summary>Whether the file is a picture with more than one frame. False for anything else, or a file that isn't there.</summary>
    public static bool IsAnimated(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !CanAnimate(path) || !File.Exists(path))
        {
            return false;
        }

        return Probed.GetOrAdd(Key(path), _ =>
        {
            try
            {
                using SKCodec? codec = SKCodec.Create(path);
                return codec is not null && codec.FrameCount > 1;
            }
            catch (Exception)
            {
                return false;
            }
        });
    }

    /// <summary>Whether a file's kind can hold several frames (GIF and WebP do).</summary>
    public static bool CanAnimate(string path) => Path.GetExtension(path).ToLowerInvariant() is ".gif" or ".webp";

    /// <summary>The file's frames, decoded once and kept while it's in use; null when it isn't an animated picture.</summary>
    public static AnimatedImage? Load(string? path)
    {
        if (!IsAnimated(path))
        {
            return null;
        }

        string key = Key(path!);
        Lazy<AnimatedImage?> entry = Decoded.GetOrAdd(key, _ =>
        {
            Order.Enqueue(key);
            return new Lazy<AnimatedImage?>(() => Decode(path!), LazyThreadSafetyMode.ExecutionAndPublication);
        });

        // Older files' frames go (they're freed once nothing still drawing holds them).
        while (Decoded.Count > Kept && Order.TryDequeue(out string? oldest))
        {
            if (oldest != key)
            {
                Decoded.TryRemove(oldest, out _);
            }
            else
            {
                Order.Enqueue(oldest);
                break;
            }
        }

        return entry.Value;
    }

    /// <summary>
    /// The picture to draw for <paramref name="path"/> <paramref name="seconds"/> into its playing: an animated one's
    /// frame then, else the picture itself (decoded now; the caller disposes what <paramref name="owned"/> says it owns).
    /// Without <paramref name="loop"/>, an animated one holds its last frame once it has played through.
    /// </summary>
    public static SKImage? FrameAt(string? path, double seconds, out bool owned, bool loop = true)
    {
        owned = false;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        if (Load(path) is { } animated)
        {
            return animated.FrameAt(seconds, loop);
        }

        owned = true;
        return SKImage.FromEncodedData(path);
    }

    /// <summary>Decodes every frame, each one whole: a frame drawn over another starts as a copy of that one.</summary>
    public static AnimatedImage? Decode(string path)
    {
        try
        {
            using SKCodec? codec = SKCodec.Create(path);
            if (codec is null || codec.FrameCount < 1)
            {
                return null;
            }

            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            SKCodecFrameInfo[] frames = codec.FrameInfo;
            int count = Math.Min(Math.Max(1, frames.Length), MaxFrames);
            var bitmaps = new SKBitmap[count];
            List<SKImage> images = new(count);
            List<double> durations = new(count);
            try
            {
                for (int index = 0; index < count; index++)
                {
                    var bitmap = new SKBitmap(info);
                    bitmap.Erase(SKColors.Transparent);
                    int required = frames.Length > index ? frames[index].RequiredFrame : -1;
                    if (required >= 0 && required < index && bitmaps[required] is { } prior)
                    {
                        // Same size and format, so the earlier frame's pixels are copied over as they are.
                        prior.GetPixelSpan().CopyTo(bitmap.GetPixelSpan());
                    }
                    else
                    {
                        required = -1;
                    }

                    SKCodecResult result = codec.GetPixels(info, bitmap.GetPixels(), new SKCodecOptions(index, required));
                    if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                    {
                        bitmap.Dispose();
                        break;
                    }

                    bitmaps[index] = bitmap;
                    images.Add(SKImage.FromBitmap(bitmap));
                    double seconds = frames.Length > index ? frames[index].Duration / 1000.0 : 0;
                    durations.Add(seconds <= 0.011 ? ShortestFrame : seconds);
                }
            }
            finally
            {
                foreach (SKBitmap? bitmap in bitmaps)
                {
                    bitmap?.Dispose();
                }
            }

            return images.Count == 0 ? null : new AnimatedImage(images, durations);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Key(string path)
    {
        long stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stamp = 0;
        }

        return $"{Path.GetFullPath(path)}|{stamp}";
    }
}
