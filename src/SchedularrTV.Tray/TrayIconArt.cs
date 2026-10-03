using Avalonia.Controls;
using SkiaSharp;

namespace SchedularrTV.Tray;

/// <summary>
/// A small CRT-style glyph drawn in code, so the app ships without image resources: the screen is green
/// when serving, amber while starting or stopping, red when something is wrong and dark when stopped.
/// </summary>
internal static class TrayIconArt
{
    public static WindowIcon Create(ServerState state)
    {
        SKColor body = state == ServerState.Stopped ? new SKColor(110, 118, 132) : new SKColor(79, 140, 255);
        SKColor screen = state switch
        {
            ServerState.Running => new SKColor(56, 178, 106),
            ServerState.Starting or ServerState.Stopping => new SKColor(243, 183, 79),
            ServerState.Failed => new SKColor(242, 103, 109),
            _ => new SKColor(60, 64, 74)
        };

        using var bitmap = new SKBitmap(64, 64, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var antenna = new SKPaint { Color = body, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 5, StrokeCap = SKStrokeCap.Round };
            using var fill = new SKPaint { Color = body, IsAntialias = true, Style = SKPaintStyle.Fill };
            using var glass = new SKPaint { Color = screen, IsAntialias = true, Style = SKPaintStyle.Fill };

            canvas.DrawLine(20, 6, 30, 20, antenna);
            canvas.DrawLine(44, 6, 34, 20, antenna);
            canvas.DrawRoundRect(new SKRect(4, 20, 60, 60), 8, 8, fill);
            canvas.DrawRoundRect(new SKRect(11, 27, 53, 53), 4, 4, glass);
        }

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        return new WindowIcon(new MemoryStream(png.ToArray()));
    }
}
