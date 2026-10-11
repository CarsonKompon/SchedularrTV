using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Service.Editor;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class GeneratedPromoScopeTests : IDisposable
{
    private static readonly DateTimeOffset Day = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"generated-promo-scope-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public void Blocks_inherit_define_their_own_borrow_another_channel_or_switch_off()
    {
        ChannelConfig network = Channel("cn", "Cartoon Network", "Classic");
        ChannelConfig adultSwim = Channel("as", "Adult Swim", "Swim Up Next");
        BlockConfig block = Block("Adult Swim");
        network.Blocks.Add(block);
        var config = new SchedulerConfig { Channels = [network, adultSwim] };

        GeneratedPromoSource inherited = GeneratedPromoPlanner.Source(config, network, block)!;
        Assert.Same(network, inherited.Owner);
        Assert.Null(inherited.DefinedIn);
        Assert.Equal("Classic", inherited.Promos.Templates[0].Name);

        block.GeneratedPromos = new GeneratedPromosConfig { Templates = [GeneratedPromoTemplates.UpNext("Block Up Next")] };
        GeneratedPromoSource own = GeneratedPromoPlanner.Source(config, network, "adult swim")!;
        Assert.Same(block, own.DefinedIn);
        Assert.Equal("Block Up Next", own.Promos.Templates[0].Name);

        block.GeneratedPromos = new GeneratedPromosConfig { From = "AS" };
        GeneratedPromoSource borrowed = GeneratedPromoPlanner.Source(config, network, block)!;
        Assert.Same(adultSwim, borrowed.Owner);
        Assert.Null(borrowed.DefinedIn);
        Assert.Equal("Swim Up Next", borrowed.Promos.Templates[0].Name);

        adultSwim.GeneratedPromos!.Templates[0].Name = "Renamed";
        Assert.Equal("Renamed", GeneratedPromoPlanner.Source(config, network, block)!.Promos.Templates[0].Name);

        block.GeneratedPromos = new GeneratedPromosConfig { Enabled = false };
        Assert.Null(GeneratedPromoPlanner.Source(config, network, block));

        block.GeneratedPromos = new GeneratedPromosConfig { From = "missing" };
        Assert.Null(GeneratedPromoPlanner.Source(config, network, block));

        adultSwim.GeneratedPromos = new GeneratedPromosConfig { From = "cn" };
        network.GeneratedPromos!.From = "as";
        Assert.Null(GeneratedPromoPlanner.Source(config, network, (BlockConfig?)null));
    }

    [Fact]
    public async Task Engine_uses_the_blocks_borrowed_templates_inside_the_block_and_the_channels_outside()
    {
        ChannelConfig network = Channel("cn", "Cartoon Network", "Classic");
        BlockConfig block = Block("Adult Swim");
        block.GeneratedPromos = new GeneratedPromosConfig { From = "as" };
        network.Blocks.Add(block);
        ChannelConfig adultSwim = Channel("as", "Adult Swim", "Swim Up Next");
        var config = new SchedulerConfig { Channels = [network, adultSwim] };
        List<MediaItem> library = [];
        for (int episode = 1; episode <= 8; episode++)
        {
            library.Add(TestMedia.Episode("SpongeBob", 1, episode, TimeSpan.FromMinutes(22)));
            library.Add(TestMedia.Episode("Squidbillies", 1, episode, TimeSpan.FromMinutes(11)));
        }

        var engine = new SchedulingEngine(new FakeMediaLibrary(library), new InMemoryPlaybackStateStore());
        ChannelSchedule schedule = await engine.BuildAsync(config, network, Day, Day.AddHours(3));

        ScheduleEntry[] generated = [.. schedule.Entries.Where(entry => entry.GeneratedTemplate is not null)];
        Assert.Contains(generated, entry => entry.BlockName == "Adult Swim");
        Assert.Contains(generated, entry => entry.BlockName is null);
        Assert.All(generated, entry => Assert.Equal(entry.BlockName == "Adult Swim" ? "Swim Up Next" : "Classic", entry.GeneratedTemplate));
    }

    [Fact]
    public void Validation_reports_missing_and_looping_sources_and_ignored_templates()
    {
        ChannelConfig network = Channel("cn", "Cartoon Network", "Classic");
        BlockConfig block = Block("Adult Swim");
        block.GeneratedPromos = new GeneratedPromosConfig { From = "nowhere" };
        network.Blocks.Add(block);
        ChannelConfig adultSwim = Channel("as", "Adult Swim", "Swim Up Next");
        adultSwim.GeneratedPromos!.From = "tb";
        ChannelConfig boomerang = Channel("tb", "Boomerang", "Boom");
        boomerang.GeneratedPromos!.From = "as";
        var config = new SchedulerConfig { Channels = [network, adultSwim, boomerang] };

        ConfigValidationResult result = SchedulerConfigLoader.Validate(config);

        Assert.Contains("channels.cn.blocks.Adult Swim.plugins.generated-promos.from: there is no channel 'nowhere'.", result.Errors);
        Assert.Contains(result.Errors, error => error.StartsWith("channels.as.plugins.generated-promos.from") && error.Contains("loop"));
        Assert.Contains(result.Warnings, warning => warning.StartsWith("channels.as.plugins.generated-promos") && warning.Contains("ignored"));
    }

    [Fact]
    public void Songs_load_from_the_old_single_path_and_one_is_picked_per_promo()
    {
        const string yaml = """
            channels:
            - id: zone
              name: The Zone
              generated_promos:
                templates:
                - name: Classic
                  music:
                    path: C:\music\one.mp3
                    paths:
                    - C:\music\two.mp3
            """;

        SchedulerConfig config = SchedulerConfigLoader.Parse(yaml);
        GeneratedMusicConfig music = config.Channels[0].GeneratedPromos!.Templates[0].Music!;

        Assert.Null(music.Path);
        Assert.Contains(@"C:\music\one.mp3", music.Paths);
        Assert.Contains(@"C:\music\two.mp3", music.Paths);
        Assert.DoesNotContain("  path:", SchedulerConfigLoader.Serialize(config));

        string[] songs = ["a.mp3", "b.mp3", "c.mp3"];
        HashSet<string> picked = [.. Enumerable.Range(0, 60).Select(index => GeneratedPromoResolver.Song(songs, $"key-{index}"))];
        Assert.Equal(songs.Length, picked.Count);
        Assert.Equal(GeneratedPromoResolver.Song(songs, "same"), GeneratedPromoResolver.Song(songs, "same"));

        music.Paths = [];
        Assert.Contains(SchedulerConfigLoader.Validate(config).Errors, error => error.Contains("at least one song"));
    }

    [Fact]
    public void Each_song_has_its_own_start_and_unlisted_songs_use_the_shared_one()
    {
        const string yaml = """
            channels:
            - id: zone
              name: The Zone
              generated_promos:
                templates:
                - name: Classic
                  music:
                    paths:
                    - C:\music\one.mp3
                    - C:\music\two.mp3
                    start_at: 5s
                    song_starts:
                      C:\music\one.mp3: 26s
                      C:\music\gone.mp3: 9s
            """;

        SchedulerConfig config = SchedulerConfigLoader.Parse(yaml);
        GeneratedMusicConfig music = config.Channels[0].GeneratedPromos!.Templates[0].Music!;

        Assert.Equal(TimeSpan.FromSeconds(26), music.StartFor(@"c:\MUSIC\one.mp3"));
        Assert.Equal(TimeSpan.FromSeconds(5), music.StartFor(@"C:\music\two.mp3"));
        Assert.False(music.SongStarts!.ContainsKey(@"C:\music\gone.mp3"));

        GeneratedMusicConfig reloaded = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config)).Channels[0].GeneratedPromos!.Templates[0].Music!;
        Assert.Equal(TimeSpan.FromSeconds(26), reloaded.StartFor(@"C:\music\one.mp3"));

        music.SongStarts[@"C:\music\two.mp3"] = TimeSpan.FromSeconds(-1);
        Assert.Contains(SchedulerConfigLoader.Validate(config).Errors, error => error.Contains("non-negative start"));
    }

    [Fact]
    public void Render_folders_follow_channel_block_and_template_and_only_cache_files_are_served()
    {
        ChannelConfig network = Channel("cn", "Cartoon: Network", "Up/Next");
        BlockConfig block = Block("Late Night");
        block.GeneratedPromos = new GeneratedPromosConfig { Templates = [GeneratedPromoTemplates.UpNext("Swim")] };
        GeneratedPromoTemplate channelTemplate = network.GeneratedPromos!.Templates[0];

        Assert.Equal(Path.Combine("Cartoon_ Network", "Up_Next"),
            GeneratedPromoService.Folder(new GeneratedPromoSource(network.GeneratedPromos, network, null), channelTemplate));
        Assert.Equal(Path.Combine("Cartoon_ Network", "Late Night", "Swim"),
            GeneratedPromoService.Folder(new GeneratedPromoSource(block.GeneratedPromos, network, block), block.GeneratedPromos.Templates[0]));

        using var service = new GeneratedPromoService(
            Options.Create(new GeneratedPromoOptions { OutputPath = Path.Combine(_directory, "renders") }),
            new TextToSpeechCatalog([]), TimeProvider.System, NullLogger<GeneratedPromoService>.Instance);
        string folder = Path.Combine(service.Root, "Cartoon_ Network", "Up_Next");
        Directory.CreateDirectory(folder);
        string render = Path.Combine(folder, $"{new string('a', 32)}.mp4");
        File.WriteAllText(render, "");
        File.WriteAllText(Path.Combine(_directory, $"{new string('b', 32)}.mp4"), "");
        File.WriteAllText(Path.Combine(folder, "notes.mp4"), "");

        Assert.Equal(render, service.RenderedFile($"Cartoon_ Network/Up_Next/{new string('a', 32)}.mp4"));
        Assert.Null(service.RenderedFile($"../{new string('b', 32)}.mp4"));
        Assert.Null(service.RenderedFile("Cartoon_ Network/Up_Next/notes.mp4"));
        Assert.Equal(Path.Combine("Cartoon_ Network", "Up_Next", $"{new string('a', 32)}.mp4"), service.RelativePath(render));
    }

    [Theory]
    [InlineData(812, 400, true, true)]
    [InlineData(810, 390, true, false)]
    [InlineData(700, 400, false, true)]
    public void Dragged_layers_snap_to_the_canvas_centre_lines(float x, float y, bool snapX, bool snapY)
    {
        (float snappedX, float snappedY, bool didX, bool didY) = GeneratedPromoEditing.SnapToCenter(x, y, 300, 280, 1920, 1080, 6);

        Assert.Equal((snapX, snapY), (didX, didY));
        Assert.Equal(snapX ? 810 : x, snappedX);
        Assert.Equal(snapY ? 400 : y, snappedY);
    }

    [Fact]
    public void File_dialog_opens_beside_the_current_file_else_the_last_folder()
    {
        string music = Path.Combine(_directory, "music");
        Directory.CreateDirectory(music);

        Assert.Equal(music, FileBrowsing.StartFolder(Path.Combine(music, "gone.mp3"), null, Environment.SpecialFolder.MyMusic));
        Assert.Equal(music, FileBrowsing.StartFolder(null, music, Environment.SpecialFolder.MyMusic));
    }

    private static ChannelConfig Channel(string id, string name, string template) => new()
    {
        Id = id,
        Name = name,
        Number = id.Length * 100 + id[0],
        TimeZone = "UTC",
        AutoTemporalTags = false,
        PadToNearest = TimeSpan.FromMinutes(30),
        BaseRotation = new ContentSelector { Include = [new ContentInclude { Show = "SpongeBob" }] },
        Promos = new PromoConfig { BaseQuery = "tag:'Promo'", UpNext = new PromoSpotConfig { SkipWhenSameSeries = false } },
        GeneratedPromos = new GeneratedPromosConfig { Templates = [Template(template)] }
    };

    private static GeneratedPromoTemplate Template(string name)
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext(name);
        template.Duration = TimeSpan.FromSeconds(8);
        return template;
    }

    private static BlockConfig Block(string name) => new()
    {
        Name = name,
        Time = TimeSpan.FromHours(13),
        EndTime = TimeSpan.FromHours(14),
        Shows = [new ContentSelector { Include = [new ContentInclude { Show = "Squidbillies" }] }]
    };
}
