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
    public async Task Shared_images_travel_with_a_template_and_only_what_is_missing_is_imported()
    {
        string defaultCard = Write("cards/default.png", "default");
        string rugrats = Write("cards/rugrats.png", "rugrats");
        string spongebob = Write("cards/spongebob.png", "spongebob");
        var cards = new GeneratedSharedImage
        {
            Name = "Title cards",
            DefaultPath = defaultCard,
            Shows = [new GeneratedShowImage { Show = "Rugrats", Path = rugrats }, new GeneratedShowImage { Show = "SpongeBob", Path = spongebob }]
        };
        var template = new GeneratedPromoTemplate
        {
            Name = "Up Next",
            VariableImages = [new GeneratedVariableImage { Name = "variable_image_01", Shared = cards.Id }]
        };

        string zip = await ExportAsync(_data, writer => GeneratedPromoPackages.ExportAsync(template, writer, [cards]));

        // The importing side already has this shared image (with its own default and a SpongeBob card), and another
        // one that happens to have the same name.
        string mineDefault = Write("mine/default.png", "mine");
        string mineSponge = Write("mine/sponge.png", "my sponge");
        var mine = new GeneratedSharedImage
        {
            Id = cards.Id,
            Name = "My cards",
            DefaultPath = mineDefault,
            Shows = [new GeneratedShowImage { Show = "spongebob", Path = mineSponge }]
        };
        var namesake = new GeneratedSharedImage { Name = "Title cards" };
        List<GeneratedSharedImage> library = [mine, namesake];

        var otherAssets = new TemplateAssets(_otherData);
        var report = new GeneratedSharedImport();
        GeneratedPromoTemplate imported;
        await using (FileStream stream = File.OpenRead(zip))
        using (TemplatePackageReader reader = await new TemplatePackages(_otherData).OpenAsync(stream))
        {
            imported = await GeneratedPromoPackages.ImportAsync(reader, otherAssets, [], library, report);
        }

        Assert.Equal(2, library.Count);
        Assert.Equal("My cards", mine.Name);
        Assert.Equal(mineDefault, mine.DefaultPath);
        Assert.Equal(2, mine.Shows.Count);
        Assert.Equal(mineSponge, mine.Shows.Single(show => show.Show == "spongebob").Path);
        GeneratedShowImage added = mine.Shows.Single(show => show.Show == "Rugrats");
        Assert.True(otherAssets.IsKept(added.Path));
        Assert.Equal("rugrats", File.ReadAllText(added.Path!));
        Assert.Equal(new[] { "My cards (+1)" }, report.Extended);
        Assert.Empty(report.Added);

        GeneratedVariableImage variable = Assert.Single(imported.VariableImages!);
        Assert.Equal(cards.Id, variable.Shared);
        Assert.Same(mine, variable.Linked);
        Assert.Null(variable.DefaultPath);
        Assert.Empty(variable.Shows);
        Assert.Null(imported.SharedImages);
        Assert.Equal(mineSponge, variable.PathFor("SpongeBob", null));
    }

    [Fact]
    public async Task A_shared_image_new_to_the_importer_is_added_whole_and_kept_apart_from_namesakes()
    {
        string card = Write("cards/default.png", "default");
        var cards = new GeneratedSharedImage { Name = "Title cards", DefaultPath = card };
        var template = new GeneratedPromoTemplate
        {
            Name = "Now Playing",
            VariableImages = [new GeneratedVariableImage { Name = "variable_image_01", Shared = cards.Id }]
        };
        string zip = await ExportAsync(_data, writer => GeneratedPromoPackages.ExportAsync(template, writer, [cards]));

        var namesake = new GeneratedSharedImage { Name = "Title cards" };
        List<GeneratedSharedImage> library = [namesake];
        var otherAssets = new TemplateAssets(_otherData);
        GeneratedPromoTemplate imported;
        await using (FileStream stream = File.OpenRead(zip))
        using (TemplatePackageReader reader = await new TemplatePackages(_otherData).OpenAsync(stream))
        {
            imported = await GeneratedPromoPackages.ImportAsync(reader, otherAssets, [], library);
        }

        Assert.Equal(2, library.Count);
        GeneratedSharedImage added = library[1];
        Assert.Equal(cards.Id, added.Id);
        Assert.NotEqual(namesake.Name, added.Name);
        Assert.True(otherAssets.IsKept(added.DefaultPath));
        Assert.Same(added, imported.VariableImages![0].Linked);
    }

    [Fact]
    public async Task Without_a_shared_library_an_import_keeps_the_pictures_as_the_templates_own()
    {
        string card = Write("cards/default.png", "default");
        var cards = new GeneratedSharedImage { Name = "Title cards", DefaultPath = card };
        var template = new GeneratedPromoTemplate
        {
            Name = "Up Next",
            VariableImages = [new GeneratedVariableImage { Name = "variable_image_01", Shared = cards.Id }]
        };
        string zip = await ExportAsync(_data, writer => GeneratedPromoPackages.ExportAsync(template, writer, [cards]));

        var otherAssets = new TemplateAssets(_otherData);
        GeneratedPromoTemplate imported;
        await using (FileStream stream = File.OpenRead(zip))
        using (TemplatePackageReader reader = await new TemplatePackages(_otherData).OpenAsync(stream))
        {
            imported = await GeneratedPromoPackages.ImportAsync(reader, otherAssets, []);
        }

        GeneratedVariableImage variable = Assert.Single(imported.VariableImages!);
        Assert.False(variable.IsShared);
        Assert.True(otherAssets.IsKept(variable.DefaultPath));
        Assert.Equal("default", File.ReadAllText(variable.PathFor("Anything", null)));
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
        look.UseCanvas = true;
        look.Canvases = [SqueezeCanvas.Create("4:3")];
        look.Canvases[0].Layers.Add(new GeneratedLayer { Kind = GeneratedLayerKind.Image, Path = bug });
        look.Canvases[0].Layers.Add(new GeneratedLayer { Kind = GeneratedLayerKind.Video, Path = Write("media/panel.mp4", "video"), Video = new() { Stretch = true } });
        look.Canvases[0].Layers.Add(new GeneratedLayer { Kind = GeneratedLayerKind.Text, CreditsText = new() { LinesPerPage = 6 } });

        string overlayZip = await ExportAsync(_data, writer => OverlayPackages.ExportAsync(overlay, writer));
        string lookZip = await ExportAsync(_data, writer => SqueezePackages.ExportAsync(look, look.Name, writer));

        var otherAssets = new TemplateAssets(_otherData);
        var packages = new TemplatePackages(_otherData);
        await using (FileStream stream = File.OpenRead(overlayZip))
        using (TemplatePackageReader reader = await packages.OpenAsync(stream))
        {
            (Overlay imported, _) = await OverlayPackages.ImportAsync(reader, otherAssets);
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
            Assert.True(imported.UseCanvas);
            Assert.Equal("4:3", Assert.Single(imported.Canvases).AspectRatio);
            Assert.Equal(SqueezeCanvas.Credits, imported.Canvases[0].Layers[0].Video!.Episode);
            Assert.Equal("bug", File.ReadAllText(imported.Canvases[0].Layers[1].Path!));
            Assert.Equal("video", File.ReadAllText(imported.Canvases[0].Layers[2].Path!));
            Assert.True(imported.Canvases[0].Layers[2].Video!.Stretch);
            Assert.Equal(6, imported.Canvases[0].Layers[3].CreditsText!.LinesPerPage);
            imported.Canvases[0].Layers[3].CreditsText!.LinesPerPage = 12;
            Assert.Equal(6, look.Canvases[0].Layers[3].CreditsText!.LinesPerPage);
            Assert.Equal(bug, look.Canvases[0].Layers[1].Path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Squeeze_canvas_packages_survive_export_import_and_schedule_reload(bool mainLook)
    {
        var template = new SqueezeTemplate
        {
            Name = "Canvas credits", UseCanvas = true, Recognition = new(0, 100, 75),
            Canvases = [SqueezeCanvas.Create("16:9"), SqueezeCanvas.Create("4:3")]
        };
        template.Canvases[0].BackgroundColor = "#123456";
        template.Canvases[0].TimelineSeconds = 90;
        template.Canvases[0].Layers[0].Keyframes = [new() { Width = 1280, Height = 720 }, new() { At = TimeSpan.FromSeconds(1), X = 40, Width = 400, Height = 600, Easing = GeneratedEasing.EaseInOut }];
        template.Canvases[0].Layers.Add(new() { Kind = GeneratedLayerKind.Text, Text = "{next}", FontSize = 48, FontWeight = 600, Italic = true, X = 500, Width = 600, Height = 400,
            CreditsText = new() { FallbackText = "Thanks for watching {show}.", LinesPerPage = 5, MatchSourcePages = true } });
        template.Canvases[1].Layers.Add(new() { Kind = GeneratedLayerKind.Image, Path = Write("media/canvas-logo.png", "logo") });
        SqueezeLook original = mainLook ? new CreditSqueezeSection().WithOnly(template) : template;
        string zip = await ExportAsync(_data, writer => SqueezePackages.ExportAsync(original, template.Name, writer));
        await using var stream = File.OpenRead(zip);
        using var reader = await new TemplatePackages(_otherData).OpenAsync(stream);
        var imported = await SqueezePackages.ImportAsync(reader, new TemplateAssets(_otherData));
        var saved = SchedulerConfigLoader.ConvertSection<CreditSqueezeSection>(new CreditSqueezeSection { Templates = [imported] })!;
        var loaded = Assert.Single(saved.Templates);
        Assert.True(loaded.UseCanvas);
        Assert.Equal(2, loaded.Canvases.Count);
        Assert.Equal("#123456", loaded.Canvases[0].BackgroundColor);
        Assert.Equal(90, loaded.Canvases[0].TimelineSeconds);
        Assert.True(loaded.Canvases[0].FitTimingToCredits);
        Assert.Equal(40, loaded.Canvases[0].Layers[0].Keyframes![1].X);
        Assert.Equal(GeneratedEasing.EaseInOut, loaded.Canvases[0].Layers[0].Keyframes![1].Easing);
        Assert.Equal(48, loaded.Canvases[0].Layers[1].FontSize);
        Assert.Equal(600, loaded.Canvases[0].Layers[1].FontWeight);
        Assert.True(loaded.Canvases[0].Layers[1].Italic);
        Assert.Equal("Thanks for watching {show}.", loaded.Canvases[0].Layers[1].CreditsText!.FallbackText);
        Assert.Equal(5, loaded.Canvases[0].Layers[1].CreditsText!.LinesPerPage);
        Assert.True(loaded.Canvases[0].Layers[1].CreditsText!.MatchSourcePages);
        Assert.Equal(75, loaded.Recognition.MinimumConfidence);
        Assert.Equal("logo", File.ReadAllText(loaded.Canvases[1].Layers[1].Path!));
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

    private GeneratedVoicePack VoicePack()
    {
        GeneratedVoicePhrase intro = new() { Name = "Coming up next", Takes = [new() { Path = Write("voice/next.wav", "next take") }] };
        GeneratedVoicePhrase show = new()
        {
            Name = "Show", Kind = GeneratedVoicePhraseKind.ShowName,
            Entries = [new() { Key = "Rugrats", Takes = [new() { Path = Write("voice/rugrats.wav", "rugrats take") }] }]
        };
        GeneratedVoicePack pack = new() { Name = "Announcer", Phrases = [intro, show] };
        pack.Templates.Add(new GeneratedVoicePackTemplate { Name = "Next", Parts = [new() { Phrase = intro.Id }, new() { Phrase = show.Id }] });
        return pack;
    }

    private static async Task<GeneratedVoicePackImport> ImportVoiceAsync(string zip, string data, List<GeneratedVoicePack> library)
    {
        await using FileStream stream = File.OpenRead(zip);
        using TemplatePackageReader reader = await new TemplatePackages(data).OpenAsync(stream);
        Assert.Equal(VoicePackPackages.Kind, reader.Manifest.Kind);
        return await VoicePackPackages.ImportAsync(reader, new TemplateAssets(data), library);
    }

    [Fact]
    public async Task Voice_packs_install_then_only_gain_what_they_lack()
    {
        GeneratedVoicePack pack = VoicePack();
        string zip = await ExportAsync(_data, writer => VoicePackPackages.ExportAsync(pack, writer));

        // Installed whole on a computer without it, its recordings kept there.
        List<GeneratedVoicePack> library = [];
        GeneratedVoicePackImport installed = await ImportVoiceAsync(zip, _otherData, library);
        Assert.True(installed.Installed);
        GeneratedVoicePack mine = Assert.Single(library);
        Assert.Equal(pack.Id, mine.Id);
        string take = Assert.Single(mine.Phrases[0].Takes).Path;
        Assert.True(new TemplateAssets(_otherData).IsKept(take));
        Assert.Equal("next take", File.ReadAllText(take));
        Assert.Null(mine.Phrases[0].Takes[0].Hash);

        // The same pack again changes nothing; local edits are kept.
        mine.Name = "My announcer";
        mine.Templates[0].Name = "Renamed";
        string zipAgain = await ExportAsync(_data, writer => VoicePackPackages.ExportAsync(pack, writer));
        GeneratedVoicePackImport again = await ImportVoiceAsync(zipAgain, _otherData, library);
        Assert.False(again.Changed);
        Assert.Contains("already up to date", again.Summary);
        Assert.Single(mine.Phrases[0].Takes);
        Assert.Equal("My announcer", mine.Name);

        // A newer copy with a new phrase, show, recording and template: only those are added.
        GeneratedVoicePhrase closer = new() { Name = "Only on Nick!", Takes = [new() { Path = Write("voice/nick.wav", "nick take") }] };
        pack.Phrases.Add(closer);
        pack.Phrases[1].Entries.Add(new() { Key = "Doug", Takes = [new() { Path = Write("voice/doug.wav", "doug take") }] });
        pack.Phrases[0].Takes.Add(new() { Path = Write("voice/next-2.wav", "another next take") });
        pack.Phrases[1].Entries[0].More = [new() { Path = Write("voice/more-rugrats.wav", "more rugrats") }];
        pack.Templates.Add(new GeneratedVoicePackTemplate { Name = "With closer", Parts = [new() { Phrase = pack.Phrases[1].Id }, new() { Phrase = closer.Id }] });
        string newer = await ExportAsync(_data, writer => VoicePackPackages.ExportAsync(pack, writer));
        GeneratedVoicePackImport updated = await ImportVoiceAsync(newer, _otherData, library);
        Assert.False(updated.Installed);
        Assert.Equal(1, updated.Phrases);
        Assert.Equal(1, updated.Values);
        Assert.Equal(1, updated.Templates);
        Assert.Equal(4, updated.Takes); // next-2, more Rugrats, Doug and the closer; nothing already there
        Assert.Single(library);
        Assert.Equal(2, mine.Phrases[0].Takes.Count);
        Assert.Equal("Renamed", mine.Templates[0].Name);
        Assert.Equal(2, mine.Templates.Count);
        Assert.Equal(3, mine.Phrases.Count);
        Assert.Single(mine.Phrases[1].Entries[0].More!);
    }

    [Fact]
    public async Task A_generated_promo_brings_its_voice_pack()
    {
        GeneratedVoicePack pack = VoicePack();
        var template = new GeneratedPromoTemplate
        {
            Name = "Up Next",
            Voice = new GeneratedVoiceConfig
            {
                Mode = GeneratedVoiceMode.Prerecorded,
                Prerecorded = new GeneratedPrerecordedVoiceConfig { Pack = pack.Id, Linked = pack }
            }
        };

        string zip = await ExportAsync(_data, writer => GeneratedPromoPackages.ExportAsync(template, writer, null, [pack]));
        Assert.Null(template.VoicePack);

        List<GeneratedVoicePack> library = [];
        GeneratedVoicePackImport report = new();
        GeneratedPromoTemplate imported;
        await using (FileStream stream = File.OpenRead(zip))
        using (TemplatePackageReader reader = await new TemplatePackages(_otherData).OpenAsync(stream))
        {
            imported = await GeneratedPromoPackages.ImportAsync(reader, new TemplateAssets(_otherData), [], null, null, library, report);
        }

        Assert.True(report.Installed);
        Assert.Null(imported.VoicePack);
        Assert.Same(Assert.Single(library), imported.Voice!.Prerecorded!.Linked);

        // Imported again where the pack is already up to date: nothing is installed or changed.
        GeneratedVoicePackImport second = new();
        await using (FileStream stream = File.OpenRead(zip))
        using (TemplatePackageReader reader = await new TemplatePackages(_otherData).OpenAsync(stream))
        {
            await GeneratedPromoPackages.ImportAsync(reader, new TemplateAssets(_otherData), ["Up Next"], null, null, library, second);
        }

        Assert.False(second.Changed);
        Assert.Single(library);
    }

    [Fact]
    public async Task A_generated_promo_with_a_voice_pack_offers_it_with_or_without_the_pack_or_the_pack_alone()
    {
        GeneratedVoicePack pack = VoicePack();
        var template = new GeneratedPromoTemplate
        {
            Name = "Up Next",
            Background = Write("bg.png", new string('b', 3000)),
            Voice = new GeneratedVoiceConfig
            {
                Mode = GeneratedVoiceMode.Prerecorded,
                Prerecorded = new GeneratedPrerecordedVoiceConfig { Pack = pack.Id, Linked = pack }
            }
        };

        IReadOnlyList<TemplateExportOption> options = (await GeneratedPromoPackages.ExportOptionsAsync(template, null, [pack]))!;
        Assert.Equal(3, options.Count);
        Assert.All(options, option => Assert.Contains("under 1 MB", option.Detail));

        // Without the pack: nothing installed on import, but the template still names it.
        string without = await ExportAsync(_data, options[1].Export);
        List<GeneratedVoicePack> library = [];
        GeneratedVoicePackImport report = new();
        GeneratedPromoTemplate imported;
        await using (FileStream stream = File.OpenRead(without))
        using (TemplatePackageReader reader = await new TemplatePackages(_otherData).OpenAsync(stream))
        {
            imported = await GeneratedPromoPackages.ImportAsync(reader, new TemplateAssets(_otherData), [], null, null, library, report);
        }

        Assert.Empty(library);
        Assert.Equal(pack.Id, imported.Voice!.Prerecorded!.Pack);

        // Just the pack: a voice pack package.
        string alone = await ExportAsync(_data, options[2].Export);
        Assert.True((await ImportVoiceAsync(alone, _otherData, library)).Installed);

        // A template without a voice pack is exported straight away.
        template.Voice = null;
        Assert.Null(await GeneratedPromoPackages.ExportOptionsAsync(template, null, [pack]));
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
