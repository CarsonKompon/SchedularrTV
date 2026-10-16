namespace SchedularrTV.Core.Editing;

/// <summary>Timeline selection shared by the promo and overlay editors; keys are compared by identity.</summary>
public sealed class KeyframeSelection<T>(Func<T, double> time) where T : class
{
    private readonly HashSet<T> _selected = new(ReferenceEqualityComparer.Instance);
    private object? _scope;
    private T? _anchor;

    public IReadOnlyList<T> Selected => _selected.OrderBy(time).ToArray();

    /// <summary>Changing layer/placement drops the selection; deleted/replaced keys are pruned.</summary>
    public void Bind(object? scope, IEnumerable<T> keys)
    {
        if (!ReferenceEquals(_scope, scope)) Clear();
        _scope = scope;
        var available = new HashSet<T>(keys, ReferenceEqualityComparer.Instance);
        _selected.RemoveWhere(key => !available.Contains(key));
        if (_anchor is not null && !available.Contains(_anchor)) _anchor = null;
    }

    /// <summary>Returns whether the clicked key is selected and can start a drag.</summary>
    public bool Click(T key, IEnumerable<T> keys, bool toggle, bool range)
    {
        T[] ordered = keys.OrderBy(time).ToArray();
        int from = Array.FindIndex(ordered, item => ReferenceEquals(item, _anchor));
        int to = Array.FindIndex(ordered, item => ReferenceEquals(item, key));
        if (range && from >= 0 && to >= 0)
        {
            if (!toggle) _selected.Clear();
            foreach (T item in ordered.Skip(Math.Min(from, to)).Take(Math.Abs(to - from) + 1)) _selected.Add(item);
        }
        else if (toggle)
        {
            if (!_selected.Add(key)) _selected.Remove(key);
            _anchor = key;
        }
        else
        {
            // Pressing a selected diamond keeps the group together for dragging.
            if (!_selected.Contains(key)) _selected.Clear();
            _selected.Add(key);
            _anchor = key;
        }

        return _selected.Contains(key);
    }

    /// <summary>Remaps the selection when editing creates a copy-on-write background placement.</summary>
    public void Replace(object scope, IEnumerable<T> keys, T? anchor = null)
    {
        Clear();
        _scope = scope;
        _selected.UnionWith(keys);
        _anchor = anchor ?? Selected.LastOrDefault();
    }

    public void Clear()
    {
        _selected.Clear();
        _anchor = null;
    }
}

public static class KeyframeMoves
{
    /// <summary>A single frame-snapped offset for the whole group, clamped without squashing its spacing.</summary>
    public static double Delta(IEnumerable<double> starts, double requested, double last, double fps)
    {
        double[] times = starts.ToArray();
        if (times.Length == 0 || !double.IsFinite(requested) || !double.IsFinite(last) || !double.IsFinite(fps) || fps <= 0) return 0;
        // Saved keyframe times use four decimal places; allow their rounding error at a frame boundary.
        double tolerance = 0.000051 * fps;
        double low = Math.Ceiling(-times.Min() * fps - tolerance) / fps;
        double high = Math.Floor((last - times.Max()) * fps + tolerance) / fps;
        return low > high ? 0 : Math.Clamp(Math.Round(requested * fps) / fps, low, high);
    }
}
