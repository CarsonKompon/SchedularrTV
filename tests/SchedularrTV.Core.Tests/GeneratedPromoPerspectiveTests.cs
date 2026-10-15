using System.Text.Json;
using SchedularrTV.Core.Configuration;
using SchedularrTV.GeneratedPromos;
using SkiaSharp;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class GeneratedPromoPerspectiveTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    // Like the side of a building going away to the right: the right edge shorter and nearer the middle.
    private static readonly PromoCorners Wall = new(0, 0, -20, 30, -20, -30, 0, 0);

    [Fact]
    public void The_perspective_takes_each_corner_where_it_was_pulled_and_keeps_the_middle_inside()
    {
        SKRect box = SKRect.Create(100, 50, 200, 100);
        double[] homography = PromoPerspective.Homography(box, Wall)!;
        (double X, double Y)[] quad = PromoPerspective.Quad(box, Wall);

        (double X, double Y)[] corners = [(100, 50), (300, 50), (300, 150), (100, 150)];
        for (int index = 0; index < 4; index++)
        {
            (double x, double y) = PromoPerspective.Apply(homography, corners[index].X, corners[index].Y);
            Assert.Equal(quad[index].X, x, 6);
            Assert.Equal(quad[index].Y, y, 6);
        }

        // Perspective, not a plain skew: the middle lands where the pulled shape's diagonals cross, past halfway,
        // as the far (shorter) side is further away.
        (double middleX, double middleY) = PromoPerspective.Apply(homography, 200, 100);
        Assert.Equal(100 + 180 * (1 + 30 / 70.0) / 2, middleX, 3);
        Assert.Equal(100, middleY, 3);

        Assert.Null(PromoPerspective.Homography(box, default));
        // The top right corner pulled past the top left: it would fold over itself, so it's left square.
        Assert.Null(PromoPerspective.Homography(box, new PromoCorners(0, 0, -400, 0, 0, 0, 0, 0)));
    }

    [Fact]
    public void Pinned_layers_are_drawn_into_their_pulled_shape()
    {
        var shape = new PromoRectLayer
        {
            Bounds = SKRect.Create(50, 50, 100, 100), Color = SKColors.Red, In = PromoTransition.None, Out = PromoTransition.None,
            // The bottom pulled out to the left, past where the square was.
            Corners = new PromoCorners(0, 0, 0, 0, 0, 0, -40, 0)
        };
        using var bitmap = new SKBitmap(200, 200);
        using (var canvas = new SKCanvas(bitmap))
        using (var images = new PromoImageCache())
        {
            PromoRenderer.DrawFrame(canvas, new PromoComposition { Width = 200, Height = 200, Duration = TimeSpan.FromSeconds(5), Layers = [shape] },
                TimeSpan.FromSeconds(1), images, drawBackground: false);
        }

        Assert.Equal(255, bitmap.GetPixel(30, 145).Alpha);
        Assert.Equal(0, bitmap.GetPixel(30, 55).Alpha);
        Assert.Equal(255, bitmap.GetPixel(100, 100).Alpha);
    }

    [Fact]
    public void Corner_pins_are_keyframed_and_reach_the_render()
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext();
        Assert.DoesNotContain("Corners", JsonSerializer.Serialize(template));
        GeneratedLayer panel = template.Layers[0];

        GeneratedPromoEditing.StartKeyframes(panel, TimeSpan.Zero);
        GeneratedPromoEditing.SetPose(panel, TimeSpan.FromSeconds(2), GeneratedPromoEditing.PoseAt(panel, TimeSpan.Zero) with { Corners = Wall });
        Assert.Null(panel.Keyframes![0].Corners);
        Assert.Equal(-20, panel.Keyframes[1].Corners!.TopRightX);

        // Half way, half pulled.
        PromoCorners halfway = GeneratedPromoEditing.PoseAt(panel, TimeSpan.FromSeconds(1)).Corners!.Value;
        Assert.Equal(-10, halfway.TopRightX, 3);
        Assert.Equal(15, halfway.TopRightY, 3);

        ResolvedGeneratedPromo promo = GeneratedPromoResolver.Resolve(template, "Channel", [new UpNextListing("Show", Noon.AddMinutes(30))], TimeZoneInfo.Utc, Noon)!;
        PromoLayer drawn = GeneratedPromoResolver.Compose(promo).Layers[0];
        Assert.Equal(Wall, drawn.Keyframes![1].Corners);
        Assert.Equal(-10, PromoAnimation.Evaluate(drawn, TimeSpan.FromSeconds(1), template.Duration, template.Height)!.Value.Corners.TopRightX, 3);

        // The editor's outline follows the pin: the top right corner is where it was pulled.
        (double X, double Y)[] quad = GeneratedPromoHierarchy.WorldQuad(template, panel, layer => layer, TimeSpan.FromSeconds(2));
        Assert.Equal(panel.Keyframes[1].X + panel.Keyframes[1].Width - 20, quad[1].X, 2);
        Assert.Equal(panel.Keyframes[1].Y + 30, quad[1].Y, 2);

        // Squaring up keeps nothing behind.
        GeneratedPromoEditing.StopKeyframes(panel, TimeSpan.Zero);
        Assert.Null(panel.Corners);
    }

    [Fact]
    public void A_pinned_group_stretches_what_is_inside_it()
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext();
        GeneratedLayer heading = template.Layers[2];
        GeneratedLayer group = GeneratedPromoHierarchy.Group(template, [heading], TimeSpan.Zero);
        (double X, double Y)[] before = GeneratedPromoHierarchy.WorldQuad(template, heading, layer => layer, TimeSpan.Zero);

        group.Corners = new GeneratedCorners { TopRightY = 40 };
        (double X, double Y)[] after = GeneratedPromoHierarchy.WorldQuad(template, heading, layer => layer, TimeSpan.Zero);

        // The heading fills the group, so its top right corner goes where the group's did.
        Assert.Equal(before[1].Y + 40, after[1].Y, 1);
        Assert.Equal(before[0].Y, after[0].Y, 1);
    }
}
