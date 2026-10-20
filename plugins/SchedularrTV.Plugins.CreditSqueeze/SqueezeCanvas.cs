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
                GeneratedLayerKind.Rect => new PromoRectLayer { Bounds = bounds, Color = Color(item.Color), Shape = item.Shape ?? GeneratedShape.Rectangle, CornerRadius = item.CornerRadius * scale },
                GeneratedLayerKind.Text => new PromoTextLayer
                {
                    Bounds = bounds, Lines = Tokens(item.Text ?? "", job).Split('\n'), Color = Color(item.Color),
                    FontFamily = item.FontFamily, MaxFontSize = item.FontSize * scale, Bold = item.Bold,
                    Align = (SKTextAlign)item.Align, WordWrap = item.WordWrap == true
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
    private static string Tokens(string text, SqueezeJob job) => text.Replace("{next}", job.Next.Title, StringComparison.Ordinal)
        .Replace("{time}", job.Next.When, StringComparison.Ordinal).Replace("{channel}", job.ChannelName, StringComparison.Ordinal)
        .Replace("{show}", job.EndingShow ?? "", StringComparison.Ordinal).Replace("{heading}", job.Heading, StringComparison.Ordinal);
}
