using SchedularrTV.Core.Configuration;
using SchedularrTV.GeneratedPromos;
using SkiaSharp;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class GeneratedPromoGroupTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private static GeneratedPromoTemplate Template() => new()
    {
        Name = "Groups",
        Width = 400,
        Height = 300,
        Duration = TimeSpan.FromSeconds(5),
        Layers =
        [
            new() { Name = "Bow", Kind = GeneratedLayerKind.Rect, X = 100, Y = 50, Width = 40, Height = 20, Rotation = 30 },
            new() { Name = "Box", Kind = GeneratedLayerKind.Rect, X = 60, Y = 120, Width = 100, Height = 80 },
            new() { Name = "Title", Kind = GeneratedLayerKind.Text, Text = "Hi", X = 250, Y = 20, Width = 100, Height = 40 }
        ]
    };

    private static IGeneratedPose Own(GeneratedLayer layer) => layer;

    private static void SameBox(WorldBox expected, WorldBox actual)
    {
        Assert.Equal(expected.CenterX, actual.CenterX, 1);
        Assert.Equal(expected.CenterY, actual.CenterY, 1);
        Assert.Equal(expected.Width, actual.Width, 1);
        Assert.Equal(expected.Height, actual.Height, 1);
        Assert.Equal(((expected.Angle % 360) + 360) % 360, ((actual.Angle % 360) + 360) % 360, 1);
    }

    [Fact]
    public void Turns_and_moves_compose_and_undo()
    {
        var space = new Rigid(90, 10, 20);
        (double x, double y) = space.Apply(5, 0);
        Assert.Equal(10, x, 6);
        Assert.Equal(25, y, 6);

        Rigid both = space.Then(new Rigid(-30, 3, 4));
        (double backX, double backY) = both.Inverse().Apply(both.Apply(7, -2).X, both.Apply(7, -2).Y);
        Assert.Equal(7, backX, 6);
        Assert.Equal(-2, backY, 6);
    }

    [Fact]
    public void Grouping_keeps_everything_where_it_is_on_screen()
    {
        GeneratedPromoTemplate template = Template();
        GeneratedLayer bow = template.Layers[0];
        GeneratedLayer box = template.Layers[1];
        WorldBox bowBefore = GeneratedPromoHierarchy.World(template, bow, Own, Second);
        WorldBox boxBefore = GeneratedPromoHierarchy.World(template, box, Own, Second);

        GeneratedLayer group = GeneratedPromoHierarchy.Group(template, [bow, box], Second);

        Assert.Equal(GeneratedLayerKind.Group, group.Kind);
        Assert.NotNull(group.Id);
        Assert.Equal(group.Id, bow.Parent);
        Assert.Equal(group.Id, box.Parent);
        Assert.Equal(GeneratedTransition.None, group.TransitionIn);
        // The group comes first, then its layers in the order they were drawn.
        Assert.Equal(new[] { group, bow, box }, template.Layers.Take(3));
        SameBox(bowBefore, GeneratedPromoHierarchy.World(template, bow, Own, Second));
        SameBox(boxBefore, GeneratedPromoHierarchy.World(template, box, Own, Second));
        // Its box fits them: the turned bow's corners included.
        Assert.True(group.X <= 60 && group.Y <= 50 && group.X + group.Width >= 160 && group.Y + group.Height >= 200);

        GeneratedPromoHierarchy.Ungroup(template, group, Second);
        Assert.DoesNotContain(group, template.Layers);
        Assert.Null(bow.Parent);
        SameBox(bowBefore, GeneratedPromoHierarchy.World(template, bow, Own, Second));
    }

    [Fact]
    public void Turning_a_group_turns_its_layers_about_its_middle_and_adds_to_their_own_turn()
    {
        GeneratedPromoTemplate template = Template();
        GeneratedLayer bow = template.Layers[0];
        GeneratedLayer group = GeneratedPromoHierarchy.Group(template, [bow, template.Layers[1]], Second);
        WorldBox before = GeneratedPromoHierarchy.World(template, bow, Own, Second);
        double pivotX = group.X + group.Width / 2.0;
        double pivotY = group.Y + group.Height / 2.0;

        group.Rotation = 90;
        WorldBox after = GeneratedPromoHierarchy.World(template, bow, Own, Second);
        (double expectedX, double expectedY) = Rigid.Rotate(before.CenterX - pivotX, before.CenterY - pivotY, 90);
        Assert.Equal(pivotX + expectedX, after.CenterX, 2);
        Assert.Equal(pivotY + expectedY, after.CenterY, 2);
        Assert.Equal(120, after.Angle, 2);
    }

    [Fact]
    public void A_group_carries_its_layers_while_they_keep_their_own_animation()
    {
        GeneratedPromoTemplate template = Template();
        GeneratedLayer bow = template.Layers[0];
        GeneratedLayer group = GeneratedPromoHierarchy.Group(template, [bow], Second);

        // The bow rocks inside the group while the group slides right.
        GeneratedPromoEditing.StartKeyframes(bow, TimeSpan.Zero);
        GeneratedPromoEditing.SetPose(bow, TimeSpan.FromSeconds(2), GeneratedPromoEditing.PoseAt(bow, TimeSpan.Zero) with { Rotation = -30 });
        GeneratedPromoEditing.StartKeyframes(group, TimeSpan.Zero);
        GeneratedPromoEditing.SetPose(group, TimeSpan.FromSeconds(2), GeneratedPromoEditing.PoseAt(group, TimeSpan.Zero) with { X = group.X + 100 });

        WorldBox start = GeneratedPromoHierarchy.World(template, bow, Own, TimeSpan.Zero);
        WorldBox end = GeneratedPromoHierarchy.World(template, bow, Own, TimeSpan.FromSeconds(2));
        Assert.Equal(start.CenterX + 100, end.CenterX, 1);
        Assert.Equal(30, start.Angle, 1);
        Assert.Equal(-30, end.Angle, 1);

        ResolvedGeneratedPromo promo = GeneratedPromoResolver.Resolve(template, "Channel", [new UpNextListing("Show", Noon)], TimeZoneInfo.Utc, Noon)!;
        PromoComposition composition = GeneratedPromoResolver.Compose(promo);
        PromoGroupLayer drawnGroup = Assert.IsType<PromoGroupLayer>(composition.Layers[0]);
        PromoLayer drawnBow = composition.Layers[1];
        Assert.Same(drawnGroup, drawnBow.Parent);
        Assert.Equal(2, drawnBow.Keyframes!.Count);
        Assert.Equal(-30, drawnBow.Keyframes[1].Rotation, 3);
    }

    [Fact]
    public void Group_timing_and_opacity_apply_to_what_is_inside()
    {
        var group = new PromoGroupLayer { Bounds = SKRect.Create(0, 0, 100, 100), Start = Second, End = TimeSpan.FromSeconds(3), Opacity = 0.5f,
            In = PromoTransition.None, Out = PromoTransition.None };
        var inside = new PromoRectLayer { Bounds = SKRect.Create(10, 10, 10, 10), Opacity = 0.5f, In = PromoTransition.None, Out = PromoTransition.None, Parent = group };

        Assert.Null(PromoAnimation.EvaluateInGroups(inside, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(5), 100));
        Assert.Equal(0.25f, PromoAnimation.EvaluateInGroups(inside, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), 100)!.Value.Opacity, 3);
        Assert.Null(PromoAnimation.EvaluateInGroups(inside, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5), 100));
    }

    [Fact]
    public void Layers_are_drawn_in_their_groups_space()
    {
        var group = new PromoGroupLayer { Bounds = SKRect.Create(0, 0, 200, 200), Rotation = 180, In = PromoTransition.None, Out = PromoTransition.None };
        var dot = new PromoRectLayer { Bounds = SKRect.Create(10, 10, 20, 20), Color = SKColors.Red, In = PromoTransition.None, Out = PromoTransition.None, Parent = group };
        using var bitmap = new SKBitmap(200, 200);
        using (var canvas = new SKCanvas(bitmap))
        using (var images = new PromoImageCache())
        {
            PromoRenderer.DrawFrame(canvas, new PromoComposition { Width = 200, Height = 200, Duration = TimeSpan.FromSeconds(5), Layers = [group, dot] },
                Second, images, drawBackground: false);
        }

        // Turned half way round the group's middle, the dot ends up in the opposite corner.
        Assert.Equal(0, bitmap.GetPixel(20, 20).Alpha);
        Assert.Equal(255, bitmap.GetPixel(180, 180).Alpha);
    }

    [Fact]
    public void Dragging_in_the_list_moves_layers_into_and_out_of_groups_without_moving_them_on_screen()
    {
        GeneratedPromoTemplate template = Template();
        GeneratedLayer bow = template.Layers[0];
        GeneratedLayer box = template.Layers[1];
        GeneratedLayer title = template.Layers[2];
        GeneratedLayer group = GeneratedPromoHierarchy.Group(template, [bow, box], Second);
        group.Rotation = 45;
        WorldBox titleBefore = GeneratedPromoHierarchy.World(template, title, Own, Second);

        Assert.True(GeneratedPromoHierarchy.Move(template, [title], group, LayerDrop.Into, Second));
        Assert.Equal(group.Id, title.Parent);
        SameBox(titleBefore, GeneratedPromoHierarchy.World(template, title, Own, Second));
        // In front of what was there: the last of the group's layers.
        Assert.Same(title, GeneratedPromoHierarchy.Children(template, group)[^1]);

        Assert.True(GeneratedPromoHierarchy.Move(template, [title], group, LayerDrop.Below, Second));
        Assert.Null(title.Parent);
        Assert.Same(title, template.Layers[0]);
        SameBox(titleBefore, GeneratedPromoHierarchy.World(template, title, Own, Second));

        // A group can't go inside itself, and only groups take layers in.
        Assert.False(GeneratedPromoHierarchy.Move(template, [group], bow, LayerDrop.Above, Second));
        Assert.False(GeneratedPromoHierarchy.Move(template, [title], box, LayerDrop.Into, Second));

        // The rows show the group with its layers under it, top first.
        List<(GeneratedLayer Layer, int Depth)> rows = GeneratedPromoHierarchy.Rows(template, _ => true);
        Assert.Equal(new[] { group, box, bow, title }, rows.Select(row => row.Layer));
        Assert.Equal(new[] { 0, 1, 1, 0 }, rows.Select(row => row.Depth));
    }

    [Fact]
    public void Duplicating_a_group_copies_what_is_inside_with_ids_of_its_own()
    {
        GeneratedPromoTemplate template = Template();
        GeneratedLayer group = GeneratedPromoHierarchy.Group(template, [template.Layers[0], template.Layers[1]], Second);

        GeneratedLayer copy = GeneratedPromoHierarchy.Duplicate(template, group);

        Assert.NotEqual(group.Id, copy.Id);
        Assert.Equal(2, GeneratedPromoHierarchy.Children(template, copy).Count);
        Assert.Equal(2, GeneratedPromoHierarchy.Children(template, group).Count);
        Assert.Equal(group.X + 20, copy.X);

        GeneratedPromoHierarchy.Remove(template, copy);
        Assert.Equal(4, template.Layers.Count);
    }

    [Fact]
    public void Locking_is_kept_with_the_schedule_but_leaves_renders_alone()
    {
        GeneratedPromoTemplate template = Template();
        string unlocked = GeneratedPromoResolver.Resolve(template, "Channel", [new UpNextListing("Show", Noon)], TimeZoneInfo.Utc, Noon)!.Key;
        Assert.DoesNotContain("locked", SchedulerConfigLoader.Serialize(new SchedulerConfig
        {
            Channels = [new() { Id = "channel", Name = "Channel", Number = 1, BaseRotation = new() { Query = "*" },
                GeneratedPromos = new() { Templates = [template] } }]
        }));

        template.Layers[0].Locked = true;
        Assert.Equal(unlocked, GeneratedPromoResolver.Resolve(template, "Channel", [new UpNextListing("Show", Noon)], TimeZoneInfo.Utc, Noon)!.Key);

        var config = new SchedulerConfig
        {
            Channels = [new() { Id = "channel", Name = "Channel", Number = 1, BaseRotation = new() { Query = "*" },
                GeneratedPromos = new() { Templates = [template] } }]
        };
        GeneratedPromoTemplate loaded = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config)).Channels[0].GeneratedPromos!.Templates[0];
        Assert.True(loaded.Layers[0].Locked);
        Assert.False(loaded.Layers[1].Locked);
    }

    [Fact]
    public void Groups_must_have_ids_and_layers_must_name_real_groups()
    {
        GeneratedPromoTemplate template = Template();
        GeneratedPromoHierarchy.Group(template, [template.Layers[0]], Second);
        var config = new SchedulerConfig
        {
            Channels = [new() { Id = "channel", Name = "Channel", Number = 1, BaseRotation = new() { Query = "*" },
                GeneratedPromos = new() { Templates = [template] } }]
        };
        Assert.DoesNotContain(SchedulerConfigLoader.Validate(config).Errors, error => error.Contains("group"));

        GeneratedPromoTemplate parsed = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config)).Channels[0].GeneratedPromos!.Templates[0];
        Assert.Equal(parsed.Layers[0].Id, parsed.Layers[1].Parent);

        template.Layers[1].Parent = "missing";
        template.Layers.Add(new GeneratedLayer { Name = "Nameless", Kind = GeneratedLayerKind.Group });
        IReadOnlyList<string> errors = SchedulerConfigLoader.Validate(config).Errors;
        Assert.Contains(errors, error => error.Contains("isn't a group"));
        Assert.Contains(errors, error => error.Contains("needs an id"));
    }
}
