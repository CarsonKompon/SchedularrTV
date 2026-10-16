using SchedularrTV.Core.Editing;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class KeyframeSelectionTests
{
    private sealed record Key(double At);

    [Fact]
    public void Control_toggles_and_plain_press_keeps_a_selected_group_for_dragging()
    {
        Key[] keys = [new(0), new(1), new(2), new(3)];
        var selection = new KeyframeSelection<Key>(key => key.At);
        selection.Bind(keys, keys);
        Assert.True(selection.Click(keys[0], keys, false, false));
        Assert.True(selection.Click(keys[2], keys, true, false));
        Assert.Equal([keys[0], keys[2]], selection.Selected);
        selection.Click(keys[0], keys, false, false);
        Assert.Equal(2, selection.Selected.Count);
        Assert.False(selection.Click(keys[2], keys, true, false));
        Assert.Same(keys[0], Assert.Single(selection.Selected));
        selection.Click(keys[3], keys, false, false);
        Assert.Same(keys[3], Assert.Single(selection.Selected));
    }

    [Fact]
    public void Shift_selects_chronological_ranges_in_both_directions_from_a_stable_anchor()
    {
        Key[] keys = [new(3), new(1), new(2), new(0)];
        var selection = new KeyframeSelection<Key>(key => key.At);
        selection.Bind(keys, keys);
        selection.Click(keys[1], keys, false, false);
        selection.Click(keys[0], keys, false, true);
        Assert.Equal([1d, 2d, 3d], selection.Selected.Select(key => key.At));
        selection.Click(keys[3], keys, false, true);
        Assert.Equal([0d, 1d], selection.Selected.Select(key => key.At));
        selection.Click(keys[0], keys, true, true);
        Assert.Equal([0d, 1d, 2d, 3d], selection.Selected.Select(key => key.At));
    }

    [Fact]
    public void Selection_uses_identity_and_prunes_removed_keys_or_a_changed_layer()
    {
        Key[] keys = [new(1), new(1)];
        var selection = new KeyframeSelection<Key>(key => key.At);
        selection.Bind(keys, keys);
        selection.Click(keys[0], keys, false, false);
        selection.Click(keys[1], keys, true, false);
        Assert.Equal(2, selection.Selected.Count);
        selection.Bind(keys, [keys[1]]);
        Assert.Same(keys[1], Assert.Single(selection.Selected));
        selection.Bind(new object(), keys);
        Assert.Empty(selection.Selected);
    }

    [Theory]
    [InlineData(-100, -1)]
    [InlineData(100, 4)]
    [InlineData(0.12, 0.1333333333)]
    public void Group_offsets_snap_and_clamp_as_one_unit(double requested, double expected)
    {
        double[] starts = [1, 3, 6];
        double delta = KeyframeMoves.Delta(starts, requested, 10, 30);
        Assert.Equal(expected, delta, 8);
        double[] moved = [.. starts.Select(time => time + delta)];
        Assert.Equal(2, moved[1] - moved[0], 8);
        Assert.Equal(3, moved[2] - moved[1], 8);
        Assert.All(moved, time => Assert.InRange(time, 0, 10));
    }

    [Fact]
    public void Saved_subframe_rounding_does_not_leave_a_group_one_frame_short_of_the_boundary()
    {
        double delta = KeyframeMoves.Delta([1.0333, 2.0333], -20, 10, 30);
        Assert.Equal(0, Math.Round(1.0333 + delta, 4));
        Assert.Equal(1, Math.Round(2.0333 + delta, 4));
    }
}
