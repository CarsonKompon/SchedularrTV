using System.Text.Json;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Query;

namespace SchedularrTV.Core.Scheduling;

/// <summary>Archive Follow: replays a real channel's archived schedule as overlay slots on the timeline.</summary>
internal sealed class ArchiveFollowTimeline : ITimelineContributor
{
    public async ValueTask<TimelineOverlay?> OverlayAsync(TimelineContext context, CancellationToken cancellationToken)
    {
        (SchedulerConfig config, ChannelConfig channel, IMediaLibrary library, TimeZoneInfo timeZone,
            DateTimeOffset start, DateTimeOffset end, IReadOnlyList<BlockOccurrence> regular) = context;

        if (channel.Follow is not { Enabled: true } follow)
        {
            return null;
        }

        Dictionary<BlockConfig, IOverlaySlot> slots = [];
        List<ScheduleDiagnostic> diagnostics = [];
        List<BlockOccurrence> followed = [];
        List<ArchiveGuideDay> archiveDays = [];

        IReadOnlyList<MediaItem> items = await library.SearchAsync(QueryParser.Parse("kind IN ['Episode', 'Movie']"), cancellationToken).ConfigureAwait(false);
        string[] titles = items.Select(TitleRotation.TitleOf).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var showMatcher = new ArchiveShowMatcher(titles);
        var itemsByTitle = items.ToLookup(TitleRotation.TitleOf, StringComparer.OrdinalIgnoreCase);
        var itemsById = items.ToLookup(item => item.Id);
        Dictionary<(string Show, string? Episode), int> matchScores = [];

        int MatchScore(ArchiveSlot slot)
        {
            var key = (slot.Show, slot.Episode);
            if (matchScores.TryGetValue(key, out int cached))
            {
                return cached;
            }

            ArchiveEpisodeAssignment? assignment = ArchiveEpisodeResolver.Assignment(follow, slot.Show, slot.Episode);
            int score;
            if (assignment is not null && itemsById.Contains(assignment.MediaId))
            {
                score = 2;
            }
            else
            {
                string? title = showMatcher.Resolve(follow, slot.Show);
                MediaItem[] pool = title is null ? [] : itemsByTitle[title].ToArray();
                score = pool.Length == 0 ? 0
                    : ArchiveEpisodeResolver.MatchParts(slot.Episode, pool).All(item => item is not null) ? 2 : 1;
            }

            matchScores[key] = score;
            return score;
        }

        DateTime first = TimeZoneInfo.ConvertTime(start, timeZone).Date;
        DateTime last = TimeZoneInfo.ConvertTime(end.AddTicks(-1), timeZone).Date;
        for (DateTime day = first; day <= last; day = day.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArchiveDay? archive;
            try
            {
                archive = await ArchiveReader.SelectDayAsync(follow, DateOnly.FromDateTime(day),
                    $"{config.Settings.Seed}|{channel.Id}", cancellationToken, MatchScore).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                diagnostics.Add(new ScheduleDiagnostic(DiagnosticLevel.Warning, TimelineBuilder.ToOffset(day, timeZone),
                    $"Archive follow: {exception.Message} Using normal scheduling for {day:yyyy-MM-dd}."));
                continue;
            }

            if (archive is null)
            {
                diagnostics.Add(new ScheduleDiagnostic(DiagnosticLevel.Info, TimelineBuilder.ToOffset(day, timeZone),
                    $"Archive follow: no eligible schedule for {day:yyyy-MM-dd} in {follow.StartYear}-{follow.EndYear}; using normal scheduling."));
                continue;
            }

            diagnostics.Add(new ScheduleDiagnostic(DiagnosticLevel.Info, TimelineBuilder.ToOffset(day, timeZone),
                $"Archive follow: {day:yyyy-MM-dd} follows {archive.Date:yyyy-MM-dd}."));
            List<ArchiveGuideBlock> archiveBlocks = [];
            archiveDays.Add(new ArchiveGuideDay(TimelineBuilder.ToOffset(day, timeZone),
                TimelineBuilder.ToOffset(day.AddDays(1), timeZone), archive.Date, archiveBlocks));
            foreach (ArchiveSlot slot in archive.Slots)
            {
                DateTimeOffset slotStart = TimelineBuilder.ToOffset(day + slot.Start, timeZone);
                DateTimeOffset slotEnd = TimelineBuilder.ToOffset(day + slot.End, timeZone);
                if (slotEnd <= start || slotStart >= end || slotEnd <= slotStart)
                {
                    continue;
                }

                archiveBlocks.Add(new ArchiveGuideBlock(slotStart, slotEnd, slot.Blocks)
                {
                    Show = slot.Show,
                    Episode = slot.Episode,
                    OriginalStart = slot.Start,
                    OriginalEnd = slot.End
                });

                BlockConfig? source = slot.Blocks
                    .Select(label => follow.Blocks.TryGetValue(label, out string? name)
                        ? channel.Blocks.FirstOrDefault(block => string.Equals(block.Name, name, StringComparison.OrdinalIgnoreCase)) : null)
                    .Where(block => block is not null).OrderByDescending(block => block!.EffectivePriority).FirstOrDefault()
                    ?? regular.FirstOrDefault(block => slotStart >= block.Start && slotStart < block.End)?.Block;
                List<ContentSelector> fallback = source is { Shows.Count: > 0 }
                    ? source.Shows : channel.BaseRotation is { } rotation ? [rotation] : [];
                string? title = showMatcher.Resolve(follow, slot.Show);
                MediaItem? exemplar = title is null ? null : items.FirstOrDefault(item =>
                    string.Equals(TitleRotation.TitleOf(item), title, StringComparison.OrdinalIgnoreCase));
                ArchiveEpisodeAssignment? assignment = ArchiveEpisodeResolver.Assignment(follow, slot.Show, slot.Episode);
                MediaItem? assigned = assignment is null ? null : items.FirstOrDefault(item => item.Id == assignment.MediaId);
                if (assigned is not null)
                {
                    exemplar = assigned;
                    title = TitleRotation.TitleOf(assigned);
                }
                ContentSelector? selector = exemplar is null ? null : new ContentSelector
                {
                    Include = [exemplar.Kind == MediaKind.Movie ? new ContentInclude { Movie = title } : new ContentInclude { Show = title }],
                    Order = PlaybackOrder.Random
                };
                var block = new BlockConfig
                {
                    Name = source?.Name ?? "Archive follow",
                    Shows = selector is null ? fallback : [selector],
                    InterstitialShows = source?.InterstitialShows,
                    Interstitials = source?.Interstitials,
                    CommercialBaseTags = source?.CommercialBaseTags,
                    PromoBaseTags = source?.PromoBaseTags,
                    AutoTemporalTags = source?.AutoTemporalTags,
                    PadToNearest = source?.PadToNearest,
                    MaxBreak = source?.MaxBreak,
                    StackToFill = source?.StackToFill,
                    MidRoll = source?.MidRoll,
                    Promos = source?.Promos,
                    // The service finds a promo's template again from the real block's name, so the slot has to
                    // choose from the same templates; without them it used the channel's and the render missed.
                    GeneratedPromos = source?.GeneratedPromos
                };
                slots[block] = new FollowedSlot(slot, selector, fallback);
                followed.Add(new BlockOccurrence(block, slotStart, slotEnd, int.MaxValue));
            }
        }

        // Returned even when no day matched, so the timeline is still flattened as it always was for this channel.
        return new TimelineOverlay(followed, slots, diagnostics, ArchiveGuideDay.Clip(archiveDays, start, end));
    }
}

