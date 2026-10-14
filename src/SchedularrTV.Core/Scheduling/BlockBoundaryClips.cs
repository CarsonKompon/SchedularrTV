using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Query;

namespace SchedularrTV.Core.Scheduling;

/// <summary>Whole clips at block boundaries, separate from the bumpers repeated at every ad break.</summary>
internal static class BlockBoundaryClips
{
    public static async ValueTask<MediaItem?> TakeAsync(ScheduleBuildSession session, BlockConfig block,
        AdFolderRole role, DateTimeOffset at, TimeSpan room, CancellationToken cancellationToken)
    {
        if (room <= TimeSpan.Zero) return null;
        IReadOnlyList<AdPoolSource> sources = AdBreakCompiler.BlockSources(session.Config, session.Channel, block, role);
        if (sources.Count == 0) return null;
        QueryExpression pool = AdBreakCompiler.Pool(sources, null, at, session.Config.Settings.TemporalTags);
        MediaSequence sequence = await session.GetPoolSequenceAsync(pool, PlaybackOrder.Shuffle, null, null,
            $"block-{role}-{block.Name}", cancellationToken).ConfigureAwait(false);
        return sequence.TryTakeForGap(item => item.Duration > TimeSpan.Zero && item.Duration <= room, out MediaItem clip) ? clip : null;
    }

    public static ScheduleEntry Entry(BlockConfig block, MediaItem clip, DateTimeOffset start, AdFolderRole role) => new()
    {
        Start = start, Duration = clip.Duration, Item = clip, Kind = ScheduleEntryKind.Commercial,
        BlockName = block.Name, Source = role == AdFolderRole.BlockStart ? "block start" : "block end"
    };
}
