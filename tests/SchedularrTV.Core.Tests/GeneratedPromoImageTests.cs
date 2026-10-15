using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class GeneratedPromoImageTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static GeneratedPromoTemplate Template() => new()
    {
        Name = "Images",
        VariableImages =
        [
            new() { Name = "variable_image_01", DefaultPath = "default.png", Shows = [new() { Show = "Target", Path = "target.png" }] },
            new() { Name = "variable_image_02", Context = GeneratedImageContext.CurrentlyWatching, DefaultPath = "default.png",
                Shows = [new() { Show = "Current", Path = "current.png" }] },
            new() { Name = "variable_image_03", Context = GeneratedImageContext.UpNext, Offset = 1, DefaultPath = "default.png",
                Shows = [new() { Show = "Later", Path = "later.png" }] }
        ],
        Layers = [new() { Kind = GeneratedLayerKind.UpNext },
            new() { Kind = GeneratedLayerKind.Image, VariableImage = "variable_image_03", X = 10, Y = 20, Width = 300, Height = 200 }]
    };

    [Fact]
    public void Images_resolve_independent_contexts_and_canvas_bounds_without_changing_voice_listings()
    {
        GeneratedPromoTemplate template = Template();
        var subjects = new GeneratedImageSubjects(new("Current", At), [new("Next", At), new(" later ", At.AddHours(1))]);
        ResolvedGeneratedPromo promo = GeneratedPromoResolver.Resolve(template, "Channel", [new("target", At)], TimeZoneInfo.Utc, At, subjects)!;
        Assert.Equal("target.png", promo.VariableImages["variable_image_01"]);
        Assert.Equal("current.png", promo.VariableImages["variable_image_02"]);
        Assert.Equal("later.png", promo.VariableImages["variable_image_03"]);
        Assert.Equal(1, GeneratedPromoResolver.ListingsNeeded(template));
        PromoImageLayer layer = Assert.Single(GeneratedPromoResolver.Compose(promo).Layers.OfType<PromoImageLayer>());
        Assert.Equal("later.png", layer.Path);
        Assert.Equal(10, layer.Bounds.Left);
        Assert.Equal(300, layer.Bounds.Width);
        Assert.Null(template.Layers[1].Path);

        ResolvedGeneratedPromo different = GeneratedPromoResolver.Resolve(template, "Channel", [new("target", At)], TimeZoneInfo.Utc, At,
            subjects with { Upcoming = [new("Next", At), new("Unknown", At.AddHours(1))] })!;
        Assert.Equal("default.png", different.VariableImages["variable_image_03"]);
        Assert.NotEqual(promo.Key, different.Key);
    }

    [Fact]
    public void Missing_show_context_and_blank_override_use_default_and_no_default_hides_image()
    {
        GeneratedPromoTemplate template = Template();
        template.VariableImages![0].Shows[0].Path = " ";
        template.VariableImages[2].DefaultPath = null;
        ResolvedGeneratedPromo promo = GeneratedPromoResolver.Resolve(template, "Channel", [new("Target", At)], TimeZoneInfo.Utc)!;
        Assert.Equal("default.png", promo.VariableImages["variable_image_01"]);
        Assert.Equal("default.png", promo.VariableImages["variable_image_02"]);
        Assert.Equal("", promo.VariableImages["variable_image_03"]);
        Assert.Empty(GeneratedPromoResolver.Compose(promo).Layers.OfType<PromoImageLayer>());
    }

    [Fact]
    public void Generator_expands_numbered_image_tokens_once_in_all_argument_groups()
    {
        GeneratedPromoTemplate template = Template();
        template.VariableImages![0].Shows[0].Path = @"C:\My images\{title}.png";
        template.Generator = new() { Arguments = ["--image={VARIABLE_IMAGE_01}", "{shows}", "{clip_arguments}", "{unknown_01}"],
            PerShow = ["{variable_image_01}"], ClipArguments = ["{variable_image_01}", "{clip}"] };
        IReadOnlyList<string> args = GeneratedPromoResolver.GeneratorArguments(template.Generator, template, "Channel",
            [new("Target", At)], TimeZoneInfo.Utc, At, ("episode.mkv", TimeSpan.Zero));
        Assert.Equal([@"--image=C:\My images\{title}.png", @"C:\My images\{title}.png", @"C:\My images\{title}.png", "episode.mkv", "{unknown_01}"], args);
    }

    [Fact]
    public void Schedule_context_skips_midroll_continuations_and_keeps_later_show_offsets()
    {
        ScheduleEntry Entry(string show, int minutes, int segment = 0) => new()
        {
            Start = At.AddMinutes(minutes), Duration = TimeSpan.FromMinutes(10), Kind = ScheduleEntryKind.Program,
            Item = TestMedia.Episode(show, 1, 1, TimeSpan.FromMinutes(20)), SegmentNumber = segment
        };
        var schedule = new ChannelSchedule
        {
            ChannelId = "channel", ChannelName = "Channel", Start = At.AddHours(-1), End = At.AddHours(2),
            Entries = [Entry("Current", -10, 1), Entry("Current", 1, 2), Entry("Next", 15), Entry("Later", 45)]
        };
        GeneratedImageSubjects subjects = GeneratedImageSubjects.FromSchedule(schedule, At, At.AddSeconds(10));
        Assert.Equal("Current", subjects.Current!.Title);
        Assert.Equal(["Next", "Later"], subjects.Upcoming.Select(show => show.Title));
        GeneratedPromoTemplate template = Template();
        template.Type = GeneratedPromoType.Eventually;
        ResolvedGeneratedPromo promo = GeneratedPromoResolver.Resolve(template, "Channel", [new("Target", At.AddDays(2))],
            TimeZoneInfo.Utc, At, subjects)!;
        Assert.Equal("later.png", promo.VariableImages["variable_image_03"]);
    }

    [Fact]
    public async Task Configuration_roundtrips_and_package_mapping_includes_defaults_and_overrides()
    {
        GeneratedPromoTemplate template = Template();
        var config = new SchedulerConfig
        {
            Channels = [new() { Id = "channel", Name = "Channel", Number = 1, BaseRotation = new() { Query = "*" },
                GeneratedPromos = new() { Templates = [template] } }]
        };
        Assert.True(SchedulerConfigLoader.Validate(config).IsValid);
        GeneratedPromoTemplate parsed = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config)).Channels[0].GeneratedPromos!.Templates[0];
        Assert.Equal(GeneratedImageContext.CurrentlyWatching, parsed.VariableImages![1].Context);
        Assert.Equal("variable_image_03", parsed.Layers[1].VariableImage);
        await GeneratedPromoPackages.MapPathsAsync(parsed, path => Task.FromResult(path is null ? null : "moved/" + path));
        Assert.Equal("moved/default.png", parsed.VariableImages[0].DefaultPath);
        Assert.Equal("moved/target.png", parsed.VariableImages[0].Shows[0].Path);
        Assert.Equal("default.png", template.VariableImages![0].DefaultPath);

        template.VariableImages.Add(new() { Name = "variable_image_01", Offset = 10 });
        template.Layers[1].VariableImage = "variable_image_missing";
        IReadOnlyList<string> errors = SchedulerConfigLoader.Validate(config).Errors;
        Assert.Contains(errors, error => error.Contains("unique name"));
        Assert.Contains(errors, error => error.Contains("offset between"));
        Assert.Contains(errors, error => error.Contains("must name a configured"));
    }

    [Theory]
    [InlineData("promoted", GeneratedImageContext.PromotedShow, 0)]
    [InlineData("watching", GeneratedImageContext.CurrentlyWatching, 0)]
    [InlineData("next", GeneratedImageContext.UpNext, 0)]
    [InlineData("Next1", GeneratedImageContext.UpNext, 0)]
    [InlineData("next2", GeneratedImageContext.UpNext, 1)]
    [InlineData("next10", GeneratedImageContext.UpNext, 9)]
    public void Image_selectors_name_the_show(string selector, GeneratedImageContext context, int offset)
    {
        Assert.True(GeneratedImageSelectors.TryParse(selector, out GeneratedImageContext parsedContext, out int parsedOffset));
        Assert.Equal((context, offset), (parsedContext, parsedOffset));
        Assert.True(GeneratedImageSelectors.TryParse(GeneratedImageSelectors.Format(context, offset), out GeneratedImageContext again, out int againOffset));
        Assert.Equal((context, offset), (again, againOffset));
    }

    [Theory]
    [InlineData("next0")]
    [InlineData("next11")]
    [InlineData("later")]
    [InlineData("")]
    public void Unknown_image_selectors_are_refused(string selector) =>
        Assert.False(GeneratedImageSelectors.TryParse(selector, out _, out _));

    [Fact]
    public void One_variable_image_can_be_used_for_several_shows()
    {
        GeneratedPromoTemplate template = Template();
        GeneratedVariableImage art = template.VariableImages![0];
        art.Shows = [new() { Show = "Current", Path = "current.png" }, new() { Show = "Next", Path = "next.png" }, new() { Show = "Later", Path = "later.png" }];
        string plainKey = GeneratedPromoResolver.Resolve(template, "Channel", [new("Target", At)], TimeZoneInfo.Utc, At)!.Key;

        template.Layers =
        [
            new() { Kind = GeneratedLayerKind.UpNext },
            new() { Kind = GeneratedLayerKind.Image, VariableImage = "variable_image_01", VariableImageShow = "watching", Width = 10, Height = 10 },
            new() { Kind = GeneratedLayerKind.Image, VariableImage = "variable_image_01", VariableImageShow = "next", Width = 10, Height = 10 },
            new() { Kind = GeneratedLayerKind.Image, VariableImage = "variable_image_01", VariableImageShow = "next2", Width = 10, Height = 10 },
            new() { Kind = GeneratedLayerKind.Image, VariableImage = "variable_image_01", Width = 10, Height = 10 }
        ];
        var subjects = new GeneratedImageSubjects(new("Current", At), [new("Next", At), new("Later", At.AddHours(1))]);
        ResolvedGeneratedPromo promo = GeneratedPromoResolver.Resolve(template, "Channel", [new("Target", At)], TimeZoneInfo.Utc, At, subjects)!;

        Assert.Equal(new[] { "current.png", "next.png", "later.png", "default.png" },
            GeneratedPromoResolver.Compose(promo).Layers.OfType<PromoImageLayer>().Select(layer => layer.Path));
        Assert.NotEqual(plainKey, promo.Key);

        template.Generator = new() { Arguments = ["--now", "{variable_image_01:watching}", "--then", "{Variable_Image_01:NEXT2}", "--usual", "{variable_image_01}"] };
        IReadOnlyList<string> args = GeneratedPromoResolver.GeneratorArguments(template.Generator, template, "Channel", [new("Target", At)], TimeZoneInfo.Utc, At, null,
            GeneratedPromoImages.Resolve(template, [new("Target", At)], subjects));
        Assert.Equal(new[] { "--now", "current.png", "--then", "later.png", "--usual", "default.png" }, args);

        template.Generator.Arguments = ["{variable_image_01:later}"];
        Assert.Contains(SchedulerConfigLoader.Validate(new SchedulerConfig
        {
            Channels = [new() { Id = "channel", Name = "Channel", Number = 1, BaseRotation = new() { Query = "*" },
                GeneratedPromos = new() { Templates = [template] } }]
        }).Errors, error => error.Contains("{variable_image_01:later}"));
    }

    [Fact]
    public void Changing_image_file_invalidates_cached_render()
    {
        string file = Path.GetTempFileName();
        try
        {
            GeneratedPromoTemplate template = Template();
            template.VariableImages![0].DefaultPath = file;
            string Key() => GeneratedPromoResolver.Resolve(template, "Channel", [new("Unassigned", At)], TimeZoneInfo.Utc)!.Key;
            string before = Key();
            File.WriteAllText(file, "changed image content");
            Assert.NotEqual(before, Key());
        }
        finally { File.Delete(file); }
    }
}
