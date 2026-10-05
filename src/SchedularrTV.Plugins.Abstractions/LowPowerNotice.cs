using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using SchedularrTV.Core.Platform;

namespace SchedularrTV.Plugins;

/// <summary>
/// A warning shown only on a Raspberry Pi or other low-power computer (<see cref="HardwareProfile.IsLowPower"/>): what
/// in this part of the app is known to be slow there, and what to choose instead. Use it from the scheduler's pages
/// and from plugins' own (<c>&lt;LowPowerNotice Title="…" Advice="…" /&gt;</c>).
/// </summary>
/// <remarks>
/// <see cref="Advice"/> entries that are null or empty are left out, so a page can pass a list of conditional ones
/// (<c>setting is heavy ? "…" : null</c>); with no title, advice or content left, nothing is shown.
/// </remarks>
public sealed class LowPowerNotice : ComponentBase
{
    /// <summary>The headline, e.g. "Web page channels are heavy on this Raspberry Pi 5".</summary>
    [Parameter] public string? Title { get; set; }

    /// <summary>One suggestion per entry; null or empty entries are skipped.</summary>
    [Parameter] public IEnumerable<string?>? Advice { get; set; }

    [Parameter] public RenderFragment? ChildContent { get; set; }

    /// <summary>A smaller hint-style note instead of a banner, for beside a single field.</summary>
    [Parameter] public bool Inline { get; set; }

    /// <summary>Shown even when the computer isn't low-power (for previews of the notice); normally false.</summary>
    [Parameter] public bool Always { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (!Always && !HardwareProfile.IsLowPower)
        {
            return;
        }

        string[] advice = [.. (Advice ?? []).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!)];
        if (string.IsNullOrWhiteSpace(Title) && advice.Length == 0 && ChildContent is null)
        {
            return;
        }

        if (Inline)
        {
            builder.OpenElement(0, "p");
            builder.AddAttribute(1, "class", "hint warn low-power-note");
            builder.AddContent(2, string.Join(" ", new[] { Title }.Concat(advice).Where(text => !string.IsNullOrWhiteSpace(text))));
            builder.AddContent(3, ChildContent);
            builder.CloseElement();
            return;
        }

        builder.OpenElement(10, "div");
        builder.AddAttribute(11, "class", "banner banner-warn low-power-notice");
        builder.AddAttribute(12, "role", "note");

        builder.OpenElement(13, "strong");
        builder.AddContent(14, string.IsNullOrWhiteSpace(Title) ? $"Running on {HardwareProfile.DeviceName}" : Title);
        builder.CloseElement();

        if (ChildContent is not null)
        {
            builder.OpenElement(20, "div");
            builder.AddAttribute(21, "style", "margin-top:4px;");
            builder.AddContent(22, ChildContent);
            builder.CloseElement();
        }

        if (advice.Length > 0)
        {
            builder.OpenElement(30, "ul");
            foreach (string item in advice)
            {
                builder.OpenElement(31, "li");
                builder.AddContent(32, item);
                builder.CloseElement();
            }

            builder.CloseElement();
        }

        builder.OpenElement(40, "p");
        builder.AddAttribute(41, "class", "meta");
        builder.AddAttribute(42, "style", "margin:6px 0 0;");
        builder.AddContent(43, $"Shown because SchedularrTV is using its low-power defaults on {HardwareProfile.Detected.Summary}. "
                               + "Change this under App settings → Hardware and video encoding.");
        builder.CloseElement();

        builder.CloseElement();
    }
}
