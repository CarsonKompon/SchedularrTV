using System.Globalization;
using System.Text.Json;
using SchedularrTV.Core.Configuration;
using SchedularrTV.GeneratedPromos;
using SkiaSharp;

namespace SchedularrTV.CreditSqueeze;

/// <summary>A canvas in a declared display shape. Coordinates are in its own pixels.</summary>
public sealed class SqueezeCanvas
{
    public const string Credits = "credits";
    public const string Panel = "panel";
    public static readonly IReadOnlyList<string> Ratios = ["16:9", "4:3", "16:10", "1:1", "21:9", "9:16"];

    public string AspectRatio { get; set; } = "16:9";
    public string BackgroundColor { get; set; } = "#000000";
    public List<GeneratedLayer> Layers { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore, YamlDotNet.Serialization.YamlIgnore]
    public int Height => 720;
    [System.Text.Json.Serialization.JsonIgnore, YamlDotNet.Serialization.YamlIgnore]
    public int Width => SqueezeLayout.Even(Height * Aspect);
    [System.Text.Json.Serialization.JsonIgnore, YamlDotNet.Serialization.YamlIgnore]
    public double Aspect => ParseAspect(AspectRatio);

    [System.Text.Json.Serialization.JsonIgnore, YamlDotNet.Serialization.YamlIgnore]
    public bool UsesCreditsText => Layers.Any(layer => layer.Kind == GeneratedLayerKind.Text && layer.CreditsText is not null);

    [System.Text.Json.Serialization.JsonIgnore, YamlDotNet.Serialization.YamlIgnore]
    public bool HasCreditsTextFallback => UsesCreditsText && Layers.Where(layer => layer.Kind == GeneratedLayerKind.Text && layer.CreditsText is not null)
        .All(layer => !string.IsNullOrWhiteSpace(layer.CreditsText!.FallbackText));

