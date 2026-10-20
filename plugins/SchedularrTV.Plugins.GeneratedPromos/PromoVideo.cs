using System.Diagnostics;
using System.Globalization;
using SkiaSharp;

namespace SchedularrTV.GeneratedPromos;

/// <summary>A video playing in a box. Its frames come from an <see cref="IPromoVideoFrames"/> while drawing.</summary>
public sealed record PromoVideoLayer : PromoLayer
{
    public required string Path { get; init; }

    /// <summary>Where in the file playback begins.</summary>
    public TimeSpan SourceStart { get; init; }

    /// <summary>Starts playing when the layer starts to appear, rather than with the promo.</summary>
    public bool StartsWithLayer { get; init; }

    /// <summary>Starts over when it runs out; otherwise it holds its last frame.</summary>
    public bool Loop { get; init; }

    /// <summary>Fills the box, cropping the video; otherwise the whole video fits inside it.</summary>
    public bool Fill { get; init; }

    public bool ChromaKey { get; init; }
    public string KeyColor { get; init; } = "#00FF00";
    public double KeyTolerance { get; init; } = 0.2;
    public double KeySoftness { get; init; } = 0.1;

    /// <summary>How big its frames are decoded: the largest the box gets (through its keyframes), in whole even pixels.</summary>
    public (int Width, int Height) FrameSize
    {
        get
        {
            float width = Bounds.Width;
            float height = Bounds.Height;
            foreach (PromoKeyframe key in Keyframes ?? [])
            {
                width = Math.Max(width, key.Bounds.Width);
                height = Math.Max(height, key.Bounds.Height);
            }

            // Big enough to look sharp, small enough to decode quickly: at most 1920 wide or 1080 high.
            float shrink = Math.Min(1, Math.Min(1920 / Math.Max(1, width), 1080 / Math.Max(1, height)));
            static int Even(float value) => Math.Max(2, (int)Math.Round(value / 2) * 2);
            return (Even(width * shrink), Even(height * shrink));
        }
    }

    /// <summary>How far into its own playback the video is at <paramref name="time"/> in the promo.</summary>
    public TimeSpan PlaybackAt(TimeSpan time) =>
        StartsWithLayer ? (time > Start ? time - Start : TimeSpan.Zero) : time;

    /// <summary>Source seek time for a still, including loop/hold behavior used by sequential decoding.</summary>
    public double SourceSecond(TimeSpan playback, double sourceDuration, int fps)
    {
        double second = Math.Max(0, SourceStart.TotalSeconds) + Math.Max(0, Math.Round(playback.TotalSeconds * fps) / fps);
        if (!double.IsFinite(sourceDuration) || sourceDuration <= 0) return second;
        return Loop ? second % sourceDuration : Math.Min(second, Math.Max(0, sourceDuration - 1d / fps));
    }

    /// <summary>The ffmpeg filters that size its frames to <see cref="FrameSize"/>, fitted or filled.</summary>
    public string SizeFilter(int fps)
    {
        (int width, int height) = FrameSize;
        string size = $"{width}:{height}";
        // Key at source resolution, before scaling mixes the background into foreground edges.
        // An alpha-capable format must also be selected BEFORE padding, even for opaque sources.
        string filter = $"fps={fps},format=rgba";
        if (ChromaKey)
        {
            SKColor color = SKColor.TryParse(KeyColor, out SKColor parsed) ? parsed : SKColors.Lime;
            double tolerance = double.IsFinite(KeyTolerance) ? Math.Clamp(KeyTolerance, 0.01, 1) : 0.2;
            double softness = double.IsFinite(KeySoftness) ? Math.Clamp(KeySoftness, 0, 1) : 0.1;
            filter += FormattableString.Invariant($",colorkey=0x{color.Red:X2}{color.Green:X2}{color.Blue:X2}:{tolerance:0.######}:{softness:0.######}");
        }
        return filter + (Fill
            ? $",scale={size}:force_original_aspect_ratio=increase,crop={size},format=bgra"
            : $",scale={size}:force_original_aspect_ratio=decrease,pad={size}:(ow-iw)/2:(oh-ih)/2:color=black@0,format=bgra");
    }
}

