using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Overlays;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>A screenbug made from files can have several, and picks one at random each time it shows.</summary>
public sealed class OverlayFileTurnsTests
{
    private static readonly DateTimeOffset Eight = new(2026, 11, 10, 20, 0, 0, TimeSpan.Zero);

    private static ScheduleEntry Episode(DateTimeOffset? start = null, int minutes = 22) => new()
    {
        Start = start ?? Eight,
        Duration = TimeSpan.FromMinutes(minutes),
        Kind = ScheduleEntryKind.Program,
        Item = TestMedia.Episode("Toons", 1, 2, TimeSpan.FromMinutes(minutes))
    };

    private static Overlay Bug(string when = OverlayWhen.Always, params string[] more) => new()
    {
        Name = "Bug",
        Kind = OverlayKinds.File,
        File = @"D:\Logos\bug-a.png",
        MoreFiles = [.. more],
        When = when,
        EveryMinutes = 5,
        OffsetSeconds = 30,
        DurationSeconds = 10,
        FadeSeconds = 0.5
    };

    private static List<(Overlay Overlay, OverlayShowing Showing)> Turns(Overlay bug, ScheduleEntry entry) =>
        [.. OverlayProcessor.TakingTurns(bug, bug, entry, OverlayTiming.For(bug, entry)!, "seed|mtv|Bug")];

    [Fact]
    public void One_file_shows_as_it_always_did()
    {
        Overlay bug = Bug();
        var turn = Assert.Single(Turns(bug, Episode()));
        Assert.Same(bug, turn.Overlay);
    }

    [Fact]
    public void Shown_throughout_it_picks_one_per_programme_and_the_programmes_vary()
    {
        Overlay bug = Bug(OverlayWhen.Always, @"D:\Logos\bug-b.png", @"D:\Logos\bug-c.png");

        HashSet<string> seen = [];
        for (int hour = 0; hour < 24; hour++)
        {
            var turn = Assert.Single(Turns(bug, Episode(Eight.AddHours(hour))));
            Assert.Contains(turn.Overlay.File!, bug.Files);
            seen.Add(turn.Overlay.File!);
        }

        Assert.True(seen.Count > 1, "every programme got the same picture");

        // The same each time the schedule is published.
        Assert.Equal(Turns(bug, Episode()).Single().Overlay.File, Turns(bug, Episode()).Single().Overlay.File);
    }

    [Fact]
    public void A_repeating_bug_picks_for_every_appearance_never_the_same_twice_running()
    {
        Overlay bug = Bug(OverlayWhen.Every, @"D:\Logos\bug-b.png");
        ScheduleEntry episode = Episode(minutes: 30);

        List<(Overlay Overlay, OverlayShowing Showing)> turns = Turns(bug, episode);

        // Every five minutes from 0:30 through a half hour: six appearances, each timed once on its own.
        Assert.Equal(6, turns.Count);
        Assert.All(turns, turn => Assert.True(turn.Showing.Timing!.FrequencyMs > (long)episode.Duration.TotalMilliseconds));
        Assert.Equal(30_000, turns[0].Showing.Timing!.PhaseMs);
        Assert.Equal(330_000, turns[1].Showing.Timing!.PhaseMs);

        // With two pictures and no repeats, they alternate.
        for (int i = 1; i < turns.Count; i++)
        {
            Assert.NotEqual(turns[i - 1].Overlay.File, turns[i].Overlay.File);
        }
    }

    [Fact]
    public void A_variant_picture_shows_as_it_is()
    {
        Overlay bug = Bug(OverlayWhen.Always, @"D:\Logos\bug-b.png");
        Overlay halloween = bug.Copy();
        halloween.File = @"D:\Logos\pumpkin.png";

        var turn = Assert.Single(OverlayProcessor.TakingTurns(bug, halloween, Episode(), new OverlayShowing(null), "seed"));
        Assert.Equal(@"D:\Logos\pumpkin.png", turn.Overlay.File);
    }
}
