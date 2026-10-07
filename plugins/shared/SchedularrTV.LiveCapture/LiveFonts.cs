using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using SchedularrTV.Plugins.Editor;

namespace SchedularrTV.LiveCapture;

/// <summary>A font a page downloads itself (from Google Fonts, or bundled with the page), so it works without being installed.</summary>
/// <param name="Family">The CSS family name, which is also what's saved.</param>
/// <param name="GoogleFamily">The Google Fonts <c>family=</c> value; null for a face the page brings itself.</param>
public sealed record WebFont(string Family, string Label, string? GoogleFamily, string Fallback);

/// <summary>
/// Lettering for the live page channels: any installed font (the same list the canvas editors offer), or one of the web
/// fonts the pages download. Turned into a CSS font stack and, for a web font, the stylesheet to load.
/// </summary>
public static class LiveFonts
{
    /// <summary>The 14-segment display face radio.html brings with it.</summary>
    public const string Segmented = "DSEG14 Classic";

    public static readonly IReadOnlyList<WebFont> Web =
    [
        new("Inter", "Inter (modern sans)", "Inter:wght@400;600;800;900", "\"Segoe UI\", \"Helvetica Neue\", Arial, sans-serif"),
        new("Oswald", "Oswald (condensed)", "Oswald:wght@400;500;600;700", "\"Arial Narrow\", \"Liberation Sans Narrow\", sans-serif"),
        new("Merriweather", "Merriweather (serif)", "Merriweather:wght@400;700;900", "Georgia, \"Times New Roman\", serif"),
        new("Roboto Slab", "Roboto Slab (slab)", "Roboto+Slab:wght@400;600;800;900", "Rockwell, \"Courier New\", serif"),
        new("Nunito", "Nunito (rounded)", "Nunito:wght@400;700;800;900", "\"Arial Rounded MT Bold\", \"Segoe UI\", sans-serif"),
        new("JetBrains Mono", "JetBrains Mono (typewriter)", "JetBrains+Mono:wght@400;700;800", "Consolas, \"Courier New\", monospace")
    ];

    public static readonly WebFont SegmentFont = new(Segmented, "DSEG14 (segmented display)", null, "Consolas, \"Courier New\", monospace");

    /// <summary>
    /// The family the old lettering presets (Sans, Condensed, Serif, Slab, Rounded, Mono, Segment) stood for, so a
    /// saved choice carries over; null for the look's own.
    /// </summary>
    public static string? FromPreset(string? preset) => preset switch
    {
        "Sans" => "Inter",
        "Condensed" => "Oswald",
        "Serif" => "Merriweather",
        "Slab" => "Roboto Slab",
        "Rounded" => "Nunito",
        "Mono" => "JetBrains Mono",
        "Segment" => Segmented,
        _ => null
    };

    /// <summary>A family name tidied for saving: trimmed, without quotes or anything that could break out of CSS; null for none.</summary>
    public static string? Clean(string? family)
    {
        if (string.IsNullOrWhiteSpace(family))
        {
            return null;
        }

        string cleaned = string.Concat(family.Where(c => c is not ('"' or '\'' or ';' or '{' or '}' or '<' or '>' or '\\'))).Trim();
        return cleaned.Length is 0 or > 100 ? null : cleaned;
    }

    /// <summary>The CSS font-family stack for a family; null for the look's own.</summary>
    public static string? Stack(string? family)
    {
        if (Clean(family) is not { } name)
        {
            return null;
        }

        WebFont? web = Find(name);
        return $"\"{name}\", {web?.Fallback ?? "\"Segoe UI\", Arial, sans-serif"}";
    }

    /// <summary>The stylesheet a web font needs; null for an installed font or one the page brings.</summary>
    public static string? StylesheetUrl(string? family) =>
        Clean(family) is { } name && Find(name)?.GoogleFamily is { } google
            ? $"https://fonts.googleapis.com/css2?family={google}&display=swap"
            : null;

    private static WebFont? Find(string name) =>
        Web.FirstOrDefault(font => string.Equals(font.Family, name, StringComparison.OrdinalIgnoreCase))
        ?? (string.Equals(name, Segmented, StringComparison.OrdinalIgnoreCase) ? SegmentFont : null);
}

/// <summary>
/// A font picker: the look's own, the web fonts given, then every installed font, each shown in its own lettering; a
/// saved font that isn't installed any more stays selectable, marked as such. The same as the canvas editors' picker.
/// </summary>
public sealed class FontSelect : ComponentBase
{
    [Inject] private IServiceProvider Services { get; set; } = default!;

    /// <summary>The family; null or empty for the look's own.</summary>
    [Parameter] public string? Value { get; set; }

    [Parameter] public EventCallback<string?> ValueChanged { get; set; }

    [Parameter] public string DefaultLabel { get; set; } = "The look's own";

    /// <summary>Fonts the page downloads, offered before the installed ones.</summary>
    [Parameter] public IReadOnlyList<WebFont> WebFonts { get; set; } = LiveFonts.Web;

    private IReadOnlyList<string> Installed =>
        (Services.GetService(typeof(IFontCatalog)) as IFontCatalog)?.Families ?? [];

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        IReadOnlyList<string> installed = Installed;
        string value = LiveFonts.Clean(Value) ?? string.Empty;
        bool known = value.Length == 0
            || WebFonts.Any(font => Same(font.Family, value))
            || installed.Any(font => Same(font, value));

        builder.OpenElement(0, "select");
        builder.AddAttribute(1, "value", value);
        if (value.Length > 0)
        {
            builder.AddAttribute(2, "style", $"font-family: \"{value}\"");
        }

        builder.AddAttribute(3, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, OnChange));

        builder.OpenElement(4, "option");
        builder.AddAttribute(5, "value", string.Empty);
        builder.AddContent(6, DefaultLabel);
        builder.CloseElement();

        if (!known)
        {
            builder.OpenElement(7, "option");
            builder.AddAttribute(8, "value", value);
            builder.AddContent(9, $"{value} (not installed)");
            builder.CloseElement();
        }

        if (WebFonts.Count > 0)
        {
            builder.OpenElement(10, "optgroup");
            builder.AddAttribute(11, "label", "Web fonts (downloaded by the page)");
            foreach (WebFont font in WebFonts)
            {
                builder.OpenElement(12, "option");
                builder.SetKey(font.Family);
                builder.AddAttribute(13, "value", font.Family);
                builder.AddAttribute(14, "style", $"font-family: \"{font.Family}\"");
                builder.AddContent(15, font.Label);
                builder.CloseElement();
            }

            builder.CloseElement();
        }

        builder.OpenElement(16, "optgroup");
        builder.AddAttribute(17, "label", installed.Count == 0 ? "Installed fonts (couldn't be listed)" : "Installed fonts");
        foreach (string font in installed)
        {
            builder.OpenElement(18, "option");
            builder.SetKey(font);
            builder.AddAttribute(19, "value", font);
            builder.AddAttribute(20, "style", $"font-family: \"{font}\"");
            builder.AddContent(21, font);
            builder.CloseElement();
        }

        builder.CloseElement();
        builder.CloseElement();
    }

    private Task OnChange(ChangeEventArgs e) => ValueChanged.InvokeAsync(LiveFonts.Clean(e.Value?.ToString()));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
