using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class SchedulingEngineTests
{
    private static readonly DateTimeOffset Day = new(2026, 1, 12, 0, 0, 0, TimeSpan.Zero); // a Monday

    private static List<MediaItem> Library()
    {
        List<MediaItem> items = [];

        for (int e = 1; e <= 20; e++)
        {
            items.Add(TestMedia.Episode("SpongeBob SquarePants", 1, e, TimeSpan.FromMinutes(22), collection: "Kids"));
            items.Add(TestMedia.Episode("Clone High", 1, e, TimeSpan.FromMinutes(22), collection: "Adult Swim at Home"));
            items.Add(TestMedia.Episode("Aqua Teen", 1, e, TimeSpan.FromMinutes(11), collection: "Adult Swim at Home"));
        }

        items.Add(TestMedia.Episode("Xmas Special", 1, 1, TimeSpan.FromMinutes(22), collection: "Adult Swim at Home", title: "A Very Xmas Episode", overview: "It is christmas."));

        for (int i = 1; i <= 12; i++)
        {
            items.Add(TestMedia.Filler($"short-{i}", TimeSpan.FromMinutes(5), "Shorts"));
        }

        foreach (int seconds in (int[])[10, 15, 20, 30, 45, 60])
        {
            for (int i = 0; i < 6; i++)
            {
                items.Add(TestMedia.Filler($"ad-{seconds}-{i}", TimeSpan.FromSeconds(seconds), "Commercials", "Adult Swim", "Any Time"));
            }
        }

        // Tier 3 filler is deliberately long so it always has to be trimmed to close a gap.
        for (int i = 0; i < 4; i++)
        {
            items.Add(TestMedia.Bumper($"bumper-{i}", TimeSpan.FromMinutes(3), tags: ["Bumpers"]));
        }

        // Promos live in the channel's brand folder, same as the ads.
        items.Add(TestMedia.Promo("promo-spongebob", TimeSpan.FromSeconds(15), "SpongeBob SquarePants", "later_today", "Adult Swim"));
        items.Add(TestMedia.Promo("upnext-spongebob", TimeSpan.FromSeconds(10), "SpongeBob SquarePants", "up_next", "Adult Swim"));
        items.Add(TestMedia.Promo("upnext-clonehigh", TimeSpan.FromSeconds(10), "Clone High", promoType: null, "Up Next", "Adult Swim"));
        items.Add(TestMedia.Promo("nowplaying-spongebob", TimeSpan.FromSeconds(5), "SpongeBob SquarePants", "now_playing", "Adult Swim"));
        items.Add(TestMedia.Promo("signoff-clonehigh", TimeSpan.FromSeconds(5), "Clone High", "this_has_been", "Adult Swim"));
        items.Add(TestMedia.PromoAt("upnext-spongebob-4pm", TimeSpan.FromSeconds(10), "SpongeBob SquarePants", "up_next", "4pm", "Adult Swim"));
        items.Add(TestMedia.PromoAt("upnext-spongebob-5pm", TimeSpan.FromSeconds(10), "SpongeBob SquarePants", "up_next", "5pm", "Adult Swim"));
        items.Add(TestMedia.Promo("watching-clonehigh", TimeSpan.FromSeconds(5), "Clone High", "youre_watching", "Adult Swim"));
        items.Add(TestMedia.Promo("backtoshow-clonehigh", TimeSpan.FromSeconds(5), "Clone High", "back_to_the_show", "Adult Swim"));
        return items;
    }

    private static SchedulerConfig Config(ChannelConfig channel) => new()
    {
        Settings = new SchedulerSettings
        {
            TimeZone = "UTC",
            Interstitials = new InterstitialsConfig
            {
                Commercials = new CommercialsConfig
                {
                    BaseQuery = "tag:\"Commercials\"",
                    Tolerance = TimeSpan.FromMilliseconds(500),
                    Granularity = TimeSpan.FromMilliseconds(250),
                    MaxRepeatsPerBreak = 4
                },
                Filler = new FillerTierConfig { Query = "tag:'Bumpers'", Trimmable = true }
            }
        },
        GlobalCollections = new Dictionary<string, ContentSelector>(StringComparer.OrdinalIgnoreCase)
        {
            ["adult_swim_shows"] = new() { Query = "collection:'Adult Swim at Home'", Order = PlaybackOrder.Shuffle },
            ["shorts"] = new() { Query = "tag:'Shorts'", Order = PlaybackOrder.Shuffle }
        },
        Channels = [channel]
    };

    private static async Task<ChannelSchedule> BuildAsync(ChannelConfig channel, TimeSpan? length = null)
    {
        var library = new FakeMediaLibrary(Library());
        var engine = new SchedulingEngine(library, new InMemoryPlaybackStateStore());
        SchedulerConfig config = Config(channel);

        return await engine.BuildAsync(config, channel, Day, Day + (length ?? TimeSpan.FromHours(24)));
    }

    private static ChannelConfig BaseChannel() => new()
    {
        Id = "cn",
        Name = "Cartoon Network",
        Number = 1,
        TimeZone = "UTC",
        CommercialBaseTags = ["Adult Swim"],
        AutoTemporalTags = false,
        BaseRotation = new ContentSelector { Collection = "adult_swim_shows", Order = PlaybackOrder.Shuffle }
    };

    [Fact]
    public async Task Produces_a_contiguous_gap_free_timeline()
    {
        ChannelSchedule schedule = await BuildAsync(BaseChannel());

        Assert.NotEmpty(schedule.Entries);

        DateTimeOffset cursor = schedule.Start;
        foreach (ScheduleEntry entry in schedule.Entries)
        {
            Assert.Equal(cursor, entry.Start);
            Assert.True(entry.Duration > TimeSpan.Zero);
            cursor = entry.End;
        }

        Assert.Equal(schedule.End, cursor);
    }

    [Fact]
    public async Task Base_rotation_fills_the_day_when_no_block_is_active()
    {
        ChannelSchedule schedule = await BuildAsync(BaseChannel());

        Assert.Contains(schedule.Entries, e => e.Kind == ScheduleEntryKind.Program && e.Source == "base rotation");
        Assert.DoesNotContain(schedule.Entries, e => e.Kind == ScheduleEntryKind.Padding && e.Duration > TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task An_anchor_slot_starts_exactly_on_time()
    {
        ChannelConfig channel = BaseChannel();
        channel.Blocks =
        [
            new BlockConfig
            {
                Name = "SpongeBob Morning Anchor",
                Time = TimeSpan.FromHours(8),
                Days = [DayOfWeek.Monday],
                Shows = [new ContentSelector { Query = "show:'SpongeBob SquarePants'", Order = PlaybackOrder.Chronological }]
            }
        ];

        ChannelSchedule schedule = await BuildAsync(channel);

        ScheduleEntry anchored = Assert.Single(schedule.Entries, e => e.Start == Day.AddHours(8));
        Assert.Equal("SpongeBob SquarePants", anchored.Item?.SeriesName);
        Assert.Equal("SpongeBob Morning Anchor", anchored.Source);
        Assert.Equal(ProgramOrigin.Anchor, anchored.ProgramOrigin);
    }

    [Fact]
    public async Task A_night_block_overrides_the_base_rotation_inside_its_window()
    {
        ChannelConfig channel = BaseChannel();
        channel.BaseRotation = new ContentSelector { Query = "show:'SpongeBob SquarePants'", Order = PlaybackOrder.Chronological };
        channel.Blocks =
        [
            new BlockConfig
            {
                Name = "Teletoon at Night",
                Time = TimeSpan.FromHours(21),
                EndTime = TimeSpan.FromHours(23),
                Recurrence = Recurrence.Daily,
                Shows = [new ContentSelector { Query = "show:'Aqua Teen'", Order = PlaybackOrder.Shuffle }]
            }
        ];

        ChannelSchedule schedule = await BuildAsync(channel);

        List<ScheduleEntry> inBlock = schedule.Entries
            .Where(e => e.Start >= Day.AddHours(21) && e.End <= Day.AddHours(23) && e.Kind == ScheduleEntryKind.Program)
            .ToList();

        Assert.NotEmpty(inBlock);
        Assert.All(inBlock, e => Assert.Equal("Aqua Teen", e.Item?.SeriesName));
        Assert.All(inBlock, e => Assert.Equal("Teletoon at Night", e.BlockName));
    }

    [Fact]
    public async Task A_nested_block_anchor_wins_over_the_block_rotation()
    {
        ChannelConfig channel = BaseChannel();
        channel.Blocks =
        [
            new BlockConfig
            {
                Name = "Teletoon at Night",
                Time = TimeSpan.FromHours(21),
                EndTime = TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59),
                Recurrence = Recurrence.Daily,
                Anchors =
                [
                    new AnchorConfig
                    {
                        Name = "Clone High",
                        Time = TimeSpan.FromHours(22),
                        Show = new ContentSelector { Query = "show:'Clone High'", Order = PlaybackOrder.Chronological }
                    }
                ],
                Shows = [new ContentSelector { Query = "show:'Aqua Teen'", Order = PlaybackOrder.Shuffle }]
            }
        ];

        ChannelSchedule schedule = await BuildAsync(channel);

        ScheduleEntry anchored = Assert.Single(schedule.Entries, e => e.Start == Day.AddHours(22));
        Assert.Equal("Clone High", anchored.Item?.SeriesName);
    }

    [Fact]
    public async Task Gaps_are_packed_with_interstitials_then_commercials()
    {
        ChannelConfig channel = BaseChannel();
        channel.BaseRotation = new ContentSelector { Query = "show:'SpongeBob SquarePants'", Order = PlaybackOrder.Chronological };
        channel.InterstitialShows = new ContentSelector { Collection = "shorts", Order = PlaybackOrder.Shuffle };
        channel.Blocks =
        [
            new BlockConfig
            {
                Name = "Anchor",
                Time = TimeSpan.FromHours(1),
                Days = [DayOfWeek.Monday],
                Shows = [new ContentSelector { Query = "show:'Clone High'", Order = PlaybackOrder.Chronological }]
            }
        ];

        ChannelSchedule schedule = await BuildAsync(channel, TimeSpan.FromHours(4));

        List<ScheduleEntry> beforeAnchor = schedule.Entries.Where(e => e.End <= Day.AddHours(1)).ToList();

        Assert.Contains(beforeAnchor, e => e.Kind == ScheduleEntryKind.Interstitial);
        Assert.Contains(beforeAnchor, e => e.Kind == ScheduleEntryKind.Commercial);
        Assert.Equal(Day.AddHours(1), beforeAnchor[^1].End);
    }

    [Fact]
    public async Task Lookahead_injects_a_promo_for_a_show_airing_later_today()
    {
        ChannelConfig channel = BaseChannel();
        channel.BaseRotation = new ContentSelector { Query = "show:'Clone High'", Order = PlaybackOrder.Chronological };
        channel.PadToNearest = TimeSpan.FromMinutes(30);
        channel.Promos = new PromoConfig { Enabled = true, BaseQuery = "tag:'Promo'", Lookahead = TimeSpan.FromHours(12), MaxPerBreak = 1 };
        channel.Blocks =
        [
            new BlockConfig
            {
                Name = "SpongeBob at 16:00",
                Time = TimeSpan.FromHours(16),
                Days = [DayOfWeek.Monday],
                Shows = [new ContentSelector { Query = "show:'SpongeBob SquarePants'", Order = PlaybackOrder.Chronological }]
            }
        ];

        ChannelSchedule schedule = await BuildAsync(channel);

        List<ScheduleEntry> promos = schedule.Entries.Where(e => e.Kind == ScheduleEntryKind.Promo).ToList();

        Assert.NotEmpty(promos);
        Assert.All(promos, p => Assert.Equal("SpongeBob SquarePants", p.PromoTargetId));

        // A "later_today" promo must land more than three hours out; nearer breaks use up_next/clock promos.
        Assert.Contains(promos, p => Day.AddHours(16) - p.Start > TimeSpan.FromHours(3));
    }

    [Fact]
    public async Task Smart_search_filters_a_block_pool_down_to_matching_episodes()
    {
        ChannelConfig channel = BaseChannel();
        channel.Blocks =
        [
            new BlockConfig
            {
                Name = "Christmas Marathon",
                Time = TimeSpan.FromHours(10),
                EndTime = TimeSpan.FromHours(12),
                Schedule = new BlockSchedule { Date = "01-12", Repeat = RepeatModifier.Yearly },
                Shows =
                [
                    new ContentSelector
                    {
                        Collection = "adult_swim_shows",
                        Filter = "title CONTAINS 'xmas' OR plot CONTAINS 'christmas'",
                        Order = PlaybackOrder.Shuffle
                    }
                ]
            }
        ];

        ChannelSchedule schedule = await BuildAsync(channel);

        List<ScheduleEntry> inBlock = schedule.Entries
            .Where(e => e.BlockName == "Christmas Marathon" && e.Kind == ScheduleEntryKind.Program)
            .ToList();

        Assert.NotEmpty(inBlock);
        Assert.All(inBlock, e => Assert.Equal("Xmas Special", e.Item?.SeriesName));
    }

    [Fact]
    public async Task Slot_padding_starts_every_programme_on_a_clock_boundary()
    {
        ChannelConfig channel = BaseChannel();
        channel.BaseRotation = new ContentSelector { Query = "show:'Clone High'", Order = PlaybackOrder.Chronological };
        channel.PadToNearest = TimeSpan.FromMinutes(30);

        ChannelSchedule schedule = await BuildAsync(channel, TimeSpan.FromHours(6));

        List<ScheduleEntry> programs = schedule.Entries.Where(e => e.Kind == ScheduleEntryKind.Program).ToList();

        Assert.Equal(12, programs.Count);
        Assert.All(programs, p => Assert.Equal(0, (p.Start - schedule.Start).Ticks % TimeSpan.FromMinutes(30).Ticks));
        Assert.Contains(schedule.Entries, e => e.Kind == ScheduleEntryKind.Commercial);
    }

    [Fact]
    public async Task An_up_next_promo_runs_last_in_the_break_before_its_show()
    {
        ChannelConfig channel = BaseChannel();
        channel.BaseRotation = new ContentSelector { Query = "show:'Clone High'", Order = PlaybackOrder.Chronological };
        channel.Promos = new PromoConfig
        {
            Enabled = true,
            BaseQuery = "tag:'Promo'",
            MaxPerBreak = 0,
            // Pinned to the end of the break. The default (any break) puts it at a seeded spot among the ads, so
            // this test only passed by luck of the default seed, which changed with the rename.
            UpNext = new PromoSpotConfig { Placement = UpNextPlacement.BeforeShow },
            NowPlaying = new PromoSpotConfig { Enabled = false }
        };
        channel.Blocks =
        [
            new BlockConfig
            {
                Name = "SpongeBob at 16:00",
                Time = TimeSpan.FromHours(16),
                Days = [DayOfWeek.Monday],
                Shows = [new ContentSelector { Query = "show:'SpongeBob SquarePants'", Order = PlaybackOrder.Chronological }]
            }
        ];

        ChannelSchedule schedule = await BuildAsync(channel);

        ScheduleEntry promo = Assert.Single(schedule.Entries, e => e.Kind == ScheduleEntryKind.UpNextPromo);

        Assert.Equal("SpongeBob SquarePants", promo.PromoTargetId);
        Assert.Equal(Day.AddHours(16), promo.End);

        ScheduleEntry show = Assert.Single(schedule.Entries, e => e.Start == Day.AddHours(16));
        Assert.Equal("SpongeBob SquarePants", show.Item?.SeriesName);
    }

    [Fact]
    public async Task An_up_next_promo_is_suppressed_when_the_same_series_continues()
    {
        ChannelConfig channel = BaseChannel();
        channel.BaseRotation = new ContentSelector { Query = "show:'Clone High'", Order = PlaybackOrder.Chronological };
        channel.PadToNearest = TimeSpan.FromMinutes(30);
        channel.Promos = new PromoConfig { Enabled = true, BaseQuery = "tag:'Promo'", MaxPerBreak = 0 };

        ChannelSchedule schedule = await BuildAsync(channel, TimeSpan.FromHours(4));

        // "Up Next" promo assets exist for Clone High, but the show never changes.
        Assert.DoesNotContain(schedule.Entries, e => e.Kind == ScheduleEntryKind.UpNextPromo);
    }

    [Fact]
    public async Task A_now_playing_promo_runs_after_the_up_next_promo()
    {
        ChannelConfig channel = SpongeBobBlockChannel();
        channel.Promos!.UpNext.Placement = UpNextPlacement.BeforeShow;
        ChannelSchedule schedule = await BuildAsync(channel);

        ScheduleEntry upNext = Assert.Single(schedule.Entries, e => e.Kind == ScheduleEntryKind.UpNextPromo);
        ScheduleEntry nowPlaying = Assert.Single(schedule.Entries, e => e.Kind == ScheduleEntryKind.NowPlayingPromo);

        Assert.Equal("nowplaying-spongebob", nowPlaying.Item?.Id);
        Assert.Equal(upNext.End, nowPlaying.Start);
        Assert.Equal(Day.AddHours(16), nowPlaying.End);
    }

    [Fact]
    public async Task By_default_up_next_airs_among_the_ads_while_now_playing_stays_last()
    {
        ChannelSchedule schedule = await BuildAsync(SpongeBobBlockChannel());

        ScheduleEntry upNext = Assert.Single(schedule.Entries, e => e.Kind == ScheduleEntryKind.UpNextPromo);
        ScheduleEntry nowPlaying = Assert.Single(schedule.Entries, e => e.Kind == ScheduleEntryKind.NowPlayingPromo);
        ScheduleEntry before = schedule.Entries.Last(e => e.Kind == ScheduleEntryKind.Program && e.End <= upNext.Start);

        Assert.Equal("SpongeBob SquarePants", upNext.PromoTargetId);
        Assert.Equal(Day.AddHours(16), nowPlaying.End);
        Assert.True(upNext.End <= nowPlaying.Start);
        Assert.DoesNotContain(schedule.Entries, e => e.Kind == ScheduleEntryKind.Program && e.Start >= upNext.End && e.Start < nowPlaying.Start);
        Assert.True(before.End <= upNext.Start);
        Assert.All(schedule.Entries.Zip(schedule.Entries.Skip(1)), pair => Assert.Equal(pair.First.End, pair.Second.Start));
    }

    [Fact]
    public async Task A_sign_off_promo_opens_the_break_after_its_show()
    {
        ChannelSchedule schedule = await BuildAsync(SignOffChannel(), TimeSpan.FromHours(4));

        List<ScheduleEntry> signOffs = schedule.Entries
            .Where(e => e.Kind == ScheduleEntryKind.ThisHasBeenPromo)
            .ToList();

        Assert.NotEmpty(signOffs);
        Assert.All(signOffs, s => Assert.Equal("signoff-clonehigh", s.Item?.Id));

        // It has to open the break, so the programme it signs off ends exactly where it starts.
        Assert.All(signOffs, s =>
        {
            ScheduleEntry before = schedule.Entries.Last(e => e.End <= s.Start);
            Assert.Equal(ScheduleEntryKind.Program, before.Kind);
            Assert.Equal("Clone High", before.Item?.SeriesName);
            Assert.Equal(before.End, s.Start);
        });
    }

    [Fact]
    public async Task A_stacked_slot_signs_off_once_rather_than_between_every_episode()
    {
        ChannelConfig channel = SignOffChannel();
        channel.PadToNearest = TimeSpan.FromHours(1);
        channel.StackToFill = true;

        ChannelSchedule schedule = await BuildAsync(channel, TimeSpan.FromHours(4));

        int episodes = schedule.Entries.Count(e => e.Kind == ScheduleEntryKind.Program);
        int signOffs = schedule.Entries.Count(e => e.Kind == ScheduleEntryKind.ThisHasBeenPromo);

        Assert.True(signOffs > 0, "the slot should still sign off");
        Assert.True(
            episodes > signOffs,
            $"expected fewer sign-offs than episodes, got {signOffs} for {episodes} episodes");
    }

    [Fact]
    public async Task A_sign_off_promo_is_suppressed_when_the_same_series_plays_next()
    {
        ChannelConfig channel = SignOffChannel();
        channel.Promos!.ThisHasBeen.SkipWhenSameSeries = true;

        ChannelSchedule schedule = await BuildAsync(channel, TimeSpan.FromHours(4));

        // Clone High runs back to back, so signing it off would be announcing the show still airing.
        Assert.DoesNotContain(schedule.Entries, e => e.Kind == ScheduleEntryKind.ThisHasBeenPromo);
    }

    [Fact]
    public async Task A_mid_roll_break_is_bookended_by_the_same_show_promos()
    {
        ChannelConfig channel = SignOffChannel();
        channel.MidRoll = new MidRollConfig
        {
            Mode = MidRollMode.Auto,
            TargetSegment = TimeSpan.FromMinutes(10),
            MinSegment = TimeSpan.FromMinutes(5),
            MinBreak = TimeSpan.FromSeconds(20)
        };

        ChannelSchedule schedule = await BuildAsync(channel, TimeSpan.FromHours(4));

        ScheduleEntry watching = schedule.Entries.First(e => e.Kind == ScheduleEntryKind.YoureWatchingPromo);
        ScheduleEntry back = schedule.Entries.First(e => e.Kind == ScheduleEntryKind.BackToTheShowPromo);

        Assert.Equal("watching-clonehigh", watching.Item?.Id);
        Assert.Equal("backtoshow-clonehigh", back.Item?.Id);

        // The show is interrupted by the first and resumes straight after the second.
        ScheduleEntry before = schedule.Entries.Last(e => e.End <= watching.Start);
        Assert.Equal(ScheduleEntryKind.Program, before.Kind);
        Assert.Equal("Clone High", before.Item?.SeriesName);

        ScheduleEntry after = schedule.Entries.First(e => e.Start >= back.End && e.Kind == ScheduleEntryKind.Program);
        Assert.Equal("Clone High", after.Item?.SeriesName);
        Assert.Equal(back.End, after.Start);
    }

    [Fact]
    public async Task A_slot_ending_break_uses_the_handover_promos_not_the_same_show_ones()
    {
        ChannelSchedule schedule = await BuildAsync(SignOffChannel(), TimeSpan.FromHours(4));

        // No mid-rolls and every slot holds one episode, so no break returns to the same show.
        Assert.DoesNotContain(schedule.Entries, e => e.Kind == ScheduleEntryKind.YoureWatchingPromo);
        Assert.DoesNotContain(schedule.Entries, e => e.Kind == ScheduleEntryKind.BackToTheShowPromo);
        Assert.Contains(schedule.Entries, e => e.Kind == ScheduleEntryKind.ThisHasBeenPromo);
    }

    /// <summary>Every slot leaves a break, so each one ends with a sign-off.</summary>
    private static ChannelConfig SignOffChannel()
    {
        ChannelConfig channel = BaseChannel();
        channel.BaseRotation = new ContentSelector { Query = "show:'Clone High'", Order = PlaybackOrder.Chronological };
        channel.PadToNearest = TimeSpan.FromMinutes(30);
        channel.Promos = new PromoConfig
        {
            Enabled = true,
            BaseQuery = "tag:'Promo'",
            MaxPerBreak = 0,
            ThisHasBeen = new PromoSpotConfig
            {
                PromoTypes = ["this_has_been"],
                Tag = "This Has Been",
                SkipWhenSameSeries = false
            }
        };

        return channel;
    }

    [Fact]
    public async Task A_clock_filed_promo_wins_at_its_hour_and_is_ignored_at_others()
    {
        ChannelConfig channel = SpongeBobBlockChannel();

        ChannelSchedule schedule = await BuildAsync(channel);

        ScheduleEntry promo = Assert.Single(schedule.Entries, e => e.Kind == ScheduleEntryKind.UpNextPromo);

        // The block starts at 16:00, so the 4pm clip beats the untimed one and the 5pm clip is out.
        Assert.Equal("upnext-spongebob-4pm", promo.Item?.Id);
    }

    private static ChannelConfig SpongeBobBlockChannel()
    {
        ChannelConfig channel = BaseChannel();
        channel.BaseRotation = new ContentSelector { Query = "show:'Clone High'", Order = PlaybackOrder.Chronological };
        channel.Promos = new PromoConfig { Enabled = true, BaseQuery = "tag:'Promo'", MaxPerBreak = 0 };
        channel.Blocks =
        [
            new BlockConfig
            {
                Name = "SpongeBob at 16:00",
                Time = TimeSpan.FromHours(16),
                Days = [DayOfWeek.Monday],
                Shows = [new ContentSelector { Query = "show:'SpongeBob SquarePants'", Order = PlaybackOrder.Chronological }]
            }
        ];

        return channel;
    }

    [Fact]
    public async Task Commercials_in_a_break_never_exceed_the_gap_and_are_never_trimmed()
    {
        ChannelConfig channel = BaseChannel();
        channel.BaseRotation = new ContentSelector { Query = "show:'Clone High'", Order = PlaybackOrder.Chronological };
        channel.PadToNearest = TimeSpan.FromMinutes(30);

        ChannelSchedule schedule = await BuildAsync(channel, TimeSpan.FromHours(6));

        List<ScheduleEntry> commercials = schedule.Entries.Where(e => e.Kind == ScheduleEntryKind.Commercial).ToList();

        Assert.NotEmpty(commercials);
        Assert.All(commercials, c =>
        {
            Assert.False(c.IsTrimmed);
            Assert.Equal(c.Item!.Duration, c.Duration);
        });
    }

    [Fact]
    public async Task Filler_trims_to_close_the_gap_without_overrunning_an_anchor()
    {
        ChannelConfig channel = BaseChannel();
        channel.BaseRotation = new ContentSelector { Query = "show:'Clone High'", Order = PlaybackOrder.Chronological };
        channel.Blocks =
        [
            new BlockConfig
            {
                // 01:07 leaves a residual no whole-second commercial combination can close.
                Name = "Odd anchor",
                Time = new TimeSpan(1, 7, 23) + TimeSpan.FromMilliseconds(750),
                Days = [DayOfWeek.Monday],
                Shows = [new ContentSelector { Query = "show:'Aqua Teen'", Order = PlaybackOrder.Chronological }]
            }
        ];

        ChannelSchedule schedule = await BuildAsync(channel, TimeSpan.FromHours(3));

        DateTimeOffset anchorStart = Day + new TimeSpan(1, 7, 23) + TimeSpan.FromMilliseconds(750);

        Assert.DoesNotContain(schedule.Entries, e => e.Kind == ScheduleEntryKind.Padding);

        ScheduleEntry lastBeforeAnchor = schedule.Entries.Last(e => e.End <= anchorStart);
        Assert.Equal(anchorStart, lastBeforeAnchor.End);

        ScheduleEntry trimmed = Assert.Single(
            schedule.Entries,
            e => e.Kind == ScheduleEntryKind.Filler && e.IsTrimmed && e.End == anchorStart);

        Assert.True(trimmed.Duration < trimmed.SourceDuration);
        Assert.Equal(trimmed.Item!.Duration, trimmed.SourceDuration);
    }

    [Fact]
    public async Task Disabling_the_filler_tier_surfaces_the_residual_as_padding()
    {
        ChannelConfig channel = BaseChannel();
        channel.BaseRotation = new ContentSelector { Query = "show:'Clone High'", Order = PlaybackOrder.Chronological };
        channel.Interstitials = new InterstitialsConfig { Filler = new FillerTierConfig { Fallback = false } };
        channel.Blocks =
        [
            new BlockConfig
            {
                Name = "Odd anchor",
                Time = new TimeSpan(1, 7, 23) + TimeSpan.FromMilliseconds(750),
                Days = [DayOfWeek.Monday],
                Shows = [new ContentSelector { Query = "show:'Aqua Teen'", Order = PlaybackOrder.Chronological }]
            }
        ];

        ChannelSchedule schedule = await BuildAsync(channel, TimeSpan.FromHours(3));

        Assert.DoesNotContain(schedule.Entries, e => e.Kind == ScheduleEntryKind.Filler);
        Assert.Contains(schedule.Entries, e => e.Kind == ScheduleEntryKind.Padding);
    }

    [Fact]
    public async Task Max_break_clamps_an_oversized_slot_gap()
    {
        ChannelConfig channel = BaseChannel();

        // 11-minute episodes on a 30-minute grid would otherwise leave a 19-minute break.
        channel.BaseRotation = new ContentSelector { Query = "show:'Aqua Teen'", Order = PlaybackOrder.Chronological };
        channel.PadToNearest = TimeSpan.FromMinutes(30);
        channel.MaxBreak = TimeSpan.FromMinutes(4);

        ChannelSchedule schedule = await BuildAsync(channel, TimeSpan.FromHours(3));

        List<ScheduleEntry> programs = schedule.Entries.Where(e => e.Kind == ScheduleEntryKind.Program).ToList();

        // Every break is now at most the cap, so far more episodes fit in the same window.
        Assert.True(programs.Count >= 10, $"expected many programmes, got {programs.Count}");

        for (int i = 0; i < programs.Count - 1; i++)
        {
            TimeSpan gap = programs[i + 1].Start - programs[i].End;
            Assert.True(gap <= TimeSpan.FromMinutes(4), $"break of {gap} exceeded the cap");
        }
    }

    [Fact]
    public async Task Without_max_break_the_grid_stays_rigid()
    {
        ChannelConfig channel = BaseChannel();
        channel.BaseRotation = new ContentSelector { Query = "show:'Aqua Teen'", Order = PlaybackOrder.Chronological };
        channel.PadToNearest = TimeSpan.FromMinutes(30);

        ChannelSchedule schedule = await BuildAsync(channel, TimeSpan.FromHours(3));

        List<ScheduleEntry> programs = schedule.Entries.Where(e => e.Kind == ScheduleEntryKind.Program).ToList();

        Assert.Equal(6, programs.Count);
        Assert.All(programs, p => Assert.Equal(0, (p.Start - schedule.Start).Ticks % TimeSpan.FromMinutes(30).Ticks));
    }

    [Fact]
    public async Task Two_builds_with_the_same_inputs_produce_identical_schedules()
    {
        ChannelConfig channel = BaseChannel();

        ChannelSchedule first = await BuildAsync(channel);
        ChannelSchedule second = await BuildAsync(channel);

        Assert.Equal(
            first.Entries.Select(e => (e.Start, e.Item?.Id, e.Kind)),
            second.Entries.Select(e => (e.Start, e.Item?.Id, e.Kind)));
    }

    [Fact]
    public async Task Persisted_state_makes_the_next_day_continue_the_rotation()
    {
        ChannelConfig channel = BaseChannel();
        channel.BaseRotation = new ContentSelector { Query = "show:'Clone High'", Order = PlaybackOrder.Chronological };

        var library = new FakeMediaLibrary(Library());
        var store = new InMemoryPlaybackStateStore();
        var engine = new SchedulingEngine(library, store);
        SchedulerConfig config = Config(channel);

        ChannelSchedule dayOne = await engine.BuildAsync(config, channel, Day, Day.AddHours(6));
        ChannelSchedule dayTwo = await engine.BuildAsync(config, channel, Day.AddHours(6), Day.AddHours(12));

        string? lastOfDayOne = dayOne.Entries.Last(e => e.Kind == ScheduleEntryKind.Program).Item?.Id;
        string? firstOfDayTwo = dayTwo.Entries.First(e => e.Kind == ScheduleEntryKind.Program).Item?.Id;

        Assert.NotEqual(lastOfDayOne, firstOfDayTwo);
    }

    [Fact]
    public async Task An_empty_pool_yields_padding_rather_than_throwing()
    {
        ChannelConfig channel = BaseChannel();
        channel.BaseRotation = new ContentSelector { Query = "show:'Does Not Exist'" };
        channel.CommercialBaseTags = ["No Such Tag"];
        channel.Interstitials = new InterstitialsConfig { Filler = new FillerTierConfig { Fallback = false } };

        ChannelSchedule schedule = await BuildAsync(channel, TimeSpan.FromHours(1));

        ScheduleEntry padding = Assert.Single(schedule.Entries);
        Assert.Equal(ScheduleEntryKind.Padding, padding.Kind);
        Assert.Equal(TimeSpan.FromHours(1), padding.Duration);
    }

    [Fact]
    public async Task An_unknown_global_collection_is_reported_clearly()
    {
        ChannelConfig channel = BaseChannel();
        channel.BaseRotation = new ContentSelector { Collection = "not_defined" };

        SchedulerConfigurationException ex = await Assert.ThrowsAsync<SchedulerConfigurationException>(
            () => BuildAsync(channel, TimeSpan.FromHours(1)));

        Assert.Contains("not_defined", ex.Message, StringComparison.Ordinal);
    }
}
