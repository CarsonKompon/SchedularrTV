using Microsoft.AspNetCore.Components;
using SchedularrTV.Service.Editor;

namespace SchedularrTV.Service.Components;

/// <summary>
/// Base for pages that render the shared <see cref="ConfigEditor"/> working copy.
/// </summary>
/// <remarks>
/// The editor is a singleton, so a discard, a save or an edit made in another tab replaces the model
/// underneath a page that has already rendered. Without this subscription the page keeps showing the
/// old data until something else happens to re-render it.
/// </remarks>
public abstract class EditorPage : ComponentBase, IDisposable
{
    [Inject]
    protected ConfigEditor Editor { get; set; } = default!;

    protected override void OnInitialized() => Editor.Changed += OnEditorChanged;

    /// <summary>Override to refresh anything cached from the working copy before re-rendering.</summary>
    protected virtual void OnEditorChanged() => InvokeAsync(StateHasChanged);

    public virtual void Dispose() => Editor.Changed -= OnEditorChanged;
}
