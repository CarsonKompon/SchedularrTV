using SchedularrTV.Core.Configuration;
using SchedularrTV.GeneratedPromos;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>Turning an animated layer into a group that takes over its place, animation and timing in every variant.</summary>
public sealed class ConvertToGroupTests
{
    private static (GeneratedPromoTemplate Template, GeneratedLayer Logo, GeneratedPromoVariant Variant) Template()
    {
        var logo = new GeneratedLayer
        {
            Id = "logo", Name = "Logo", Kind = GeneratedLayerKind.Image, Path = "logo.png",
            X = 100, Y = 50, Width = 200, Height = 100, Opacity = 0.8f, Rotation = 10,
            Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(9), TransitionIn = GeneratedTransition.SlideUp,
            Effects = [LayerEffect.New(LayerEffectKind.Outline)],
            Keyframes =
            [
                new() { At = TimeSpan.Zero, X = 100, Y = 50, Width = 200, Height = 100, Opacity = 0.8f },
                new() { At = TimeSpan.FromSeconds(2), X = 300, Y = 50, Width = 200, Height = 100, Opacity = 1 }
            ]
        };
        var variant = new GeneratedPromoVariant
        {
            Id = "v1",
            Layers = [new GeneratedLayerOverride { Layer = "logo", X = 500, Y = 400, Width = 200, Height = 100, Opacity = 1 }],
            Timings = [new GeneratedLayerTiming { Layer = "logo", Start = TimeSpan.FromSeconds(3) }],
            Colors = [new GeneratedLayerColor { Layer = "logo", Color = "#FF0000" }]
        };
        var template = new GeneratedPromoTemplate { Layers = [logo], Variants = [variant] };
        return (template, logo, variant);
    }

    [Fact]
    public void The_group_takes_the_layer_s_place_animation_timing_and_effects()
    {
        (GeneratedPromoTemplate template, GeneratedLayer logo, _) = Template();

        GeneratedLayer group = GeneratedPromoHierarchy.ConvertToGroup(template, logo);

        Assert.Equal(GeneratedLayerKind.Group, group.Kind);
        Assert.Equal("Logo", group.Name);
        Assert.Equal((100f, 50f, 200f, 100f, 0.8f, 10f), (group.X, group.Y, group.Width, group.Height, group.Opacity, group.Rotation!.Value));
        Assert.Equal(new[] { 100f, 300f }, group.Keyframes!.Select(key => key.X));
        Assert.Equal((TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(9), GeneratedTransition.SlideUp), (group.Start, group.End, group.TransitionIn));
        Assert.Single(group.Effects!);

        // The layer just fills the group, always shown while it is.
        Assert.Equal(group.Id, logo.Parent);
        Assert.Equal((0f, 0f, 200f, 100f, 1f), (logo.X, logo.Y, logo.Width, logo.Height, logo.Opacity));
        Assert.Null(logo.Rotation);
        Assert.Null(logo.Keyframes);
        Assert.Null(logo.Effects);
        Assert.Equal((TimeSpan.Zero, (TimeSpan?)null, GeneratedTransition.None), (logo.Start, logo.End, logo.TransitionIn));
        Assert.Equal(new[] { group, logo }, template.Layers);
    }

    [Fact]
    public void Each_variant_s_own_placement_timing_and_hiding_move_to_the_group_and_its_colour_stays()
    {
        (GeneratedPromoTemplate template, GeneratedLayer logo, GeneratedPromoVariant variant) = Template();
        variant.Hidden = ["logo"];

        GeneratedLayer group = GeneratedPromoHierarchy.ConvertToGroup(template, logo);

        GeneratedLayerOverride placed = Assert.Single(variant.Layers);
        Assert.Equal((group.Id, 500f, 400f), (placed.Layer, placed.X, placed.Y));
        Assert.Equal(group.Id, Assert.Single(variant.Timings!).Layer);
        Assert.Equal(new[] { group.Id }, variant.Hidden);
        Assert.Equal("logo", Assert.Single(variant.Colors!).Layer);
    }

    [Fact]
    public void A_size_animation_stays_on_the_layer_centred_in_the_group()
    {
        (GeneratedPromoTemplate template, GeneratedLayer logo, _) = Template();
        logo.Keyframes![1].Width = 400;
        logo.Keyframes[1].Height = 200;

        GeneratedLayer group = GeneratedPromoHierarchy.ConvertToGroup(template, logo);

        // The group's box keeps one size, its middle where the original's was.
        Assert.All(group.Keyframes!, key => Assert.Equal((200f, 100f), (key.Width, key.Height)));
        Assert.Equal(300 + 200 - 100, group.Keyframes![1].X);
        // The layer grows about the group's middle.
        GeneratedKeyframe grown = logo.Keyframes![1];
        Assert.Equal((-100f, -50f, 400f, 200f), (grown.X, grown.Y, grown.Width, grown.Height));
    }

    [Fact]
    public void Groups_and_masks_can_t_be_turned_into_groups()
    {
        Assert.False(GeneratedPromoHierarchy.CanConvertToGroup(new GeneratedLayer { Name = "G", Kind = GeneratedLayerKind.Group }));
        Assert.False(GeneratedPromoHierarchy.CanConvertToGroup(new GeneratedLayer
        {
            Name = "M", Kind = GeneratedLayerKind.Rect, Mask = new GeneratedLayerMask { Mode = GeneratedMaskMode.Add, Target = "x" }
        }));
    }
}
