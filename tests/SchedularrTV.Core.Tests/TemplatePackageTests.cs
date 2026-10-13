using SchedularrTV.Core.Configuration;
using SchedularrTV.CreditSqueeze;
using SchedularrTV.GeneratedPromos;
using SchedularrTV.Overlays;
using SchedularrTV.Service.Editor;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class TemplatePackageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"template-packages-{Guid.NewGuid():N}");
    private readonly string _source;
    private readonly string _data;
    private readonly string _otherData;

    public TemplatePackageTests()
    {
        _source = Directory.CreateDirectory(Path.Combine(_directory, "source")).FullName;
        _data = Directory.CreateDirectory(Path.Combine(_directory, "data")).FullName;
        _otherData = Directory.CreateDirectory(Path.Combine(_directory, "other-data")).FullName;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Theory]
    [InlineData("node_modules/", "node_modules", true, true)]
    [InlineData("node_modules/", "src/node_modules", true, true)]
    [InlineData("node_modules/", "node_modules", false, false)]
    [InlineData("*.log", "logs/today.log", false, true)]
    [InlineData("/dist", "dist", true, true)]
    [InlineData("/dist", "src/dist", true, false)]
    [InlineData("docs/*.md", "docs/a.md", false, true)]
    [InlineData("docs/*.md", "docs/deep/a.md", false, false)]
    [InlineData("**/cache", "a/b/cache", true, true)]
    [InlineData("build/**", "build/x/y.js", false, true)]
    [InlineData("file?.txt", "file1.txt", false, true)]
    [InlineData("[ab].txt", "c.txt", false, false)]
    [InlineData("# comment", "# comment", false, false)]
    public void Gitignore_patterns_match_as_git_does(string pattern, string path, bool isDirectory, bool ignored) =>
        Assert.Equal(ignored, GitIgnore.IsIgnored([pattern], path, isDirectory));

    [Fact]
    public void Gitignore_negation_brings_a_file_back()
    {
        string[] lines = ["*.env", "!example.env"];
        Assert.True(GitIgnore.IsIgnored(lines, "secret.env", false));
        Assert.False(GitIgnore.IsIgnored(lines, "example.env", false));
    }

    [Fact]
    public void Folder_listing_follows_nested_gitignores_and_skips_git()
    {
        Write("index.js");
        Write(".gitignore", "node_modules/\n*.log\n");
        Write("node_modules/pkg/index.js");
        Write("debug.log");
        Write(".git/HEAD");
        Write("assets/.gitignore", "*.tmp\n!keep.tmp\n");
        Write("assets/a.tmp");
        Write("assets/keep.tmp");
        Write("assets/logo.png");

        List<string> files = [.. GitIgnore.Files(_source)];

        Assert.Equal(new[] { "assets/.gitignore", "assets/keep.tmp", "assets/logo.png", ".gitignore", "index.js" }, files);
    }

    [Fact]
    public async Task Kept_files_are_shared_by_content_and_left_alone_once_kept()
    {
        var assets = new TemplateAssets(_data);
        string logo = Write("pics/logo.png", "png bytes");
        string again = Write("other/logo.png", "png bytes");

        string kept = await assets.KeepAsync("overlays", logo);
        string keptAgain = await assets.KeepAsync("overlays", again);

        Assert.True(assets.IsKept(kept));
        Assert.Equal(kept, keptAgain);
        Assert.Equal("logo.png", Path.GetFileName(kept));
        Assert.Equal(kept, await assets.KeepAsync("overlays", kept));
        Assert.Equal("relative.png", await assets.KeepAsync("overlays", "relative.png"));

        File.Delete(logo);
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public async Task Generated_promo_round_trips_with_its_files_and_generator_folder()
    {
        string background = Write("media/loop.mp4", "video");
        string song = Write("media/song.mp3", "audio");
        string image = Write("media/logo.png", "image");
        string generator = Path.Combine(_source, "My Generator");
        Write("My Generator/render.js", "console.log('hi')");
        Write("My Generator/.gitignore", "node_modules/\n");
        Write("My Generator/node_modules/big/index.js");

        var template = new GeneratedPromoTemplate
        {
            Name = "Up Next",
            Background = background,
            Layers = [new GeneratedLayer { Kind = GeneratedLayerKind.Image, Path = image }],
            Music = new GeneratedMusicConfig
            {
                Paths = [song],
                SongVolumes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [song] = 0.5 },
                SongStarts = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase) { [song] = TimeSpan.FromSeconds(3) }
            },
            Generator = new GeneratedScriptConfig
            {
                Command = "node",
                WorkingDirectory = generator,
                SetupCommand = "npm install",
                Arguments = [Path.Combine(generator, "render.js"), "--out", "{output}"]
            }
        };

        string zip = await ExportAsync(_data, writer => GeneratedPromoPackages.ExportAsync(template, writer));

        var otherAssets = new TemplateAssets(_otherData);
        GeneratedPromoTemplate imported;
        await using (FileStream stream = File.OpenRead(zip))
        using (TemplatePackageReader reader = await new TemplatePackages(_otherData).OpenAsync(stream))
        {
            Assert.Equal(GeneratedPromoPackages.Kind, reader.Manifest.Kind);
            Assert.Contains("npm install", reader.Manifest.Runs!);
            imported = await GeneratedPromoPackages.ImportAsync(reader, otherAssets, ["Up Next"]);
        }

        Assert.Equal("Up Next 2", imported.Name);
        Assert.True(otherAssets.IsKept(imported.Background));
        Assert.Equal("video", File.ReadAllText(imported.Background!));
        Assert.True(otherAssets.IsKept(imported.Layers[0].Path));
        string importedSong = Assert.Single(imported.Music!.Paths);
        Assert.True(otherAssets.IsKept(importedSong));
        Assert.Equal(0.5, imported.Music.VolumeFor(importedSong));
        Assert.Equal(TimeSpan.FromSeconds(3), imported.Music.StartFor(importedSong));

        string folder = imported.Generator!.WorkingDirectory!;
        Assert.True(otherAssets.IsKept(folder));
        Assert.True(File.Exists(Path.Combine(folder, "render.js")));
        Assert.False(Directory.Exists(Path.Combine(folder, "node_modules")));
        Assert.Equal(Path.Combine(folder, "render.js"), imported.Generator.Arguments[0]);
        Assert.Equal("npm install", imported.Generator.SetupCommand);
    }

    [Fact]
    public async Task Canvas_template_carries_its_fonts_and_the_renderer_can_use_them()
    {
        // Any installed family with files on disk will do; a machine without fonts has nothing to test.
        string? family = SkiaSharp.SKFontManager.Default.GetFontFamilies()
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name) && FontFiles.FilesFor(name).Count > 0);
        if (family is null)
        {
            return;
        }

        var template = new GeneratedPromoTemplate
        {
            Name = "Fonts",
            Layers =
            [
                new GeneratedLayer { Kind = GeneratedLayerKind.Text, Text = "Hi", FontFamily = family },
                new GeneratedLayer { Kind = GeneratedLayerKind.Rect, FontFamily = "Not A Real Font Family" }
            ]
        };

        string zip = await ExportAsync(_data, writer => GeneratedPromoPackages.ExportAsync(template, writer));

        var otherAssets = new TemplateAssets(_otherData);
        await using FileStream stream = File.OpenRead(zip);
        using TemplatePackageReader reader = await new TemplatePackages(_otherData).OpenAsync(stream);
        Assert.Equal(FontFiles.FilesFor(family).Count, reader.Manifest.Fonts.Count);

        await GeneratedPromoPackages.ImportAsync(reader, otherAssets, []);
        string? before = FontFiles.KeptFolder;
        try
        {
            FontFiles.KeptFolder = Path.Combine(otherAssets.Root, FontFiles.Category);
            Assert.Contains(family, FontFiles.KeptFamilies(), StringComparer.OrdinalIgnoreCase);
            using SkiaSharp.SKTypeface? kept = FontFiles.KeptTypeface(family, bold: true);
            Assert.True(FontFiles.IsFamily(kept, family));
        }
        finally
        {
            FontFiles.KeptFolder = before;
        }
    }

    [Fact]
    public async Task Overlay_and_squeeze_look_round_trip_with_their_files()
    {
        string bug = Write("media/bug.png", "bug");
        string backdrop = Write("media/backdrop.png", "backdrop");
        var overlay = new Overlay { Name = "Bug", Kind = OverlayKinds.File, File = bug, Variants = [new OverlayVariant { File = bug }] };
        var look = new SqueezeTemplate { Name = "Neon", BackgroundPath = backdrop, AccentColor = "#00ff00" };

        string overlayZip = await ExportAsync(_data, writer => OverlayPackages.ExportAsync(overlay, writer));
        string lookZip = await ExportAsync(_data, writer => SqueezePackages.ExportAsync(look, look.Name, writer));

        var otherAssets = new TemplateAssets(_otherData);
        var packages = new TemplatePackages(_otherData);
        await using (FileStream stream = File.OpenRead(overlayZip))
        using (TemplatePackageReader reader = await packages.OpenAsync(stream))
        {
            Overlay imported = await OverlayPackages.ImportAsync(reader, otherAssets);
            Assert.Equal("Bug", imported.Name);
            Assert.True(otherAssets.IsKept(imported.File));
            Assert.Equal(imported.File, imported.Variants[0].File);
        }

        await using (FileStream stream = File.OpenRead(lookZip))
        using (TemplatePackageReader reader = await packages.OpenAsync(stream))
        {
            Assert.Equal(SqueezePackages.Kind, reader.Manifest.Kind);
            Assert.Null(reader.Manifest.Runs);
            SqueezeTemplate imported = await SqueezePackages.ImportAsync(reader, otherAssets);
            Assert.Equal("#00ff00", imported.AccentColor);
            Assert.Equal("backdrop", File.ReadAllText(imported.BackgroundPath!));
        }
    }

    [Fact]
    public async Task A_zip_without_a_manifest_is_turned_away()
    {
        string zip = Path.Combine(_directory, "plain.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            archive.CreateEntry("readme.txt");
        }

        await using FileStream stream = File.OpenRead(zip);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TemplatePackages(_otherData).OpenAsync(stream));
    }

    private static async Task<string> ExportAsync(string data, Func<TemplatePackageWriter, Task<string>> fill)
    {
        var packages = new TemplatePackages(data);
        string id;
        using (TemplatePackageWriter writer = packages.Create())
        {
            await fill(writer);
            id = writer.Id;
        }

        return packages.Exported(id) ?? throw new InvalidOperationException("The export wasn't kept.");
    }

    private string Write(string relative, string text = "x")
    {
        string path = Path.Combine(_source, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }
}
