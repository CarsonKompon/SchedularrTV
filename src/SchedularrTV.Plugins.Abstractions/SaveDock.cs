using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;

namespace SchedularrTV.Plugins;

/// <summary>
/// The "Unsaved changes" bar along the bottom of the screen, with Discard and Save, the same one the schedule editor and
/// App settings use. A page keeps its edits in a working copy, shows this while that copy differs from what's saved, and
/// only writes when Save is pressed (<c>&lt;SaveDock Dirty="_dirty" OnSave="Save" OnDiscard="Load" /&gt;</c>).
/// </summary>
public sealed class SaveDock : ComponentBase
{
    private const string UndoIcon = """<path d="M9 14 4 9l5-5"/><path d="M4 9h10.5a5.5 5.5 0 0 1 5.5 5.5a5.5 5.5 0 0 1-5.5 5.5H11"/>""";
    private const string SaveIcon = """<path d="M15.2 3a2 2 0 0 1 1.4.6l3.8 3.8a2 2 0 0 1 .6 1.4V19a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z"/><path d="M17 21v-7a1 1 0 0 0-1-1H8a1 1 0 0 0-1 1v7"/><path d="M7 3v4a1 1 0 0 0 1 1h7"/>""";

    /// <summary>Whether there is anything unsaved; the bar is hidden otherwise.</summary>
    [Parameter] public bool Dirty { get; set; }

    [Parameter] public string Label { get; set; } = "Unsaved changes";

    /// <summary>False while something being edited can't be saved yet; <see cref="Problem"/> says why.</summary>
    [Parameter] public bool CanSave { get; set; } = true;

    /// <summary>Why Save is unavailable, shown beside the label and as the button's tooltip.</summary>
    [Parameter] public string? Problem { get; set; }

    [Parameter] public EventCallback OnSave { get; set; }

    [Parameter] public EventCallback OnDiscard { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (!Dirty)
        {
            return;
        }

        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "save-dock");
        builder.AddAttribute(2, "role", "region");
        builder.AddAttribute(3, "aria-label", Label);

        builder.OpenElement(10, "div");
        builder.AddAttribute(11, "class", "save-dock-text");
        builder.OpenElement(12, "span");
        builder.AddAttribute(13, "class", "status-dot warn");
        builder.CloseElement();
        builder.OpenElement(14, "span");
        builder.AddContent(15, !CanSave && !string.IsNullOrWhiteSpace(Problem) ? $"{Label}: {Problem}" : Label);
        builder.CloseElement();
        builder.CloseElement();

        builder.OpenElement(20, "button");
        builder.AddAttribute(21, "type", "button");
        builder.AddAttribute(22, "class", "btn btn-sm btn-ghost");
        builder.AddAttribute(23, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => OnDiscard.InvokeAsync()));
        builder.AddMarkupContent(24, Icon(UndoIcon));
        builder.AddContent(25, "Discard");
        builder.CloseElement();

        builder.OpenElement(30, "button");
        builder.AddAttribute(31, "type", "button");
        builder.AddAttribute(32, "class", "btn btn-sm btn-primary");
        builder.AddAttribute(33, "disabled", !CanSave);
        builder.AddAttribute(34, "title", CanSave ? null : Problem);
        builder.AddAttribute(35, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => OnSave.InvokeAsync()));
        builder.AddMarkupContent(36, Icon(SaveIcon));
        builder.AddContent(37, "Save changes");
        builder.CloseElement();

        builder.CloseElement();
    }

    // The same stroke icons as the app's own (Lucide, ISC licence), inline so they inherit currentColor.
    private static string Icon(string paths) =>
        """<svg class="icon" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" """
        + """stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">""" + paths + "</svg>";
}
