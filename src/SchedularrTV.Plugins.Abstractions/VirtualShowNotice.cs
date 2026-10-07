using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace SchedularrTV.Plugins;

/// <summary>
/// Explains, at the top of a live-page plugin's page (radio, guide, web page shows), that each entry becomes a "virtual
/// show" of empty, fixed-length episodes that tune into the live broadcast when they air. One line until "More info" is
/// opened. Use it as <c>&lt;VirtualShowNotice /&gt;</c>; styled by <c>.virtual-show-notice</c> in the app's stylesheet.
/// </summary>
public sealed class VirtualShowNotice : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        // A <details> opens and closes in the browser, so it works on pages that aren't interactive too.
        builder.OpenElement(0, "details");
        builder.AddAttribute(1, "class", "banner banner-info virtual-show-notice");
        builder.AddAttribute(2, "role", "note");

        builder.OpenElement(3, "summary");
        builder.OpenElement(4, "span");
        builder.AddContent(5, "This plugin will create \"virtual shows\" in your TV library.");
        builder.CloseElement();
        builder.OpenElement(6, "span");
        builder.AddAttribute(7, "class", "btn btn-sm virtual-show-more");
        builder.AddAttribute(8, "aria-hidden", "true");
        builder.CloseElement();
        builder.CloseElement();

        builder.OpenElement(10, "p");
        builder.AddContent(11, "Instead of using real video files, it'll generate empty blocks of time at different lengths, like 3, 5, 30, "
                               + "or 60 minutes. Think of them as dummy episodes that tell your scheduler exactly when and how long to "
                               + "play this show.");
        builder.CloseElement();

        builder.OpenElement(12, "p");
        builder.AddContent(13, "Because they work like normal show episodes, you can use them two different ways. You can either use the "
                               + "long blocks to run a dedicated 24/7 channel, or drop the short blocks into your other channels as quick "
                               + "commercial breaks and filler. When the schedule hits one of these blocks, it'll automatically tune right "
                               + "into the live broadcast.");
        builder.CloseElement();

        builder.CloseElement();
    }
}