/// <summary>Frames of video layers, to draw.</summary>
public interface IPromoVideoFrames
{
    /// <summary>The frame <paramref name="layer"/> shows <paramref name="playback"/> into its playback; null when there's none.</summary>
    /// <remarks>The image belongs to the provider and stays good until the next call for the same layer.</remarks>
    SKImage? Frame(PromoVideoLayer layer, TimeSpan playback);
}

/// <summary>Frames already decoded, one per layer: what a still preview draws.</summary>
public sealed class FixedVideoFrames(IReadOnlyDictionary<PromoVideoLayer, SKImage> frames) : IPromoVideoFrames
{
    public SKImage? Frame(PromoVideoLayer layer, TimeSpan playback) => frames.GetValueOrDefault(layer);
}

/// <summary>
/// Video layers' frames for a render, read in order from one ffmpeg per layer: the renderer asks for frames forwards
/// only, so each video is decoded once, straight through. A video that runs out holds its last frame.
/// </summary>
public sealed class PromoVideoStreams(string ffmpegPath, int fps, IReadOnlyDictionary<string, string?>? decoders = null) : IPromoVideoFrames, IDisposable
{
    private sealed class Reader(Process process, SKImageInfo info) : IDisposable
    {
        private readonly byte[] _buffer = new byte[info.BytesSize];
        private readonly SKBitmap _bitmap = new(info);
        public int Index { get; private set; } = -1;
        public bool Ended { get; private set; }
        public SKImage? Image { get; private set; }

        /// <summary>Reads on to frame <paramref name="target"/> (or the last there is).</summary>
        public void ReadTo(int target)
        {
            bool advanced = false;
            while (!Ended && Index < target)
            {
                try
                {
                    process.StandardOutput.BaseStream.ReadExactly(_buffer);
                    Index++;
                    advanced = true;
                }
                catch (EndOfStreamException)
                {
                    Ended = true;
                }
                catch (IOException)
                {
                    Ended = true;
                }
            }

            if (advanced)
            {
                System.Runtime.InteropServices.Marshal.Copy(_buffer, 0, _bitmap.GetPixels(), Math.Min(_buffer.Length, _bitmap.ByteCount));
                _bitmap.NotifyPixelsChanged();
                Image?.Dispose();
                Image = SKImage.FromBitmap(_bitmap);
            }
        }

        public void Dispose()
        {
            Image?.Dispose();
            _bitmap.Dispose();
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            process.Dispose();
        }
    }

    private readonly Dictionary<PromoVideoLayer, Reader?> _streams = new(ReferenceEqualityComparer.Instance);

    public SKImage? Frame(PromoVideoLayer layer, TimeSpan playback)
    {
        if (!_streams.TryGetValue(layer, out Reader? stream))
        {
            stream = Open(layer);
            _streams[layer] = stream;
        }

        if (stream is null)
        {
            return null;
        }

        stream.ReadTo((int)Math.Round(playback.TotalSeconds * fps));
        return stream.Image;
    }

    private Reader? Open(PromoVideoLayer layer)
    {
        if (!File.Exists(layer.Path))
        {
            return null;
        }

        (int width, int height) = layer.FrameSize;
        var info = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        List<string> arguments = ["-hide_banner", "-loglevel", "error"];
        if (layer.Loop) arguments.AddRange(["-stream_loop", "-1"]);
        if (decoders?.GetValueOrDefault(layer.Path) is { } decoder) arguments.AddRange(["-c:v", decoder]);
        arguments.AddRange(["-ss", Seconds(layer.SourceStart), "-i", layer.Path, "-an", "-vf", layer.SizeFilter(fps),
            "-f", "rawvideo", "-pix_fmt", "bgra", "-"]);
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        Process? process = Work.Track(Process.Start(info), "Generated Promos", "Decoding promo video");
        if (process is null)
        {
            return null;
        }

        // Its complaints are read off as they come, so a chatty ffmpeg never stalls waiting for them to be read.
        _ = process.StandardError.ReadToEndAsync();
        // ffmpeg's bgra is straight (not premultiplied) alpha.
        return new Reader(process, new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
    }

    public void Dispose()
    {
        foreach (Reader? stream in _streams.Values)
        {
            stream?.Dispose();
        }

        _streams.Clear();
    }

    internal static string Seconds(TimeSpan value) => Math.Max(0, value.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);
}
