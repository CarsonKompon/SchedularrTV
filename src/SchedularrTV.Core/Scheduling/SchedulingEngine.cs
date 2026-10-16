using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Query;

namespace SchedularrTV.Core.Scheduling;

/// <summary>Where a break sits, which decides what may be scheduled into it.</summary>
internal enum BreakPlacement
{
    /// <summary>Between programmes; all three interstitial tiers and "up next" promos apply.</summary>
    BetweenProgrammes,

    /// <summary>Inside a programme; short shows and "up next" promos would make no sense here.</summary>
    MidRoll
}

/// <summary>
/// Builds a deterministic, gap-free linear schedule for one channel over an arbitrary window.
/// </summary>
/// <remarks>
/// Layer precedence, highest first: block anchors → channel anchors/anchor-slot blocks → block windows →
/// base rotation. Any remaining time is resolved by the two-pass gap packer.
/// </remarks>
public sealed class SchedulingEngine(
    IMediaLibrary library,
    IPlaybackStateStore stateStore,
    ILogger<SchedulingEngine>? logger = null)
{
    private static readonly TimeSpan Epsilon = TimeSpan.FromSeconds(1);
    private const int MaxIterations = 20_000;

    private readonly ILogger _logger = logger ?? NullLogger<SchedulingEngine>.Instance;

    /// <summary>The features layered on the core planner (Archive Follow, promos, generated promos).</summary>
    // Settable so a builder can switch features as plugins are turned on or off between builds.
    internal EngineModules Modules { get; set; } = EngineModules.Default;

    /// <summary>What viewers watch, for channels with viewer-aware scheduling; null leaves every channel as configured.</summary>
    public IViewerPreference? ViewerPreference { get; set; }

    public async Task<ChannelSchedule> BuildAsync(
        SchedulerConfig config,
        ChannelConfig channel,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken = default,
        Action<ScheduleBuildProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(channel);

        if (end <= start)
        {
            throw new ArgumentException("End must be after start.", nameof(end));
        }

        TimeZoneInfo timeZone = ResolveTimeZone(
            string.IsNullOrWhiteSpace(channel.TimeZone) ? config.Settings.TimeZone : channel.TimeZone);
        var session = new ScheduleBuildSession(config, library, stateStore, channel, timeZone, start)
        {
            Overlays = Modules.Overlays,
            TitleFactors = channel.ViewerAware is { Enabled: true } ? ViewerPreference?.TitleFactors(channel) : null
        };

        List<ScheduleDiagnostic> diagnostics = [];
        List<ScheduleEntry> entries = [];

        progress?.Invoke(new ScheduleBuildProgress("Preparing library and timeline", 0));
        Timeline timeline = await session.BuildTimelineAsync(start, end, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<BlockOccurrence> blocks = timeline.Blocks;
        IReadOnlyList<AnchorOccurrence> anchors = timeline.Anchors;
        diagnostics.AddRange(timeline.Diagnostics);
        diagnostics.AddRange(SwitchedOffFeatures(config, channel, start));

        var build = new BuildContext(session, blocks, anchors, entries);
        var rotationIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        // Slot alignment is measured from the channel's anchor, falling back to local midnight, so
        // breaks land on wall-clock boundaries (:00, :15, :30) regardless of when the build starts.
        DateTimeOffset slotOrigin = SlotOrigin(channel, timeZone, start);

        DateTimeOffset cursor = start;
        int anchorIndex = 0;
        int iterations = 0;
        int reportedPercent = -1;
        Dictionary<BlockOccurrence, MediaItem?> blockClosers = [];

        while (cursor < end && iterations++ < MaxIterations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int percent = Math.Clamp((int)((cursor - start) / (end - start) * 100), 0, 99);
            if (percent != reportedPercent)
            {
                progress?.Invoke(new ScheduleBuildProgress("Scheduling programmes", percent));
                reportedPercent = percent;
            }
            session.InterstitialShowsInSlot = 0;
            // Anchors whose time has passed (because the previous programme overran) are dropped.
            while (anchorIndex < anchors.Count && anchors[anchorIndex].Start < cursor - Epsilon)
            {
                diagnostics.Add(new ScheduleDiagnostic(
                    DiagnosticLevel.Warning,
                    anchors[anchorIndex].Start,
                    $"Anchor '{anchors[anchorIndex].Name}' was skipped: the preceding programme overran by {cursor - anchors[anchorIndex].Start:g}."));
                anchorIndex++;
            }

            BlockOccurrence? activeBlock = blocks.FirstOrDefault(b => cursor >= b.Start && cursor < b.End);

            // Reserve the block's closer before picking programmes or filling their breaks.
            DateTimeOffset? blockContentEnd = null;
            if (activeBlock is not null)
            {
                if (!blockClosers.TryGetValue(activeBlock, out MediaItem? closer))
                {
                    closer = activeBlock.EndsBlock
                        ? await BlockBoundaryClips.TakeAsync(session, activeBlock.Block, AdFolderRole.BlockEnd,
                            activeBlock.End.AddTicks(-1), activeBlock.End - cursor, cancellationToken).ConfigureAwait(false)
                        : null;
                    blockClosers[activeBlock] = closer;
                    DateTimeOffset openingLimit = NextBoundary(cursor, end, activeBlock, blocks, anchors, anchorIndex);
                    if (closer is not null && activeBlock.End - closer.Duration < openingLimit) openingLimit = activeBlock.End - closer.Duration;
                    if (activeBlock.StartsBlock && cursor == activeBlock.Start
                        && await BlockBoundaryClips.TakeAsync(session, activeBlock.Block, AdFolderRole.BlockStart,
                            cursor, openingLimit - cursor, cancellationToken).ConfigureAwait(false) is { } opener)
                    {
                        entries.Add(BlockBoundaryClips.Entry(activeBlock.Block, opener, cursor, AdFolderRole.BlockStart));
                        cursor += opener.Duration;
                    }
                }
                blockContentEnd = activeBlock.End - (closer?.Duration ?? TimeSpan.Zero);
                if (cursor >= blockContentEnd)
                {
                    if (closer is not null && cursor == blockContentEnd)
                    {
                        entries.Add(BlockBoundaryClips.Entry(activeBlock.Block, closer, cursor, AdFolderRole.BlockEnd));
                        cursor = activeBlock.End;
                        continue;
                    }
                    // Fixed anchors may overrun the reservation. Neither programme nor boundary clip is cut.
                    if (closer is not null)
                        diagnostics.Add(new ScheduleDiagnostic(DiagnosticLevel.Warning, cursor,
                            $"Block '{activeBlock.Block.Name}' closing clip was skipped: programming overran its reserved time."));
                    blockClosers[activeBlock] = null;
                    blockContentEnd = activeBlock.End;
                    if (cursor >= activeBlock.End) continue;
                }
            }

            // 1. A time-locked anchor fires now.
            if (anchorIndex < anchors.Count && anchors[anchorIndex].Start <= cursor + Epsilon)
            {
                AnchorOccurrence anchor = anchors[anchorIndex];
                anchorIndex++;

                DateTimeOffset before = cursor;
                cursor = await ScheduleAnchorAsync(session, anchor, cursor, end, entries, diagnostics, cancellationToken)
                    .ConfigureAwait(false);

                if (cursor > before)
                {
                    continue;
                }

                diagnostics.Add(new ScheduleDiagnostic(
                    DiagnosticLevel.Warning,
                    anchor.Start,
                    $"Anchor '{anchor.Name}' produced no playable item; falling through to the active rotation."));
            }

            // 2. Work out how far we may schedule before the next fixed point.
            DateTimeOffset boundary = NextBoundary(cursor, end, activeBlock, blocks, anchors, anchorIndex);
            if (blockContentEnd is { } contentEnd && contentEnd < boundary) boundary = contentEnd;
            TimeSpan gap = boundary - cursor;

            // 3. Main programming from the active block pool or the base rotation.
            ProgramPick? pick = await TakeProgramAsync(session, channel, activeBlock, rotationIndex, cursor, gap, cancellationToken)
                .ConfigureAwait(false);

            if (pick is not null)
            {
                MediaItem program = pick.Item;
                List<MediaItem> stack = pick.FixedStack is { } fixedStack ? [.. fixedStack] : [program];

                // Blocks count against the slot, so a stacked slot fits one fewer episode when the
                // title opens with a theme or closes with an end card. Sized on the shortest clip
                // each block could play; longer ones then spend break time rather than overrun.
                IReadOnlyList<ResolvedBumper> bumpers = await session
                    .GetBumpersAsync(pick.Selector, TitleRotation.TitleOf(program), cancellationToken)
                    .ConfigureAwait(false);

                TimeSpan bookends = bumpers.Aggregate(
                    TimeSpan.Zero,
                    (total, b) => total + b.MinimumFor(program));

                TimeSpan media = TimeSpan.FromTicks(stack.Sum(item => item.Duration.Ticks)) + bookends;
                DateTimeOffset slotEnd = cursor + media;

                // Align the next programme to a slot boundary; the leftover becomes the ad break.
                TimeSpan? padToNearest = activeBlock?.Block.PadToNearest ?? channel.PadToNearest;
                if (pick.FixedStack is not null)
                {
                    // A fixed stack (an archived slot) fills the slot to its boundary.
                    slotEnd = boundary;
                    if ((activeBlock?.Block.StackToFill ?? channel.StackToFill) == true)
                    {
                        StackToFill(pick, stack, cursor, slotEnd, media, activeBlock?.Block.MidRoll ?? channel.MidRoll);
                    }
                }
                else if (padToNearest is { Ticks: > 0 } interval)
                {
                    DateTimeOffset padded = RoundUp(cursor + media, interval, slotOrigin);

                    if ((activeBlock?.Block.StackToFill ?? channel.StackToFill) == true && padded <= boundary)
                    {
                        media = StackToFill(pick, stack, cursor, padded, media, activeBlock?.Block.MidRoll ?? channel.MidRoll);
                    }

                    DateTimeOffset naturalEnd = cursor + media;

                    // A programme much shorter than the slot would leave a huge ad block, so the
                    // break is clamped. That drops off the grid until a programme happens to land
                    // on it again, which is the right trade for a mixed-length library. Applied
                    // after stacking, so it caps whatever break the extra episodes left behind.
                    if ((activeBlock?.Block.MaxBreak ?? channel.MaxBreak) is { } cap && padded - naturalEnd > cap)
                    {
                        padded = naturalEnd + cap;
                    }

                    slotEnd = padded <= boundary ? padded : naturalEnd;
                }

                int slotEntryStart = entries.Count;
                build.AnchorIndex = anchorIndex;
                build.Premieres = pick.Premieres;
                cursor = await ScheduleStackAsync(
                        build, config, channel, activeBlock, stack, bumpers, cursor, slotEnd, cancellationToken)
                    .ConfigureAwait(false);

                if (pick.OriginOf is { } originOf)
                {
                    for (int entryIndex = slotEntryStart; entryIndex < entries.Count; entryIndex++)
                    {
                        if (entries[entryIndex].Kind == ScheduleEntryKind.Program)
                        {
                            entries[entryIndex] = entries[entryIndex] with { ProgramOrigin = originOf(entries[entryIndex].Item) };
                        }
                    }
                }

                build.Premieres = NoPremieres;

                continue;
            }

            // 4. Nothing fits: resolve the remainder as an interstitial/commercial break.
            if (ResolveInterstitials(config, channel, activeBlock?.Block).MaxShowsPerSlot is not null
                && (activeBlock?.Block.PadToNearest ?? channel.PadToNearest) is { Ticks: > 0 } gapInterval)
            {
                DateTimeOffset nextSlot = RoundUp(cursor.AddTicks(1), gapInterval, slotOrigin);
                if (nextSlot < boundary) boundary = nextSlot;
            }
            build.AnchorIndex = anchorIndex;
            await FillGapAsync(
                    build,
                    config,
                    channel,
                    activeBlock?.Block,
                    cursor,
                    boundary,
                    BreakPlacement.BetweenProgrammes,
                    cancellationToken)
                .ConfigureAwait(false);

            cursor = boundary;
        }

        if (iterations >= MaxIterations)
        {
            diagnostics.Add(new ScheduleDiagnostic(DiagnosticLevel.Error, cursor, "Schedule build aborted: iteration limit reached."));
            _logger.LogError("Channel {ChannelId} schedule build hit the iteration limit at {Cursor}.", channel.Id, cursor);
        }

        progress?.Invoke(new ScheduleBuildProgress("Saving playback state", 99));
        await session.FlushAsync(cancellationToken).ConfigureAwait(false);
        progress?.Invoke(new ScheduleBuildProgress("Complete", 100));

        return new ChannelSchedule
        {
            ChannelId = channel.Id,
            ChannelName = channel.Name,
            ChannelNumber = channel.Number,
            ChannelTvgId = channel.TvgId,
            ChannelGroup = channel.Group,
            ChannelLogo = channel.Logo,
            Start = start,
            End = end,
            Entries = entries,
            ArchiveFollowEnabled = Modules.HasArchiveFollow && channel.Follow is { Enabled: true },
            ArchiveDays = timeline.GuideDays,
            Diagnostics = diagnostics
        };
    }

    /// <summary>Settings this channel has for features whose plugins are switched off, which are ignored.</summary>
    private IEnumerable<ScheduleDiagnostic> SwitchedOffFeatures(SchedulerConfig config, ChannelConfig channel, DateTimeOffset start)
    {
        if (!Modules.HasArchiveFollow && channel.Follow is { Enabled: true })
        {
            yield return new ScheduleDiagnostic(DiagnosticLevel.Warning, start,
                "Archive Follow is set up for this channel, but the Archive Follow plugin is off, so the channel's own blocks are scheduled instead.");
        }

        if (!Modules.HasGeneratedPromos
            && (GeneratedPromoPlanner.Source(config, channel, (BlockConfig?)null) is not null
                || channel.Blocks.Any(block => block.GeneratedPromos is { Enabled: true })))
        {
            yield return new ScheduleDiagnostic(DiagnosticLevel.Info, start,
                Modules.HasPromos
                    ? "Generated promos are set up for this channel, but the Generated promos plugin is off, so only promo clips air."
                    : "Generated promos are set up for this channel, but the Promos plugin is off, so no promos air.");
        }
    }

    private static DateTimeOffset NextBoundary(
        DateTimeOffset cursor,
        DateTimeOffset end,
        BlockOccurrence? activeBlock,
        IReadOnlyList<BlockOccurrence> blocks,
        IReadOnlyList<AnchorOccurrence> anchors,
        int anchorIndex)
    {
        DateTimeOffset boundary = end;

        if (activeBlock is not null && activeBlock.End < boundary)
        {
            boundary = activeBlock.End;
        }
        else if (activeBlock is null)
        {
            DateTimeOffset? nextBlockStart = blocks
                .Where(b => b.Start > cursor)
                .Select(b => (DateTimeOffset?)b.Start)
                .Min();

            if (nextBlockStart is { } nbs && nbs < boundary)
            {
                boundary = nbs;
            }
        }

        if (anchorIndex < anchors.Count && anchors[anchorIndex].Start > cursor && anchors[anchorIndex].Start < boundary)
        {
            boundary = anchors[anchorIndex].Start;
        }

        return boundary;
    }

    private async ValueTask<DateTimeOffset> ScheduleAnchorAsync(
        ScheduleBuildSession session,
        AnchorOccurrence anchor,
        DateTimeOffset cursor,
        DateTimeOffset end,
        List<ScheduleEntry> entries,
        List<ScheduleDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        List<ContentSelector> selectors = anchor.Anchor.AllSelectors().ToList();
        if (selectors.Count == 0)
        {
            return cursor;
        }

        int count = Math.Max(1, anchor.Anchor.Count);
        bool anchorBlock = anchor.Block is { IsAnchorSlot: true };
        bool opened = false;
        for (int i = 0; i < count; i++)
        {
            ContentSelector selector = selectors[i % selectors.Count];
            MediaSequence? sequence = await session.GetSequenceAsync(selector, "shared", cancellationToken).ConfigureAwait(false);

            if (sequence is not null)
            {
                sequence.At = cursor;
            }

            if (sequence is null || sequence.IsEmpty || !sequence.TryTake(null, out MediaItem item))
            {
                diagnostics.Add(new ScheduleDiagnostic(
                    DiagnosticLevel.Warning,
                    anchor.Start,
                    $"Anchor '{anchor.Name}' selector resolved to an empty pool."));
                continue;
            }

            if (anchorBlock && !opened)
            {
                opened = true;
                if (await BlockBoundaryClips.TakeAsync(session, anchor.Block!, AdFolderRole.BlockStart, cursor,
                        end - cursor - item.Duration, cancellationToken).ConfigureAwait(false) is { } opener)
                {
                    entries.Add(BlockBoundaryClips.Entry(anchor.Block!, opener, cursor, AdFolderRole.BlockStart));
                    cursor += opener.Duration;
                }
            }

            entries.Add(new ScheduleEntry
            {
                Start = cursor,
                Duration = item.Duration,
                Kind = ScheduleEntryKind.Program,
                Item = item,
                ProgramOrigin = ProgramOrigin.Anchor,
                IsPremiere = sequence.LastTakeWasPremiere,
                Source = anchor.Name,
                BlockName = anchor.Block?.Name
            });

            cursor += item.Duration;
        }

        if (anchorBlock && opened
            && await BlockBoundaryClips.TakeAsync(session, anchor.Block!, AdFolderRole.BlockEnd, cursor,
                end - cursor, cancellationToken).ConfigureAwait(false) is { } closer)
        {
            entries.Add(BlockBoundaryClips.Entry(anchor.Block!, closer, cursor, AdFolderRole.BlockEnd));
            cursor += closer.Duration;
        }

        return cursor;
    }

    /// <summary>
    /// Adds further episodes of the same title while whole ones still fit inside the slot, and
    /// returns the total running time of the stack.
    /// </summary>
    /// <remarks>
    /// A short programme on a long grid otherwise leaves the whole remainder as adverts. Only the
    /// same title is stacked: two eleven-minute episodes back to back read as one half-hour of that
    /// show, whereas a different show appearing mid-slot would look like the grid had slipped.
    /// </remarks>
    private static TimeSpan StackToFill(ProgramPick pick, List<MediaItem> stack, DateTimeOffset start, DateTimeOffset slotEnd,
        TimeSpan media, MidRollConfig? midRoll)
    {
        string title = TitleRotation.TitleOf(pick.Item);

        // A short pool can wrap around while the slot is still being filled, which would air the
        // same episode twice in a row.
        HashSet<string> used = new(stack.Select(item => item.Id), StringComparer.Ordinal);

        while (true)
        {
            TimeSpan room = slotEnd - (start + media);
            pick.Sequence.At = start + media;

            if (!pick.Sequence.TryTakeSameTitle(title,
                    candidate => candidate.Duration > TimeSpan.Zero
                        && candidate.Duration <= room
                        && !used.Contains(candidate.Id)
                        && string.Equals(TitleRotation.TitleOf(candidate), title, StringComparison.OrdinalIgnoreCase)
                        && candidate.Duration + StackBreakReserve([.. stack, candidate], midRoll) <= room,
                    out MediaItem extra))
            {
                return media;
            }

            stack.Add(extra);
            used.Add(extra.Id);
            if (pick.Sequence.LastTakeWasPremiere) pick.Premieres.Add(extra.Id);
            media += extra.Duration;
        }
    }

    /// <summary>
    /// The break time a stack keeps room for: a full-length break between each of its segments, and at least the
    /// shortest break at its end (which takes whatever's left of the slot anyway). Reserving a full break at the end
    /// too turned away a second eleven-minute episode from a half hour, which then aired as one episode and eighteen
    /// minutes of adverts.
    /// </summary>
    private static TimeSpan StackBreakReserve(IReadOnlyList<MediaItem> stack, MidRollConfig? midRoll)
    {
        if (midRoll is null || midRoll.Mode == MidRollMode.None || midRoll.MaxBreakLength < midRoll.MinBreak
            || midRoll.MaxBreakLength <= TimeSpan.Zero) return TimeSpan.Zero;
        int segments = MidRollPlanner.SplitStack(stack, midRoll, TimeSpan.MaxValue).Count;
        return TimeSpan.FromTicks(midRoll.MaxBreakLength.Ticks * Math.Max(0, segments - 1)) + midRoll.MinBreak;
    }

    /// <summary>Lays a slot's programmes out, sharing the leftover time between them as breaks.</summary>
    private async ValueTask<DateTimeOffset> ScheduleStackAsync(
        BuildContext build,
        SchedulerConfig config,
        ChannelConfig channel,
        BlockOccurrence? activeBlock,
        IReadOnlyList<MediaItem> stack,
        IReadOnlyList<ResolvedBumper> bumpers,
        DateTimeOffset start,
        DateTimeOffset slotEnd,
        CancellationToken cancellationToken)
    {
        List<ScheduleEntry> entries = build.Entries;
        DateTimeOffset cursor = start;
        string seed = $"{config.Settings.Seed}|{channel.Id}|{start:O}";

        // Outros are reserved before the intros play so a long opening cannot eat the end card.
        List<MediaItem> outros = ChooseClips(
            bumpers.Where(b => b.Block.Position == BumperPosition.Outro),
            stack[^1],
            slotEnd - (start + TimeSpan.FromTicks(stack.Sum(i => i.Duration.Ticks))),
            seed);

        TimeSpan reserved = TimeSpan.FromTicks(outros.Sum(o => o.Duration.Ticks));

        foreach (MediaItem clip in ChooseClips(
                     bumpers.Where(b => b.Block.Position == BumperPosition.Intro),
                     stack[0],
                     slotEnd - cursor - reserved,
                     seed))
        {
            entries.Add(new ScheduleEntry
            {
                Start = cursor,
                Duration = clip.Duration,
                Kind = ScheduleEntryKind.Program,
                Item = clip,
                Source = activeBlock?.Block.Name ?? "intro",
                BlockName = activeBlock?.Block.Name,
                IsSlotIntro = true
            });

            cursor += clip.Duration;
        }

        // Handed to the last programme rather than emitted here: the end cards have to run straight
        // after the episode, with the slot's break after them, not wedged between the two.
        return await ScheduleProgramsAsync(
                build, config, channel, activeBlock, stack, outros, cursor, slotEnd, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Walks the blocks in order, giving each the room left after the ones before it, so an earlier
    /// block taking its longest clip is what shortens the choice for a later one.
    /// </summary>
    private static List<MediaItem> ChooseClips(
        IEnumerable<ResolvedBumper> bumpers,
        MediaItem episode,
        TimeSpan available,
        string seed)
    {
        List<MediaItem> chosen = [];

        foreach (ResolvedBumper bumper in bumpers)
        {
            if (available <= TimeSpan.Zero)
            {
                break;
            }

            // Whatever the later blocks need at minimum is not this block's to spend.
            if (bumper.Choose(episode, available, seed) is { } clip)
            {
                chosen.Add(clip);
                available -= clip.Duration;
            }
        }

        return chosen;
    }

    private async ValueTask<DateTimeOffset> ScheduleProgramsAsync(
        BuildContext build,
        SchedulerConfig config,
        ChannelConfig channel,
        BlockOccurrence? activeBlock,
        IReadOnlyList<MediaItem> stack,
        IReadOnlyList<MediaItem> outros,
        DateTimeOffset cursor,
        DateTimeOffset slotEnd,
        CancellationToken cancellationToken)
    {
        if (stack.Count == 1 || (activeBlock?.Block.MidRoll ?? channel.MidRoll) is { Mode: not MidRollMode.None })
        {
            return await ScheduleProgramAsync(
                    build, config, channel, activeBlock, stack, cursor, slotEnd, cancellationToken,
                    outros: outros)
                .ConfigureAwait(false);
        }

        long media = stack.Sum(i => i.Duration.Ticks);

        // The end cards are scheduled after the last episode, so their time cannot also be handed
        // out as break time or the slot runs out before they can play.
        long reserved = outros.Sum(o => o.Duration.Ticks);
        long leftover = Math.Max((slotEnd - cursor).Ticks - media - reserved, 0);
        long share = leftover / stack.Count;

        for (int i = 0; i < stack.Count; i++)
        {
            bool last = i == stack.Count - 1;

            // The last episode absorbs the rounding so the stack still lands exactly on the slot end.
            DateTimeOffset subEnd = last
                ? slotEnd
                : cursor + stack[i].Duration + TimeSpan.FromTicks(share);

            cursor = await ScheduleProgramAsync(
                    build, config, channel, activeBlock, [stack[i]], cursor, subEnd, cancellationToken,
                    continuesSlot: i > 0, endsSlot: last, outros: last ? outros : null)
                .ConfigureAwait(false);
        }

        return cursor;
    }

    private static async ValueTask<ProgramPick?> TakeProgramAsync(
        ScheduleBuildSession session,
        ChannelConfig channel,
        BlockOccurrence? activeBlock,
        Dictionary<string, int> rotationIndex,
        DateTimeOffset at,
        TimeSpan gap,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ContentSelector> selectors = activeBlock is not null && activeBlock.Block.Shows.Count > 0
            ? activeBlock.Block.Shows
            : channel.BaseRotation is not null ? [channel.BaseRotation] : [];

        // An overlay slot (Archive Follow) picks first; failing that its own fallback selectors rotate.
        if (activeBlock is not null && session.OverlaySlots.TryGetValue(activeBlock.Block, out IOverlaySlot? overlay))
        {
            OverlayPick overlaid = await overlay.PickAsync(session, channel, gap, cancellationToken).ConfigureAwait(false);
            if (overlaid.Pick is { } overlayPick)
            {
                return overlayPick;
            }
            selectors = overlaid.Fallback;
        }

        if (selectors.Count == 0)
        {
            return null;
        }

        string rotationKey = activeBlock?.Block.Name ?? "__base__";
        rotationIndex.TryGetValue(rotationKey, out int offset);

        if (selectors.Count > 1 && session.TitleFactors is { Count: > 0 })
        {
            ProgramPick? weighted = await TakeWeightedAsync(session, selectors, rotationKey, offset, at, gap, cancellationToken).ConfigureAwait(false);
            if (weighted is not null)
            {
                return weighted;
            }
        }

        // Round-robin across the pool's selectors so multi-show blocks interleave rather than
        // draining the first show completely.
        for (int i = 0; i < selectors.Count; i++)
        {
            ContentSelector selector = selectors[(offset + i) % selectors.Count];
            MediaSequence? sequence = await session.GetSequenceAsync(selector, "shared", cancellationToken).ConfigureAwait(false);

            if (sequence is null || sequence.IsEmpty)
            {
                continue;
            }

            sequence.At = at;
            if (sequence.TryTake(item => item.Duration > TimeSpan.Zero && item.Duration <= gap, out MediaItem chosen))
            {
                rotationIndex[rotationKey] = (offset + i + 1) % selectors.Count;
                return Picked(chosen, sequence, selector);
            }
        }

        return null;
    }

    /// <summary>
    /// Viewer-aware turns among a block's shows: a smooth weighted round-robin, each show weighted by its viewer
    /// factor (the mean over its titles), so a favourite comes round more often but still spread out, and a skipped
    /// show still gets the odd turn. Null when no show here moves, or none has anything that fits, so the ordinary
    /// round-robin decides.
    /// </summary>
    private static async ValueTask<ProgramPick?> TakeWeightedAsync(
        ScheduleBuildSession session,
        IReadOnlyList<ContentSelector> selectors,
        string rotationKey,
        int offset,
        DateTimeOffset at,
        TimeSpan gap,
        CancellationToken cancellationToken)
    {
        double[] weights = new double[selectors.Count];
        bool moves = false;
        for (int i = 0; i < selectors.Count; i++)
        {
            IReadOnlySet<string> titles = await session.TitlesOfAsync(selectors[i], cancellationToken).ConfigureAwait(false);
            weights[i] = titles.Count == 0 ? 1 : titles.Average(title => ViewerWeights.Of(session.TitleFactors, title));
            moves |= Math.Abs(weights[i] - 1) > ViewerWeights.Neutral;
        }

        if (!moves)
        {
            return null;
        }

        if (!session.SelectorCredits.TryGetValue(rotationKey, out double[]? credits) || credits.Length != selectors.Count)
        {
            credits = new double[selectors.Count];
            session.SelectorCredits[rotationKey] = credits;
        }

        double total = weights.Sum();
        double[] earned = [.. credits.Select((credit, i) => credit + weights[i])];

        // Highest credit first; ties go in the ordinary round-robin order so the result stays deterministic.
        IEnumerable<int> order = Enumerable.Range(0, selectors.Count)
            .OrderByDescending(i => earned[i])
            .ThenBy(i => (i - offset + selectors.Count) % selectors.Count);

        foreach (int i in order)
        {
            ContentSelector selector = selectors[i];
            MediaSequence? sequence = await session.GetSequenceAsync(selector, "shared", cancellationToken).ConfigureAwait(false);
            if (sequence is not null)
            {
                sequence.At = at;
            }

            if (sequence is null || sequence.IsEmpty
                || !sequence.TryTake(item => item.Duration > TimeSpan.Zero && item.Duration <= gap, out MediaItem chosen))
            {
                continue;
            }

            Array.Copy(earned, credits, credits.Length);
            credits[i] -= total;
            return Picked(chosen, sequence, selector);
        }

        return null;
    }

    private static readonly HashSet<string> NoPremieres = [];

    /// <summary>A pick that remembers whether its programme is a new episode (a drop's first airing).</summary>
    private static ProgramPick Picked(MediaItem chosen, MediaSequence sequence, ContentSelector selector)
    {
        ProgramPick pick = new(chosen, sequence, selector);
        if (sequence.LastTakeWasPremiere)
        {
            pick.Premieres.Add(chosen.Id);
        }

        return pick;
    }

    /// <summary>Whether a break's <paramref name="roll"/>th short programme is used, at <paramref name="chance"/> percent.</summary>
    internal static bool UsesShort(string breakSeed, int roll, int chance) =>
        chance >= 100 || (chance > 0
            && DeterministicRandom.FromSeedText(breakSeed, "interstitial-chance", roll.ToString(System.Globalization.CultureInfo.InvariantCulture)).Next(100) < chance);

    /// <summary>
    /// Fills a break: break contributors' openers, interstitial shows, their items after those, commercials
    /// and filler with their mix-ins dropped among them, padding, then their closing items. Finalizers then
    /// run over the finished break.
    /// </summary>
    private async ValueTask FillGapAsync(
        BuildContext build,
        SchedulerConfig config,
        ChannelConfig channel,
        BlockConfig? block,
        DateTimeOffset from,
        DateTimeOffset to,
        BreakPlacement placement,
        CancellationToken cancellationToken,
        bool endsSlot = false,
        bool continuesSlot = false,
        SlotBreak? slot = null)
    {
        ScheduleBuildSession session = build.Session;
        List<ScheduleEntry> entries = build.Entries;
        DateTimeOffset cursor = from;
        int breakStart = entries.Count;
        InterstitialsConfig interstitials = ResolveInterstitials(config, channel, block);
        CommercialsConfig commercials = interstitials.Commercials!;
        FillerTierConfig fillerTier = interstitials.Filler!;
        TimeSpan tolerance = commercials.Tolerance ?? TimeSpan.FromMilliseconds(500);
        string breakSeed = $"{config.Settings.Seed}|{channel.Id}|{from:O}";

        var context = new BreakContext
        {
            Build = build,
            Config = config,
            Channel = channel,
            Block = block,
            From = from,
            To = to,
            Placement = placement,
            EndsSlot = endsSlot,
            ContinuesSlot = continuesSlot,
            Slot = slot,
            Interstitials = interstitials,
            Tolerance = tolerance,
            BreakSeed = breakSeed,
            PackAsync = (packStart, packEnd, into, token) =>
                PackBreakAsync(session, config, block, commercials, fillerTier, packStart, packEnd, into, token)
        };

        List<IBreakPlan> plans = [];
        foreach (IBreakContributor contributor in Modules.Breaks)
        {
            if (await contributor.PlanAsync(context, cancellationToken).ConfigureAwait(false) is { } plan)
            {
                plans.Add(plan);
            }
        }

        foreach (IBreakPlan plan in plans)
        {
            if (plan.Opener is { } opener)
            {
                entries.Add(opener with { Start = cursor });
                cursor += opener.Duration;
            }
        }

        // Closing items and held mix-ins keep their room free while everything else is packed.
        List<ScheduleEntry> tail = [.. plans.SelectMany(plan => plan.Tail)];
        List<(ScheduleEntry Entry, string Salt)> held = [.. plans
            .Select(plan => plan.Held)
            .Where(item => item is not null)
            .Select(item => item!.Value)];

        DateTimeOffset packTo = to - TimeSpan.FromTicks(tail.Sum(item => item.Duration.Ticks));
        DateTimeOffset fillTo = packTo - TimeSpan.FromTicks(held.Sum(item => item.Entry.Duration.Ticks));

        // Tier 1: macro-fill with interstitial shows/shorts. A short programme dropped into the
        // middle of another one makes no sense, so this only runs between programmes.
        ContentSelector? interstitialSelector = placement == BreakPlacement.BetweenProgrammes
            ? interstitials.Shows
            : null;

        if (interstitialSelector is not null
            && session.InterstitialShowsInSlot < (interstitials.MaxShowsPerSlot ?? int.MaxValue))
        {
            MediaSequence? shorts = await session
                .GetSequenceAsync(interstitialSelector, "interstitial", cancellationToken)
                .ConfigureAwait(false);

            int chance = Math.Clamp(interstitials.ShowChance ?? InterstitialsConfig.DefaultShowChance, 0, 100);
            int rolls = 0;
            while (shorts is { IsEmpty: false } && fillTo - cursor > tolerance
                && session.InterstitialShowsInSlot < (interstitials.MaxShowsPerSlot ?? int.MaxValue))
            {
                // Each short that could go in is a roll of the dice, seeded by the break, so rebuilds agree. A miss
                // hands the rest of the break to the adverts.
                if (!UsesShort(breakSeed, rolls++, chance))
                {
                    break;
                }

                TimeSpan remaining = fillTo - cursor;
                if (!shorts.TryTakeForGap(i => i.Duration > TimeSpan.Zero && i.Duration <= remaining, out MediaItem shortItem))
                {
                    break;
                }

                entries.Add(new ScheduleEntry
                {
                    Start = cursor,
                    Duration = shortItem.Duration,
                    Kind = ScheduleEntryKind.Interstitial,
                    Item = shortItem,
                    Source = interstitialSelector.Collection ?? interstitialSelector.Query ?? "interstitial",
                    BlockName = block?.Name
                });

                cursor += shortItem.Duration;
                session.InterstitialShowsInSlot++;
            }
        }

        foreach (IBreakPlan plan in plans)
        {
            foreach (ScheduleEntry item in await plan.AfterInterstitialsAsync(cursor, fillTo, cancellationToken).ConfigureAwait(false))
            {
                entries.Add(item with { Start = cursor });
                cursor += item.Duration;
            }
        }

        // Mix-ins are dropped in after packing rather than before, so the knapsack still sees one
        // contiguous break and packs it as tightly as it would have done without them.
        List<(ScheduleEntry Entry, string Salt)> mixIns = [];
        DateTimeOffset adsTo = fillTo;
        foreach (IBreakPlan plan in plans)
        {
            if (await plan.ReserveMixInAsync(cursor, adsTo, cancellationToken).ConfigureAwait(false) is { } mixIn)
            {
                mixIns.Add(mixIn);
                adsTo -= mixIn.Entry.Duration;
            }
        }

        // Tiers 2 and 3: strict-fit commercials, then trimmable filler for whatever is left.
        int firstAd = entries.Count;

        if (adsTo - cursor > tolerance)
        {
            cursor = await PackBreakAsync(
                    session, config, block, commercials, fillerTier, cursor, adsTo, entries, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach ((ScheduleEntry entry, string salt) in mixIns.Concat(held))
        {
            cursor = MixIn(entries, firstAd, cursor, breakSeed, salt, entry);
        }

        TimeSpan residual = packTo - cursor;
        if (residual > TimeSpan.Zero)
        {
            // Below tolerance the gap is imperceptible, so stretch the previous slot rather than
            // emitting a black playout item for a fraction of a second.
            if (residual <= tolerance && entries.Count > 0)
            {
                entries[^1] = entries[^1] with { Duration = entries[^1].Duration + residual };
            }
            else
            {
                entries.Add(new ScheduleEntry
                {
                    Start = cursor,
                    Duration = residual,
                    Kind = ScheduleEntryKind.Padding,
                    Source = "padding",
                    BlockName = block?.Name
                });
            }

            cursor = packTo;
        }

        foreach (ScheduleEntry item in tail)
        {
            entries.Add(item with { Start = cursor });
            cursor += item.Duration;
        }

        foreach (IBreakFinalizer finalizer in Modules.Finalizers)
        {
            await finalizer.FinalizeAsync(context, breakStart, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Drops a promo in at a random point among the ads just packed, so it reads as one of them
    /// rather than as a bookend. Entries after the insertion point shift by its duration.
    /// </summary>
    private static DateTimeOffset MixIn(
        List<ScheduleEntry> entries,
        int firstAd,
        DateTimeOffset cursor,
        string seed,
        string salt,
        ScheduleEntry promo)
    {
        int slots = entries.Count - firstAd + 1;
        int index = firstAd + DeterministicRandom.FromSeedText(seed, salt).Next(slots);

        entries.Insert(index, promo with { Start = index < entries.Count ? entries[index].Start : cursor });

        for (int i = index + 1; i < entries.Count; i++)
        {
            entries[i] = entries[i] with { Start = entries[i].Start + promo.Duration };
        }

        return cursor + promo.Duration;
    }

    private static async ValueTask<DateTimeOffset> PackBreakAsync(
        ScheduleBuildSession session,
        SchedulerConfig config,
        BlockConfig? block,
        CommercialsConfig commercials,
        FillerTierConfig fillerTier,
        DateTimeOffset cursor,
        DateTimeOffset packTo,
        List<ScheduleEntry> entries,
        CancellationToken cancellationToken)
    {
        MediaSequence commercialSequence = await session
            .GetPoolSequenceAsync(
                InterstitialQueryBuilder.BuildCommercials(commercials, cursor, config.Settings.TemporalTags),
                commercials.Order ?? PlaybackOrder.Shuffle,
                commercials.MinItemDuration,
                commercials.MaxItemDuration,
                "commercials",
                cancellationToken)
            .ConfigureAwait(false);

        List<PackedItem> packedItems = [];
        Dictionary<string, int> repeats = new(StringComparer.Ordinal);
        int maxRepeats = Math.Max(1, commercials.MaxRepeatsPerBreak ?? 1);
        TimeSpan available = packTo - cursor;
        while (available > TimeSpan.Zero)
        {
            IReadOnlyList<MediaItem> candidates = commercialSequence.PreviewForGap(256,
                item => item.Duration > TimeSpan.Zero && item.Duration <= available
                    && repeats.GetValueOrDefault(item.Id) < maxRepeats);
            PackResult pass = KnapsackPacker.Pack(candidates, available,
                commercials.Granularity ?? TimeSpan.FromMilliseconds(250), 1);
            if (pass.Items.Count == 0) break;
            foreach (PackedItem item in pass.Items)
            {
                commercialSequence.Consume(item.Item.Id);
                repeats[item.Item.Id] = repeats.GetValueOrDefault(item.Item.Id) + 1;
                packedItems.Add(item);
            }
            available -= pass.Filled;
        }

        MediaSequence? fillerSequence = null;
        List<MediaItem> fillerCandidates = [];

        if (fillerTier.IsEnabled)
        {
            QueryExpression fillerQuery = InterstitialQueryBuilder.BuildFiller(fillerTier, cursor, config.Settings.TemporalTags);
            if (fillerQuery is not NoneExpression)
            {
                fillerSequence = await session
                    .GetPoolSequenceAsync(
                        fillerQuery,
                        fillerTier.Order ?? PlaybackOrder.Shuffle,
                        fillerTier.MinItemDuration,
                        fillerTier.MaxItemDuration,
                        "filler",
                        cancellationToken)
                    .ConfigureAwait(false);

                fillerCandidates.AddRange(fillerSequence.PreviewForGap(128,
                    item => item.Duration > TimeSpan.Zero && (fillerTier.IsTrimmable || item.Duration <= available)));
            }
        }

        // strict_fit: false gives the trim pass permission to fall back to the commercial pool.
        if (!commercials.IsStrictFit)
        {
            fillerCandidates.AddRange(commercialSequence.PreviewForGap(256,
                item => item.Duration > TimeSpan.Zero && repeats.GetValueOrDefault(item.Id) < maxRepeats));
        }

        PackResult packed = KnapsackPacker.Pack(
            [],
            available,
            commercials.Granularity ?? TimeSpan.FromMilliseconds(250),
            commercials.MaxRepeatsPerBreak ?? 1,
            fillerCandidates,
            new FillerPackOptions
            {
                Trimmable = fillerTier.IsTrimmable,
                StopBeforeEnd = fillerTier.StopBeforeEnd ?? false,
                MinTrimmedDuration = fillerTier.MinTrimmedDuration ?? TimeSpan.Zero,
                Loop = fillerTier.Loop ?? false
            });

        packedItems.AddRange(packed.Items);
        foreach (PackedItem item in packedItems)
        {
            // The two pools can overlap, so mark the item played in whichever sequence holds it.
            if (item.IsFiller) commercialSequence.Consume(item.Item.Id);
            fillerSequence?.Consume(item.Item.Id);

            entries.Add(new ScheduleEntry
            {
                Start = cursor,
                Duration = item.Duration,
                Kind = item.IsFiller ? ScheduleEntryKind.Filler : ScheduleEntryKind.Commercial,
                Item = item.Item,
                IsTrimmed = item.Trimmed,
                StopBeforeEnd = item.StopBeforeEnd,
                SourceDuration = item.Trimmed ? item.Item.Duration : null,
                Source = item.IsFiller ? "filler" : "commercial",
                BlockName = block?.Name
            });

            cursor += item.Duration;
        }

        return cursor;
    }

    /// <summary>
    /// Lays a programme onto the timeline between <paramref name="start"/> and
    /// <paramref name="slotEnd"/>, splitting it at chapter markers when mid-rolls are enabled so the
    /// break time is spread through the programme rather than dumped at the end.
    /// </summary>
    private async ValueTask<DateTimeOffset> ScheduleProgramAsync(
        BuildContext build,
        SchedulerConfig config,
        ChannelConfig channel,
        BlockOccurrence? activeBlock,
        IReadOnlyList<MediaItem> programmes,
        DateTimeOffset start,
        DateTimeOffset slotEnd,
        CancellationToken cancellationToken,
        bool continuesSlot = false,
        bool endsSlot = true,
        IReadOnlyList<MediaItem>? outros = null)
    {
        List<ScheduleEntry> entries = build.Entries;
        BlockConfig? block = activeBlock?.Block;
        string source = block?.Name ?? "base rotation";

        TimeSpan programmeDuration = TimeSpan.FromTicks(programmes.Sum(programme => programme.Duration.Ticks));
        TimeSpan budget = slotEnd - (start + programmeDuration + TimeSpan.FromTicks(outros?.Sum(o => o.Duration.Ticks) ?? 0));
        if (budget < TimeSpan.Zero)
        {
            budget = TimeSpan.Zero;
        }

        MidRollConfig? midRoll = block?.MidRoll ?? channel.MidRoll;
        IReadOnlyList<MediaSegment> segments = MidRollPlanner.SplitStack(programmes, midRoll, budget);

        // Mid-rolls are capped so a generous slot does not produce five-minute interruptions; the
        // leftover lands in the break after the programme, which is where longer filler belongs.
        TimeSpan perMidRoll = TimeSpan.Zero;
        if (segments.Count > 1)
        {
            TimeSpan cap = midRoll?.MaxBreakLength ?? TimeSpan.FromMinutes(4);
            perMidRoll = TimeSpan.FromTicks(Math.Min(budget.Ticks / segments.Count, cap.Ticks));

            if (perMidRoll < (midRoll?.MinBreak ?? TimeSpan.FromSeconds(30)))
            {
                segments = [new MediaSegment(TimeSpan.Zero, programmeDuration)];
                perMidRoll = TimeSpan.Zero;
            }
        }

        // Break contributors see where each break sits in the slot (Up Next can air in any of them).
        int midRolls = perMidRoll > TimeSpan.Zero ? segments.Count - 1 : 0;

        DateTimeOffset cursor = start;
        TimeSpan mediaPlayed = TimeSpan.Zero;

        for (int i = 0; i < segments.Count; i++)
        {
            MediaSegment segment = segments[i];
            bool isLast = i == segments.Count - 1;

            TimeSpan programmeStart = TimeSpan.Zero;
            for (int programmeIndex = 0; programmeIndex < programmes.Count; programmeIndex++)
            {
                MediaItem programme = programmes[programmeIndex];
                TimeSpan programmeEnd = programmeStart + programme.Duration;
                TimeSpan overlapStart = segment.InPoint > programmeStart ? segment.InPoint : programmeStart;
                TimeSpan overlapEnd = segment.OutPoint < programmeEnd ? segment.OutPoint : programmeEnd;
                if (overlapEnd > overlapStart)
                {
                    int partCount = segments.Count(part => part.InPoint < programmeEnd && part.OutPoint > programmeStart);
                    int partNumber = segments.Take(i + 1).Count(part => part.InPoint < programmeEnd && part.OutPoint > programmeStart);
                    entries.Add(new ScheduleEntry
                    {
                        Start = cursor,
                        Duration = overlapEnd - overlapStart,
                        Kind = ScheduleEntryKind.Program,
                        Item = programme,
                        InPoint = overlapStart - programmeStart,
                        IsTrimmed = partCount > 1,
                        SourceDuration = partCount > 1 ? programme.Duration : null,
                        SegmentNumber = partCount > 1 ? partNumber : 0,
                        SegmentCount = partCount > 1 ? partCount : 0,
                        Source = source,
                        BlockName = block?.Name,
                        ContinuesSlot = continuesSlot || programmeIndex > 0,
                        // Stamped here, not afterwards, so the breaks inside this slot already know.
                        IsPremiere = build.Premieres.Contains(programme.Id)
                    });
                    cursor += overlapEnd - overlapStart;
                }
                programmeStart = programmeEnd;
            }
            mediaPlayed += segment.Duration;

            // Straight after the closing episode, so the break belongs to the slot that follows
            // rather than splitting the show from its own end cards.
            if (isLast && outros is not null)
            {
                foreach (MediaItem clip in outros)
                {
                    if (cursor + clip.Duration > slotEnd)
                    {
                        break;
                    }

                    entries.Add(new ScheduleEntry
                    {
                        Start = cursor,
                        Duration = clip.Duration,
                        Kind = ScheduleEntryKind.Program,
                        Item = clip,
                        Source = block?.Name ?? "outro",
                        BlockName = block?.Name,
                        IsSlotOutro = true
                    });

                    cursor += clip.Duration;
                }
            }

            // Breaks are placed on absolute boundaries so rounding never drifts off the slot end.
            DateTimeOffset breakEnd = isLast
                ? slotEnd
                : start + mediaPlayed + TimeSpan.FromTicks(perMidRoll.Ticks * (i + 1));

            if (breakEnd > cursor)
            {
                await FillGapAsync(
                        build, config, channel, block, cursor, breakEnd,
                        isLast ? BreakPlacement.BetweenProgrammes : BreakPlacement.MidRoll,
                        cancellationToken,
                        endsSlot: isLast && endsSlot,
                        continuesSlot: !(isLast && endsSlot),
                        slot: new SlotBreak(start, slotEnd, endsSlot, midRolls, i, isLast))
                    .ConfigureAwait(false);

                cursor = breakEnd;
            }
        }

        return cursor;
    }

    /// <summary>
    /// The breaks a channel (or one of its blocks) fills, with every inherited setting resolved as a build sees it.
    /// For the editor, e.g. to show which folders they're drawn from.
    /// </summary>
    public static InterstitialsConfig ResolvedBreaks(SchedulerConfig config, ChannelConfig channel, BlockConfig? block = null) =>
        ResolveInterstitials(config, channel, block);

    /// <summary>The promo settings a channel (or block) uses, and the brand folders they're drawn from, as a build sees them.</summary>
    public static (PromoConfig Promos, List<string>? BrandTags) ResolvedPromos(SchedulerConfig config, ChannelConfig channel, BlockConfig? block = null)
    {
        PromoConfig promos = ResolvePromoConfig(config, channel, block);
        return (promos, promos.FromAdBreaks ? promos.BaseTags : ResolvePromoBaseTags(block, channel, promos, ResolveInterstitials(config, channel, block).Commercials!));
    }

    /// <summary>
    /// The promo settings of a block: built from the ad breaks in use when there are any, else the block's, the
    /// channel's or the global tag-based ones.
    /// </summary>
    internal static PromoConfig ResolvePromoConfig(SchedulerConfig config, ChannelConfig channel, BlockConfig? block) =>
        AdBreakCompiler.InUse(config, channel, block) is { } adBreaks
            ? AdBreakCompiler.Promos(adBreaks, config.Settings.TemporalTags)
            : block?.Promos ?? channel.Promos ?? config.Settings.Promos;

    /// <summary>Which brand folders promos come from; see <see cref="PromoBreakContributor.ResolveBaseTags"/>.</summary>
    internal static List<string>? ResolvePromoBaseTags(
        BlockConfig? block,
        ChannelConfig channel,
        PromoConfig promos,
        CommercialsConfig commercials) =>
        PromoBreakContributor.ResolveBaseTags(block, channel, promos, commercials);

    /// <summary>
    /// Collapses block → channel → settings into one interstitial definition. The YAML shorthands
    /// (<c>interstitial_shows</c>, <c>commercial_base_tags</c>, <c>auto_temporal_tags</c>) win at
    /// whichever level they are declared.
    /// </summary>
    internal static InterstitialsConfig ResolveInterstitials(SchedulerConfig config, ChannelConfig channel, BlockConfig? block)
    {
        return AdBreakCompiler.InUse(config, channel, block) is { } adBreaks
            ? AdBreakCompiler.Interstitials(config, channel, block, adBreaks)
            : ResolveTagBasedInterstitials(config, channel, block);
    }

    /// <summary>
    /// The older, tag-based break settings collapsed block → channel → settings, ignoring any ad breaks: what a
    /// schedule from before ad breaks did, and what its settings are moved into ad breaks as.
    /// </summary>
    internal static InterstitialsConfig ResolveTagBasedInterstitials(SchedulerConfig config, ChannelConfig channel, BlockConfig? block)
    {
        InterstitialsConfig resolved = (block?.Interstitials ?? new InterstitialsConfig())
            .Merge(channel.Interstitials)
            .Merge(config.Settings.Interstitials);

        bool? autoTags = block?.AutoTemporalTags ?? channel.AutoTemporalTags;

        CommercialsConfig commercials = resolved.Commercials!.Merge(null);
        commercials.BaseTags = block?.CommercialBaseTags ?? channel.CommercialBaseTags ?? commercials.BaseTags;
        commercials.AutoTemporalTags = autoTags ?? commercials.AutoTemporalTags;

        FillerTierConfig filler = resolved.Filler!.Merge(null);
        filler.AutoTemporalTags ??= autoTags;

        // Only the pool is inherited, never the brands: bumpers are usually filed under specific
        // brands, so narrowing them to the channel's advert brands can empty the tier entirely.
        if (string.IsNullOrWhiteSpace(filler.Query))
        {
            filler.BaseQuery ??= commercials.BaseQuery;
        }

        return new InterstitialsConfig
        {
            Shows = block?.InterstitialShows ?? channel.InterstitialShows ?? resolved.Shows,
            MaxShowsPerSlot = resolved.MaxShowsPerSlot,
            ShowChance = resolved.ShowChance,
            Commercials = commercials,
            Filler = filler
        };
    }

    public static TimeZoneInfo ResolveTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>
    /// The most recent grid boundary at or before <paramref name="moment"/>. Building from here rather
    /// than from "now" keeps a rebuild on the grid instead of starting a programme mid-slot.
    /// </summary>
    public static DateTimeOffset AlignToGrid(SchedulerConfig config, ChannelConfig channel, DateTimeOffset moment)
    {
        if (channel.PadToNearest is not { Ticks: > 0 } interval)
        {
            return moment;
        }

        TimeZoneInfo timeZone = ResolveTimeZone(
            string.IsNullOrWhiteSpace(channel.TimeZone) ? config.Settings.TimeZone : channel.TimeZone);

        DateTimeOffset origin = SlotOrigin(channel, timeZone, moment);
        long elapsed = (moment - origin).Ticks;

        if (elapsed <= 0)
        {
            return moment;
        }

        return origin + TimeSpan.FromTicks(elapsed - (elapsed % interval.Ticks));
    }

    private static DateTimeOffset SlotOrigin(ChannelConfig channel, TimeZoneInfo timeZone, DateTimeOffset moment) =>
        channel.ScheduleStart
        ?? new DateTimeOffset(TimeZoneInfo.ConvertTime(moment, timeZone).Date, timeZone.GetUtcOffset(moment));

    /// <summary>The first grid boundary strictly after <paramref name="moment"/>.</summary>
    public static DateTimeOffset SlotBoundaryAfter(SchedulerConfig config, ChannelConfig channel, DateTimeOffset moment)
    {
        DateTimeOffset aligned = AlignToGrid(config, channel, moment);

        return channel.PadToNearest is { Ticks: > 0 } interval && aligned <= moment
            ? aligned + interval
            : aligned;
    }

    /// <summary>Rounds up to the next slot boundary measured from <paramref name="origin"/>.</summary>
    private static DateTimeOffset RoundUp(DateTimeOffset value, TimeSpan interval, DateTimeOffset origin)
    {
        long elapsed = (value - origin).Ticks;
        long remainder = elapsed % interval.Ticks;
        return remainder == 0 ? value : value + TimeSpan.FromTicks(interval.Ticks - remainder);
    }
}
