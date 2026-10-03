using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class ArchiveFollowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"archive-follow-{Guid.NewGuid():N}");

    public ArchiveFollowTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Same_month_day_uses_available_years_and_is_stable_across_rebuilds()
    {
        WriteDay(2002, "09-15");
        WriteDay(2004, "09-15");
        WriteDay(2003, "09-14");
        var follow = Follow();
        var date = new DateOnly(2026, 9, 15);
        ArchiveDay? first = await ArchiveReader.SelectDayAsync(follow, date, "channel");
        ArchiveDay? second = await ArchiveReader.SelectDayAsync(follow, date, "channel");
        Assert.NotNull(first);
        Assert.Contains(first.Date.Year, new[] { 2002, 2004 });
        Assert.Equal(first.Date, second?.Date);
        Assert.Null(await ArchiveReader.SelectDayAsync(follow, new DateOnly(2026, 9, 16), "channel"));
    }

    [Fact]
    public async Task Scan_reads_block_index_and_unique_shows_in_year_range()
    {
        WriteDay(2002, "09-15");
        WriteDay(2004, "09-15");
        File.WriteAllText(Path.Combine(_directory, "_block_index.json"), """{"Toonami '00":{"count":12}}""");
        ArchiveScan scan = await ArchiveReader.ScanAsync(Follow());
        Assert.Equal(2, scan.Days);
        Assert.Equal(["Toonami '00"], scan.Blocks);
        Assert.Equal(["Dragon Ball Z"], scan.Shows);
        Assert.Equal(new ArchiveEpisode("Dragon Ball Z", "Final Atonement", 2), Assert.Single(scan.Episodes));
        Assert.Empty(scan.Warnings);
    }

    [Fact]
    public async Task Scan_retains_index_counts_and_counts_each_show_occurrence_in_the_year_range()
    {
        WriteSchedule("""
            {"schedule":[{"show":"Frequent"},{"show":"frequent"},{"show":"Rare"}]}
            """);
        WriteDay(2004, "09-15");
        WriteDay(2005, "09-15");
        File.WriteAllText(Path.Combine(_directory, "_block_index.json"), """
            {"Popular":{"count":100},"popular":{"count":20},"Rare":{"count":3},"Unknown":{}}
            """);
        ArchiveScan scan = await ArchiveReader.ScanAsync(Follow());
        Assert.Equal(120, scan.BlockCounts["POPULAR"]);
        Assert.Equal(3, scan.BlockCounts["Rare"]);
        Assert.Equal(0, scan.BlockCounts["Unknown"]);
        Assert.Equal(2, scan.ShowCounts["Frequent"]);
        Assert.Equal(1, scan.ShowCounts["Rare"]);
        Assert.Equal(1, scan.ShowCounts["Dragon Ball Z"]);
        Assert.Equal(2, scan.Days);
    }

    [Fact]
    public async Task Slot_end_and_multi_block_labels_are_preserved()
    {
        WriteDay(2002, "09-15");
        ArchiveDay? day = await ArchiveReader.SelectDayAsync(Follow(), new DateOnly(2026, 9, 15), "channel");
        ArchiveSlot slot = Assert.Single(day!.Slots);
        Assert.Equal(TimeSpan.FromMinutes(30), slot.End);
        Assert.Equal(["Toonami '00", "Special"], slot.Blocks);
        Assert.Equal("Final Atonement", slot.Episode);
    }

    [Fact]
    public async Task Scan_includes_titles_without_usable_times_and_reports_broken_files()
    {
        WriteSchedule("""{"schedule":[{"time":null,"show":"Unscheduled show"}]}""");
        File.WriteAllText(Path.Combine(_directory, "_block_index.json"), "{}");
        File.WriteAllText(Path.Combine(_directory, "2002", "09", "2002-09-16.json"), "invalid json");
        ArchiveScan scan = await ArchiveReader.ScanAsync(Follow());
        Assert.Equal(["Unscheduled show"], scan.Shows);
        Assert.Single(scan.Warnings);
    }

    [Fact]
    public void Explicit_unmapping_overrides_automatic_matching_and_aliases_can_share_a_title()
    {
        var follow = Follow();
        Assert.Equal("Dragon Ball Z", ArchiveReader.ResolveShow(follow, "Dragon Ball Z!", ["Dragon Ball Z"]));
        follow.Shows["Dragon Ball Z!"] = null;
        Assert.Null(ArchiveReader.ResolveShow(follow, "Dragon Ball Z!", ["Dragon Ball Z"]));
        follow.Shows["Bugs and Daffy"] = "Looney Tunes";
        follow.Shows["Bugs and Tweety"] = "Looney Tunes";
        Assert.Equal("Looney Tunes", ArchiveReader.ResolveShow(follow, "Bugs and Daffy", ["Looney Tunes"]));
        Assert.Equal("Looney Tunes", ArchiveReader.ResolveShow(follow, "Bugs and Tweety", ["Looney Tunes"]));
    }

    private ArchiveFollowConfig Follow() => new() { Enabled = true, Directory = _directory, StartYear = 2002, EndYear = 2004 };

    [Fact]
    public async Task Weekday_selection_filters_month_and_retains_deterministic_calendar_weights()
    {
        WriteDay(2002, "09-02");
        WriteDay(2002, "10-07");
        WriteDay(2002, "09-03");
        WriteDay(2003, "09-01");
        var follow = Follow();
        follow.FollowWeekday = true;
        var date = new DateOnly(2026, 9, 21);
        Assert.True(follow.FollowMonth);
        for (int seed = 0; seed < 20; seed++)
        {
            ArchiveDay? day = await ArchiveReader.SelectDayAsync(follow, date, seed.ToString());
            Assert.Equal(DayOfWeek.Monday, day!.Date.DayOfWeek);
            Assert.Equal(9, day.Date.Month);
        }
        follow.FollowMonth = false;
        Assert.Equal(1, ArchiveReader.CalendarWeight(follow, date, new DateOnly(2002, 10, 7)));
        Assert.Equal(4, ArchiveReader.CalendarWeight(follow, date, new DateOnly(2002, 9, 2)));
        follow.StartYear = 1998;
        WriteDay(1998, "09-21");
        Assert.Equal(16, ArchiveReader.CalendarWeight(follow, date, new DateOnly(1998, 9, 21)));
        for (int seed = 0; seed < 30; seed++)
        {
            ArchiveDay? day = await ArchiveReader.SelectDayAsync(follow, date, seed.ToString());
            byte[] hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{seed}|{date:yyyy-MM-dd}"));
            ulong ticket = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(hash) % (3600UL * 25);
            DateOnly expected = ticket < 3600 * 16 ? new(1998, 9, 21)
                : ticket < 3600 * 20 ? new(2002, 9, 2) : ticket < 3600 * 21 ? new(2002, 10, 7) : new(2003, 9, 1);
            Assert.Equal(expected, day!.Date);
        }
    }

    [Fact]
    public void Coverage_counts_possibilities_over_a_whole_year_including_leap_day()
    {
        var follow = Follow();
        DateOnly[] dates = [new(2002, 9, 2), new(2003, 9, 1), new(2002, 10, 7), new(2004, 2, 29)];
        ArchiveCoverage exact = ArchiveReader.Coverage(follow, dates, 2026);
        Assert.Equal(365, exact.Days);
        Assert.Equal(362, exact.ZeroMatches);
        Assert.Equal(3, exact.OneMatch);
        Assert.Equal(3.0 / 365, exact.AverageMatches);
        Assert.Equal(4, ArchiveReader.Coverage(follow, dates, 2028).OneMatch);
        follow.FollowWeekday = true;
        ArchiveCoverage month = ArchiveReader.Coverage(follow, dates, 2026);
        Assert.Equal(353, month.ZeroMatches);
        Assert.Equal(8, month.OneMatch);
        Assert.Equal(16.0 / 365, month.AverageMatches);
        follow.FollowMonth = false;
        ArchiveCoverage weekday = ArchiveReader.Coverage(follow, dates, 2026);
        Assert.Equal(261, weekday.ZeroMatches);
        Assert.Equal(52, weekday.OneMatch);
        Assert.Equal(208.0 / 365, weekday.AverageMatches);
    }

    [Fact]
    public async Task Date_inventory_and_selection_respect_layout_years_and_no_eligible_weekdays()
    {
        WriteDay(2002, "09-03");
        WriteDay(2005, "09-05");
        File.WriteAllText(Path.Combine(_directory, "2002", "09", "2002-10-07.json"), "{}");
        File.WriteAllText(Path.Combine(_directory, "2002", "09", "index.json"), "{}");
        var follow = Follow();
        Assert.Equal([new DateOnly(2002, 9, 3)], ArchiveReader.ListDates(follow));
        follow.FollowWeekday = true;
        Assert.Null(await ArchiveReader.SelectDayAsync(follow, new DateOnly(2026, 9, 21), "seed"));
        follow.FollowMonth = false;
        Assert.Null(await ArchiveReader.SelectDayAsync(follow, new DateOnly(2026, 9, 21), "seed"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ArchiveReader.ListDates(follow, cancellation.Token));
    }

    [Fact]
    public async Task Legacy_options_ignore_month_switch_and_handle_february_29()
    {
        var config = SchedulerConfigLoader.Parse("""
            channels:
            - id: test
              name: Test
              follow:
                enabled: true
            """);
        Assert.False(config.Channels[0].Follow!.FollowWeekday);
        Assert.True(config.Channels[0].Follow!.FollowMonth);
        WriteDay(2004, "02-29");
        var follow = Follow();
        follow.FollowMonth = false;
        Assert.Equal(new DateOnly(2004, 2, 29),
            (await ArchiveReader.SelectDayAsync(follow, new DateOnly(2028, 2, 29), "seed"))!.Date);
        Assert.Equal(0, ArchiveReader.CalendarWeight(follow, new DateOnly(2026, 9, 21), new DateOnly(2002, 9, 2)));
        Assert.Equal(new ArchiveCoverage(2028, 366, 0, 366, 0), ArchiveReader.Coverage(follow, [], 2028));
        Assert.Equal(1, ArchiveReader.Coverage(follow,
            [new(2004, 2, 29), new(2004, 2, 29), new(2008, 9, 21)], 2028).OneMatch);
    }

    [Theory]
    [InlineData("EP07", 1, 7)]
    [InlineData("Ep: 03", 1, 3)]
    [InlineData("Episode 9", 1, 9)]
    [InlineData("S02E03", 2, 3)]
    [InlineData("Season 2 Episode 3", 2, 3)]
    public void Numbered_archive_labels_use_episode_metadata_when_no_literal_title_exists(string label, int season, int number)
    {
        MediaItem episode = new() { Id = "numbered", Title = "A real title", SeasonNumber = season, EpisodeNumber = number };
        MediaItem otherSeason = new() { Id = "other", Title = "Another title", SeasonNumber = 5, EpisodeNumber = number };
        Assert.Same(episode, ArchiveEpisodeMatcher.Match(label, [otherSeason, episode]));
        MediaItem literal = new() { Id = "literal", Title = label, SeasonNumber = 6, EpisodeNumber = 1 };
        Assert.Same(literal, ArchiveEpisodeMatcher.Match(label, [episode, literal]));
        Assert.Null(ArchiveEpisodeMatcher.Match(label, [otherSeason]));
    }

    [Fact]
    public void Numeric_words_inside_real_titles_do_not_trigger_episode_number_matching()
    {
        MediaItem episode = new() { Id = "episode", Title = "Pilot", SeasonNumber = 1, EpisodeNumber = 9 };
        Assert.Null(ArchiveEpisodeMatcher.Match("The Lost Episode 9", [episode]));
    }

    [Fact]
    public async Task Complete_days_are_favored_but_sparse_and_empty_days_can_still_be_selected()
    {
        WriteSchedule("""{"schedule":[{"time":"00:00","end_time":"24:00","show":"Complete"}]}""");
        WriteDay(2003, "09-15");
        WriteDay(2004, "09-15");
        File.WriteAllText(Path.Combine(_directory, "2004", "09", "2004-09-15.json"), """{"schedule":[]}""");
        Dictionary<int, int> selected = new() { [2002] = 0, [2003] = 0, [2004] = 0 };
        for (int seed = 0; seed < 300; seed++)
        {
            ArchiveDay? day = await ArchiveReader.SelectDayAsync(Follow(), new DateOnly(2026, 9, 15), seed.ToString());
            selected[day!.Date.Year]++;
        }
        Assert.True(selected[2002] > 240);
        Assert.True(selected[2003] > 0);
        Assert.True(selected[2004] > 0);
    }

    [Fact]
    public async Task Slot_scores_favor_more_matches_and_episode_matches_without_excluding_unmatched_years()
    {
        for (int year = 2002; year <= 2004; year++)
        {
            WriteDay(year, "09-15");
            File.WriteAllText(Path.Combine(_directory, year.ToString(), "09", $"{year}-09-15.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    schedule = new[]
                    {
                        new { time = "00:00", end_time = "00:30", show = (year - 2002).ToString() },
                        new { time = "00:30", end_time = "01:00", show = (year - 2002).ToString() }
                    }
                }));
        }

        Dictionary<int, int> selected = new() { [2002] = 0, [2003] = 0, [2004] = 0 };
        var date = new DateOnly(2026, 9, 15);
        for (int seed = 0; seed < 450; seed++)
        {
            string selectionSeed = seed.ToString();
            ArchiveDay? day = await ArchiveReader.SelectDayAsync(Follow(), date, selectionSeed,
                matchScore: slot => int.Parse(slot.Show));
            selected[day!.Date.Year]++;
            byte[] hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{selectionSeed}|{date:yyyy-MM-dd}"));
            ulong ticket = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(hash) % (3600UL + 7201 + 10802);
            int expectedYear = ticket < 3600 ? 2002 : ticket < 3600 + 7201 ? 2003 : 2004;
            Assert.Equal(expectedYear, day.Date.Year);
        }

        Assert.True(selected[2002] > 0);
        Assert.True(selected[2003] > selected[2002]);
        Assert.True(selected[2004] > selected[2003]);
    }

    [Theory]
    [InlineData("22", "21")]
    [InlineData("21", "20")]
    [InlineData("221", "211")]
    [InlineData("20", "10")]
    [InlineData("210", "200")]
    [InlineData("2211", "21")]
    public async Task Better_library_coverage_outweighs_full_day_and_exact_calendar_date(string better, string worse)
    {
        var follow = Follow();
        follow.StartYear = 1998;
        follow.FollowWeekday = true;
        follow.FollowMonth = false;
        DateOnly preferred = new(2002, 10, 7);
        DateOnly calendarMatch = new(1998, 9, 21);
        foreach (var candidate in new[] { (Date: preferred, Scores: better), (Date: calendarMatch, Scores: worse) })
        {
            string directory = Path.Combine(_directory, candidate.Date.ToString("yyyy"), candidate.Date.ToString("MM"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"{candidate.Date:yyyy-MM-dd}.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    schedule = candidate.Scores.Select((score, index) => new
                    {
                        time = TimeSpan.FromMinutes(index * 30).ToString(@"hh\:mm"),
                        end_time = candidate.Date == calendarMatch && index == candidate.Scores.Length - 1
                            ? "24:00" : TimeSpan.FromMinutes((index + 1) * 30).ToString(@"hh\:mm"),
                        show = score.ToString()
                    })
                }));
        }

        ulong worseWeight = 86400 * 16;
        ulong betterWeight = worseWeight + 1 + (ulong)Math.Max(3600, better.Length * 1800);
        var date = new DateOnly(2026, 9, 21);
        HashSet<DateOnly> selected = [];
        for (int seed = 0; seed < 32; seed++)
        {
            ArchiveDay? day = await ArchiveReader.SelectDayAsync(follow, date, seed.ToString(),
                matchScore: slot => int.Parse(slot.Show));
            byte[] hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{seed}|{date:yyyy-MM-dd}"));
            ulong ticket = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(hash) % (worseWeight + betterWeight);
            Assert.Equal(ticket < worseWeight ? calendarMatch : preferred, day!.Date);
            selected.Add(day.Date);
        }
        Assert.Equal(2, selected.Count);
    }

    [Theory]
    [InlineData("Squidbillies", "Missing", "auto", null, 1)]
    [InlineData("Squidbillies", "Pilot", "auto", null, 2)]
    [InlineData("Squibillies", "EP07", "auto", null, 2)]
    [InlineData("Alias", "Pilot", "mapped", null, 2)]
    [InlineData("Squidbillies", "Pilot", "disabled", null, 0)]
    [InlineData("Alias", "Pilot", "stale", null, 0)]
    [InlineData("Unavailable", "Pilot", "auto", null, 0)]
    [InlineData("Unavailable", "Missing", "disabled", "episode", 2)]
    [InlineData("Unavailable", "Pilot", "auto", "deleted", 0)]
    [InlineData("Squidbillies", "Pilot", "auto", "deleted", 2)]
    [InlineData("Squidbillies", "Pilot / Missing", "auto", null, 1)]
    [InlineData("Squidbillies", "Pilot / EP07", "auto", null, 2)]
    [InlineData("Squidbillies", "", "auto", null, 1)]
    public async Task Engine_weights_slots_using_library_matches_and_configured_overrides(
        string show, string episodeTitle, string mapping, string? assignedId, int expectedScore)
    {
        WriteSchedule(System.Text.Json.JsonSerializer.Serialize(new
        {
            schedule = new[] { new { time = "00:00", end_time = "00:30", show, episode = episodeTitle } }
        }));
        WriteDay(2003, "09-15");
        WriteDay(2004, "09-15");
        File.WriteAllText(Path.Combine(_directory, "2003", "09", "2003-09-15.json"),
            """{"schedule":[{"time":"00:00","end_time":"00:30","show":"Show only","episode":"Missing"}]}""");
        File.WriteAllText(Path.Combine(_directory, "2004", "09", "2004-09-15.json"),
            """{"schedule":[{"time":"00:00","end_time":"00:30","show":"Exact episode","episode":"Pilot"}]}""");
        var follow = Follow();
        follow.Shows["Show only"] = "Squidbillies";
        follow.Shows["Exact episode"] = "Squidbillies";
        if (mapping != "auto")
        {
            follow.Shows[show] = mapping == "disabled" ? null : mapping == "stale" ? "Deleted show" : "Squidbillies";
        }
        if (assignedId is not null)
        {
            follow.Episodes.Add(new() { Show = show, Episode = episodeTitle, MediaId = assignedId });
        }

        MediaItem episode = new()
        {
            Id = "episode", Kind = MediaKind.Episode, SeriesName = "Squidbillies", Title = "Pilot",
            SeasonNumber = 1, EpisodeNumber = 7, Duration = TimeSpan.FromMinutes(22)
        };
        var channel = new ChannelConfig { Id = "test", Name = "Test", Follow = follow };
        var start = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        for (int seed = 0; seed < 16; seed++)
        {
            var config = new SchedulerConfig();
            config.Settings.Seed = seed.ToString();
            ArchiveDay? expected = await ArchiveReader.SelectDayAsync(follow, new DateOnly(2026, 9, 15),
                $"{config.Settings.Seed}|{channel.Id}", matchScore: slot =>
                    slot.Show == show ? expectedScore : slot.Show == "Show only" ? 1 : 2);
            ChannelSchedule schedule = await new SchedulingEngine(new FakeMediaLibrary([episode]), new InMemoryPlaybackStateStore())
                .BuildAsync(config, channel, start, start.AddMinutes(30));
            Assert.Equal(expected!.Date, Assert.Single(schedule.ArchiveDays).Date);
        }
    }

    [Fact]
    public void Show_matching_corrects_typos_but_preserves_explicit_assignments_and_unmapping()
    {
        var matcher = new ArchiveShowMatcher(["Squidbillies", "Squid Game", "Looney Tunes"]);
        var follow = Follow();
        Assert.Equal("Squidbillies", matcher.Resolve(follow, "Squibillies"));
        Assert.Equal("Squidbillies", ArchiveReader.ResolveShow(follow, "Squibillies", ["Squidbillies", "Squid Game"]));
        follow.Shows["Squibillies"] = null;
        Assert.Null(matcher.Resolve(follow, "Squibillies"));
        follow.Shows["Squibillies"] = "Looney Tunes";
        Assert.Equal("Looney Tunes", matcher.Resolve(follow, "Squibillies"));
        follow.Shows.Remove("Squibillies");
        Assert.Equal("Squidbillies", matcher.Resolve(follow, "Squibillies"));
    }

    [Fact]
    public void Show_matching_prefers_exact_matches_and_rejects_ambiguous_or_weak_matches()
    {
        var matcher = new ArchiveShowMatcher(["The Great Escape", "The Great Escapes", "Run", "A-B", "AB"]);
        Assert.Equal("The Great Escape", matcher.Match("The Great Escape"));
        Assert.Null(matcher.Match("The Great Escap"));
        Assert.Null(matcher.Match("Fun"));
        Assert.Null(matcher.Match("Unrelated show"));
        Assert.Null(matcher.Match("A B"));
    }

    [Fact]
    public async Task Engine_uses_the_same_fuzzy_show_match_as_the_editor()
    {
        WriteSchedule("""
            {"schedule":[{"time":"00:00","end_time":"00:30","show":"Squibillies","episode":"Pilot"}]}
            """);
        var channel = new ChannelConfig { Id = "test", Name = "Test", Follow = Follow() };
        var start = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        ChannelSchedule schedule = await new SchedulingEngine(
                new FakeMediaLibrary([Episode("squidbillies", "Squidbillies", "Pilot", 22)]), new InMemoryPlaybackStateStore())
            .BuildAsync(new SchedulerConfig(), channel, start, start.AddMinutes(30));
        Assert.Equal("Squidbillies", schedule.Entries.First().Item?.SeriesName);
        AssertContiguous(schedule);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void Follow_settings_and_explicit_unmapping_survive_yaml_roundtrip(bool weekday, bool month)
    {
        var follow = Follow();
        follow.FollowWeekday = weekday;
        follow.FollowMonth = month;
        follow.Shows["Dragon Ball Z"] = null;
        follow.Shows["Bugs and Daffy"] = "Looney Tunes";
        var config = new SchedulerConfig { Channels = [new ChannelConfig { Id = "test", Name = "Test", Follow = follow }] };
        follow.Episodes.Add(new() { Show = "Bugs and Daffy", Episode = "EP07", MediaId = "library-id" });
        ArchiveFollowConfig restored = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config)).Channels[0].Follow!;
        Assert.True(restored.Enabled);
        Assert.Equal(weekday, restored.FollowWeekday);
        Assert.Equal(month, restored.FollowMonth);
        Assert.Equal(_directory, restored.Directory);
        Assert.Equal(2002, restored.StartYear);
        Assert.Equal(2004, restored.EndYear);
        Assert.True(restored.Shows.ContainsKey("dragon ball z"));
        Assert.Null(ArchiveReader.ResolveShow(restored, "dragon ball z", ["Dragon Ball Z"]));
        Assert.Equal("Looney Tunes", restored.Shows["Bugs and Daffy"]);
        Assert.Equal("library-id", ArchiveEpisodeResolver.Assignment(restored, "bugs AND daffy", " ep07 ")?.MediaId);
        Assert.Null(ArchiveEpisodeResolver.Assignment(restored, "Other show", "EP07"));
    }

    [Fact]
    public void Invalid_year_ranges_and_stale_block_assignments_are_validation_errors()
    {
        var follow = Follow();
        follow.StartYear = 2005;
        follow.Blocks["Toonami '00"] = "Deleted block";
        ConfigValidationResult validation = SchedulerConfigLoader.Validate(new SchedulerConfig
        {
            Channels = [new ChannelConfig { Id = "test", Name = "Test", Follow = follow }]
        });
        Assert.Contains(validation.Errors, error => error.Contains("start_year"));
        Assert.Contains(validation.Errors, error => error.Contains("Deleted block"));
    }

    [Theory]
    [InlineData("The Warriors Decision", "The Warrior's Decision")]
    [InlineData("Final Atonment", "Final Atonement")]
    [InlineData("Trap Happy Porky (edited)", "Trap Happy Porky")]
    public void Episode_matching_handles_punctuation_typos_and_archive_annotations(string archived, string actual)
    {
        MediaItem episode = new() { Id = "match", Title = actual };
        Assert.Same(episode, ArchiveEpisodeMatcher.Match(archived, [episode, new MediaItem { Id = "other", Title = "An Unrelated Episode" }]));
    }

    [Theory]
    [InlineData("Big Bots / Gooey Aliens / Misplaced in Space")]
    [InlineData("Big Bots/Gooey Aliens That Control Your Mind/Misplaced In Space")]
    [InlineData("Big Bot's / Gooey Aliens / Mispalced in Space")]
    public void Several_listed_episodes_match_one_combined_file_even_with_shortened_names(string archived)
    {
        MediaItem combined = Episode("combined", 2, 94, "Big Bots / Gooey Aliens That Control Your Mind / Misplaced in Space");
        MediaItem[] pool = [Episode("other", 2, 97, "Critical Gas / Let's Save the World You Jerk! / Average Joe"), combined,
            Episode("single", 2, 98, "Misplaced in Time")];

        Assert.Equal([combined], ArchiveEpisodeResolver.MatchParts(archived, pool));
    }

    [Fact]
    public void Listed_episodes_mix_combined_files_separate_files_and_misses()
    {
        MediaItem pair = Episode("pair", 1, 1, "Changes / The Big Sister");
        MediaItem single = Episode("single", 1, 2, "Dee Dee's Room");
        MediaItem[] pool = [pair, single, Episode("unrelated", 1, 3, "Golden Diskette / Dexter's Rival")];

        Assert.Equal([pair, single, null], ArchiveEpisodeResolver.MatchParts("Changes / The Big Sister / Dee Dee's Room / Lost in Space", pool));
        Assert.Equal([null, null], ArchiveEpisodeResolver.MatchParts("Changes / Dexter's Rival", pool));
        Assert.Equal([null, null], ArchiveEpisodeResolver.MatchParts("Big Bots / Gooey Aliens", [Episode("combined", 2, 94, "Big Bots / Sticky Aliens")]));
    }

    [Fact]
    public void Generic_segment_names_shared_by_several_combined_files_are_not_guessed()
    {
        MediaItem first = Episode("first", 1, 1, "Rocky & Bullwinkle - Jet Fuel Formula (1) / Fractured Fairy Tales - Rapunzel");
        MediaItem second = Episode("second", 1, 2, "Rocky & Bullwinkle - Jet Fuel Formula (3) / Fractured Fairy Tales - Puss and Boots");
        MediaItem copy = Episode("copy", 1, 1, "Rocky & Bullwinkle - Jet Fuel Formula (1) / Fractured Fairy Tales - Rapunzel");

        Assert.Equal([null, null], ArchiveEpisodeResolver.MatchParts("Rocky & Bullwinkle / Fractured Fairy Tales", [first, second]));
        Assert.Equal([second], ArchiveEpisodeResolver.MatchParts("Rocky & Bullwinkle - Jet Fuel Formula (3) / Fractured Fairy Tales - Puss", [first, second]));
        Assert.Contains(Assert.Single(ArchiveEpisodeResolver.MatchParts("Rocky & Bullwinkle - Jet Fuel Formula / Fractured Fairy Tales - Rapunzel", [first, copy])), new[] { first, copy });
    }

    private static MediaItem Episode(string id, int season, int episode, string title) =>
        new() { Id = id, Title = title, SeriesName = "Dexter's Laboratory", Kind = MediaKind.Episode, SeasonNumber = season, EpisodeNumber = episode, Duration = TimeSpan.FromMinutes(22) };

    [Fact]
    public void Episode_matching_rejects_ambiguous_titles_and_different_part_numbers()
    {
        Assert.Null(ArchiveEpisodeMatcher.Match("The Great Escap", [
            new MediaItem { Id = "first", Title = "The Great Escape" },
            new MediaItem { Id = "second", Title = "The Great Escapes" }]));
        Assert.Null(ArchiveEpisodeMatcher.Match("The Great Escape Part 2", [new MediaItem { Id = "first", Title = "The Great Escape Part 1" }]));
        Assert.Null(ArchiveEpisodeMatcher.Match("Fun", [new MediaItem { Id = "first", Title = "Run" }]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Engine_uses_exact_episode_and_mapped_block_breaks_outside_its_normal_hours(bool followOnly)
    {
        WriteDay(2002, "09-15");
        var follow = Follow();
        follow.Blocks["Toonami '00"] = "Toonami";
        ChannelConfig channel = new()
        {
            Id = "test",
            Name = "Test",
            Follow = follow,
            MaxBreak = TimeSpan.FromSeconds(10),
            BaseRotation = new ContentSelector { Include = [new ContentInclude { Show = "Fallback" }] },
            Blocks = [new BlockConfig
            {
                Name = "Toonami", FollowOnly = followOnly,
                Time = followOnly ? null : TimeSpan.FromHours(12), EndTime = followOnly ? null : TimeSpan.FromHours(13),
                Shows = [new ContentSelector { Include = [new ContentInclude { Show = "Block fallback" }] }],
                CommercialBaseTags = ["Toonami"]
            }]
        };
        MediaItem exact = new() { Id = "exact", Title = "Final Atonement", SeriesName = "Dragon Ball Z", Kind = MediaKind.Episode, Duration = TimeSpan.FromMinutes(22) };
        var library = new FakeMediaLibrary([
            TestMedia.Episode("Dragon Ball Z", 1, 1, TimeSpan.FromMinutes(22)), exact,
            TestMedia.Episode("Fallback", 1, 1, TimeSpan.FromMinutes(22)),
            TestMedia.Filler("ad", TimeSpan.FromSeconds(30), "Commercials", "Toonami")]);
        var engine = new SchedulingEngine(library, new InMemoryPlaybackStateStore());
        var start = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        ChannelSchedule schedule = await engine.BuildAsync(new SchedulerConfig(), channel, start, start.AddMinutes(30));
        Assert.Equal("exact", schedule.Entries.First().Item?.Id);
        Assert.True(schedule.ArchiveFollowEnabled);
        ArchiveGuideDay archiveDay = Assert.Single(schedule.ArchiveDays);
        Assert.Equal(new DateOnly(2002, 9, 15), archiveDay.Date);
        Assert.Equal(start, archiveDay.Start);
        Assert.Equal(start.AddMinutes(30), archiveDay.End);
        Assert.Equal(["Toonami '00", "Special"], Assert.Single(archiveDay.Blocks).Labels);
        ArchiveGuideBlock archiveSlot = Assert.Single(archiveDay.Blocks);
        Assert.Equal("Dragon Ball Z", archiveSlot.Show);
        Assert.Equal("Final Atonement", archiveSlot.Episode);
        Assert.Equal(TimeSpan.Zero, archiveSlot.OriginalStart);
        Assert.Equal(TimeSpan.FromMinutes(30), archiveSlot.OriginalEnd);
        ArchiveGuideBlock clippedSlot = Assert.Single(Assert.Single(ArchiveGuideDay.Clip(
            schedule.ArchiveDays, start.AddMinutes(10), start.AddMinutes(20))).Blocks);
        Assert.Equal(start.AddMinutes(10), clippedSlot.Start);
        Assert.Equal(archiveSlot.Show, clippedSlot.Show);
        Assert.Equal(archiveSlot.Episode, clippedSlot.Episode);
        Assert.Equal(archiveSlot.OriginalStart, clippedSlot.OriginalStart);
        Assert.Equal(archiveSlot.OriginalEnd, clippedSlot.OriginalEnd);
        Assert.All(schedule.Entries, entry => Assert.Equal("Toonami", entry.BlockName));
        Assert.Contains(schedule.Entries, entry => entry.Item?.Id == "ad");
        Assert.Equal(start.AddMinutes(30), schedule.Entries.Last().End);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Missing_or_unmapped_show_uses_mapped_block_rotation(bool unmap, bool followOnly)
    {
        WriteDay(2002, "09-15");
        var follow = Follow();
        follow.Blocks["Toonami '00"] = "Toonami";
        if (unmap) follow.Shows["Dragon Ball Z"] = null;
        ChannelConfig channel = new()
        {
            Id = "test",
            Name = "Test",
            Follow = follow,
            PadToNearest = TimeSpan.FromMinutes(30),
            BaseRotation = new ContentSelector { Include = [new ContentInclude { Show = "Base" }] },
            Blocks = [new BlockConfig
            {
                Name = "Toonami", FollowOnly = followOnly,
                Time = followOnly ? null : TimeSpan.FromHours(12), EndTime = followOnly ? null : TimeSpan.FromHours(13),
                Shows = [new ContentSelector { Include = [new ContentInclude { Show = "Block fallback" }] }]
            }]
        };
        List<MediaItem> items = [TestMedia.Episode("Block fallback", 1, 1, TimeSpan.FromMinutes(22)), TestMedia.Episode("Base", 1, 1, TimeSpan.FromMinutes(22))];
        if (unmap) items.Add(TestMedia.Episode("Dragon Ball Z", 1, 1, TimeSpan.FromMinutes(22)));
        var engine = new SchedulingEngine(new FakeMediaLibrary(items), new InMemoryPlaybackStateStore());
        var start = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        ChannelSchedule schedule = await engine.BuildAsync(new SchedulerConfig(), channel, start, start.AddMinutes(30));
        Assert.Equal("Block fallback", schedule.Entries.First().Item?.SeriesName);
        Assert.Equal(ProgramOrigin.Normal, schedule.Entries.First().ProgramOrigin);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Follow_only_block_is_ignored_when_follow_is_disabled_or_archive_is_missing(bool enabled)
    {
        var follow = Follow();
        follow.Enabled = enabled;
        follow.Blocks["Toonami '00"] = "Archive settings";
        var channel = new ChannelConfig
        {
            Id = "test", Name = "Test", Follow = follow,
            BaseRotation = new ContentSelector { Include = [new ContentInclude { Show = "Base" }] },
            Blocks = [new BlockConfig
            {
                Name = "Archive settings", FollowOnly = true,
                Time = TimeSpan.Zero, EndTime = TimeSpan.FromHours(1),
                Shows = [new ContentSelector { Include = [new ContentInclude { Show = "Archive only" }] }]
            }]
        };
        if (!enabled) WriteDay(2002, "09-15");
        var engine = new SchedulingEngine(new FakeMediaLibrary([
            TestMedia.Episode("Base", 1, 1, TimeSpan.FromMinutes(30)),
            TestMedia.Episode("Archive only", 1, 1, TimeSpan.FromMinutes(30))]), new InMemoryPlaybackStateStore());
        var start = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        ChannelSchedule schedule = await engine.BuildAsync(new SchedulerConfig(), channel, start, start.AddMinutes(30));
        Assert.Equal("Base", schedule.Entries.First().Item?.SeriesName);
        Assert.DoesNotContain(schedule.Entries, entry => entry.BlockName == "Archive settings");
    }

    private void WriteDay(int year, string monthDay)
    {
        string directory = Path.Combine(_directory, year.ToString(), monthDay[..2]);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{year}-{monthDay}.json"), """
            {"schedule":[{"time":"00:00","end_time":"00:30","show":"Dragon Ball Z","episode":"Final Atonement","blocks":["Toonami '00","Special"]}]}
            """);
    }

    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public async Task Segment_titles_match_fuzzily_and_the_next_slot_starts_on_time_with_midrolls()
    {
        WriteSchedule("""
            {"schedule":[
                {"time":"00:00","end_time":"00:30","show":"Shorts","episode":"A Corny Concerto / Ali Baba Buny"},
                {"time":"00:30","end_time":"01:00","show":"Next","episode":"The Next Episode"}]}
            """);
        var channel = new ChannelConfig
        {
            Id = "test",
            Name = "Test",
            Follow = Follow(),
            MaxBreak = TimeSpan.FromSeconds(5),
            MidRoll = new MidRollConfig { Mode = MidRollMode.Auto },
            Anchors = [new AnchorConfig { Time = TimeSpan.Zero, Show = new ContentSelector { Include = [new ContentInclude { Show = "Unwanted anchor" }] } }]
        };
        List<MediaItem> items = [Episode("first", "Shorts", "A Corny Concerto", 7), Episode("second", "Shorts", "Ali Baba Bunny", 7),
            Episode("next", "Next", "The Next Episode", 22), Episode("anchor", "Unwanted anchor", "Anchor", 22)];
        var start = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        ChannelSchedule schedule = await new SchedulingEngine(new FakeMediaLibrary(items), new InMemoryPlaybackStateStore())
            .BuildAsync(new SchedulerConfig(), channel, start, start.AddHours(1));
        Assert.Equal(["first", "second", "next"], schedule.Entries.Where(entry => entry.Item?.Kind == MediaKind.Episode).Select(entry => entry.Item!.Id).Distinct());
        Assert.Equal(start.AddMinutes(30), schedule.Entries.First(entry => entry.Item?.Id == "next").Start);
        Assert.All(schedule.Entries.Where(entry => entry.Kind == ScheduleEntryKind.Program),
            entry => Assert.Equal(ProgramOrigin.Archive, entry.ProgramOrigin));
        AssertContiguous(schedule);
    }

    [Theory]
    [InlineData(false, 15)]
    [InlineData(true, 16)]
    public async Task Disabled_follow_or_missing_day_uses_the_regular_rotation(bool enabled, int day)
    {
        WriteDay(2002, "09-15");
        var follow = Follow();
        follow.Enabled = enabled;
        var channel = new ChannelConfig
        {
            Id = "test",
            Name = "Test",
            Follow = follow,
            PadToNearest = TimeSpan.FromMinutes(30),
            BaseRotation = new ContentSelector { Include = [new ContentInclude { Show = "Base" }] }
        };
        var start = new DateTimeOffset(2026, 9, day, 0, 0, 0, TimeSpan.Zero);
        ChannelSchedule schedule = await new SchedulingEngine(new FakeMediaLibrary([Episode("base", "Base", "Base", 22)]), new InMemoryPlaybackStateStore())
            .BuildAsync(new SchedulerConfig(), channel, start, start.AddMinutes(30));
        Assert.Equal("base", schedule.Entries.First().Item?.Id);
        AssertContiguous(schedule);
    }

    [Fact]
    public async Task Archive_uses_channel_local_date_and_does_not_overrun_a_slot()
    {
        WriteSchedule("""
            {"schedule":[{"time":"23:30","end_time":"00:00","show":"Dragon Ball Z","episode":"Final Atonement"}]}
            """);
        var channel = new ChannelConfig
        {
            Id = "test",
            Name = "Test",
            Follow = Follow(),
            TimeZone = "America/New_York",
            BaseRotation = new ContentSelector { Include = [new ContentInclude { Show = "Base" }] },
            PadToNearest = TimeSpan.FromMinutes(30)
        };
        var start = new DateTimeOffset(2026, 9, 16, 3, 30, 0, TimeSpan.Zero);
        ChannelSchedule schedule = await new SchedulingEngine(new FakeMediaLibrary([
            Episode("too-long", "Dragon Ball Z", "Final Atonement", 40), Episode("base", "Base", "Base", 22)]), new InMemoryPlaybackStateStore())
            .BuildAsync(new SchedulerConfig(), channel, start, start.AddMinutes(30));
        Assert.Contains(schedule.Diagnostics, diagnostic => diagnostic.Message.Contains("follows 2002-09-15"));
        Assert.Equal("base", schedule.Entries.First().Item?.Id);
        AssertContiguous(schedule);
    }

    [Fact]
    public async Task Leap_day_only_selects_existing_leap_years()
    {
        WriteDay(2004, "02-29");
        ArchiveDay? day = await ArchiveReader.SelectDayAsync(Follow(), new DateOnly(2028, 2, 29), "channel");
        Assert.Equal(new DateOnly(2004, 2, 29), day?.Date);
    }

    private void WriteSchedule(string json)
    {
        string directory = Path.Combine(_directory, "2002", "09");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "2002-09-15.json"), json);
    }

    private static MediaItem Episode(string id, string show, string title, int minutes) => new()
    {
        Id = id,
        SeriesName = show,
        Title = title,
        Kind = MediaKind.Episode,
        Duration = TimeSpan.FromMinutes(minutes)
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_episode_assignment_overrides_automatic_match_even_without_an_available_archive_show(bool missingShow)
    {
        WriteDay(2002, "09-15");
        var follow = Follow();
        follow.Episodes.Add(new() { Show = "Dragon Ball Z", Episode = "Final Atonement", MediaId = "assigned" });
        List<MediaItem> items = [Episode("assigned", "Other Show", "Chosen episode", 22)];
        if (!missingShow) items.Add(Episode("automatic", "Dragon Ball Z", "Final Atonement", 22));
        var channel = new ChannelConfig { Id = "test", Name = "Test", Follow = follow };
        var start = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var schedule = await new SchedulingEngine(new FakeMediaLibrary(items), new InMemoryPlaybackStateStore())
            .BuildAsync(new SchedulerConfig(), channel, start, start.AddMinutes(30));
        Assert.Equal("assigned", schedule.Entries.First().Item?.Id);
        Assert.Equal(ProgramOrigin.Archive, schedule.Entries.First().ProgramOrigin);
        AssertContiguous(schedule);
    }

    [Theory]
    [InlineData("deleted", 22)]
    [InlineData("assigned", 40)]
    public async Task Unavailable_or_overlong_assignment_uses_a_fitting_show_episode(string assignedId, int minutes)
    {
        WriteDay(2002, "09-15");
        var follow = Follow();
        follow.Episodes.Add(new() { Show = "Dragon Ball Z", Episode = "Final Atonement", MediaId = assignedId });
        var channel = new ChannelConfig { Id = "test", Name = "Test", Follow = follow };
        List<MediaItem> items = [Episode("fits", "Dragon Ball Z", "Another story", 22)];
        if (assignedId != "deleted") items.Add(Episode("assigned", "Dragon Ball Z", "Too long", minutes));
        var start = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var schedule = await new SchedulingEngine(new FakeMediaLibrary(items), new InMemoryPlaybackStateStore())
            .BuildAsync(new SchedulerConfig(), channel, start, start.AddMinutes(30));
        Assert.Equal("fits", schedule.Entries.First().Item?.Id);
        Assert.Equal(ProgramOrigin.ArchiveRandom, schedule.Entries.First().ProgramOrigin);
        AssertContiguous(schedule);
    }

    [Fact]
    public async Task Unmatched_episode_uses_seeded_random_same_show_fallback()
    {
        WriteDay(2002, "09-15");
        var channel = new ChannelConfig { Id = "test", Name = "Test", Follow = Follow() };
        var items = Enumerable.Range(1, 12).Select(number => Episode($"episode-{number}", "Dragon Ball Z", $"Story {number}", 22)).ToArray();
        var start = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        HashSet<string> picks = [];
        for (int seed = 0; seed < 12; seed++)
        {
            var config = new SchedulerConfig();
            config.Settings.Seed = seed.ToString();
            var schedule = await new SchedulingEngine(new FakeMediaLibrary(items), new InMemoryPlaybackStateStore())
                .BuildAsync(config, channel, start, start.AddMinutes(30));
            var repeated = await new SchedulingEngine(new FakeMediaLibrary(items), new InMemoryPlaybackStateStore())
                .BuildAsync(config, channel, start, start.AddMinutes(30));
            MediaItem chosen = schedule.Entries.First().Item!;
            Assert.Equal("Dragon Ball Z", chosen.SeriesName);
            Assert.Equal(ProgramOrigin.ArchiveRandom, schedule.Entries.First().ProgramOrigin);
            Assert.Equal(chosen.Id, repeated.Entries.First().Item?.Id);
            picks.Add(chosen.Id);
        }
        Assert.True(picks.Count > 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mixed_archive_slots_preserve_matched_and_random_episode_origins(bool stackToFill)
    {
        WriteSchedule(stackToFill
            ? """{"schedule":[{"time":"00:00","end_time":"00:30","show":"Shorts","episode":"Pilot"}]}"""
            : """{"schedule":[{"time":"00:00","end_time":"00:30","show":"Shorts","episode":"Pilot / Missing"}]}""");
        var channel = new ChannelConfig { Id = "test", Name = "Test", Follow = Follow(), StackToFill = stackToFill,
            MidRoll = new MidRollConfig { Mode = MidRollMode.Auto } };
        var start = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var schedule = await new SchedulingEngine(new FakeMediaLibrary([
            Episode("matched", "Shorts", "Pilot", 7), Episode("random", "Shorts", "Another story", 7)]), new InMemoryPlaybackStateStore())
            .BuildAsync(new SchedulerConfig(), channel, start, start.AddMinutes(30));
        Assert.Contains(schedule.Entries, entry => entry.Item?.Id == "matched");
        Assert.Contains(schedule.Entries, entry => entry.Item?.Id == "random");
        Assert.All(schedule.Entries.Where(entry => entry.Item?.Id == "matched"),
            entry => Assert.Equal(ProgramOrigin.Archive, entry.ProgramOrigin));
        Assert.All(schedule.Entries.Where(entry => entry.Item?.Id == "random"),
            entry => Assert.Equal(ProgramOrigin.ArchiveRandom, entry.ProgramOrigin));
        AssertContiguous(schedule);
    }

    [Fact]
    public void Episode_review_uses_numbered_segment_and_show_matching_but_keeps_missing_entries()
    {
        var follow = Follow();
        follow.Shows["Alias"] = "Show";
        MediaItem numbered = new() { Id = "numbered", Kind = MediaKind.Episode, SeriesName = "Show", Title = "Pilot", SeasonNumber = 1, EpisodeNumber = 7 };
        var rows = SchedularrTV.Service.Editor.ArchiveEpisodeReview.Unmatched(follow,
            [new("Alias", "EP07", 4), new("Alias", "Pilot / Ending", 3), new("Alias", "Pilot / Missing", 2),
             new("Unavailable", "Pilot", 1), new("Alias", "", 1)],
            [numbered, Episode("ending", "Show", "Ending", 7)]);
        Assert.Equal(3, rows.Count);
        Assert.Equal("Show", rows[0].LibraryShow);
        Assert.Null(rows[1].LibraryShow);
        follow.Shows["Alias"] = null;
        Assert.Null(Assert.Single(SchedularrTV.Service.Editor.ArchiveEpisodeReview.Unmatched(follow,
            [new("Alias", "EP07", 1)], [numbered])).LibraryShow);
    }

    private static void AssertContiguous(ChannelSchedule schedule)
    {
        DateTimeOffset cursor = schedule.Start;
        foreach (ScheduleEntry entry in schedule.Entries)
        {
            Assert.Equal(cursor, entry.Start);
            cursor = entry.End;
        }
        Assert.Equal(schedule.End, cursor);
    }
}