using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SchedularrTV.Service.Editor;

/// <summary>
/// Undo and redo for a canvas editor (overlays, generated promos, credit squeeze looks): each change the editor commits
/// is kept as a snapshot of what it edits, and undoing puts an earlier snapshot back. Changes made close together (one
/// action that commits twice, a slider dragged along) are one step.
/// </summary>
public sealed class CanvasHistory
{
    private readonly Func<string> _capture;
    private readonly Func<string, Task> _restore;
    private readonly TimeProvider _time;
    private readonly List<string> _undo = [];
    private readonly List<string> _redo = [];
    private string _current;
    private DateTimeOffset _lastRecord = DateTimeOffset.MinValue;
    private bool _restoring;

    /// <summary>How many steps back are kept.</summary>
    public int Limit { get; }

    /// <summary>Changes recorded within this long of the one before are taken as the same step.</summary>
    public static readonly TimeSpan MergeWithin = TimeSpan.FromMilliseconds(400);

    /// <param name="capture">A snapshot of what's edited, as it is now (see <see cref="CanvasSnapshots"/>).</param>
    /// <param name="restore">Puts a snapshot back into what's edited, and lets the editor catch up (selection, preview, saving).</param>
    public CanvasHistory(Func<string> capture, Func<string, Task> restore, int limit = 100, TimeProvider? time = null)
    {
        _capture = capture;
        _restore = restore;
        _time = time ?? TimeProvider.System;
        Limit = Math.Max(1, limit);
        _current = capture();
    }

    public bool CanUndo => _undo.Count > 0 && !_restoring;

    public bool CanRedo => _redo.Count > 0 && !_restoring;

    public int UndoCount => _undo.Count;

    public int RedoCount => _redo.Count;

    /// <summary>Raised whenever what can be undone or redone changes.</summary>
    public event Action? Changed;

    /// <summary>
    /// Takes note of what's edited after a change: a new step (and nothing left to redo) when it differs from the last
    /// one, folded into that step when it came close behind it. Nothing happens while a step is being put back.
    /// </summary>
    public void Record()
    {
        if (_restoring)
        {
            return;
        }

        string now = _capture();
        if (string.Equals(now, _current, StringComparison.Ordinal))
        {
            return;
        }

        DateTimeOffset at = _time.GetUtcNow();
        bool merge = _undo.Count > 0 && _redo.Count == 0 && at - _lastRecord < MergeWithin;
        if (!merge)
        {
            _undo.Add(_current);
            if (_undo.Count > Limit)
            {
                _undo.RemoveAt(0);
            }
        }

        _current = now;
        _lastRecord = at;
        _redo.Clear();
        Changed?.Invoke();
    }

    /// <summary>Forgets every step: what's edited now is where history starts (another item opened in the editor, say).</summary>
    public void Reset()
    {
        _undo.Clear();
        _redo.Clear();
        _current = _capture();
        _lastRecord = DateTimeOffset.MinValue;
        Changed?.Invoke();
    }

    /// <summary>Goes back a step; false when there's nothing to undo.</summary>
    public Task<bool> UndoAsync() => StepAsync(_undo, _redo);

    /// <summary>Goes forward a step that was undone; false when there's nothing to redo.</summary>
    public Task<bool> RedoAsync() => StepAsync(_redo, _undo);

    private async Task<bool> StepAsync(List<string> from, List<string> to)
    {
        if (from.Count == 0 || _restoring)
        {
            return false;
        }

        // Anything changed since the last record (and not recorded) is kept first, so it isn't lost.
        Record();
        if (from.Count == 0)
        {
            return false;
        }

        string target = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(_current);
        _restoring = true;
        try
        {
            await _restore(target).ConfigureAwait(true);
        }
        finally
        {
            _restoring = false;
            // What it reads as once put back (and tidied by the editor), so the next change compares with that.
            _current = _capture();
            _lastRecord = DateTimeOffset.MinValue;
            Changed?.Invoke();
        }

        return true;
    }
}

/// <summary>
/// Snapshots of what a canvas editor edits, for <see cref="CanvasHistory"/>: JSON of the whole thing, including the
/// simple settings kept out of JSON elsewhere (a layer's lock in the editor), put back into the same object so
/// everything holding it sees the change.
/// </summary>
public static class CanvasSnapshots
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { IncludeIgnoredSettings } }
    };

    public static string Capture<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Read<T>(string snapshot) => JsonSerializer.Deserialize<T>(snapshot, Options)
        ?? throw new InvalidOperationException("The snapshot is empty.");

    /// <summary>
    /// Puts <paramref name="snapshot"/> back into <paramref name="target"/> itself: each of its settable properties takes
    /// the snapshot's value. Links to things outside it that aren't in snapshots (a shared image or voice pack it uses)
    /// are kept at the top; deeper ones are relinked by the editor.
    /// </summary>
    public static void RestoreInto<T>(T target, string snapshot) where T : class
    {
        T read = Read<T>(snapshot);
        foreach (PropertyInfo property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || property.GetMethod is not { IsPublic: true } || property.SetMethod is not { IsPublic: true })
            {
                continue;
            }

            if (Ignored(property) && !Simple(property.PropertyType))
            {
                continue;
            }

            property.SetValue(target, property.GetValue(read));
        }
    }

    // JsonIgnore'd settings that are simple values (a bool, a number, an enum) and can be set go into snapshots too:
    // a layer locked in the editor is part of what undo puts back. References kept out of JSON stay out.
    private static void IncludeIgnoredSettings(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        foreach (PropertyInfo property in info.Type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || !Ignored(property) || !Simple(property.PropertyType)
                || property.GetMethod is not { IsPublic: true } || property.SetMethod is not { IsPublic: true }
                || info.Properties.Any(existing => existing.Name == "~" + property.Name))
            {
                continue;
            }

            JsonPropertyInfo added = info.CreateJsonPropertyInfo(property.PropertyType, "~" + property.Name);
            added.Get = property.GetValue;
            added.Set = property.SetValue;
            info.Properties.Add(added);
        }
    }

    private static bool Ignored(PropertyInfo property) =>
        property.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always };

    private static bool Simple(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(TimeSpan);
    }
}
