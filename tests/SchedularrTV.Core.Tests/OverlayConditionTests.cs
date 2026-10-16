using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Overlays;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class OverlayConditionTests
{
    private static readonly DateTimeOffset Eight = new(2026, 10, 3, 20, 0, 0, TimeSpan.Zero);

    private static ScheduleEntry Programme(bool premiere = false, MediaKind kind = MediaKind.Episode) => new()
    {
        Start = Eight,
        Duration = TimeSpan.FromMinutes(22),
        Kind = ScheduleEntryKind.Program,
        IsPremiere = premiere,
        Item = kind == MediaKind.Movie
            ? new MediaItem { Id = "film", Title = "A Film", Kind = MediaKind.Movie, Duration = TimeSpan.FromMinutes(22) }
            : TestMedia.Episode("Toons", 1, 2, TimeSpan.FromMinutes(22))
    };

    private static ScheduleEntry UpNextPromo() => new()
    {
        Start = Eight,
        Duration = TimeSpan.FromSeconds(10),
        Kind = ScheduleEntryKind.UpNextPromo,
        Item = TestMedia.Filler("up-next", TimeSpan.FromSeconds(10))
    };

    private static OverlayContention.Candidate Candidate(Overlay overlay, ScheduleEntry entry) =>
        new(overlay, [.. OverlayTiming.Rules(overlay).Select(rule => (rule, OverlayTiming.For(rule, entry)!))]);

    [Fact]
    public void New_episode_conditions_follow_the_drop_premiere()
    {
        Overlay badge = new() { Conditions = new OverlayConditions { Episodes = OverlayAiring.New } };

        Assert.True(OverlayConditionMatcher.Matches(badge, Programme(premiere: true), Programme(premiere: true), null));
        Assert.False(OverlayConditionMatcher.Matches(badge, Programme(), Programme(), null));

        Overlay beforeNew = new() { Conditions = new OverlayConditions { Next = OverlayAiring.New } };
        Assert.True(OverlayConditionMatcher.Matches(beforeNew, Programme(), Programme(), Programme(premiere: true)));
        Assert.False(OverlayConditionMatcher.Matches(beforeNew, Programme(), Programme(), Programme()));
    }

    [Fact]
    public void What_is_on_picks_out_films_and_up_next_promos()
    {
        Overlay films = new() { Conditions = new OverlayConditions { On = [OverlayOn.Movie] } };
        Assert.True(OverlayConditionMatcher.Matches(films, Programme(kind: MediaKind.Movie), null, null));
        Assert.False(OverlayConditionMatcher.Matches(films, Programme(), null, null));

        // Naming a break reaches it even though the overlay is otherwise for programmes.
        Overlay upNext = new() { Conditions = new OverlayConditions { On = [OverlayOn.UpNext] } };
        Assert.True(OverlayTiming.AppliesTo(upNext, UpNextPromo()));
        Assert.True(OverlayConditionMatcher.Matches(upNext, UpNextPromo(), null, null));
        Assert.False(OverlayConditionMatcher.Matches(upNext, Programme(), null, null));
    }

    [Fact]
    public void Overlapping_overlays_that_are_not_additive_take_turns()
    {
        ScheduleEntry entry = Programme();
        Overlay first = new() { Name = "Lower third A", When = OverlayWhen.Start, OffsetSeconds = 10, DurationSeconds = 8, Additive = false };
        Overlay second = new() { Name = "Lower third B", When = OverlayWhen.Start, OffsetSeconds = 12, DurationSeconds = 8, Additive = false };
        Overlay bug = new() { Name = "Bug", When = OverlayWhen.Always, Additive = true };
        Overlay later = new() { Name = "Up next", When = OverlayWhen.End, OffsetSeconds = 45, DurationSeconds = 10, Additive = false };

        IReadOnlyList<OverlayContention.Candidate> shown = OverlayContention.Resolve(
            [Candidate(bug, entry), Candidate(first, entry), Candidate(second, entry), Candidate(later, entry)], entry, "seed");

        Assert.Contains(shown, candidate => candidate.Overlay == bug);
        Assert.Contains(shown, candidate => candidate.Overlay == later);
        Assert.Single(shown, candidate => candidate.Overlay == first || candidate.Overlay == second);

        // The same pick every time it's worked out.
        IReadOnlyList<OverlayContention.Candidate> again = OverlayContention.Resolve(
            [Candidate(bug, entry), Candidate(first, entry), Candidate(second, entry), Candidate(later, entry)], entry, "seed");
        Assert.Equal(shown.Select(candidate => candidate.Overlay.Name), again.Select(candidate => candidate.Overlay.Name));
    }

    [Fact]
    public void Overlays_saved_before_the_setting_keep_stacking()
    {
        ScheduleEntry entry = Programme();
        Overlay first = new() { Name = "A", When = OverlayWhen.Always };
        Overlay second = new() { Name = "B", When = OverlayWhen.Always };

        Assert.Equal(2, OverlayContention.Resolve([Candidate(first, entry), Candidate(second, entry)], entry, "seed").Count);
    }

    [Fact]
    public void Either_choice_comes_up_across_airings()
    {
        Overlay first = new() { Name = "A", When = OverlayWhen.Always, Additive = false };
        Overlay second = new() { Name = "B", When = OverlayWhen.Always, Additive = false };
        HashSet<string> winners = [];
        for (int airing = 0; airing < 40; airing++)
        {
            ScheduleEntry entry = Programme();
            winners.Add(OverlayContention.Resolve([Candidate(first, entry), Candidate(second, entry)], entry, $"seed|{airing}").Single().Overlay.Name);
        }

        Assert.Equal(2, winners.Count);
    }
}
