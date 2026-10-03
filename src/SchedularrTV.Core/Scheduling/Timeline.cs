using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;

namespace SchedularrTV.Core.Scheduling;

/// <summary>The blocks and anchors a build walks through, with any overlay slots laid on top.</summary>
internal sealed record Timeline(
    IReadOnlyList<BlockOccurrence> Blocks,
    IReadOnlyList<AnchorOccurrence> Anchors,
    IReadOnlyDictionary<BlockConfig, IOverlaySlot> Slots,
    IReadOnlyList<ScheduleDiagnostic> Diagnostics,
    IReadOnlyList<ArchiveGuideDay> GuideDays)
{
    public static async Task<Timeline> BuildAsync(
        SchedulerConfig config,
        ChannelConfig channel,
        IMediaLibrary library,
        TimeZoneInfo timeZone,
        DateTimeOffset start,
        DateTimeOffset end,
        IReadOnlyList<ITimelineContributor> contributors,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<BlockOccurrence> regular = TimelineBuilder.BuildBlockOccurrences(channel, timeZone, start, end);
        IReadOnlyList<AnchorOccurrence> anchors = TimelineBuilder.BuildAnchorOccurrences(channel, regular, timeZone, start, end);

        List<TimelineOverlay> overlays = [];
        var context = new TimelineContext(config, channel, library, timeZone, start, end, regular);
        foreach (ITimelineContributor contributor in contributors)
        {
            if (await contributor.OverlayAsync(context, cancellationToken).ConfigureAwait(false) is { } overlay)
            {
                overlays.Add(overlay);
            }
        }

        if (overlays.Count == 0)
        {
            return new Timeline(regular, anchors, new Dictionary<BlockConfig, IOverlaySlot>(), [], []);
        }

        // Overlay slots outrank everything regular they cover, and the anchors inside them don't air.
        List<BlockOccurrence> overlaid = [.. overlays.SelectMany(overlay => overlay.Blocks)];
        IReadOnlyList<BlockOccurrence> blocks = TimelineBuilder.Flatten([.. overlaid, .. regular], start, end);
        anchors = anchors.Where(anchor => !overlaid.Any(slot => anchor.Start >= slot.Start && anchor.Start < slot.End)).ToArray();

        Dictionary<BlockConfig, IOverlaySlot> slots = [];
        foreach ((BlockConfig block, IOverlaySlot slot) in overlays.SelectMany(overlay => overlay.Slots))
        {
            slots[block] = slot;
        }

        return new Timeline(
            blocks,
            anchors,
            slots,
            [.. overlays.SelectMany(overlay => overlay.Diagnostics)],
            [.. overlays.SelectMany(overlay => overlay.GuideDays)]);
    }
}
