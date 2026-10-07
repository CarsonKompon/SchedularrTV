using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace SchedularrTV.Plugins;

/// <summary>
/// Explains, at the top of a live-page plugin's page (radio, guide, web page shows), that each entry becomes a "virtual
/// show" of empty, fixed-length episodes that tune into the live broadcast when they air. Use it as
/// <c>&lt;VirtualShowNotice /&gt;</c>.
/// </summary>
public sealed class VirtualShowNotice : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "banner virtual-show-notice");
        builder.AddAttribute(2, "role", "note");

        builder.OpenElement(3, "p");
        builder.AddAttribute(4, "style", "margin:0;");
        builder.AddContent(5, "This plugin will create a \"virtual show\" in your TV library. Instead of using real video files, it'll "
                              + "generate empty blocks of time at different lengths, like 3, 5, 30, or 60 minutes. Think of them as dummy "
                              + "episodes that tell your scheduler exactly when and how long to play this show.");
        builder.CloseElement();

        builder.OpenElement(6, "p");
        builder.AddAttribute(7, "style", "margin:8px 0 0;");
        builder.AddContent(8, "Because they work like normal show episodes, you can use them two different ways. You can either use the "
                              + "long blocks to run a dedicated 24/7 channel, or drop the short blocks into your other channels as quick "
                              + "commercial breaks and filler. When the schedule hits one of these blocks, it'll automatically tune right "
                              + "into the live broadcast.");
        builder.CloseElement();

        builder.CloseElement();
    }
}
