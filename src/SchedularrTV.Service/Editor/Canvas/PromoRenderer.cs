using System.Diagnostics;
using System.Globalization;
using System.Text;
using SkiaSharp;
using SchedularrTV.Core.Platform;
using SchedularrTV.Plugins.Performance;
using SchedularrTV.Service.Editor;

namespace SchedularrTV.GeneratedPromos;

public sealed record PromoRenderResult(string OutputPath, int Frames, TimeSpan Elapsed);

public static class PromoFonts
{
    /// <summary>
    /// Font families the renderer can draw with, including fonts installed for the current user only and fonts kept in
    /// the data folder from imported templates.
    /// </summary>
    public static IReadOnlyList<string> Installed() =>
    [
        .. SKFontManager.Default.GetFontFamilies()
            .Concat(FontFiles.KeptFamilies())
            .Where(family => !string.IsNullOrWhiteSpace(family) && family[0] != '@')
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
    ];
}

/// <summary>Draws composition frames with SkiaSharp and streams them to ffmpeg as raw video.</summary>
public sealed class PromoRenderer(string ffmpegPath = "ffmpeg")
{
    public string FfprobePath { get; init; } = "ffprobe";
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif"
    };

    /// <summary>
    /// The encoder, preset and threads; unset, the <c>Rendering</c> app settings in use now (x264 <c>medium</c>, or
    /// <c>superfast</c> on two threads on a Raspberry Pi).
    /// </summary>
    public VideoEncoderChoice? Encoding { get; init; }

    /// <summary>The bitrate a 1080p promo gets from a hardware encoder (scaled to the promo's size); unset, the app setting.</summary>
    public int? HardwareBitrateKbps { get; init; }

    /// <summary>Kept for callers that only name an encoder; <see cref="Encoding"/> wins when set.</summary>
    public string VideoEncoder
    {
        get => (Encoding ?? CurrentSettings.Video).Encoder.Name;
        init => Encoding = (Encoding ?? CurrentSettings.Video) with { Encoder = H264Encoder.Find(value) };
    }

    private static RenderEncodingSettings CurrentSettings => RenderEncodingSettings.Current(StandardPreset);

    /// <summary>The standard preset: measured on 1080p cartoon footage, about a quarter smaller than veryfast/crf 20 at equal SSIM.</summary>
    public const string StandardPreset = "medium";

    public async Task<PromoRenderResult> RenderAsync(PromoComposition composition, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(composition.Duration, TimeSpan.Zero);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        bool videoBackground = composition.BackgroundPath is { } background && !IsImage(background);
        var stopwatch = Stopwatch.StartNew();
        string? mixed = null;
        if (composition.AudioPath is not null || composition.Music is not null || composition.BackgroundAudio is not null || composition.OverlayAudio is not null
            || composition.LayerAudio.Count > 0)
        {
            // Tempo changes mixed straight into AAC lose the voice in ffmpeg, so the mix is made as PCM first.
            mixed = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, $".{Guid.NewGuid():N}.audio.wav");
            await RunAsync(AudioArguments(composition, mixed), cancellationToken).ConfigureAwait(false);
        }

        RenderEncodingSettings settings = CurrentSettings;
        VideoEncoderChoice encoding = Encoding ?? settings.Video;
        try
        {
            try
            {
                return await RenderVideoAsync(composition, outputPath, videoBackground, mixed, encoding, HardwareBitrateKbps ?? settings.HardwareBitrateKbps,
                    stopwatch, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException) when (encoding.Encoder.Hardware && !cancellationToken.IsCancellationRequested)
            {
                // A hardware encoder this computer (or this ffmpeg) doesn't have mustn't stop promos being made: the
                // same promo is made in software instead.
                return await RenderVideoAsync(composition, outputPath, videoBackground, mixed, encoding with { Encoder = H264Encoder.Software },
                    0, stopwatch, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (mixed is not null)
            {
                File.Delete(mixed);
            }
        }
    }

    private async Task<PromoRenderResult> RenderVideoAsync(
        PromoComposition composition,
        string outputPath,
        bool videoBackground,
        string? mixedAudio,
        VideoEncoderChoice encoding,
        int hardwareBitrateKbps,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        using var images = new PromoImageCache();
        using Process ffmpeg = Process.Start(new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }.WithArguments(Arguments(composition, outputPath, videoBackground, mixedAudio, encoding, hardwareBitrateKbps)))
            ?? throw new InvalidOperationException("ffmpeg could not be started.");
        Work.Track(ffmpeg, "Generated Promos", "Encoding promo");

        // The frames are drawn here, in the app, while ffmpeg encodes them: named on their own so that drawing's CPU is
        // put down to the promo rather than to the app.
        using WorkScope drawing = Work.Begin("Generated Promos", "Drawing promo frames");

        var errors = new StringBuilder();
        Task drain = Task.Run(async () =>
        {
            while (await ffmpeg.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                lock (errors)
                {
                    errors.AppendLine(line);
                    if (errors.Length > 16_000) errors.Remove(0, errors.Length - 16_000);
                }
            }
        }, cancellationToken);

        try
        {
            var info = new SKImageInfo(composition.Width, composition.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            // A transparent overlay has to reach ffmpeg with straight alpha.
            var output = info.WithAlphaType(videoBackground ? SKAlphaType.Unpremul : SKAlphaType.Premul);
            byte[] frame = new byte[output.BytesSize];
            using SKSurface surface = SKSurface.Create(info);
            using var pixels = new SKBitmap(output);
            Stream input = ffmpeg.StandardInput.BaseStream;
            PromoLayerFrame?[]? previous = null;
            int[]? previousPages = null;
            PromoTextLayer[] pagedText = [.. composition.Layers.OfType<PromoTextLayer>().Where(text => text.Pages is { Count: > 0 })];
            // Video layers' frames, each video decoded straight through by an ffmpeg of its own as the frames are drawn.
            Dictionary<string, string?> decoders = [];
            foreach (string path in composition.Layers.OfType<PromoVideoLayer>().Select(layer => layer.Path).Distinct())
                decoders[path] = await PromoVideoDecoder.FindAsync(FfprobePath, path, cancellationToken).ConfigureAwait(false);
            using var videos = new PromoVideoStreams(ffmpegPath, composition.Fps, decoders);
            PromoVideoLayer[] videoLayers = [.. composition.Layers.OfType<PromoVideoLayer>()];

            // Animated pictures (GIFs) change from frame to frame by themselves, so frames aren't reused while one shows.
            bool animatedPictures = composition.Layers.OfType<PromoImageLayer>().Any(picture => images.IsAnimated(picture.Path))
                                    || (!videoBackground && images.IsAnimated(composition.BackgroundPath));

            for (int index = 0; index < composition.FrameCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var time = TimeSpan.FromSeconds(index / (double)composition.Fps);
                PromoLayerFrame?[] states = [.. composition.Layers.Select(layer => PromoAnimation.Evaluate(layer, time, composition.Duration, composition.Height))];
                bool videoShowing = videoLayers.Any(video => PromoAnimation.EvaluateInGroups(video, time, composition.Duration, composition.Height) is not null);
                int[] pages = [.. pagedText.Select(text => text.PageAt(time))];

                // Held poses between transitions are identical, so resend the last frame unchanged (not while a video plays).
                if (previous is null || videoShowing || animatedPictures || !states.SequenceEqual(previous) || !pages.SequenceEqual(previousPages ?? []))
                {
                    DrawFrame(surface.Canvas, composition, time, images, !videoBackground, videos);
                    if (!surface.ReadPixels(output, pixels.GetPixels(), pixels.RowBytes, 0, 0))
                    {
                        throw new InvalidOperationException("Could not read the rendered frame.");
                    }

                    pixels.GetPixelSpan().CopyTo(frame);
                    previous = states;
                    previousPages = pages;
                }

                await input.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            }

            input.Close();
            await ffmpeg.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await drain.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException || cancellationToken.IsCancellationRequested)
        {
            if (!ffmpeg.HasExited) ffmpeg.Kill(entireProcessTree: true);
            if (cancellationToken.IsCancellationRequested) throw;
            await ffmpeg.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (ffmpeg.ExitCode != 0)
        {
            string log;
            lock (errors) log = errors.ToString();
            throw new InvalidOperationException($"ffmpeg exited with code {ffmpeg.ExitCode}: {log.Trim()}");
        }

        return new PromoRenderResult(outputPath, composition.FrameCount, stopwatch.Elapsed);
    }

    /// <param name="videos">Where video layers' frames come from; without it they're left out.</param>
    public static void DrawFrame(SKCanvas canvas, PromoComposition composition, TimeSpan time, PromoImageCache images, bool drawBackground = true,
        IPromoVideoFrames? videos = null)
    {
        canvas.Clear(drawBackground ? composition.BackgroundColor : SKColors.Transparent);
        var frame = SKRect.Create(composition.Width, composition.Height);

        // An animated picture background (a GIF) plays from the start of the promo.
        if (drawBackground && composition.BackgroundPath is { } background && images.GetAt(background, time) is { } image)
        {
            DrawImage(canvas, image, frame, cover: true, paint: null);
        }

        // A target is isolated before masking, so a cutout reveals (and never erases) the footage below it.
        // Mask sources stay in the composition for animation/frame-change detection and editor selection.
        var masks = composition.Layers
            .Where(layer => layer is PromoRectLayer or PromoImageLayer && layer.Mask?.Target is { Length: > 0 })
            .ToLookup(layer => layer.Mask!.Target!, StringComparer.Ordinal);
        var members = new HashSet<PromoLayer>(composition.Layers, ReferenceEqualityComparer.Instance);
        var visited = new HashSet<PromoLayer>(ReferenceEqualityComparer.Instance);
        foreach (PromoLayer layer in composition.Layers.Where(layer => layer.Parent is null || !members.Contains(layer.Parent)))
            DrawNode(layer);

        void DrawNode(PromoLayer layer)
        {
            if (layer.Mask is not null || !visited.Add(layer)) return;
            PromoLayer[] applied = layer.Id is { } id ? [.. masks[id]] : [];
            if (applied.Length == 0)
            {
                DrawContents();
                return;
            }

            canvas.SaveLayer(frame, null);
            DrawContents();
            ApplyMasks(SchedularrTV.Core.Configuration.GeneratedMaskMode.Add, SKBlendMode.DstIn);
            ApplyMasks(SchedularrTV.Core.Configuration.GeneratedMaskMode.Subtract, SKBlendMode.DstOut);
            canvas.Restore();

            void DrawContents()
            {
                // Each leaf already applies its ancestors' transforms. Composite the subtree in world space,
                // then mask that finished image once (including any masks on nested groups or individual layers).
                if (layer is PromoGroupLayer group)
                {
                    foreach (PromoLayer child in composition.Layers.Where(child => ReferenceEquals(child.Parent, group)))
                        DrawNode(child);
                }
                else DrawLayer(canvas, layer, composition, time, images, videos);
            }

            void ApplyMasks(SchedularrTV.Core.Configuration.GeneratedMaskMode mode, SKBlendMode blend)
            {
                if (!applied.Any(mask => mask.Mask!.Mode == mode)) return;
                using var paint = new SKPaint { BlendMode = blend };
                // A full-frame transparent layer is essential for DstIn: outside the drawn masks is zero alpha.
                canvas.SaveLayer(frame, paint);
                foreach (PromoLayer mask in applied.Where(mask => mask.Mask!.Mode == mode))
                    DrawLayer(canvas, mask, composition, time, images, videos);
                canvas.Restore();
            }
        }
    }

    private static void DrawLayer(SKCanvas canvas, PromoLayer layer, PromoComposition composition, TimeSpan time,
        PromoImageCache images, IPromoVideoFrames? videos)
    {
        // Groups draw nothing; their layers draw inside them. A layer shows only while every group it's in does.
        if (PromoAnimation.EvaluateInGroups(layer, time, composition.Duration, composition.Height) is not { } state || state.Opacity <= 0)
            return;

        SKRect bounds = state.Bounds;
        canvas.Save();
        // Into the space of the groups it's in, outermost first: each moves its contents to its corner and turns them about its middle.
        foreach (PromoGroupLayer group in Groups(layer))
        {
            if (PromoAnimation.Evaluate(group, time, composition.Duration, composition.Height) is { } around)
            {
                Place(canvas, around, around.Bounds);
                Pin(canvas, around);
                canvas.Translate(around.Bounds.Left, around.Bounds.Top);
            }
        }

        Place(canvas, state, bounds);
        Pin(canvas, state);
        bool translucent = state.Opacity < 0.999f;
        if (translucent)
        {
            // Shadows spill past the text box, so leave headroom around the offscreen layer.
            SKRect area = bounds;
            area.Inflate(bounds.Height * 0.25f + 8, bounds.Height * 0.25f + 8);
            if (layer is PromoRectLayer soft) area.Inflate(soft.SoftEdges * 4, soft.SoftEdges * 4);
            using var alpha = new SKPaint { Color = SKColors.Black.WithAlpha((byte)Math.Round(Math.Clamp(state.Opacity, 0, 1) * 255)) };
            canvas.SaveLayer(area, alpha);
        }

        switch (layer)
        {
            case PromoRectLayer rect:
                DrawShape(canvas, rect, bounds);
                break;
            case PromoTextLayer text:
                DrawText(canvas, text.Pages is null ? text : text with { Lines = text.LinesAt(time) }, bounds, images);
                break;
            // An animated picture (a GIF) plays from when its layer appears, looping.
            case PromoImageLayer picture when images.GetAt(picture.Path, time - layer.Start) is { } bitmap:
                DrawImage(canvas, bitmap, bounds, cover: false, paint: null);
                break;
            // Its frames come already fitted to (or filling) a box of its shape, so they're drawn to fill the box.
            case PromoVideoLayer video when videos?.Frame(video, video.PlaybackAt(time)) is { } still:
                canvas.DrawImage(still, bounds, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), null);
                break;
        }

        if (translucent) canvas.Restore();
        canvas.Restore();
    }

    private static void DrawShape(SKCanvas canvas, PromoRectLayer layer, SKRect bounds)
    {
        using var blur = layer.SoftEdges > 0 ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, layer.SoftEdges) : null;
        using var paint = new SKPaint { Color = layer.Color, IsAntialias = true, MaskFilter = blur };
        using var path = new SKPathBuilder();
        switch (layer.Shape)
        {
            case SchedularrTV.Core.Configuration.GeneratedShape.Ellipse:
                canvas.DrawOval(bounds, paint);
                return;
            case SchedularrTV.Core.Configuration.GeneratedShape.Triangle:
                path.MoveTo(bounds.MidX, bounds.Top);
                path.LineTo(bounds.Right, bounds.Bottom);
                path.LineTo(bounds.Left, bounds.Bottom);
                break;
            case SchedularrTV.Core.Configuration.GeneratedShape.Diamond:
                path.MoveTo(bounds.MidX, bounds.Top);
                path.LineTo(bounds.Right, bounds.MidY);
                path.LineTo(bounds.MidX, bounds.Bottom);
                path.LineTo(bounds.Left, bounds.MidY);
                break;
            case SchedularrTV.Core.Configuration.GeneratedShape.Star:
                for (int i = 0; i < 10; i++)
                {
                    double angle = -Math.PI / 2 + i * Math.PI / 5;
                    float radius = i % 2 == 0 ? 1 : 0.4f;
                    float x = bounds.MidX + (float)Math.Cos(angle) * bounds.Width / 2 * radius;
                    float y = bounds.MidY + (float)Math.Sin(angle) * bounds.Height / 2 * radius;
                    if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
                }
                break;
            default:
                canvas.DrawRoundRect(bounds, layer.CornerRadius, layer.CornerRadius, paint);
                return;
        }
        path.Close();
        using var outline = path.Detach();
        canvas.DrawPath(outline, paint);
    }

    /// <summary>The groups a layer is inside, outermost first.</summary>
    private static List<PromoGroupLayer> Groups(PromoLayer layer)
    {
        List<PromoGroupLayer> groups = [];
        for (PromoGroupLayer? group = layer.Parent; group is not null; group = group.Parent)
        {
            groups.Insert(0, group);
        }

        return groups;
    }

    /// <summary>
    /// A layer's corner pin: what's drawn in its box from here on is stretched with perspective to its pulled corners
    /// (and, for a group, everything inside it).
    /// </summary>
    private static void Pin(SKCanvas canvas, PromoLayerFrame state)
    {
        if (PromoPerspective.Matrix(state.Bounds, state.Corners) is { } perspective)
        {
            canvas.Concat(perspective);
        }
    }

    /// <summary>A layer's transition movement and zoom, and its rotation, all about the middle of its box.</summary>
    private static void Place(SKCanvas canvas, PromoLayerFrame state, SKRect bounds)
    {
        canvas.Translate(state.OffsetX, state.OffsetY);
        if (Math.Abs(state.Rotation) > 0.001f)
        {
            canvas.RotateDegrees(state.Rotation, bounds.MidX, bounds.MidY);
        }

        canvas.Scale(state.Scale, state.Scale, bounds.MidX, bounds.MidY);
    }

    private static void DrawText(SKCanvas canvas, PromoTextLayer layer, SKRect bounds, PromoImageCache cache)
    {
        if (layer.Lines.Count == 0)
        {
            return;
        }

        SKTypeface typeface = cache.Typeface(layer.FontFamily, layer.Bold);
        using var font = new SKFont(typeface, layer.MaxFontSize) { Edging = SKFontEdging.SubpixelAntialias, Subpixel = true };
        using var paint = new SKPaint { Color = layer.Color, IsAntialias = true };
        using SKImageFilter? shadow = layer.Shadow is { } shade
            ? SKImageFilter.CreateDropShadow(0, font.Size * 0.04f, font.Size * 0.05f, font.Size * 0.05f, shade)
            : null;
        paint.ImageFilter = shadow;

        // Shrinks to fit (shared with overlays), wrapping between words first when the layer asks to.
        // Letter and line spacing are part of the fit, so spaced-out text still fits its box.
        // Characters the font can't draw are swapped for ones it can ("&" → "and", "é" → "e") before fitting, so the fit
        // measures what's drawn.
        IReadOnlyList<string> drawable = [.. layer.Lines.Select(line => cache.Drawable(typeface, line))];
        IReadOnlyList<string> lines = SchedularrTV.Service.Rendering.TextFit.Fit(drawable, bounds, font, paint, layer.WordWrap,
            letterSpacing: layer.LetterSpacing, lineSpacing: layer.LineSpacing);

        float x = layer.Align switch
        {
            SKTextAlign.Center => bounds.MidX,
            SKTextAlign.Right => bounds.Right,
            _ => bounds.Left
        };
        float block = SchedularrTV.Service.Rendering.TextFit.Block(font, lines.Count, layer.LineSpacing);
        float y = layer.VerticalAlign switch
        {
            PromoVerticalAlign.Middle => bounds.Top + (bounds.Height - block) / 2,
            PromoVerticalAlign.Bottom => bounds.Bottom - block,
            _ => bounds.Top
        } - font.Metrics.Ascent;

        float lineHeight = SchedularrTV.Service.Rendering.TextFit.LineHeight(font, layer.LineSpacing);
        foreach (string line in lines)
        {
            SchedularrTV.Service.Rendering.TextFit.Draw(canvas, line, x, y, layer.Align, font, paint, layer.LetterSpacing);
            y += lineHeight;
        }
    }

    private static void DrawImage(SKCanvas canvas, SKImage image, SKRect bounds, bool cover, SKPaint? paint)
    {
        float scale = cover
            ? Math.Max(bounds.Width / image.Width, bounds.Height / image.Height)
            : Math.Min(bounds.Width / image.Width, bounds.Height / image.Height);
        var destination = SKRect.Create(
            bounds.MidX - image.Width * scale / 2,
            bounds.MidY - image.Height * scale / 2,
            image.Width * scale,
            image.Height * scale);

        canvas.Save();
        canvas.ClipRect(bounds);
        canvas.DrawImage(image, destination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        canvas.Restore();
    }

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    private static string Seconds(TimeSpan value) =>
        Math.Max(0, value.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);

    private static IEnumerable<string> Arguments(PromoComposition composition, string outputPath, bool videoBackground, string? mixedAudio,
        VideoEncoderChoice encoding, int hardwareBitrateKbps)
    {
        string fps = composition.Fps.ToString(CultureInfo.InvariantCulture);
        string duration = Seconds(composition.Duration);
        List<string> args =
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{composition.Width}x{composition.Height}", "-r", fps, "-i", "-"
        ];

        int next = 1;
        int? background = null;
        if (videoBackground)
        {
            args.AddRange(composition.BackgroundStart > TimeSpan.Zero
                ? ["-stream_loop", "-1", "-ss", Seconds(composition.BackgroundStart), "-i", composition.BackgroundPath!]
                : ["-stream_loop", "-1", "-i", composition.BackgroundPath!]);
            background = next++;
        }

        int audio = next;
        args.AddRange(mixedAudio is null ? ["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo"] : ["-i", mixedAudio]);
        next++;

        int? overlay = null;
        if (composition.OverlayPath is { } overlayPath)
        {
            args.AddRange(["-i", overlayPath]);
            overlay = next++;
        }

        string size = $"{composition.Width}:{composition.Height}";
        string overlayChain = overlay is { } generated
            ? $"[{generated}:v:0]scale={size}:force_original_aspect_ratio=decrease,setsar=1,fps={fps},format=yuva420p,"
              + $"tpad=stop_mode=clone:stop_duration={duration}[ov];"
            : string.Empty;
        const string centred = "overlay=(W-w)/2:(H-h)/2:format=auto";

        string video = background is { } index
            ? $"{overlayChain}[{index}:v:0]scale={size}:force_original_aspect_ratio=increase,crop={size},fps={fps},setsar=1[bg];"
              + (overlay is null ? "[bg]" : $"[bg][ov]{centred}[base];[base]")
              + "[0:v]overlay=format=auto,format=yuv420p[v]"
            : overlay is null
                ? "[0:v]format=yuv420p[v]"
                : $"{overlayChain}[0:v][ov]{centred},format=yuv420p[v]";

        args.AddRange(
        [
            "-filter_complex", $"{video};[{audio}:a]apad[a]",
            "-map", "[v]", "-map", "[a]", "-t", duration, "-r", fps
        ]);

        // Software: quality-based (crf 22, tuned for animation). Hardware encoders have no quality setting, so they get
        // the 1080p bitrate scaled to the promo's size.
        int bitrate = Math.Max(1500, (int)(Math.Max(hardwareBitrateKbps, 500) * (composition.Width * (double)composition.Height) / (1920 * 1080)));
        args.AddRange(encoding.VideoArguments(bitrate, crf: 22, profileHigh: false, tune: "animation"));

        args.AddRange(["-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", outputPath]);
        return args;
    }

    /// <summary>
    /// The voice's volume filter (empty at 1): quieter is a plain gain; a boost is followed by a limiter so loud peaks
    /// are held under full scale instead of clipping. Music ducking keys off the voice as it ends up.
    /// </summary>
    public static string VoiceLevel(double volume)
    {
        double level = double.IsFinite(volume) ? Math.Clamp(volume, 0, SchedularrTV.Core.Configuration.GeneratedVoiceConfig.MaxVolume) : 1;
        if (Math.Abs(level - 1) < 0.001)
        {
            return string.Empty;
        }

        string gain = $",volume={level.ToString("0.###", CultureInfo.InvariantCulture)}";
        return level > 1 ? $"{gain},alimiter=limit=0.95:attack=5:release=50:level=disabled" : gain;
    }

    private static List<string> AudioArguments(PromoComposition composition, string outputPath)
    {
        List<string> args = ["-hide_banner", "-loglevel", "error", "-y"];
        args.AddRange(composition.AudioPath is { } audioPath ? ["-i", audioPath] : ["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo"]);
        PromoMusic[] beds = [.. new[] { composition.Music, composition.BackgroundAudio, composition.OverlayAudio }.OfType<PromoMusic>(), .. composition.LayerAudio];
        foreach (PromoMusic bed in beds)
        {
            args.AddRange(bed.Loop
                ? ["-stream_loop", "-1", "-ss", Seconds(bed.StartAt), "-i", bed.Path]
                : ["-ss", Seconds(bed.StartAt), "-i", bed.Path]);
        }

        string delay = ((long)composition.AudioStart.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        string tempo = Math.Clamp(composition.AudioTempo, 0.5, 2).ToString("0.###", CultureInfo.InvariantCulture);
        string voice = $"[0:a]aresample=48000,aformat=channel_layouts=stereo,atempo={tempo}{VoiceLevel(composition.AudioVolume)},adelay={delay}:all=1,apad";
        string ending = $"afade=t=out:st={Seconds(composition.Duration - TimeSpan.FromMilliseconds(250))}:d=0.25[a]";
        bool voiced = composition.AudioPath is not null;
        int keys = voiced ? beds.Count(bed => bed.DuckUnderVoice) : 0;
        List<string> chains = [keys == 0 ? $"{voice}[voice]" : $"{voice},asplit={keys + 1}[voice]{string.Concat(Enumerable.Range(0, keys).Select(key => $"[key{key}]"))}"];
        List<string> mix = ["[voice]"];
        int nextKey = 0;

        // The music can also dip under the episode clip's sound: the clip (as loud as it plays, after its own volume
        // and any layers muting it) is split off to drive the music's compressor.
        int clip = composition.BackgroundAudio is { } background ? Array.IndexOf(beds, background) : -1;
        bool musicUnderClip = clip >= 0 && composition.Music is { DuckUnderBackground: true } && Array.IndexOf(beds, composition.Music) is >= 0;
        for (int index = 0; index < beds.Length; index++)
        {
            PromoMusic bed = beds[index];
            TimeSpan fadeOut = bed.FadeOut > composition.Duration ? composition.Duration : bed.FadeOut;
            chains.Add($"[{index + 1}:a:0]aresample=48000,aformat=channel_layouts=stereo,"
                // A video layer's sound held back until its picture starts playing (its levels are in promo time, after this).
                + (bed.Delay > TimeSpan.Zero ? $"adelay={(long)bed.Delay.TotalMilliseconds}:all=1," : string.Empty)
                + $"volume={Math.Clamp(bed.Volume, 0, 1).ToString("0.###", CultureInfo.InvariantCulture)},"
                + $"afade=t=in:st=0:d={Seconds(bed.FadeIn)},afade=t=out:st={Seconds(composition.Duration - fadeOut)}:d={Seconds(fadeOut)},"
                // Layers that block the episode's sound: quoted, since the expression is full of commas.
                + (bed.Levels is { Count: > 0 } levels ? $"volume='{PromoAudioEnvelope.Expression(levels)}':eval=frame," : string.Empty)
                + (musicUnderClip && index == clip ? $"apad,asplit=2[bed{index}][clipkey]" : $"apad[bed{index}]"));
            string label = $"[bed{index}]";
            if (voiced && bed.DuckUnderVoice)
            {
                chains.Add($"{label}[key{nextKey++}]sidechaincompress=threshold=0.02:ratio=10:attack=15:release=350[ducked{index}]");
                label = $"[ducked{index}]";
            }

            if (musicUnderClip && ReferenceEquals(bed, composition.Music))
            {
                // Gentler than under the voice: the episode's sound comes and goes, and the music should ease with it.
                chains.Add($"{label}[clipkey]sidechaincompress=threshold=0.03:ratio=6:attack=40:release=600[underclip{index}]");
                label = $"[underclip{index}]";
            }

            mix.Add(label);
        }

        string sound = beds.Length == 0
            ? $"{voice},{ending}"
            : $"{string.Join(';', chains)};{string.Concat(mix)}amix=inputs={mix.Count}:duration=first:normalize=0,{ending}";

        args.AddRange(["-filter_complex", sound, "-map", "[a]", "-t", Seconds(composition.Duration), "-c:a", "pcm_s16le", outputPath]);
        return args;
    }

    private async Task RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        using Process process = Process.Start(new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }.WithArguments(arguments)) ?? throw new InvalidOperationException("ffmpeg could not be started.");
        Work.Track(process, "Generated Promos", "Mixing promo audio");

        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg could not mix the audio (exit {process.ExitCode}): {(await error.ConfigureAwait(false)).Trim()}");
        }
    }
}

