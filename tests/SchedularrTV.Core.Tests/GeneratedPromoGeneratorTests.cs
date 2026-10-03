using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Service.Editor;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class GeneratedPromoGeneratorTests : IDisposable
{
    private static readonly DateTimeOffset Evening = new(2026, 9, 30, 20, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"generated-promo-generator-{Guid.NewGuid():N}");

    public GeneratedPromoGeneratorTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public void Arguments_expand_per_show_and_clip_groups_without_re_reading_titles()
    {
        GeneratedPromoTemplate template = Template(new GeneratedScriptConfig
        {
            Command = "node",
            Arguments = ["tools/bumper.js", "next3", "{shows}", "{clip_arguments}", "--duration", "{duration}", "--aspect", "{aspect}", "--out", "{output}"],
            PerShow = ["--show", "{title}", "--at", "{time_or_next}"],
            ClipArguments = ["--bg", "{clip}", "--bg-start", "{clip_start}"],
            Shows = 3
        });
        UpNextListing[] listings =
        [
            new("SpongeBob SquarePants", Evening),
            new("Inuyasha {title}", Evening.AddMinutes(30)),
            new("Dragon Booster", Evening.AddMinutes(60)),
            new("Not passed", Evening.AddMinutes(90))
        ];

        ResolvedGeneratedPromo promo = GeneratedPromoResolver.Resolve(template, "Zone", listings, TimeZoneInfo.Utc)!;

        Assert.Equal(3, GeneratedPromoResolver.ListingsNeeded(template));
        Assert.Equal(
        [
            "tools/bumper.js", "next3",
            "--show", "SpongeBob SquarePants", "--at", "NEXT",
            "--show", "Inuyasha {title}", "--at", "8:30 PM",
            "--show", "Dragon Booster", "--at", "9:00 PM",
            "--duration", "8", "--aspect", "16:9", "--out", GeneratedPromoResolver.OutputToken
        ], promo.GeneratorArguments);
        Assert.Contains("--show \"Inuyasha {title}\" --at \"8:30 PM\"", ExternalPromoGenerator.Describe(template.Generator!, promo.GeneratorArguments));
        Assert.EndsWith("--out <output>", ExternalPromoGenerator.Describe(template.Generator!, promo.GeneratorArguments));

        string episode = Path.Combine(_directory, "episode.mkv");
        File.WriteAllText(episode, "");
        listings[0] = listings[0] with { Path = episode, Runtime = TimeSpan.FromMinutes(22) };
        ResolvedGeneratedPromo handedOnly = GeneratedPromoResolver.Resolve(template, "Zone", listings, TimeZoneInfo.Utc)!;
        int clipAt = handedOnly.GeneratorArguments.ToList().IndexOf("--bg");
        Assert.Equal(episode, handedOnly.GeneratorArguments[clipAt + 1]);
        Assert.InRange(double.Parse(handedOnly.GeneratorArguments[clipAt + 3], System.Globalization.CultureInfo.InvariantCulture), 60, 22 * 60 - 8);
        Assert.Null(GeneratedPromoResolver.Compose(handedOnly).BackgroundPath);

        template.BackgroundClip = new GeneratedClipConfig { StartAt = TimeSpan.FromSeconds(12.5), IncludeAudio = true };
        ResolvedGeneratedPromo withClip = GeneratedPromoResolver.Resolve(template, "Zone", listings, TimeZoneInfo.Utc)!;
        Assert.Contains(["--bg", episode, "--bg-start", "12.5"], Windows(withClip.GeneratorArguments, 4));
        Assert.NotEqual(promo.Key, withClip.Key);

        Assert.Null(GeneratedPromoResolver.Compose(withClip).BackgroundPath);
        Assert.Null(GeneratedPromoResolver.Compose(withClip).BackgroundAudio);
        Assert.Empty(GeneratedPromoResolver.Compose(withClip).Layers);

        template.Generator!.Arguments = ["tools/bumper.js", "{shows}", "--out", "{output}"];
        template.Generator.ClipArguments = [];
        ResolvedGeneratedPromo behind = GeneratedPromoResolver.Resolve(template, "Zone", listings, TimeZoneInfo.Utc)!;
        Assert.Equal(episode, GeneratedPromoResolver.Compose(behind).BackgroundPath);
        template.BackgroundClip = null;
        Assert.Null(GeneratedPromoResolver.Resolve(template, "Zone", listings, TimeZoneInfo.Utc)!.ClipPath);
    }

    [Fact]
    public void Generator_templates_need_no_layers_but_a_usable_setup()
    {
        GeneratedPromoTemplate template = Template(new GeneratedScriptConfig { Command = "node", Arguments = ["x.js"] });
        var config = new SchedulerConfig
        {
            Channels = [new ChannelConfig
            {
                Id = "zone", Name = "Zone", Number = 1, BaseRotation = new ContentSelector { Query = "*" },
                GeneratedPromos = new GeneratedPromosConfig { Templates = [template] }
            }]
        };

        Assert.True(SchedulerConfigLoader.Validate(config).IsValid, string.Join("; ", SchedulerConfigLoader.Validate(config).Errors));

        template.Generator!.OutputExtension = ".gif";
        template.Generator.WorkingDirectory = "relative";
        template.Generator.Command = " ";
        IReadOnlyList<string> errors = SchedulerConfigLoader.Validate(config).Errors;
        Assert.Contains(errors, error => error.Contains("output_extension"));
        Assert.Contains(errors, error => error.Contains("working_directory"));
        Assert.Contains(errors, error => error.Contains("needs a command"));

        GeneratedPromoTemplate parsed = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config)).Channels[0].GeneratedPromos!.Templates[0];
        Assert.Equal(".gif", parsed.Generator!.OutputExtension);
        Assert.Equal(["x.js"], parsed.Generator.Arguments);
    }

    [Fact]
    public async Task Transparent_generator_video_is_laid_over_the_background_with_its_sound_and_held_to_length()
    {
        GeneratedPromoTemplate template = Template(new GeneratedScriptConfig
        {
            Command = "ffmpeg",
            Arguments = ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=green:size=160x180:rate=10",
                "-f", "lavfi", "-i", "sine=frequency=110:sample_rate=48000", "-t", "3",
                "-vf", "format=rgba,pad=320:180:0:0:color=black@0", "-c:v", "prores_ks", "-profile:v", "4", "-pix_fmt", "yuva444p10le",
                "-c:a", "pcm_s16le", "-shortest", "{output}"],
            OutputExtension = ".mov"
        });
        template.BackgroundColor = "#FF0000";
        using GeneratedPromoService service = Service();

        GeneratedPromoRender render = await service.RenderAsync(GeneratedPromoResolver.Resolve(template, "Zone", [new("Inuyasha", Evening)], TimeZoneInfo.Utc)!);

        Assert.InRange(await Probe(render.Path), 7.95, 8.1);
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.GetDirectoryName(render.Path)!), file => file.Contains(".source", StringComparison.Ordinal));
        foreach (double second in (double[])[1, 6])
        {
            using SKBitmap frame = await Frame(render.Path, second);
            SKColor left = frame.GetPixel(40, 90), right = frame.GetPixel(280, 90);
            Assert.True(left.Green > 100 && left.Red < 60, $"generated video at {second}s: {left}");
            Assert.True(right.Red > 200 && right.Green < 60, $"background through the transparent part at {second}s: {right}");
        }

        Assert.True(await Volume(render.Path, 0.5, 2) > -40, "the generator's own sound should be kept");
        Assert.True(await Volume(render.Path, 4, 3) < -60, "its sound should stop with it rather than loop");

        template.Generator!.KeepAudio = false;
        GeneratedPromoRender muted = await service.RenderAsync(GeneratedPromoResolver.Resolve(template, "Zone", [new("Inuyasha", Evening)], TimeZoneInfo.Utc)!);
        Assert.True(await Volume(muted.Path, 0.5, 2) < -60);
    }

    [Fact]
    public async Task Generator_output_is_found_from_the_printed_path_and_failures_are_reported()
    {
        string made = Path.Combine(_directory, "made elsewhere.mp4");
        File.WriteAllText(made, "");
        var printer = new GeneratedScriptConfig { Command = "cmd.exe", Arguments = ["/c", "echo rendering... & echo " + made] };
        Assert.Equal(made, await ExternalPromoGenerator.RunAsync(printer, printer.Arguments, Path.Combine(_directory, "unused.mp4"), CancellationToken.None));

        var failing = new GeneratedScriptConfig { Command = "cmd.exe", Arguments = ["/c", "echo browser not found 1>&2 & exit 3"] };
        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExternalPromoGenerator.RunAsync(failing, failing.Arguments, Path.Combine(_directory, "x.mp4"), CancellationToken.None));
        Assert.Contains("code 3", failed.Message);
        Assert.Contains("browser not found", failed.Message);

        var silent = new GeneratedScriptConfig { Command = "cmd.exe", Arguments = ["/c", "echo done"] };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExternalPromoGenerator.RunAsync(silent, silent.Arguments, Path.Combine(_directory, "x.mp4"), CancellationToken.None));

        var slow = new GeneratedScriptConfig { Command = "ping.exe", Arguments = ["-n", "30", "127.0.0.1"], Timeout = TimeSpan.FromSeconds(1) };
        var timer = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            ExternalPromoGenerator.RunAsync(slow, slow.Arguments, Path.Combine(_directory, "x.mp4"), CancellationToken.None));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15));

        var missing = new GeneratedScriptConfig { Command = Path.Combine(_directory, "no-such-program.exe") };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExternalPromoGenerator.RunAsync(missing, [], Path.Combine(_directory, "x.mp4"), CancellationToken.None));
    }

    [Fact]
    public async Task Sample_previews_borrow_a_channel_episode_and_a_missing_clip_is_reported_plainly()
    {
        string episode = Path.Combine(_directory, "Pingu S03E08.mkv"), elsewhere = Path.Combine(_directory, "Other.mkv");
        File.WriteAllText(episode, "");
        File.WriteAllText(elsewhere, "");
        MediaItem Item(string id, string show, string? path, params string[] collections) => new()
        {
            Id = id, Title = $"{show} episode", SeriesName = show, Kind = MediaKind.Episode,
            Duration = TimeSpan.FromMinutes(5), Path = path, Collections = collections
        };
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton(new LibraryBrowser(new FakeMediaLibrary([
                Item("0", "Other channel", elsewhere), Item("a", "Unreadable", Path.Combine(_directory, "missing.mkv"), "Treehouse"),
                Item("b", "Pingu", episode, "Treehouse")]), TimeProvider.System))
            .BuildServiceProvider();
        using GeneratedPromoService service = Service();
        var preview = new GeneratedPromoPreview(service, Options.Create(new GeneratedPromoOptions()), TimeProvider.System, services);
        var channel = new ChannelConfig { Id = "ch27", Name = "Treehouse", BaseRotation = new ContentSelector { Collection = "Treehouse" } };
        GeneratedPromoTemplate template = Template(new GeneratedScriptConfig { Command = "cmd.exe", Arguments = ["/c", "{clip}", "{output}"] });

        Assert.Null(preview.Listings(channel, template, out bool sample)[0].Path);
        Assert.True(sample);
        await preview.PrepareStandInAsync(channel);
        UpNextListing standIn = preview.Listings(channel, template, out _)[0];
        Assert.Equal(("Pingu", episode), (standIn.Title, standIn.Path));
        Assert.Equal(episode, GeneratedPromoResolver.Resolve(template, "Treehouse", [standIn], TimeZoneInfo.Utc)!.GeneratorArguments[1]);

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RenderAsync(GeneratedPromoResolver.Resolve(template, "Treehouse", [new("Pingu", Evening)], TimeZoneInfo.Utc)!));
        Assert.Contains("{clip}", failed.Message);
    }

    private static GeneratedPromoTemplate Template(GeneratedScriptConfig generator) => new()
    {
        Name = "Zone Next",
        Type = GeneratedPromoType.UpNext,
        Duration = TimeSpan.FromSeconds(8),
        Width = 320,
        Height = 180,
        Fps = 10,
        Generator = generator
    };

    private GeneratedPromoService Service() => new(
        Options.Create(new GeneratedPromoOptions { OutputPath = Path.Combine(_directory, "renders") }),
        new TextToSpeechCatalog([]), TimeProvider.System, NullLogger<GeneratedPromoService>.Instance);

    private static IEnumerable<string[]> Windows(IReadOnlyList<string> values, int size) =>
        Enumerable.Range(0, Math.Max(0, values.Count - size + 1)).Select(start => values.Skip(start).Take(size).ToArray());

    private async Task<SKBitmap> Frame(string video, double second)
    {
        string png = Path.Combine(_directory, $"frame-{Guid.NewGuid():N}.png");
        await Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-ss", second.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", video, "-frames:v", "1", png);
        return SKBitmap.Decode(png);
    }

    private static async Task<double> Probe(string path)
    {
        string output = await Run("ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", path);
        return double.Parse(output, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<double> Volume(string path, double start, double length)
    {
        var info = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (string argument in (string[])["-hide_banner", "-nostats", "-ss", start.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "-t", length.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", path, "-vn",
                     "-af", "bandpass=f=110:width_type=q:w=4,volumedetect", "-f", "null", "-"])
            info.ArgumentList.Add(argument);
        using Process process = Process.Start(info)!;
        string log = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(log, @"mean_volume: (-?[\d.]+|-inf) dB");
        Assert.True(match.Success, log);
        return match.Groups[1].Value == "-inf" ? double.NegativeInfinity : double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> Run(string file, params string[] arguments)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using Process process = Process.Start(info)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        return output;
    }
}
