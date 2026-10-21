using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Query;

namespace SchedularrTV.Core.Scheduling;

/// <summary>One episode of a drop show, in release order.</summary>
public sealed record DropEpisode(string Id, string Label);

/// <summary>Where one drop show on a channel is, for the channel page.</summary>
/// <param name="StateKey">The saved state of the programming the show is part of.</param>
/// <param name="Title">The show.</param>
/// <param name="Where">Which of the channel's programming it drops in (base rotation, a block, an anchor).</param>
/// <param name="Episodes">Its episodes in release order.</param>
/// <param name="ReleasedNow">Episodes out right now, this time through.</param>
/// <param name="Scheduled">Episodes that have had (or are scheduled for) their new airing in the built schedule.</param>
/// <param name="Cycle">How many times the show has started over.</param>
/// <param name="LatestDrop">When the latest drop was.</param>
/// <param name="NextDrop">When the next one is; null when there are no more.</param>
/// <param name="OnSeasonBreak">Between seasons: a season has finished and the next hasn't started.</param>
/// <param name="Finished">Every episode is out (and it isn't set to start over).</param>
/// <param name="Reset">The manual reset it counts from, if any.</param>
public sealed record DropShowStatus(
    string StateKey,
    string Title,
    string Where,
    DropConfig Config,
    IReadOnlyList<DropEpisode> Episodes,
    int ReleasedNow,
    int Scheduled,
    int Cycle,
    DateTimeOffset? LatestDrop,
    DateTimeOffset? NextDrop,
    bool OnSeasonBreak,
    bool Finished,
    DropReset? Reset);

