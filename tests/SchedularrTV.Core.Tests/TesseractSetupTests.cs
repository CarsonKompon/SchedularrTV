using Microsoft.Extensions.Configuration;
using SchedularrTV.CreditSqueeze;
using SchedularrTV.Plugins;
using Xunit;
using Xunit.Abstractions;

namespace SchedularrTV.Core.Tests;

public sealed class TesseractSetupTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tesseract-setup-test-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }

    [Theory]
    [InlineData("../eng")]
    [InlineData("eng+../../foo")]
    [InlineData("")]
    [InlineData("C:\\data")]
    public void Language_downloads_cannot_escape_the_data_directory(string language) =>
        Assert.Throws<InvalidOperationException>(() => TesseractSetup.Languages(language));

    [Fact]
    public void Language_codes_support_multiple_languages_and_scripts() =>
        Assert.Equal(["eng", "fra", "script/Latin"], TesseractSetup.Languages("eng + fra + eng + script/Latin"));

    [Fact]
    public async Task Missing_executable_is_reported_without_downloading_anything()
    {
        var context = new Context(_folder);
        context.Configuration["TesseractPath"] = Path.Combine(_folder, "missing.exe");
        using var setup = new TesseractSetup(new CreditSqueezeSettings(context));
        Assert.False((await setup.CheckAsync(CancellationToken.None)).Ready);
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public async Task Only_completed_downloads_are_selected_and_later_custom_settings_take_precedence()
    {
        var context = new Context(_folder);
        context.Configuration["TesseractPath"] = Path.Combine(_folder, "missing.exe");
        var settings = new CreditSqueezeSettings(context);
        Directory.CreateDirectory(settings.ManagedTesseractFolder);
        string managed = Path.Combine(settings.ManagedTesseractFolder, "tesseract.exe");
        await File.WriteAllTextAsync(managed, "test");
        Assert.NotEqual(managed, settings.Ocr.Executable);
        await File.WriteAllTextAsync(Path.Combine(settings.ManagedTesseractFolder, ".ready"), settings.ConfiguredTesseract!);
        Assert.Equal(managed, settings.Ocr.Executable);
        Assert.Equal(Path.Combine(settings.ManagedTesseractFolder, "tessdata"), settings.Ocr.DataDirectory);
        context.Configuration["TesseractPath"] = Path.Combine(_folder, "custom.exe");
        Assert.Equal(context.Configuration["TesseractPath"], settings.Ocr.Executable);
        Assert.Null(settings.Ocr.DataDirectory);
    }

    // Opt-in: downloads only into a unique temporary folder and removes it afterwards.
    [Fact]
    [Trait("Category", "ToolDownload")]
    public async Task Downloaded_windows_tools_can_run_and_find_the_requested_language()
    {
        if (Environment.GetEnvironmentVariable("SCHEDULARR_TEST_TOOL_DOWNLOAD") != "1") return;
        var context = new Context(_folder);
        context.Configuration["TesseractPath"] = Path.Combine(_folder, "missing.exe");
        using var setup = new TesseractSetup(new CreditSqueezeSettings(context));
        if (!setup.CanDownload) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await setup.InstallAsync(null, timeout.Token);
        var status = await setup.CheckAsync(timeout.Token);
        Assert.True(status.Ready, status.Problem);
        string imagePath = Path.Combine(_folder, "credits.png");
        using (var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(1000, 180)))
        {
            surface.Canvas.Clear(SkiaSharp.SKColors.White);
            using var font = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.Default, 70);
            using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black, IsAntialias = true };
            surface.Canvas.DrawText("Written by Alex Smith", 30, 110, SkiaSharp.SKTextAlign.Left, font, paint);
            using var image = surface.Snapshot();
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(imagePath, data.ToArray(), timeout.Token);
        }
        var lines = await new TesseractCreditsOcr().ReadAsync(imagePath, new CreditSqueezeSettings(context).Ocr, timeout.Token);
        Assert.Contains("Alex Smith", string.Join(' ', lines.Select(line => line.Text)));
        using (var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(1000, 260)))
        {
            surface.Canvas.Clear(new SkiaSharp.SKColor(235, 220, 90));
            surface.Canvas.Scale(.65f, 1);
            using var font = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.Default, 70);
            using var outline = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black, Style = SkiaSharp.SKPaintStyle.Stroke, StrokeWidth = 3, IsAntialias = true };
            using var fill = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(210, 30, 45), IsAntialias = true };
            foreach (var (text, y) in new[] { ("Matt Brown", 100f), ("Michelle Smith", 200f) })
            {
                surface.Canvas.DrawText(text, 40, y, SkiaSharp.SKTextAlign.Left, font, outline);
                surface.Canvas.DrawText(text, 40, y, SkiaSharp.SKTextAlign.Left, font, fill);
            }
            using var image = surface.Snapshot();
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(imagePath, data.ToArray(), timeout.Token);
        }
        var stylized = await new TesseractCreditsOcr().ReadAsync(imagePath, new CreditSqueezeSettings(context).Ocr, timeout.Token);
        Assert.Contains("Matt Brown", string.Join(' ', stylized.Select(line => line.Text)));
        Assert.Contains("Michelle Smith", string.Join(' ', stylized.Select(line => line.Text)));
        Assert.Empty(Directory.GetFiles(_folder, ".ocr-contrast-*"));
        string secondPath = Path.Combine(_folder, "second card.png");
        using (var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(1000, 260)))
        {
            surface.Canvas.Clear(SkiaSharp.SKColors.Black);
            using var font = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.Default, 70);
            using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.White, IsAntialias = true };
            surface.Canvas.DrawText("Alex Smith", 40, 100, SkiaSharp.SKTextAlign.Left, font, paint);
            surface.Canvas.DrawText("Jamie Roe", 40, 200, SkiaSharp.SKTextAlign.Left, font, paint);
            using var image = surface.Snapshot();
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(secondPath, data.ToArray(), timeout.Token);
        }
        string[] frames = Enumerable.Range(0, 12).Select(index => index % 2 == 0 ? imagePath : secondPath).ToArray();
        var fast = new CreditSqueezeSettings(context).Ocr with { Profile = "fast" };
        var timer = System.Diagnostics.Stopwatch.StartNew();
        List<string> baseline = [];
        foreach (string frame in frames) baseline.Add(string.Join(' ', (await new TesseractCreditsOcr().ReadAsync(frame, fast, timeout.Token)).Select(line => line.Text)));
        long individualMs = timer.ElapsedMilliseconds;
        timer.Restart();
        var batched = await new TesseractCreditsOcr().ReadBatchAsync(frames, fast, timeout.Token);
        long batchMs = timer.ElapsedMilliseconds;
        Assert.Equal(1, batched.Processes);
        Assert.Equal(12, batched.Frames.Count);
        for (int index = 0; index < frames.Length; index++)
            Assert.Equal(baseline[index], string.Join(' ', batched.Frames[index].Select(line => line.Text)));
        output.WriteLine($"12-frame CLI benchmark: individual={individualMs}ms; batch={batchMs}ms; processes=12 -> {batched.Processes}.");

        await setup.InstallQualityModelsAsync(null, timeout.Token);
        var enhanced = new CreditSqueezeSettings(context).Ocr;
        Assert.NotNull(enhanced.QualityDataDirectory);
        timer.Restart();
        var qualityEngine = new TesseractCreditsOcr();
        var quality = await qualityEngine.ReadBatchAsync([imagePath, secondPath], enhanced, timeout.Token);
        Assert.Equal("tessdata_best", quality.Model);
        Assert.Contains("Matt Brown", string.Join(' ', quality.Frames[0].Select(line => line.Text)));
        Assert.Contains("Alex Smith", string.Join(' ', quality.Frames[1].Select(line => line.Text)));
        output.WriteLine($"Accuracy-model benchmark: {timer.ElapsedMilliseconds}ms for two synthetic cards, {quality.Processes} process(es), {quality.RetriedFrames} retries.");
        var cached = await qualityEngine.ReadBatchAsync([imagePath, secondPath], enhanced, timeout.Token);
        Assert.Equal(0, cached.Processes);
        Assert.Equal(2, cached.CachedFrames);
        Assert.Empty(Directory.GetFiles(_folder, ".ocr-input-*"));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_folder, "tools"), ".tesseract-*"));
    }

    private sealed class Context(string folder) : IPluginContext
    {
        public PluginManifest Manifest { get; } = new() { Id = "credit-squeeze", Name = "Credit squeeze" };
        public string? PluginDirectory => null;
        public string DataDirectory => folder;
        public string AppDataDirectory => folder;
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().AddInMemoryCollection().Build();
        public IConfiguration AppConfiguration { get; } = new ConfigurationBuilder().Build();
        public bool IsLoaded(string pluginId) => false;
        public string StaticFilesPath => "/plugins/credit-squeeze/";
    }
}