/// <summary>Decoded images shared across frames; a layer never re-decodes its file per frame.</summary>
public sealed class PromoImageCache : IDisposable
{
    private readonly Dictionary<string, SKImage?> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Family, bool Bold), SKTypeface> _typefaces = [];
    private readonly Dictionary<(SKTypeface Typeface, string Text), string> _drawable = [];

    /// <summary>
    /// <paramref name="text"/> as <paramref name="typeface"/> can draw it (see <see cref="SchedularrTV.Service.Rendering.GlyphFallback"/>),
    /// worked out once per line and font rather than every frame.
    /// </summary>
    public string Drawable(SKTypeface typeface, string text)
    {
        if (!_drawable.TryGetValue((typeface, text), out string? drawable))
        {
            drawable = SchedularrTV.Service.Rendering.GlyphFallback.Apply(text, typeface);
            _drawable[(typeface, text)] = drawable;
        }

        return drawable;
    }

    public SKTypeface Typeface(string family, bool bold)
    {
        if (!_typefaces.TryGetValue((family, bold), out SKTypeface? typeface))
        {
            typeface = SKTypeface.FromFamilyName(family, bold ? SKFontStyle.Bold : SKFontStyle.Normal);
            // A family that isn't installed may have come with an imported template, kept in the data folder.
            if (!FontFiles.IsFamily(typeface, family) && FontFiles.KeptTypeface(family, bold) is { } kept)
            {
                typeface?.Dispose();
                typeface = kept;
            }

            _typefaces[(family, bold)] = typeface;
        }

        return typeface;
    }

    /// <summary>Whether <paramref name="path"/> is an animated picture (a GIF or WebP with several frames).</summary>
    public bool IsAnimated(string? path) => SchedularrTV.Service.Images.AnimatedImages.IsAnimated(path);

    /// <summary>
    /// The picture at <paramref name="path"/> as it shows <paramref name="playing"/> into its playing: an animated one's
    /// frame then (looping), else the picture.
    /// </summary>
    public SKImage? GetAt(string path, TimeSpan playing) =>
        SchedularrTV.Service.Images.AnimatedImages.Load(path) is { } animated ? animated.FrameAt(playing.TotalSeconds) : Get(path);

    public SKImage? Get(string path)
    {
        if (!_images.TryGetValue(path, out SKImage? image))
        {
            image = File.Exists(path) ? SKImage.FromEncodedData(path) : null;
            _images[path] = image;
        }

        return image;
    }

    public void Dispose()
    {
        foreach (SKImage? image in _images.Values) image?.Dispose();
        foreach (SKTypeface typeface in _typefaces.Values) typeface.Dispose();
        _images.Clear();
        _typefaces.Clear();
    }
}

public static class ProcessStartInfoExtensions
{
    public static ProcessStartInfo WithArguments(this ProcessStartInfo info, IEnumerable<string> arguments)
    {
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }
}
