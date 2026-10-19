using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.GeneratedPromos;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class GeneratedPromoVariantTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly UpNextListing[] Listings = [new("Rugrats", Noon.AddMinutes(30))];

    private static ResolvedGeneratedPromo Resolve(GeneratedPromoTemplate template, DateTimeOffset? at = null) =>
        GeneratedPromoResolver.Resolve(template, "Channel", Listings, TimeZoneInfo.Utc, at ?? Noon)!;

    private static IReadOnlyList<string> Texts(PromoComposition composition) =>
        [.. composition.Layers.OfType<PromoTextLayer>().Select(layer => string.Join('\n', layer.Lines))];

    [Fact]
    public void Edited_backgrounds_become_variants_and_plain_ones_stay_on_the_default()
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext();
        GeneratedPromoEditing.SetBackgrounds(template, ["plain-a.mp4", "edited.mp4", "plain-b.mp4"]);
        GeneratedLayer panel = template.Layers[0];
        GeneratedPromoEditing.Place(template, "edited.mp4", panel).X = 900;
        template.BackgroundRules = [new GeneratedBackgroundRule { Background = "edited.mp4", MatchShows = false, TimeRange = new() }];

        Assert.True(GeneratedPromoVariants.Convert(template));

        GeneratedPromoVariant variant = Assert.Single(template.Variants!);
        Assert.Equal("edited", variant.Name);
        Assert.Equal("edited.mp4", variant.Background);
        Assert.Equal(900, Assert.Single(variant.Layers).X);
        Assert.Equal("edited.mp4", Assert.Single(variant.BackgroundRules!).Background);
        Assert.Null(template.BackgroundOverrides);
        Assert.Null(template.BackgroundRules);
        Assert.Equal(new[] { "plain-a.mp4", "plain-b.mp4" }, template.BackgroundChoices);
        // The default look stands for its two backgrounds, so each of the three is as likely as before.
        Assert.Equal((double?)2, template.DefaultWeight);

        // Nothing left to convert the second time.
        Assert.False(GeneratedPromoVariants.Convert(template));
    }

    [Fact]
    public void When_every_background_was_edited_the_first_becomes_the_default_look()
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext();
        GeneratedPromoEditing.SetBackgrounds(template, ["one.mp4", "two.mp4"]);
        GeneratedLayer heading = template.Layers[2];
        string usual = heading.Color;
        GeneratedPromoEditing.Recolour(template, "one.mp4", heading, "#202020");
        GeneratedPromoEditing.Retime(template, "two.mp4", heading).Start = TimeSpan.FromSeconds(2);

        GeneratedPromoVariants.Convert(template);

        // The default look is "one" (never a look without a background), as likely as "two".
        Assert.Equal(new[] { "one.mp4" }, template.BackgroundChoices);
        Assert.Null(template.DefaultWeight);
        Assert.Equal("#202020", heading.Color);
        GeneratedPromoVariant two = Assert.Single(template.Variants!);
        Assert.Equal("two.mp4", two.Background);

        // "two" still looks as it did: the old colour (now its own), and its own timing.
        Assert.Equal(usual, GeneratedPromoResolver.ColorFor(template, two.Id, heading));
        Assert.Equal(TimeSpan.FromSeconds(2), GeneratedPromoResolver.TimingFor(template, two.Id, heading).Start);
    }

    [Fact]
    public void Templates_an_earlier_conversion_left_without_a_default_background_are_repaired()
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext();
        GeneratedLayer heading = template.Layers[2];
        heading.Id = "head";
        template.Variants =
        [
            new GeneratedPromoVariant { Name = "red", Background = "red.mp4", Layers = [new GeneratedLayerOverride { Layer = "head", X = 500, Width = 10, Height = 10 }] },
            new GeneratedPromoVariant { Name = "blue", Background = "blue.mp4" }
        ];
        template.DefaultWeight = 0;
        float usualX = heading.X;

        Assert.True(GeneratedPromoVariants.RepairConverted(template));

        Assert.Equal(new[] { "red.mp4" }, template.BackgroundChoices);
        Assert.Null(template.DefaultWeight);
        Assert.Equal(500, heading.X);
        GeneratedPromoVariant blue = Assert.Single(template.Variants!);
        Assert.Equal(usualX, GeneratedPromoResolver.Pose(template, blue.Id, heading).X);

        // A template set up that way on purpose (its own names, say) is left alone.
        template.Variants = [new GeneratedPromoVariant { Name = "Night", Background = "night.mp4" }];
        template.Background = null;
        template.DefaultWeight = 0;
        Assert.False(GeneratedPromoVariants.RepairConverted(template));
    }

    [Fact]
    public void A_variant_draws_its_own_layers_backgrounds_and_placements_and_hides_what_it_hides()
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext();
        GeneratedPromoEditing.SetBackgrounds(template, ["default.png"]);
        GeneratedLayer heading = template.Layers[2];
        GeneratedLayer channel = template.Layers[5];
        GeneratedPromoVariant night = new() { Name = "Night", Background = "night.png" };
        template.Variants = [night];
        template.DefaultWeight = 0; // only the variant is picked
        GeneratedPromoEditing.Place(template, night.Id, heading).X = 777;
        channel.Id ??= "chan";
        night.Hidden = [channel.Id];
        template.Layers.Add(new GeneratedLayer { Name = "Moon", Kind = GeneratedLayerKind.Text, Text = "GOOD NIGHT", Variant = night.Id, Width = 400, Height = 100 });

        ResolvedGeneratedPromo promo = Resolve(template);
        Assert.Equal(night.Id, promo.Variant);
        PromoComposition drawn = GeneratedPromoResolver.Compose(promo);
        Assert.Equal("night.png", drawn.BackgroundPath);
        Assert.Contains("GOOD NIGHT", Texts(drawn));
        Assert.DoesNotContain("Channel", Texts(drawn));
        Assert.Equal(777, drawn.Layers.OfType<PromoTextLayer>().First(layer => layer.Lines.Contains("UP NEXT")).Bounds.Left);

        // The default look draws none of the variant's layers, with the template's own placement.
        template.Variants = null;
        template.DefaultWeight = null;
        PromoComposition plain = GeneratedPromoResolver.Compose(Resolve(template));
        Assert.Equal("default.png", plain.BackgroundPath);
        Assert.DoesNotContain("GOOD NIGHT", Texts(plain));
        Assert.Contains("Channel", Texts(plain));
        Assert.Equal(heading.X, plain.Layers.OfType<PromoTextLayer>().First(layer => layer.Lines.Contains("UP NEXT")).Bounds.Left);
    }

    [Fact]
    public void A_variant_outside_its_availability_isnt_picked_and_the_default_stands_in()
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext();
        GeneratedPromoVariant evening = new()
        {
            Name = "Evening",
            Availability = new GeneratedPromoAvailability { TimeRange = new GeneratedBackgroundTimeRange { Start = TimeSpan.FromHours(18), End = TimeSpan.FromHours(23) } }
        };
        template.Variants = [evening];
        template.DefaultWeight = 0;

        Assert.Null(Resolve(template, Noon).Variant);
        Assert.Equal(evening.Id, Resolve(template, Noon.AddHours(7)).Variant);

        // Turned off, it's never picked.
        evening.Enabled = false;
        Assert.Null(Resolve(template, Noon.AddHours(7)).Variant);

        // Each variant (and the default) is a different promo.
        evening.Enabled = true;
        Assert.NotEqual(Resolve(template, Noon).Key, Resolve(template, Noon.AddHours(7)).Key);
    }

    [Fact]
    public void Layers_pick_one_of_their_wordings_or_files_by_their_rules()
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext();
        GeneratedLayer heading = template.Layers[2];
        heading.ChoiceRule = new GeneratedBackgroundRule
        {
            MatchShows = false, TimeRange = new GeneratedBackgroundTimeRange { Start = TimeSpan.FromHours(6), End = TimeSpan.FromHours(10) }
        };
        heading.Alternatives = [new GeneratedLayerChoice { Value = "COMING UP" }];

        // At noon the layer's own wording can't be picked, so the other one is.
        ResolvedGeneratedPromo noon = Resolve(template);
        Assert.Equal("COMING UP", noon.LayerChoices[2]);
        Assert.Contains("COMING UP", Texts(GeneratedPromoResolver.Compose(noon)));

        // In its time range, either can be; the pick is fixed for the same promo.
        DateTimeOffset morning = Noon.AddHours(-4);
        Assert.Equal(Resolve(template, morning).Key, Resolve(template, morning).Key);

        // An image layer picks between files the same way.
        GeneratedLayer logo = new() { Name = "Logo", Kind = GeneratedLayerKind.Image, Path = "day.png", Width = 100, Height = 100,
            ChoiceRule = new GeneratedBackgroundRule { MatchShows = false, TimeRange = new GeneratedBackgroundTimeRange { Start = TimeSpan.FromHours(1), End = TimeSpan.FromHours(2) } },
            Alternatives = [new GeneratedLayerChoice { Value = "other.png" }] };
        template.Layers.Add(logo);
        PromoComposition drawn = GeneratedPromoResolver.Compose(Resolve(template));
        Assert.Contains(drawn.Layers.OfType<PromoImageLayer>(), image => image.Path == "other.png");
    }

    [Fact]
    public void Variants_survive_the_schedule_file()
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext();
        GeneratedPromoVariant variant = new() { Name = "Holiday", Weight = 3, Background = "snow.mp4", Hidden = ["x"] };
        template.Variants = [variant];
        template.Layers.Add(new GeneratedLayer { Name = "Snow", Kind = GeneratedLayerKind.Text, Text = "Ho ho", Variant = variant.Id,
            Alternatives = [new GeneratedLayerChoice { Value = "Merry", Rule = new GeneratedBackgroundRule { Shows = ["Rugrats"] } }] });
        SchedulerConfig config = new();
        config.Channels.Add(new ChannelConfig { Id = "nick", Name = "Nick", Number = 1, BaseRotation = new() { Query = "*" }, GeneratedPromos = new() { Templates = [template] } });

        GeneratedPromoTemplate read = SchedulerConfigLoader.Parse(YamlCommentMerger.Merge(string.Empty, SchedulerConfigLoader.Serialize(config)))
            .Channels[0].GeneratedPromos!.Templates[0];
        GeneratedPromoVariant back = Assert.Single(read.Variants!);
        Assert.Equal(variant.Id, back.Id);
        Assert.Equal(3.0, back.Weight);
        Assert.Equal("snow.mp4", back.Background);
        Assert.Equal(new[] { "x" }, back.Hidden!);
        GeneratedLayer snow = read.Layers[^1];
        Assert.Equal(variant.Id, snow.Variant);
        Assert.Equal("Merry", Assert.Single(snow.Alternatives!).Value);
        Assert.Equal(new[] { "Rugrats" }, snow.Alternatives![0].Rule!.Shows);
    }
}
