using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Overlays;
using SchedularrTV.Plugins.Playout;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class OverlayImageTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "overlay-images-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    public void Dispose() => TestFolders.Delete(_folder);

    private static Overlay Bug() => new()
    {
        Kind = OverlayKinds.File, VariableImage = "variable_image_01", File = "static.png", FadeSeconds = 0,
        VariableImages = [new() { Name = "variable_image_01", Context = GeneratedImageContext.CurrentlyWatching,
            DefaultPath = "default.png", Shows = [new() { Show = "Current", Path = "current.png" }, new() { Show = "Later", Path = "later.png" }] }]
    };

    private static ScheduleEntry Programme(string title, int minute, int segment = 0) => new()
    {
        Start = At.AddMinutes(minute), Duration = TimeSpan.FromMinutes(10), Kind = ScheduleEntryKind.Program,
        Item = TestMedia.Episode(title, 1, 1, TimeSpan.FromMinutes(20)), SegmentNumber = segment
    };

    private static ScheduleEntry[] Entries() =>
    [
        Programme("Current", 0, 1),
        new() { Start = At.AddMinutes(10), Duration = TimeSpan.FromMinutes(1), Kind = ScheduleEntryKind.Commercial,
            Item = TestMedia.Filler("Ad", TimeSpan.FromMinutes(1), "Commercials") },
        Programme("Current", 11, 2), Programme("Next", 25), Programme("Later", 50)
    ];

    [Fact]
    public void Contexts_skip_breaks_and_continuations_and_use_preceding_programme_during_ads()
    {
        ScheduleEntry[] entries = Entries();
        foreach (int index in new[] { 0, 1, 2 })
        {
            (string? current, IReadOnlyList<string> upcoming) = OverlayImages.Subjects(entries, index);
            Assert.Equal("Current", current);
            Assert.Equal(["Next", "Later"], upcoming);
        }
        Assert.Empty(OverlayImages.Subjects(entries, 4).Upcoming);
    }

    [Fact]
    public void Resolution_is_case_insensitive_and_falls_back_without_mutating_saved_overlay()
    {
        Overlay overlay = Bug();
        overlay.Layers = [new() { Kind = OverlayLayerKinds.Image, VariableImage = "VARIABLE_IMAGE_01", Path = "unused.png" }];
        Overlay current = OverlayImages.Resolve(overlay, " current ", []);
        Assert.Equal("current.png", current.File);
        Assert.Equal("current.png", current.Layers[0].Path);
        Assert.Equal("static.png", overlay.File);
        Assert.Equal("unused.png", overlay.Layers[0].Path);
        Assert.Equal("default.png", OverlayImages.Resolve(overlay, "Unassigned", []).File);
        overlay.VariableImages[0].Context = GeneratedImageContext.UpNext;
        overlay.VariableImages[0].Offset = 1;
        Assert.Equal("later.png", OverlayImages.Resolve(overlay, "Current", ["Next", "Later"]).File);
        Assert.Equal("default.png", OverlayImages.Resolve(overlay, "Current", ["Next"]).File);
        overlay.VariableImages[0].DefaultPath = null;
        Assert.Null(OverlayImages.Resolve(overlay, null, []).File);
    }

    [Fact]
    public void Block_override_applies_when_no_show_override_matches()
    {
        Overlay overlay = Bug();
        overlay.VariableImages[0].Shows.Add(new() { Show = "Toonami", Block = true, Path = "toonami.png" });
        overlay.VariableImages[0].Shows.Add(new() { Show = "Later", Block = true, Path = "block-named-later.png" });

        Assert.Equal("current.png", OverlayImages.ResolveFor(overlay, new("Current", "Toonami"), []).File);
        Assert.Equal("toonami.png", OverlayImages.ResolveFor(overlay, new("Unassigned", " toonami "), []).File);
        Assert.Equal("default.png", OverlayImages.ResolveFor(overlay, new("Unassigned", "Other block"), []).File);
        // A block override never matches a show title, and vice versa.
        Assert.Equal("default.png", OverlayImages.ResolveFor(overlay, new("Toonami"), []).File);
        Assert.Equal("later.png", OverlayImages.ResolveFor(overlay, new("Later", "Later"), []).File);

        ScheduleEntry[] entries = [.. Entries().Select(entry => entry with { BlockName = "Toonami" })];
        (OverlayImageSubject? current, IReadOnlyList<OverlayImageSubject> upcoming) = OverlayImages.Programmes(entries, 1);
        Assert.Equal(new OverlayImageSubject("Current", "Toonami"), current);
        Assert.All(upcoming, subject => Assert.Equal("Toonami", subject.Block));
    }

    [Fact]
    public void Each_use_of_one_variable_can_choose_its_own_show_and_fall_back_independently()
    {
        Overlay overlay = Bug();
        overlay.VariableImageShow = "next2";
        overlay.VariableImages[0].Shows.Add(new() { Show = "Next", Path = "next.png" });
        overlay.Layers = [.. new string?[] { "watching", "next", "next2", "next10", null, "invalid" }.Select(selector =>
            new OverlayLayer { Kind = OverlayLayerKinds.Image, VariableImage = "VARIABLE_IMAGE_01", VariableImageShow = selector, Path = "saved.png" })];

        Overlay result = OverlayImages.Resolve(overlay, "Current", ["Next", "Later"]);
        Assert.Equal("later.png", result.File);
        Assert.Equal(["current.png", "next.png", "later.png", "default.png", "current.png", "current.png"], result.Layers.Select(layer => layer.Path));
        Assert.All(overlay.Layers, layer => Assert.Equal("saved.png", layer.Path));
        Assert.Equal(GeneratedImageContext.CurrentlyWatching, overlay.VariableImages[0].Context);

        overlay.VariableImages[0].Context = GeneratedImageContext.UpNext;
        overlay.VariableImages[0].Offset = 1;
        result = OverlayImages.Resolve(overlay, "Current", ["Next", "Later"]);
        Assert.Equal("current.png", result.Layers[0].Path);
        Assert.Equal("later.png", result.Layers[4].Path);
        overlay.VariableImages[0].DefaultPath = null;
        Assert.Null(OverlayImages.Resolve(overlay, "Current", []).File);
    }

    [Fact]
    public void Per_use_selection_passes_the_selected_show_and_block_to_automatic_images()
    {
        Overlay overlay = Bug();
        var shared = new GeneratedSharedImage { DefaultPath = "fallback.png", Automatic = new() { Query = "{show} {block}" } };
        overlay.VariableImages[0].Shared = shared.Id;
        overlay.VariableImageShow = "next2";
        overlay.Layers = [new() { Kind = OverlayLayerKinds.Image, VariableImage = "variable_image_01", VariableImageShow = "watching" }];
        var lookup = new RecordingImages();
        Overlay result = OverlayImages.ResolveFor(overlay, new("Current", "Morning"), [new("Next", "Afternoon"), new("Later", "Evening")], [shared], lookup);
        Assert.Equal("Later Evening.png", result.File);
        Assert.Equal("Current Morning.png", result.Layers[0].Path);
        Assert.Equal(["Later Evening", "Current Morning"], lookup.Queries);
    }

    private sealed class RecordingImages : IAutomaticImageLookup
    {
        public List<string> Queries { get; } = [];
        public string? Find(AutomaticImageConfig config, string query)
        {
            Queries.Add(query);
            return query + ".png";
        }
    }

    [Fact]
    public async Task Screenbug_show_selector_is_used_on_air()
    {
        Overlay overlay = Bug();
        overlay.VariableImageShow = "next2";
        ChannelSchedule result = await Process(overlay, Entries());
        Assert.Equal("later.png", PathOf(result.Entries[0]));
        Assert.Equal("default.png", PathOf(result.Entries[3]));
    }

    [Fact]
    public async Task Screenbug_resolves_on_air_with_variant_priority_and_no_default_hides_it()
    {
        Overlay bug = Bug();
        bug.AppliesTo = OverlayAppliesTo.Everything;
        bug.Variants = [new() { File = "special.png", Query = "show:'Next'" }];
        ChannelSchedule result = await Process(bug, Entries());
        Assert.Equal("current.png", PathOf(result.Entries[0]));
        Assert.Equal("current.png", PathOf(result.Entries[1]));
        Assert.Equal("special.png", PathOf(result.Entries[3]));
        Assert.Equal("later.png", PathOf(result.Entries[4]));
        bug.VariableImages[0].DefaultPath = null;
        bug.Variants.Clear();
        result = await Process(bug, [Programme("Unknown", 0)]);
        Assert.Null(result.Entries[0].Annotations);
    }

    [Fact]
    public async Task Canvas_draws_resolved_image_and_uses_distinct_cache_entries_and_preview()
    {
        Directory.CreateDirectory(_folder);
        string red = Picture("red.png", SKColors.Red), blue = Picture("blue.png", SKColors.Blue);
        Overlay overlay = Bug();
        overlay.Kind = OverlayKinds.Design;
        overlay.Width = overlay.Height = 64;
        overlay.VariableImages[0].DefaultPath = blue;
        overlay.VariableImages[0].Shows[0].Path = red;
        overlay.Layers = [new() { Kind = OverlayLayerKinds.Image, VariableImage = "variable_image_01", Width = 64, Height = 64 }];
        ChannelSchedule result = await Process(overlay, [Programme("Current", 0), Programme("Unknown", 30)]);
        string first = PathOf(result.Entries[0]), second = PathOf(result.Entries[1]);
        Assert.NotEqual(first, second);
        using SKBitmap redBitmap = SKBitmap.Decode(first), blueBitmap = SKBitmap.Decode(second);
        Assert.Equal(SKColors.Red, redBitmap.GetPixel(32, 32));
        Assert.Equal(SKColors.Blue, blueBitmap.GetPixel(32, 32));
        using SKBitmap preview = SKBitmap.Decode(OverlayRenderer.Preview(OverlayImages.Resolve(overlay, "Current", []), new Dictionary<string, string>(), 1));
        Assert.Equal(SKColors.Red, preview.GetPixel(32, 32));

        overlay.VariableImages[0].DefaultPath = null;
        var renderer = new OverlayRenderer(_folder);
        using SKBitmap hidden = SKBitmap.Decode(renderer.Render(OverlayImages.Resolve(overlay, "Unknown", []), new Dictionary<string, string>()));
        Assert.Equal(0, hidden.GetPixel(32, 32).Alpha);
    }

    [Fact]
    public async Task Copies_and_yaml_preserve_variables_and_packages_map_all_images_independently()
    {
        Overlay original = Bug();
        original.VariableImageShow = "next2";
        original.Layers = [new() { Kind = OverlayLayerKinds.Image, VariableImage = "variable_image_01", VariableImageShow = "watching" }];
        Overlay copy = original.Copy();
        await OverlayPackages.MapPathsAsync(copy, path => Task.FromResult(path is null ? null : "mapped/" + path));
        Assert.Equal("mapped/default.png", copy.VariableImages[0].DefaultPath);
        Assert.Equal("mapped/current.png", copy.VariableImages[0].Shows[0].Path);
        Assert.Equal("current.png", original.VariableImages[0].Shows[0].Path);
        var config = new SchedulerConfig { Channels = [Channel(copy)] };
        SchedulerConfig parsed = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config));
        Overlay roundtrip = OverlayResolver.Read(parsed.Channels[0].Plugins)!.Overlays[0];
        Assert.Equal("variable_image_01", roundtrip.VariableImage);
        Assert.Equal("next2", roundtrip.VariableImageShow);
        Assert.Equal("watching", roundtrip.Layers[0].VariableImageShow);
        Assert.Equal(GeneratedImageContext.CurrentlyWatching, roundtrip.VariableImages[0].Context);
        Assert.Equal("mapped/current.png", roundtrip.VariableImages[0].Shows[0].Path);
    }

    private static ChannelConfig Channel(Overlay overlay) => new()
    {
        Id = "channel", Name = "Channel", Number = 1,
        Plugins = new() { [OverlayResolver.PluginId] = new OverlaysSection { Overlays = [overlay] } }
    };

    private async Task<ChannelSchedule> Process(Overlay overlay, IReadOnlyList<ScheduleEntry> entries)
    {
        ChannelConfig channel = Channel(overlay);
        var config = new SchedulerConfig { Channels = [channel] };
        var schedule = new ChannelSchedule { ChannelId = channel.Id, ChannelName = channel.Name, Start = At, End = At.AddHours(2), Entries = entries };
        var processor = new OverlayProcessor(new OverlayRenderer(_folder), NullLogger<OverlayProcessor>.Instance);
        return await processor.ProcessAsync(new PlayoutContext(config, channel, schedule), CancellationToken.None);
    }

    private static string PathOf(ScheduleEntry entry) => (string)JsonNode.Parse(entry.Annotations![OverlayProcessor.GraphicsAnnotation])![0]!["source"]!["path"]!;

    private string Picture(string name, SKColor color)
    {
        using SKSurface surface = SKSurface.Create(new SKImageInfo(8, 8));
        surface.Canvas.Clear(color);
        using SKImage image = surface.Snapshot();
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, png.ToArray());
        return path;
    }
}
