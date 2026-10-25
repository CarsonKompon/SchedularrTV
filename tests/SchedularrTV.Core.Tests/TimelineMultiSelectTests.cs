using System.Collections;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using SchedularrTV.Core.Configuration;
using SchedularrTV.GeneratedPromos;
using SchedularrTV.GeneratedPromos.Components;
using SchedularrTV.Overlays;
using SchedularrTV.Overlays.Components;
using SchedularrTV.Service.Editor;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>Exercises the editors' actual event handlers without starting their asynchronous preview renderers.</summary>
public sealed class TimelineMultiSelectTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Selected_keys_drag_ease_and_delete_together_in_both_editors(bool overlay, bool background)
    {
        using var editor = new Editor(overlay, background);
        object[] originals = editor.Keys;
        await editor.Click(originals[0]);
        await editor.Click(originals[2], ctrl: true);
        Assert.Equal(2, editor.Selection.Length);

        // Pressing a selected key keeps the group; one second right moves both and leaves the middle key alone.
        await editor.Call("StartKeyDrag", new PointerEventArgs { ClientX = 100 }, editor.Layer, originals[0]);
        await editor.Call("OverlayMove", new PointerEventArgs { ClientX = 200 });
        await editor.Call("OverlayEnd", new PointerEventArgs { ClientX = 200 });
        Assert.Equal([2d, 3d, 6d], editor.Keys.Select(At));
        Assert.Equal(2, editor.Selection.Length);
        if (background)
        {
            Assert.Equal([1d, 3d, 5d], originals.Select(At));
            Assert.DoesNotContain(editor.Selection, key => originals.Contains(key));
        }

        var easing = new ChangeEventArgs { Value = nameof(GeneratedEasing.EaseInOut) };
        if (overlay) await editor.Call("SetSelectedEasing", easing);
        else await editor.Call("SetSelectedEasing", editor.Layer, easing);
        Assert.All(editor.Selection, key => Assert.Equal(GeneratedEasing.EaseInOut, Ease(key)));
        Assert.Equal(GeneratedEasing.Linear, Ease(editor.Keys[1]));
        if (background) Assert.All(originals, key => Assert.Equal(GeneratedEasing.Linear, Ease(key)));

        await editor.Call("RulerKey", new KeyboardEventArgs { Key = "Delete" });
        Assert.Equal(3d, At(Assert.Single(editor.Keys)));
        Assert.Empty(editor.Selection);
        if (background) Assert.Equal(3, ((GeneratedLayer)editor.Layer).Keyframes!.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shift_range_and_control_deselection_drive_bulk_actions(bool overlay)
    {
        using var editor = new Editor(overlay, false);
        object[] keys = editor.Keys;
        await editor.Click(keys[0]);
        await editor.Click(keys[2], shift: true);
        Assert.Equal(3, editor.Selection.Length);
        await editor.Click(keys[1], ctrl: true);
        Assert.Equal([keys[0], keys[2]], editor.Selection);
        await editor.Call("RemoveSelectedKeys", editor.Layer);
        Assert.Same(keys[1], Assert.Single(editor.Keys));

        await editor.Click(keys[1]);
        await editor.Call("RulerKey", new KeyboardEventArgs { Key = "Backspace" });
        Assert.Empty(editor.Keys);
        Assert.Empty(editor.Selection);
    }

    [Fact]
    public async Task Bulk_easing_creates_a_background_placement_without_changing_base_keys()
    {
        using var editor = new Editor(false, true);
        object[] originals = editor.Keys;
        await editor.Click(originals[0]);
        await editor.Click(originals[2], shift: true);
        await editor.Call("SetSelectedEasing", editor.Layer, new ChangeEventArgs { Value = nameof(GeneratedEasing.Hold) });
        Assert.All(editor.Keys, key => Assert.Equal(GeneratedEasing.Hold, Ease(key)));
        Assert.All(originals, key => Assert.Equal(GeneratedEasing.Linear, Ease(key)));
        Assert.Equal(3, editor.Selection.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dragging_against_both_edges_keeps_spacing_and_returning_to_start_has_no_drift(bool overlay)
    {
        using var editor = new Editor(overlay, false);
        object[] keys = editor.Keys;
        await editor.Click(keys[0]);
        await editor.Click(keys[2], ctrl: true);
        await editor.Call("StartKeyDrag", new PointerEventArgs { ClientX = 100 }, editor.Layer, keys[0]);
        await editor.Call("OverlayMove", new PointerEventArgs { ClientX = -5000 });
        Assert.Equal(0, At(keys[0]));
        Assert.Equal(4, At(keys[2]));
        Assert.Equal(3, At(keys[1]));
        await editor.Call("OverlayMove", new PointerEventArgs { ClientX = 5000 });
        Assert.Equal(9.9667, At(keys[2]), 4);
        Assert.Equal(4, At(keys[2]) - At(keys[0]), 4);
        Assert.Equal(3, At(keys[1]));
        await editor.Call("OverlayEnd", new PointerEventArgs { ClientX = 100 });
        Assert.Equal([1d, 3d, 5d], editor.Keys.Select(At));
    }

    private static double At(object key) => ((TimeSpan)key.GetType().GetProperty("At")!.GetValue(key)!).TotalSeconds;
    private static GeneratedEasing Ease(object key) => (GeneratedEasing)key.GetType().GetProperty("Easing")!.GetValue(key)!;

    private sealed class Editor : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly ComponentBase _component;
        private readonly GeneratedPromoTemplate? _template;
        private readonly bool _background;
        public object Layer { get; }

        public Editor(bool overlay, bool background)
        {
            _background = background;
            if (overlay)
            {
                var layer = new OverlayLayer { Keyframes = [.. new[] { 1, 3, 5 }.Select(at => new OverlayKeyframe { At = TimeSpan.FromSeconds(at) })] };
                Layer = layer;
                _component = new OverlayEditor();
                var model = new Overlay
                {
                    Layers = [layer], AnimationSeconds = 10, Fps = 30, Schedule = new() { Always = true }
                };
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["Overlay"] = model }).SetParameterProperties(_component);
                Field("_history", new CanvasHistory(() => CanvasSnapshots.Capture(model), snapshot => Call("RestoreSnapshot", snapshot)));
                Field("_previewRunning", true);
            }
            else
            {
                var layer = new GeneratedLayer { Id = "test-layer", Keyframes = [.. new[] { 1, 3, 5 }.Select(at => new GeneratedKeyframe { At = TimeSpan.FromSeconds(at) })] };
                Layer = layer;
                _template = new GeneratedPromoTemplate
                {
                    Layers = [layer], Duration = TimeSpan.FromSeconds(10), Fps = 30,
                    Variants = background ? [new GeneratedPromoVariant { Id = "v1", Name = "Variant" }] : null
                };
                _component = new GeneratedPromoTemplateEditor();
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["Template"] = _template }).SetParameterProperties(_component);
                // These handler tests skip OnInitialized to avoid starting preview services, but commits still need real undo history.
                Field("_history", new CanvasHistory(() => CanvasSnapshots.Capture(_template), snapshot => Call("RestoreSnapshot", snapshot)));
                Field("_stillRunning", true);
                if (background) Field("_editVariant", "v1");
            }
            Field("_layer", Layer);
            Field("_rulerWidth", 1000d);
        }

        public object[] Keys => Layer is OverlayLayer overlay ? [.. (overlay.Keyframes ?? []).Cast<object>()]
            : [.. (GeneratedPromoResolver.Pose(_template!, _background ? "v1" : null, (GeneratedLayer)Layer).Keyframes ?? []).Cast<object>()];
        public object[] Selection => [.. ((IEnumerable)_component.GetType().GetProperty("SelectedKeys", Private)!.GetValue(_component)!).Cast<object>()];

        public async Task Click(object key, bool ctrl = false, bool shift = false)
        {
            var pointer = new PointerEventArgs { ClientX = 100, CtrlKey = ctrl, ShiftKey = shift };
            await Call("StartKeyDrag", pointer, Layer, key);
            await Call("OverlayEnd", pointer);
        }

        public async Task Call(string method, params object[] arguments)
        {
            object? result = _component.GetType().GetMethod(method, Private)!.Invoke(_component, arguments);
            if (result is Task task) await task;
        }

        private void Field(string name, object value) => _component.GetType().GetField(name, Private)!.SetValue(_component, value);
        public void Dispose() => ((CancellationTokenSource)_component.GetType().GetField("_life", Private)!.GetValue(_component)!).Dispose();
    }
}
