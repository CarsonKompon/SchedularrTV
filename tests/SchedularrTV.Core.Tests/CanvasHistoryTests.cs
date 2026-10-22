using SchedularrTV.Core.Configuration;
using SchedularrTV.GeneratedPromos;
using SchedularrTV.Overlays;
using SchedularrTV.Service.Editor;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class CanvasHistoryTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Pass(double seconds = 2) => Now = Now.AddSeconds(seconds);
    }

    private static (GeneratedPromoTemplate Template, CanvasHistory History, Clock Clock) Editing()
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext();
        var clock = new Clock();
        CanvasHistory history = null!;
        history = new CanvasHistory(() => CanvasSnapshots.Capture(template), snapshot =>
        {
            CanvasSnapshots.RestoreInto(template, snapshot);
            return Task.CompletedTask;
        }, time: clock);
        return (template, history, clock);
    }

    [Fact]
    public async Task Changes_undo_and_redo_in_place()
    {
        (GeneratedPromoTemplate template, CanvasHistory history, Clock clock) = Editing();
        List<GeneratedLayer> layers = template.Layers;
        float x = template.Layers[0].X;

        template.Layers[0].X = 500;
        history.Record();
        clock.Pass();
        template.Layers.Add(new GeneratedLayer { Name = "Extra", Kind = GeneratedLayerKind.Rect });
        history.Record();
        Assert.Equal(2, history.UndoCount);

        Assert.True(await history.UndoAsync());
        Assert.DoesNotContain(template.Layers, layer => layer.Name == "Extra");
        Assert.Equal(500, template.Layers[0].X);

        Assert.True(await history.UndoAsync());
        Assert.Equal(x, template.Layers[0].X);
        Assert.False(history.CanUndo);
        Assert.False(await history.UndoAsync());

        Assert.True(await history.RedoAsync());
        Assert.True(await history.RedoAsync());
        Assert.Contains(template.Layers, layer => layer.Name == "Extra");
        Assert.False(history.CanRedo);

        // The same template object throughout, so whatever holds it sees the change.
        Assert.NotSame(layers, template.Layers);
    }

    [Fact]
    public async Task A_new_change_after_undoing_drops_what_could_be_redone()
    {
        (GeneratedPromoTemplate template, CanvasHistory history, Clock clock) = Editing();
        template.Name = "One";
        history.Record();
        clock.Pass();
        template.Name = "Two";
        history.Record();
        clock.Pass();

        await history.UndoAsync();
        Assert.Equal("One", template.Name);
        template.Name = "Three";
        history.Record();
        Assert.False(history.CanRedo);
        await history.UndoAsync();
        Assert.Equal("One", template.Name);
    }

    [Fact]
    public async Task Changes_close_together_are_one_step_and_nothing_changed_records_nothing()
    {
        (GeneratedPromoTemplate template, CanvasHistory history, Clock clock) = Editing();
        string name = template.Name;
        history.Record();
        Assert.False(history.CanUndo);

        template.Name = "A";
        history.Record();
        clock.Pass(0.1);
        template.Name = "AB";
        history.Record();
        Assert.Equal(1, history.UndoCount);

        await history.UndoAsync();
        Assert.Equal(name, template.Name);
    }

    [Fact]
    public async Task A_layer_locked_in_the_editor_is_put_back_too()
    {
        (GeneratedPromoTemplate template, CanvasHistory history, _) = Editing();
        template.Layers[1].Locked = true;
        history.Record();

        await history.UndoAsync();
        Assert.False(template.Layers[1].Locked);
        await history.RedoAsync();
        Assert.True(template.Layers[1].Locked);
    }

    [Fact]
    public async Task Overlays_restore_in_place_and_keep_old_steps_within_the_limit()
    {
        var overlay = new Overlay { Name = "Bug", Layers = [new OverlayLayer { Name = "Box", Kind = OverlayLayerKinds.Rect }] };
        var clock = new Clock();
        var history = new CanvasHistory(() => CanvasSnapshots.Capture(overlay), snapshot =>
        {
            CanvasSnapshots.RestoreInto(overlay, snapshot);
            return Task.CompletedTask;
        }, limit: 3, time: clock);

        for (int step = 1; step <= 5; step++)
        {
            overlay.Layers[0].X = step * 10;
            history.Record();
            clock.Pass();
        }

        Assert.Equal(3, history.UndoCount);
        while (await history.UndoAsync()) { }
        Assert.Equal(20, overlay.Layers[0].X);
    }
}
