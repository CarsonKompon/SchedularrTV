using System.Globalization;
using System.Text.Json;
using SchedularrTV.Core.Configuration;

namespace SchedularrTV.GeneratedPromos;

/// <summary>Template edits shared by the generated promo editor.</summary>
public static class GeneratedPromoEditing
{
    public static (string Rgb, int AlphaPercent) SplitColor(string? hex)
    {
        string value = hex?.Trim() ?? string.Empty;
        if (value.Length == 9 && value[0] == '#'
            && byte.TryParse(value.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte alpha))
        {
            return ($"#{value[3..].ToLowerInvariant()}", (int)Math.Round(alpha / 2.55));
        }

        return value.Length == 7 && value[0] == '#' ? (value.ToLowerInvariant(), 100) : ("#ffffff", 100);
    }

    public static string JoinColor(string rgb, int alphaPercent)
    {
        string color = rgb.Length == 7 && rgb[0] == '#' ? rgb.ToUpperInvariant() : "#FFFFFF";
        int alpha = (int)Math.Round(Math.Clamp(alphaPercent, 0, 100) * 2.55);
        return alpha >= 255 ? color : $"#{alpha:X2}{color[1..]}";
    }

    public static void Move<T>(List<T> items, T item, int delta)
    {
        int from = items.IndexOf(item);
        int to = from + delta;
        if (from < 0 || to < 0 || to >= items.Count)
        {
            return;
        }

        items.RemoveAt(from);
        items.Insert(to, item);
    }

    public static T Copy<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    public static string UniqueName(IEnumerable<string> existing, string name)
    {
        HashSet<string> taken = new(existing, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name))
        {
            return name;
        }

        for (int index = 2; ; index++)
        {
            string candidate = $"{name} {index}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>A sensibly sized layer centred on the canvas.</summary>
    public static GeneratedLayer NewLayer(GeneratedPromoTemplate template, GeneratedLayerKind kind)
    {
        float width = template.Width * (kind == GeneratedLayerKind.Rect ? 0.5f : 0.4f);
        float height = template.Height * (kind is GeneratedLayerKind.Text or GeneratedLayerKind.UpNext || GeneratedPromoKinds.IsShowLayer(kind) ? 0.12f : 0.3f);
        var layer = new GeneratedLayer
        {
            Kind = kind,
            Name = UniqueName(template.Layers.Select(existing => existing.Name), kind switch
            {
                GeneratedLayerKind.UpNext => "Up Next",
                GeneratedLayerKind.NowPlaying => "Now Playing",
                GeneratedLayerKind.ThisHasBeen => "This Has Been",
                GeneratedLayerKind.ComingUp => "Coming Up",
                GeneratedLayerKind.Eventually => "Eventually",
                GeneratedLayerKind.YoureWatching => "You're Watching",
                GeneratedLayerKind.BackToTheShow => "Back to the Show",
                GeneratedLayerKind.Rect => "Rectangle",
                GeneratedLayerKind.Image => "Image",
                _ => "Text"
            }),
            X = (float)Math.Round((template.Width - width) / 2),
            Y = (float)Math.Round((template.Height - height) / 2),
            Width = (float)Math.Round(width),
            Height = (float)Math.Round(height),
            FontSize = (float)Math.Round(template.Height * 0.08),
            Color = kind == GeneratedLayerKind.Rect ? "#96000000" : "#FFFFFF",
            Text = kind == GeneratedLayerKind.Text ? "Only on {channel}" : null,
            UpNext = kind == GeneratedLayerKind.UpNext ? new GeneratedUpNextOptions() : null,
            Show = GeneratedPromoKinds.IsShowLayer(kind) ? GeneratedPromoResolver.DefaultShowOptions(kind) : null
        };
        return layer;
    }

    /// <summary>Pulls a layer whose centre is within <paramref name="threshold"/> of the canvas centre lines onto them.</summary>
    public static (float X, float Y, bool SnappedX, bool SnappedY) SnapToCenter(
        float x, float y, float width, float height, int canvasWidth, int canvasHeight, float threshold)
    {
        float centredX = (canvasWidth - width) / 2;
        float centredY = (canvasHeight - height) / 2;
        bool snapX = Math.Abs(x - centredX) <= threshold;
        bool snapY = Math.Abs(y - centredY) <= threshold;
        return (snapX ? centredX : x, snapY ? centredY : y, snapX, snapY);
    }

    /// <summary>Changes the output size, keeping every layer in the same place relative to the frame.</summary>
    public static void Resize(GeneratedPromoTemplate template, int width, int height)
    {
        float scaleX = width / (float)template.Width;
        float scaleY = height / (float)template.Height;
        foreach (GeneratedLayer layer in template.Layers)
        {
            layer.X = (float)Math.Round(layer.X * scaleX);
            layer.Y = (float)Math.Round(layer.Y * scaleY);
            layer.Width = Math.Max(1, (float)Math.Round(layer.Width * scaleX));
            layer.Height = Math.Max(1, (float)Math.Round(layer.Height * scaleY));
            layer.FontSize = Math.Max(4, (float)Math.Round(layer.FontSize * scaleY));
            layer.CornerRadius = (float)Math.Round(layer.CornerRadius * Math.Min(scaleX, scaleY));
        }

        template.Width = width;
        template.Height = height;
    }
}