/// <summary>One archived slot: airs the archived episode(s) when the library has them, otherwise the source block's shows.</summary>
internal sealed record FollowedSlot(ArchiveSlot Slot, ContentSelector? Selector, IReadOnlyList<ContentSelector> Fallback) : IOverlaySlot
{
    public async ValueTask<OverlayPick> PickAsync(ScheduleBuildSession session, ChannelConfig channel, TimeSpan gap, CancellationToken cancellationToken)
    {
        if (Selector is { } archiveSelector)
        {
            MediaSequence? archiveSequence = await session.GetSequenceAsync(archiveSelector, $"follow|{session.Config.Settings.Seed}", cancellationToken).ConfigureAwait(false);
            if (archiveSequence is { IsEmpty: false })
            {
                List<MediaItem> matched = [];
                TimeSpan remaining = gap;
                IReadOnlyList<MediaItem?> parts = ArchiveEpisodeResolver.Resolve(channel.Follow!, Slot.Show,
                    Slot.Episode, archiveSequence.Pool);
                foreach (MediaItem? part in parts)
                {
                    if (part is { Duration.Ticks: > 0 } && part.Duration <= remaining && matched.All(previous => previous.Id != part.Id))
                    {
                        matched.Add(part);
                        remaining -= part.Duration;
                        archiveSequence.Consume(part.Id);
                    }
                }

                HashSet<string> matchedIds = new(matched.Select(item => item.Id), StringComparer.Ordinal);
                int missing = Math.Max(matched.Count == 0 ? 1 : 0, parts.Count(part => part is null));
                for (int index = 0; index < missing; index++)
                {
                    if (!archiveSequence.TryTake(item => item.Duration > TimeSpan.Zero && item.Duration <= remaining
                        && matched.All(previous => previous.Id != item.Id), out MediaItem alternate))
                    {
                        break;
                    }
                    matched.Add(alternate);
                    remaining -= alternate.Duration;
                }
                if (matched.Count > 0)
                {
                    return new OverlayPick(
                        new ProgramPick(matched[0], archiveSequence, archiveSelector, matched,
                            item => item is not null && matchedIds.Contains(item.Id) ? ProgramOrigin.Archive : ProgramOrigin.ArchiveRandom),
                        Fallback);
                }
            }
        }

        return new OverlayPick(null, Fallback);
    }
}