/// <summary>
/// Reads where a channel's drop shows are without building anything, and puts one back to a chosen episode.
/// </summary>
public static class DropStatusReader
{
    /// <summary>Every show on <see cref="PlaybackOrder.Drop"/> in <paramref name="channel"/>'s programming.</summary>
    public static async Task<IReadOnlyList<DropShowStatus>> ReadAsync(SchedulerConfig config, ChannelConfig channel,
        IMediaLibrary library, IPlaybackStateStore store, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        TimeZoneInfo zone = SchedulingEngine.ResolveTimeZone(
            string.IsNullOrWhiteSpace(channel.TimeZone) ? config.Settings.TimeZone : channel.TimeZone);

        // Nothing is flushed, so reading the state this way never moves it.
        var session = new ScheduleBuildSession(config, library, store, channel, zone, now);

        Dictionary<string, (List<string> Where, MediaSequence Sequence)> found = new(StringComparer.Ordinal);
        List<string> order = [];
        foreach ((string where, ContentSelector selector) in Programming(channel))
        {
            MediaSequence? sequence;
            try
            {
                sequence = await session.GetSequenceAsync(selector, "shared", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SchedulerConfigurationException or QueryParseException)
            {
                continue;
            }

            if (sequence is null || !sequence.DropShows.Any())
            {
                continue;
            }

            if (found.TryGetValue(sequence.Key, out var seen))
            {
                if (!seen.Where.Contains(where, StringComparer.OrdinalIgnoreCase)) seen.Where.Add(where);
                continue;
            }

            found[sequence.Key] = (new List<string> { where }, sequence);
            order.Add(sequence.Key);
        }

        List<DropShowStatus> shows = [];
        foreach (string key in order)
        {
            (List<string> where, MediaSequence parent) = found[key];
            foreach ((string title, MediaSequence show) in parent.DropShows)
            {
                shows.Add(Describe(key, title, string.Join(", ", where), show, now));
            }
        }

        return [.. shows.OrderBy(show => show.Title, StringComparer.OrdinalIgnoreCase).ThenBy(show => show.Where, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Puts a drop show back so <paramref name="episodesOut"/> episodes (in release order) are out, as repeats, from
    /// <paramref name="now"/>. The next one drops at the next drop time, or straight away with <paramref name="dropNow"/>.
    /// Rebuild the channel afterwards for the schedule to follow.
    /// </summary>
    public static async Task ResetAsync(IPlaybackStateStore store, DropShowStatus show, int episodesOut, bool dropNow,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        PlaybackState parent = await store.GetAsync(show.StateKey, cancellationToken).ConfigureAwait(false)
            ?? new PlaybackState { Key = show.StateKey };
        parent.Titles ??= new Dictionary<string, PlaybackState>(StringComparer.OrdinalIgnoreCase);

        string? titleKey = parent.Titles.Keys.FirstOrDefault(key => string.Equals(key, show.Title, StringComparison.OrdinalIgnoreCase));
        PlaybackState state = titleKey is not null ? parent.Titles[titleKey] : new PlaybackState { Key = $"{show.StateKey}|title|{show.Title}" };

        int count = Math.Clamp(episodesOut, 0, show.Episodes.Count);
        state.Premiered = count;
        state.LastPremieredId = count > 0 ? show.Episodes[count - 1].Id : null;
        state.DropOrigin = null;
        state.DropCycle = null;
        state.DropReset = new DropReset { At = now, Out = count, Immediate = dropNow, Start = show.Config.Start };
        state.Bag = [];
        state.Cursor = 0;

        parent.Titles[titleKey ?? show.Title] = state;
        await store.SaveAsync(parent, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Episode label for pickers: "S01E03 · Title".</summary>
    public static string Label(MediaItem item)
    {
        string number = item.SeasonNumber is { } season && item.EpisodeNumber is { } episode
            ? $"S{season:00}E{episode:00}"
            : item.EpisodeNumber is { } only ? $"E{only:00}" : string.Empty;
        string name = string.IsNullOrWhiteSpace(item.Title) ? item.Id : item.Title;
        return number.Length > 0 ? $"{number} · {name}" : name;
    }

    private static DropShowStatus Describe(string key, string title, string where, MediaSequence show, DateTimeOffset now)
    {
        DropPlan plan = show.Drop!;
        DropRelease status = plan.StatusAt(now);
        int total = show.Pool.Count;
        int released = Math.Min(total, status.Released);
        DateTimeOffset? next = plan.NextReleaseAfter(now);
        bool finished = released >= total && next is null;

        // Between a season's last drop and the next season's first, with the break still to run.
        bool onBreak = !finished && plan.Config.HasSeasonBreak && released > 0
            && (plan.SeasonEnds.Contains(released) || released >= total)
            && status.LatestDrop is not null;

        // The saved count is for the built schedule's horizon, in whichever cycle it had reached.
        int scheduled = Math.Min(total, show.Cycle >= status.Cycle ? show.Premiered : 0);

        return new DropShowStatus(key, title, where, plan.Config,
            [.. show.Pool.Select(item => new DropEpisode(item.Id, Label(item)))],
            released, scheduled, status.Cycle, status.LatestDrop, next, onBreak, finished, plan.Reset);
    }

    private static IEnumerable<(string Where, ContentSelector Selector)> Programming(ChannelConfig channel)
    {
        if (channel.BaseRotation is { } rotation)
        {
            yield return ("Base rotation", rotation);
        }

        foreach (AnchorConfig anchor in channel.Anchors)
        {
            foreach (ContentSelector selector in anchor.AllSelectors())
            {
                yield return (AnchorName(anchor), selector);
            }
        }

        foreach (BlockConfig block in channel.Blocks)
        {
            foreach (ContentSelector selector in block.Shows)
            {
                yield return (block.Name, selector);
            }

            foreach (AnchorConfig anchor in block.Anchors)
            {
                foreach (ContentSelector selector in anchor.AllSelectors())
                {
                    yield return ($"{block.Name}: {AnchorName(anchor)}", selector);
                }
            }
        }
    }

    private static string AnchorName(AnchorConfig anchor) =>
        string.IsNullOrWhiteSpace(anchor.Name) ? $"Anchor at {anchor.Time:hh\\:mm}" : anchor.Name;
}