    private static IReadOnlyList<CreditsOcrLine> CreditLines(GeneratedCreditsTextOptions credits, SqueezeJob job)
    {
        if (job.OcrTranscript is { UsedFallback: false, Lines.Count: > 0 } transcript) return transcript.Lines;
        return string.IsNullOrWhiteSpace(credits.FallbackText) ? [] : Tokens(credits.FallbackText, job)
            .Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => new CreditsOcrLine(line.Trim(), 100)).ToArray();
    }

    /// <summary>Every page gets an equal share of the text layer's visible time, without losing the last page.</summary>
    public static IReadOnlyList<PromoTextPage> CreditsPages(IReadOnlyList<CreditsOcrLine> lines, int linesPerPage, TimeSpan start, TimeSpan end)
    {
        if (end <= start || lines.Count == 0) return [];
        int size = Math.Clamp(linesPerPage, 1, 30);
        int count = (lines.Count + size - 1) / size;
        return Enumerable.Range(0, count).Select(index => new PromoTextPage(start + TimeSpan.FromTicks((end - start).Ticks * index / count),
            lines.Skip(index * size).Take(size).Select(line => line.Text).ToArray())).ToArray();
    }

    /// <summary>Use the page already on screen when the layer appears, then follow the source's sampled changes.</summary>
    public static IReadOnlyList<PromoTextPage> SourceCreditsPages(IReadOnlyList<CreditsOcrPage> pages, TimeSpan start, TimeSpan end)
    {
        if (end <= start || pages.Count == 0) return [];
        var ordered = pages.OrderBy(page => page.At).ToArray();
        var current = ordered.LastOrDefault(page => page.At <= start);
        List<PromoTextPage> result = [new(start, current?.Lines.Select(line => line.Text).ToArray() ?? [])];
        result.AddRange(ordered.Where(page => page.At > start && page.At < end)
            .Select(page => new PromoTextPage(page.At, page.Lines.Select(line => line.Text).ToArray())));
        return result;
    }

    private static IReadOnlyList<PromoTextPage> TextPages(GeneratedLayer layer, SqueezeJob job)
    {
        var credits = layer.CreditsText!;
        TimeSpan start = layer.Start > TimeSpan.Zero ? layer.Start : TimeSpan.Zero;
        TimeSpan end = layer.End is { } stop && stop < job.Length ? stop : job.Length;
        if (end <= start) return [];
        IReadOnlyList<PromoTextPage> pages;
        if (credits.MatchSourcePages && job.OcrTranscript is { UsedFallback: false, Pages.Count: > 0 } source)
            pages = SourceCreditsPages(source.Pages, start, end);
        else if (credits.MatchSourcePages)
            // Authored fallback has no scan boundaries: show it together rather than applying a hidden line limit.
            pages = [new(start, CreditLines(credits, job).Select(line => line.Text).ToArray())];
        else pages = CreditsPages(CreditLines(credits, job), credits.LinesPerPage, start, end);
        return pages.Select(page => page with { Lines = page.Lines.Select(line => GeneratedTextStyle.Cased(line, layer.TextCase)).ToArray() }).ToArray();
    }

    public static double ParseAspect(string value)
    {
        if (value.Split(':') is [var left, var right]
            && double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out double width)
            && double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out double height)
            && double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0)
            return Math.Clamp(width / height, 0.25, 4);
        throw new ArgumentException("Choose a valid canvas aspect ratio (for example 16:9).", nameof(value));
    }

    public static SqueezeCanvas Create(string ratio)
    {
        var canvas = new SqueezeCanvas { AspectRatio = ratio };
        canvas.Layers.Add(new GeneratedLayer
        {
            Name = "Source credits video", Kind = GeneratedLayerKind.Video,
            Width = canvas.Width, Height = canvas.Height,
            TransitionIn = GeneratedTransition.None, TransitionOut = GeneratedTransition.None,
            Video = new GeneratedVideoOptions { Episode = Credits, Stretch = true, IncludeAudio = true }
        });
        return canvas;
    }

    public SqueezeCanvas Copy() => JsonSerializer.Deserialize<SqueezeCanvas>(JsonSerializer.Serialize(this))!;

    /// <summary>Prefer the source's shape; a lone canvas always wins, otherwise use the nearest shape.</summary>
    public static SqueezeCanvas? Select(IReadOnlyList<SqueezeCanvas>? canvases, double? sourceAspect) =>
        canvases?.OrderBy(canvas => Math.Abs(Math.Log(canvas.Aspect / (sourceAspect is > 0 ? sourceAspect.Value : 16 / 9d)))).FirstOrDefault();

    public PromoComposition Compose(SqueezeJob job, int outputHeight = 720)
    {
        float scale = Math.Clamp(outputHeight, 144, 2160) / (float)Height;
        SKRect Bounds(float x, float y, float width, float height) => SKRect.Create(x * scale, y * scale, Math.Max(2, width * scale), Math.Max(2, height * scale));
        List<PromoLayer> layers = [];
        List<PromoMusic> audio = [];
        foreach (var (item, index) in Layers.Select((item, index) => (item, index)))
        {
            var bounds = Bounds(item.X, item.Y, item.Width, item.Height);
            GeneratedVideoOptions options = item.Video ?? new();
            string? path = options.Episode switch
            {
                Credits => job.SourcePath,
                Panel => job.PromoPath,
                _ => item.Path
            };
            PromoLayer? layer = item.Kind switch
            {
                 GeneratedLayerKind.Rect => new PromoRectLayer { Bounds = bounds, Color = Color(item.Color), Shape = item.Shape ?? GeneratedShape.Rectangle, CornerRadius = item.CornerRadius * scale, SoftEdges = Math.Max(0, item.SoftEdges ?? 0) * scale },
                GeneratedLayerKind.Text => new PromoTextLayer
                {
                    Bounds = bounds, Lines = item.CreditsText is null ? GeneratedTextStyle.Cased(Tokens(item.Text ?? "", job), item.TextCase).Split('\n') : [], Color = Color(item.Color),
                    Pages = item.CreditsText is not null ? TextPages(item, job) : null,
                    FontFamily = item.FontFamily, MaxFontSize = item.FontSize * scale, Bold = item.Bold,
                    FontWeight = item.FontWeight, Italic = item.Italic,
                    Shadow = item.Shadow ? new SKColor(0, 0, 0, 160) : null,
                    LetterSpacing = SchedularrTV.Service.Rendering.TextFit.LetterSpacing(item.LetterSpacing),
                    LineSpacing = SchedularrTV.Service.Rendering.TextFit.LineSpacing(item.LineSpacing),
                    Align = (SKTextAlign)item.Align, WordWrap = item.WordWrap == true,
                    VerticalAlign = (PromoVerticalAlign)(item.VerticalAlign ?? GeneratedVerticalAlign.Top)
                },
                GeneratedLayerKind.Image when !string.IsNullOrWhiteSpace(path) => new PromoImageLayer { Bounds = bounds, Path = path },
                GeneratedLayerKind.Video when options.Episode == Panel && path is not null && PromoRenderer.IsImage(path) => new PromoImageLayer { Bounds = bounds, Path = path },
                GeneratedLayerKind.Video when !string.IsNullOrWhiteSpace(path) => new PromoVideoLayer
                {
                    Bounds = bounds, Path = path,
                    SourceStart = options.Episode == Credits ? job.SourceStart : options.FileStart,
                    StartsWithLayer = options.Episode != Credits,
                    Loop = options.Episode is not (Credits or Panel) && options.Loop,
                    Fill = options.Fill, Stretch = options.Stretch,
                    SourceAspect = options.Episode == Credits ? Aspect : null, SourceCrop = job.CanvasCrop,
                    ChromaKey = options.ChromaKey, KeyColor = options.KeyColor, KeyTolerance = options.KeyTolerance, KeySoftness = options.KeySoftness
                },
                _ => null
            };
            if (layer is null) continue;
            layer = layer with
            {
                Id = index.ToString(CultureInfo.InvariantCulture),
                Start = item.Start, End = item.End, In = (PromoTransition)item.TransitionIn, Out = (PromoTransition)item.TransitionOut,
                TransitionDuration = item.TransitionDuration, TransitionOutDuration = item.TransitionOutDuration,
                Opacity = item.Opacity, Rotation = item.Rotation ?? 0,
                Keyframes = item.Keyframes?.OrderBy(key => key.At).Select(key => new PromoKeyframe(key.At,
                    Bounds(key.X, key.Y, key.Width, key.Height), key.Opacity, (PromoEasing)key.Easing, key.Rotation)).ToList()
            };
            layers.Add(layer);
            if (layer is PromoVideoLayer video && options.IncludeAudio)
                audio.Add(new PromoMusic(video.Path, options.Volume * (options.Episode == Credits ? job.CreditsVolume : 1), video.SourceStart,
                    TimeSpan.Zero, TimeSpan.Zero, false, video.Loop)
                {
                    Delay = video.StartsWithLayer ? video.Start : TimeSpan.Zero,
                    Levels = PromoAudioEnvelope.ForLayer(video, job.Length)
                });
        }
        return new PromoComposition { Width = SqueezeLayout.Even(Width * scale), Height = SqueezeLayout.Even(Height * scale), Duration = job.Length, BackgroundColor = Color(BackgroundColor), Layers = layers, LayerAudio = audio };
    }

    private static SKColor Color(string text) => SKColor.TryParse(text, out var color) ? color : SKColors.Black;
    private static string Tokens(string text, SqueezeJob job) => text.Replace("{up_next}", job.Next.Title, StringComparison.Ordinal).Replace("{next}", job.Next.Title, StringComparison.Ordinal)
        .Replace("{time}", job.Next.When, StringComparison.Ordinal).Replace("{channel}", job.ChannelName, StringComparison.Ordinal)
        .Replace("{show}", job.EndingShow ?? "", StringComparison.Ordinal).Replace("{heading}", job.Heading, StringComparison.Ordinal);
}
