using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class MidRollTests
{
    private static readonly MidRollConfig Chapters = new()
    {
        Mode = MidRollMode.Chapters,
        TargetSegment = TimeSpan.FromMinutes(15),
        MinSegment = TimeSpan.FromMinutes(5),
        ChapterTolerance = TimeSpan.FromMinutes(4),
        MaxBreaks = 8,
        MinBreak = TimeSpan.FromSeconds(30)
    };

    private static MidRollConfig Auto(double targetMinutes = 15) => new()
    {
        Mode = MidRollMode.Auto,
        TargetSegment = TimeSpan.FromMinutes(targetMinutes),
        MinSegment = TimeSpan.FromMinutes(5),
        ChapterTolerance = TimeSpan.FromMinutes(4),
        MaxBreaks = 8,
        MinBreak = TimeSpan.FromSeconds(30)
    };

    private static MediaItem Movie(TimeSpan duration, params TimeSpan[] chapters) => new()
    {
        Id = "movie",
        Title = "A Movie",
        Kind = MediaKind.Movie,
        Duration = duration,
        Chapters = chapters,
        Path = @"M:\movies\a.mkv"
    };

    private static TimeSpan Min(double m) => TimeSpan.FromMinutes(m);

    [Fact]
    public void Stack_chapters_combine_episode_boundaries_with_internal_markers()
    {
        var config = Auto();
        config.ChapterTolerance = Min(1);
        var parts = MidRollPlanner.SplitStack([Movie(Min(7)), Movie(Min(18), Min(8))], config, Min(6));
        Assert.Equal([TimeSpan.Zero, Min(15)], parts.Select(part => part.InPoint));
        Assert.Equal(Min(25), parts[^1].OutPoint);
        Assert.Single(MidRollPlanner.SplitStack([Movie(Min(7)), Movie(Min(18), Min(8))], null, Min(6)));
    }

    [Fact]
    public async Task Stack_cuts_inside_an_episode_preserve_its_offsets_and_block_override()
    {
        var channel = new ChannelConfig
        {
            Id = "stack", Name = "Stack", PadToNearest = Min(40), StackToFill = true,
            MidRoll = new() { Mode = MidRollMode.None },
            BaseRotation = new() { Include = [new ContentInclude { Show = "Shorts" }], Order = PlaybackOrder.SeasonEpisode },
            Blocks = [new BlockConfig
            {
                Name = "Breaks", Time = TimeSpan.Zero, EndTime = Min(40),
                MidRoll = new() { Mode = MidRollMode.Auto, ChapterTolerance = Min(1) }
            }],
            Interstitials = new() { Commercials = new() { Query = "tag:'Commercials'", MaxRepeatsPerBreak = 100 } }
        };
        var schedule = await new SchedulingEngine(new FakeMediaLibrary([
            TestMedia.Episode("Shorts", 1, 1, Min(12)), TestMedia.Episode("Shorts", 1, 2, Min(12)),
            TestMedia.Filler("ad", TimeSpan.FromSeconds(30), "Commercials")]), new InMemoryPlaybackStateStore())
            .BuildAsync(new SchedulerConfig(), channel, Day, Day.AddMinutes(40));
        var parts = schedule.Entries.Where(entry => entry.Kind == ScheduleEntryKind.Program).ToArray();
        Assert.Equal(3, parts.Length);
        Assert.Equal([Min(12), Min(3), Min(9)], parts.Select(part => part.Duration));
        Assert.Equal([TimeSpan.Zero, TimeSpan.Zero, Min(3)], parts.Select(part => part.InPoint));
        Assert.Equal([0, 1, 2], parts.Select(part => part.SegmentNumber));
        Assert.Equal([0, 2, 2], parts.Select(part => part.SegmentCount));
        Assert.False(parts[0].IsTrimmed);
        Assert.True(parts[1].IsTrimmed);
        Assert.Equal(Min(12), parts[2].SourceDuration);
        Assert.Equal(Day.AddMinutes(19), parts[2].Start);
        DateTimeOffset cursor = Day;
        foreach (var entry in schedule.Entries)
        {
            Assert.Equal(cursor, entry.Start);
            cursor = entry.End;
        }
        Assert.Equal(Day.AddMinutes(40), cursor);
    }

    // --- Cadence --------------------------------------------------------------------------------

    [Fact]
    public void Break_count_scales_with_runtime_rather_than_being_fixed()
    {
        // The whole point: a 90-minute film gets more breaks than a 30-minute one, not longer parts.
        int shortFilm = MidRollPlanner.Split(Movie(Min(30)), Auto(), Min(6)).Count;
        int longFilm = MidRollPlanner.Split(Movie(Min(90)), Auto(), Min(6)).Count;

        Assert.Equal(2, shortFilm);
        Assert.Equal(6, longFilm);
    }

    [Fact]
    public void Auto_mode_splits_evenly_when_there_are_no_chapters()
    {
        IReadOnlyList<MediaSegment> segments = MidRollPlanner.Split(Movie(Min(90)), Auto(), Min(6));

        Assert.Equal([TimeSpan.Zero, Min(15), Min(30), Min(45), Min(60), Min(75)], segments.Select(s => s.InPoint));
        Assert.Equal(Min(90), segments[^1].OutPoint);
    }

    [Fact]
    public void Auto_mode_still_prefers_a_nearby_chapter_over_an_even_cut()
    {
        // Markers 2 and 2.5 minutes off the cadence are within tolerance and win; the middle
        // break has no marker nearby and falls back to an even cut.
        MediaItem movie = Movie(Min(60), Min(13), Min(40.5));

        IReadOnlyList<MediaSegment> segments = MidRollPlanner.Split(movie, Auto(), Min(6));

        Assert.Equal([TimeSpan.Zero, Min(13), Min(28), Min(40.5)], segments.Select(s => s.InPoint));
    }

    [Fact]
    public void Chapters_mode_uses_the_nearest_marker_even_when_it_is_off_cadence()
    {
        MediaItem movie = Movie(Min(60), Min(50));

        IReadOnlyList<MediaSegment> segments = MidRollPlanner.Split(movie, Chapters, Min(6));

        // Only one marker exists, so it is the break rather than no break at all.
        Assert.Equal([TimeSpan.Zero, Min(50)], segments.Select(s => s.InPoint));
    }

    [Fact]
    public void The_cadence_resumes_from_where_a_chapter_actually_landed()
    {
        MediaItem movie = Movie(Min(75), Min(18));

        IReadOnlyList<MediaSegment> segments = MidRollPlanner.Split(movie, Auto(), Min(6));

        Assert.Equal(Min(18), segments[1].InPoint);
        Assert.Equal(Min(33), segments[2].InPoint);
    }

    // --- Guards ---------------------------------------------------------------------------------

    [Fact]
    public void Chapters_split_a_programme_into_contiguous_parts()
    {
        MediaItem movie = Movie(Min(60), Min(0), Min(15), Min(32), Min(48));

        IReadOnlyList<MediaSegment> segments = MidRollPlanner.Split(movie, Chapters, Min(6));

        Assert.Equal(4, segments.Count);
        Assert.Equal(TimeSpan.Zero, segments[0].InPoint);
        Assert.Equal(Min(60), segments[^1].OutPoint);

        for (int i = 0; i < segments.Count - 1; i++)
        {
            Assert.Equal(segments[i].OutPoint, segments[i + 1].InPoint);
        }

        Assert.Equal(Min(60), segments.Aggregate(TimeSpan.Zero, (sum, s) => sum + s.Duration));
    }

    [Fact]
    public void Chapter_marks_closer_than_the_minimum_segment_are_ignored()
    {
        // Borat has markers six seconds apart; cutting to advert there would be absurd.
        MediaItem movie = Movie(Min(95), Min(0), TimeSpan.FromSeconds(6), Min(63), Min(90), Min(90.5));

        IReadOnlyList<MediaSegment> segments = MidRollPlanner.Split(movie, Auto(), Min(6));

        Assert.All(segments, s => Assert.True(s.Duration >= Chapters.MinSegment, $"segment of {s.Duration} is too short"));
        Assert.DoesNotContain(TimeSpan.FromSeconds(6), segments.Select(s => s.InPoint));
    }

    [Fact]
    public void No_part_is_ever_shorter_than_the_minimum()
    {
        MediaItem movie = Movie(Min(31), Min(29), Min(30));

        IReadOnlyList<MediaSegment> segments = MidRollPlanner.Split(movie, Auto(), Min(6));

        Assert.All(segments, s => Assert.True(s.Duration >= Min(5), $"segment of {s.Duration} is too short"));
    }

    [Fact]
    public void The_break_cap_is_a_safety_net()
    {
        var capped = new MidRollConfig { Mode = MidRollMode.Auto, TargetSegment = Min(10), MinSegment = Min(5), MaxBreaks = 2 };

        IReadOnlyList<MediaSegment> segments = MidRollPlanner.Split(Movie(Min(120)), capped, Min(6));

        Assert.Equal(3, segments.Count);
    }

    [Fact]
    public void A_programme_shorter_than_one_segment_is_never_split()
    {
        Assert.Single(MidRollPlanner.Split(Movie(Min(18)), Auto(), Min(6)));
    }

    [Fact]
    public void Chapters_mode_leaves_a_programme_without_chapters_whole()
    {
        Assert.Single(MidRollPlanner.Split(Movie(Min(90)), Chapters, Min(6)));
    }

    [Fact]
    public void A_break_budget_too_small_to_notice_leaves_the_programme_whole()
    {
        MediaItem movie = Movie(Min(60), Min(0), Min(20), Min(40));

        Assert.Single(MidRollPlanner.Split(movie, Chapters, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Mid_roll_off_by_default()
    {
        MediaItem movie = Movie(Min(60), Min(0), Min(20), Min(40));

        Assert.Single(MidRollPlanner.Split(movie, null, Min(6)));
        Assert.Single(MidRollPlanner.Split(movie, new MidRollConfig(), Min(6)));
    }

    // --- End to end -----------------------------------------------------------------------------

    private static readonly DateTimeOffset Day = new(2026, 1, 12, 0, 0, 0, TimeSpan.Zero);

    private static async Task<ChannelSchedule> BuildAsync(MidRollConfig? midRoll, bool withShorts = false)
    {
        List<MediaItem> library =
        [
            Movie(Min(24), Min(0), Min(8), Min(16))
        ];

        for (int i = 0; i < 20; i++)
        {
            library.Add(TestMedia.Filler($"ad-{i}", TimeSpan.FromSeconds(30), "Commercials"));
        }

        for (int i = 0; i < 6; i++)
        {
            library.Add(TestMedia.Episode("Shorts", 1, i + 1, Min(1), collection: "Shorts"));
        }

        var channel = new ChannelConfig
        {
            Id = "ch",
            Name = "Channel",
            Number = 1,
            TimeZone = "UTC",
            PadToNearest = Min(30),
            MidRoll = midRoll,
            BaseRotation = new ContentSelector { Query = "kind == 'Movie'", Order = PlaybackOrder.Random },
            Interstitials = withShorts
                ? new InterstitialsConfig
                {
                    ShowChance = 100,
                    Shows = new ContentSelector { Query = "collection:'Shorts'", Order = PlaybackOrder.Shuffle }
                }
                : null
        };

        var config = new SchedulerConfig
        {
            Settings = new SchedulerSettings
            {
                TimeZone = "UTC",
                Interstitials = new InterstitialsConfig
                {
                    Commercials = new CommercialsConfig
                    {
                        BaseQuery = "tag:\"Commercials\"",
                        Granularity = TimeSpan.FromMilliseconds(250),
                        MaxRepeatsPerBreak = 12
                    },
                    Filler = new FillerTierConfig { Fallback = false }
                }
            },
            Channels = [channel]
        };

        var engine = new SchedulingEngine(new FakeMediaLibrary(library), new InMemoryPlaybackStateStore());
        return await engine.BuildAsync(config, channel, Day, Day.AddHours(1));
    }

    [Fact]
    public async Task Interstitial_shows_never_appear_inside_a_programme()
    {
        ChannelSchedule schedule = await BuildAsync(
            new MidRollConfig { Mode = MidRollMode.Chapters, TargetSegment = Min(8), MinSegment = Min(5), ChapterTolerance = Min(4) },
            withShorts: true);

        List<ScheduleEntry> ordered = schedule.Entries.OrderBy(e => e.Start).ToList();

        for (int i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Kind != ScheduleEntryKind.Interstitial)
            {
                continue;
            }

            // A short between parts of the same programme would mean the mid-roll used tier 1.
            ScheduleEntry? previousProgram = ordered.Take(i).LastOrDefault(e => e.Kind == ScheduleEntryKind.Program);
            Assert.False(
                previousProgram is { SegmentCount: > 0 } p && p.SegmentNumber < p.SegmentCount,
                "an interstitial short was scheduled inside a programme");
        }

        // They are still used after programmes.
        Assert.Contains(schedule.Entries, e => e.Kind == ScheduleEntryKind.Interstitial);
    }

    [Fact]
    public async Task A_mid_roll_break_is_capped_and_the_remainder_goes_to_the_end()
    {
        ChannelSchedule schedule = await BuildAsync(new MidRollConfig
        {
            Mode = MidRollMode.Chapters,
            TargetSegment = Min(8),
            MinSegment = Min(5),
            ChapterTolerance = Min(4),
            MaxBreakLength = Min(1)
        });

        List<ScheduleEntry> ordered = schedule.Entries
            .Where(e => e.Start < Day.AddMinutes(30))
            .OrderBy(e => e.Start)
            .ToList();

        List<ScheduleEntry> parts = ordered.Where(e => e.Kind == ScheduleEntryKind.Program).ToList();
        Assert.Equal(3, parts.Count);

        // Each mid-roll honours the cap; the tail soaks up everything left in the slot.
        for (int i = 0; i < parts.Count - 1; i++)
        {
            TimeSpan gap = parts[i + 1].Start - parts[i].End;
            Assert.True(gap <= Min(1), $"mid-roll break of {gap} exceeded the cap");
        }

        TimeSpan tail = Day.AddMinutes(30) - parts[^1].End;
        Assert.True(tail > Min(1), $"expected the leftover budget after the programme, got {tail}");
    }

    [Fact]
    public async Task Adverts_are_spread_between_chapter_parts()
    {
        ChannelSchedule schedule = await BuildAsync(new MidRollConfig
        {
            Mode = MidRollMode.Chapters,
            TargetSegment = Min(8),
            MinSegment = Min(5),
            ChapterTolerance = Min(4),
            MaxBreaks = 3
        });

        List<ScheduleEntry> firstProgramme = schedule.Entries
            .TakeWhile(e => e.Start < Day.AddMinutes(30))
            .ToList();

        List<ScheduleEntry> parts = firstProgramme.Where(e => e.Kind == ScheduleEntryKind.Program).ToList();

        Assert.Equal(3, parts.Count);
        Assert.Equal([1, 2, 3], parts.Select(p => p.SegmentNumber));
        Assert.Equal(TimeSpan.Zero, parts[0].InPoint);
        Assert.Equal(Min(8), parts[1].InPoint);
        Assert.Equal(Min(16), parts[2].InPoint);

        // Every part is followed by adverts, including the two mid-rolls.
        for (int i = 0; i < parts.Count; i++)
        {
            int partIndex = firstProgramme.IndexOf(parts[i]);
            Assert.True(
                partIndex + 1 < firstProgramme.Count
                && firstProgramme[partIndex + 1].Kind == ScheduleEntryKind.Commercial,
                $"part {i + 1} was not followed by an advert break");
        }
    }

    [Fact]
    public async Task Without_mid_roll_all_the_advert_time_runs_after_the_programme()
    {
        ChannelSchedule schedule = await BuildAsync(midRoll: null);

        List<ScheduleEntry> firstProgramme = schedule.Entries
            .TakeWhile(e => e.Start < Day.AddMinutes(30))
            .ToList();

        Assert.Single(firstProgramme, e => e.Kind == ScheduleEntryKind.Program);
        Assert.Equal(ScheduleEntryKind.Program, firstProgramme[0].Kind);
    }

    [Fact]
    public async Task Splitting_keeps_the_timeline_contiguous_and_on_the_grid()
    {
        ChannelSchedule schedule = await BuildAsync(new MidRollConfig
        {
            Mode = MidRollMode.Chapters,
            TargetSegment = Min(8),
            MinSegment = Min(5),
            ChapterTolerance = Min(4)
        });

        DateTimeOffset cursor = schedule.Start;
        foreach (ScheduleEntry entry in schedule.Entries)
        {
            Assert.Equal(cursor, entry.Start);
            cursor = entry.End;
        }

        Assert.Equal(schedule.End, cursor);

        // Each programme still starts on the half hour.
        foreach (ScheduleEntry first in schedule.Entries.Where(e => e.SegmentNumber is 0 or 1 && e.Kind == ScheduleEntryKind.Program))
        {
            Assert.Equal(0, (first.Start - schedule.Start).Ticks % Min(30).Ticks);
        }
    }
}
