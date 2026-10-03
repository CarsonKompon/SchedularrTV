using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class GeneratedPromoTypeTests
{
    private static readonly DateTimeOffset Monday = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Midnight = new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);

    private static ChannelConfig Channel(params GeneratedPromoTemplate[] templates) => new()
    {
        Id = "zone",
        Name = "The Zone",
        Number = 1,
        TimeZone = "UTC",
        AutoTemporalTags = false,
        PadToNearest = TimeSpan.FromMinutes(30),
        BaseRotation = new ContentSelector { Include = [new ContentInclude { Show = "SpongeBob" }, new ContentInclude { Show = "Rugrats" }] },
        Promos = new PromoConfig { BaseQuery = "tag:'Promo'", Eventually = new EventuallyConfig { Chance = 1 } },
        Anchors = [new AnchorConfig { Name = "Prime time", Time = TimeSpan.FromHours(20), Show = new ContentSelector { Include = [new ContentInclude { Show = "Rocket Power" }] } }],
        GeneratedPromos = new GeneratedPromosConfig { Templates = [.. templates] }
    };

    private static List<MediaItem> Library()
    {
        List<MediaItem> items = [];
        for (int episode = 1; episode <= 10; episode++)
        {
            items.Add(TestMedia.Episode("SpongeBob", 1, episode, TimeSpan.FromMinutes(22), title: $"Sponge {episode}"));
            items.Add(TestMedia.Episode("Rugrats", 1, episode, TimeSpan.FromMinutes(22), title: $"Rugrat {episode}"));
            items.Add(TestMedia.Episode("Rocket Power", 1, episode, TimeSpan.FromMinutes(22), title: $"Rocket {episode}"));
        }

        return items;
    }

    private static async Task<ChannelSchedule> BuildAsync(ChannelConfig channel, DateTimeOffset start, TimeSpan length) =>
        await new SchedulingEngine(new FakeMediaLibrary(Library()), new InMemoryPlaybackStateStore())
            .BuildAsync(new SchedulerConfig { Channels = [channel] }, channel, start, start + length);

    [Fact]
    public async Task Every_generated_slot_carries_stand_in_commercials_that_fill_it()
    {
        ChannelConfig channel = Channel(GeneratedPromoTemplates.ForType(GeneratedPromoType.NowPlaying));
        List<MediaItem> library = [.. Library(),
            .. Enumerable.Range(1, 12).Select(index => TestMedia.Filler($"ad-{index}", TimeSpan.FromSeconds(index % 3 == 0 ? 15 : 5), "Commercials"))];

        ChannelSchedule schedule = await new SchedulingEngine(new FakeMediaLibrary(library), new InMemoryPlaybackStateStore())
            .BuildAsync(new SchedulerConfig { Channels = [channel] }, channel, Monday, Monday.AddHours(3));

        ScheduleEntry[] generated = [.. schedule.Entries.Where(entry => entry.GeneratedTemplate is not null)];
        Assert.NotEmpty(generated);
        Assert.All(generated, entry =>
        {
            Assert.NotNull(entry.Fallback);
            Assert.Equal(entry.Start, entry.Fallback![0].Start);
            Assert.All(entry.Fallback, standIn => Assert.Contains(standIn.Kind, (ScheduleEntryKind[])[ScheduleEntryKind.Commercial, ScheduleEntryKind.Filler]));
            Assert.InRange(entry.Fallback[^1].End, entry.End - TimeSpan.FromSeconds(1), entry.End);
        });
    }

    [Fact]
    public async Task Every_promo_type_reserves_its_generated_template_when_the_library_has_no_clip()
    {
        GeneratedPromoTemplate[] templates = [.. Enum.GetValues<GeneratedPromoType>()
            .Where(type => type != GeneratedPromoType.UpNext)
            .Select(type => GeneratedPromoTemplates.ForType(type))];
        ChannelConfig channel = Channel(templates);

        // The anchor has to fall inside the built window for Coming Up to see it, as it does with a 12h horizon.
        ChannelSchedule schedule = await BuildAsync(channel, Monday, TimeSpan.FromHours(10));

        ScheduleEntry[] nowPlaying = [.. schedule.Entries.Where(entry => entry.Kind == ScheduleEntryKind.NowPlayingPromo)];
        ScheduleEntry[] signOffs = [.. schedule.Entries.Where(entry => entry.Kind == ScheduleEntryKind.ThisHasBeenPromo)];
        ScheduleEntry[] comingUp = [.. schedule.Entries.Where(entry => entry.Kind == ScheduleEntryKind.Promo)];
        ScheduleEntry[] eventually = [.. schedule.Entries.Where(entry => entry.Kind == ScheduleEntryKind.EventuallyPromo)];

        Assert.NotEmpty(nowPlaying);
        Assert.NotEmpty(signOffs);
        Assert.NotEmpty(comingUp);
        Assert.NotEmpty(eventually);
        Assert.All(nowPlaying, entry => Assert.Equal("Now Playing", entry.GeneratedTemplate));
        Assert.All(signOffs, entry => Assert.Equal("This Has Been", entry.GeneratedTemplate));
        Assert.All(comingUp, entry =>
        {
            Assert.Equal("Coming Up", entry.GeneratedTemplate);
            Assert.Equal(TimeSpan.FromSeconds(8), entry.Duration);
        });

        // The anchor is promoted while it's ahead; with nothing fixed ahead, whatever the rotation airs a while later is.
        Assert.Contains(comingUp, entry => entry.GeneratedSubject is not null);
        Assert.All(comingUp, entry =>
        {
            if (entry.GeneratedSubject is null)
            {
                Assert.InRange(entry.GeneratedTargetAt!.Value - entry.End, TimeSpan.FromMinutes(45), TimeSpan.FromHours(3.1));
            }
            else
            {
                Assert.Equal(Midnight.AddHours(20), entry.GeneratedTargetAt);
                Assert.Equal("Rocket Power", entry.GeneratedSubject);
            }
        });
        Assert.Contains(comingUp, entry => entry.Start > Midnight.AddHours(20) && entry.GeneratedSubject is null);
        Assert.All(eventually, entry =>
        {
            Assert.Equal("Eventually", entry.GeneratedTemplate);
            Assert.Equal("Rocket Power", entry.GeneratedSubject);
            Assert.InRange(entry.GeneratedTargetAt!.Value - entry.Start, TimeSpan.FromHours(12), TimeSpan.FromDays(7));
        });
        Assert.All(schedule.Entries.Where(entry => entry.GeneratedTemplate is not null), entry => Assert.Null(entry.Item!.Path));
        Assert.All(schedule.Entries.Zip(schedule.Entries.Skip(1)), pair => Assert.Equal(pair.First.End, pair.Second.Start));

        foreach (ScheduleEntry signOff in signOffs)
        {
            ScheduleEntry previous = schedule.Entries.Last(entry => entry.End <= signOff.Start && entry.Kind == ScheduleEntryKind.Program);
            if (schedule.Entries.FirstOrDefault(entry => entry.Start >= signOff.End && entry.Kind == ScheduleEntryKind.Program) is { } next)
            {
                Assert.NotEqual(previous.Item!.SeriesName, next.Item!.SeriesName);
            }
        }
    }

    [Fact]
    public async Task Library_clips_keep_their_spots_unless_a_template_is_mixed_in()
    {
        GeneratedPromoTemplate fallback = GeneratedPromoTemplates.ForType(GeneratedPromoType.NowPlaying);
        List<MediaItem> library = [.. Library(),
            TestMedia.Promo("ident-sponge", TimeSpan.FromSeconds(5), "SpongeBob", "now_playing"),
            TestMedia.Promo("ident-rugrats", TimeSpan.FromSeconds(5), "Rugrats", "now_playing")];
        ChannelConfig channel = Channel(fallback);
        var engine = new SchedulingEngine(new FakeMediaLibrary(library), new InMemoryPlaybackStateStore());

        ChannelSchedule clipsOnly = await engine.BuildAsync(new SchedulerConfig { Channels = [channel] }, channel, Monday, Monday.AddHours(3));
        Assert.All(clipsOnly.Entries.Where(entry => entry.Kind == ScheduleEntryKind.NowPlayingPromo),
            entry => Assert.StartsWith("ident-", entry.Item!.Id, StringComparison.Ordinal));

        fallback.Usage = GeneratedPromoUsage.Mixed;
        fallback.MixChance = 1;
        ChannelSchedule mixed = await new SchedulingEngine(new FakeMediaLibrary(library), new InMemoryPlaybackStateStore())
            .BuildAsync(new SchedulerConfig { Channels = [channel] }, channel, Monday, Monday.AddHours(3));
        Assert.All(mixed.Entries.Where(entry => entry.Kind == ScheduleEntryKind.NowPlayingPromo),
            entry => Assert.Equal("Now Playing", entry.GeneratedTemplate));
    }

    [Fact]
    public async Task Coming_up_can_promote_the_opening_of_a_later_block_and_names_what_actually_airs()
    {
        ChannelConfig channel = Channel(GeneratedPromoTemplates.ForType(GeneratedPromoType.ComingUp));
        channel.Anchors.Clear();
        channel.Blocks.Add(new BlockConfig
        {
            Name = "Prime time", Time = TimeSpan.FromHours(20), EndTime = TimeSpan.FromHours(22),
            Shows = [new ContentSelector { Include = [new ContentInclude { Show = "Rocket Power" }] }]
        });

        ChannelSchedule schedule = await BuildAsync(channel, Monday, TimeSpan.FromHours(10));

        ScheduleEntry promo = Assert.Single(schedule.Entries, entry => entry.GeneratedTemplate == "Coming Up"
            && entry.Start == schedule.Entries.Where(candidate => candidate.GeneratedTemplate == "Coming Up").Min(candidate => candidate.Start));
        Assert.Equal(Midnight.AddHours(20), promo.GeneratedTargetAt);
        Assert.Equal("Rocket Power", Assert.Single(GeneratedPromoService.Subject(GeneratedPromoType.ComingUp, schedule, promo, 1)).Title);
        Assert.All(schedule.Entries.Where(entry => entry.GeneratedTemplate == "Coming Up"),
            entry => Assert.True(entry.GeneratedTargetAt > entry.End));
        // A promo in the last break of the built window has no programme after it here.
        Assert.All(schedule.Entries.Where(entry => entry.GeneratedTemplate == "Coming Up"), entry =>
        {
            if (schedule.Entries.FirstOrDefault(candidate => candidate.Start >= entry.End && candidate.Kind == ScheduleEntryKind.Program) is { } next)
            {
                Assert.NotEqual(next.Item!.SeriesName, entry.GeneratedSubject);
            }
        });
    }

    [Fact]
    public async Task A_rotation_run_channel_still_gets_coming_up_and_eventually_named_from_the_built_schedule()
    {
        ChannelConfig channel = Channel(
            GeneratedPromoTemplates.ForType(GeneratedPromoType.ComingUp),
            GeneratedPromoTemplates.ForType(GeneratedPromoType.Eventually));
        channel.Anchors.Clear();
        channel.BaseRotation!.Include.Add(new ContentInclude { Show = "Rocket Power" });

        ChannelSchedule schedule = await BuildAsync(channel, Monday, TimeSpan.FromHours(10));

        ScheduleEntry[] comingUp = [.. schedule.Entries.Where(entry => entry.GeneratedTemplate == "Coming Up")];
        ScheduleEntry[] eventually = [.. schedule.Entries.Where(entry => entry.GeneratedTemplate == "Eventually")];
        Assert.NotEmpty(comingUp);
        Assert.NotEmpty(eventually);
        Assert.All(comingUp, entry =>
        {
            Assert.Null(entry.GeneratedSubject);
            Assert.InRange(entry.GeneratedTargetAt!.Value - entry.End, TimeSpan.FromMinutes(45), TimeSpan.FromHours(3.1));
        });
        Assert.All(eventually, entry =>
        {
            Assert.Null(entry.GeneratedSubject);
            Assert.InRange(entry.GeneratedTargetAt!.Value - entry.Start, TimeSpan.FromHours(11), TimeSpan.FromHours(47));
        });

        // Named from what the schedule airs then, never the shows either side of the promo (the next one is Up Next's).
        int named = 0;
        foreach (ScheduleEntry entry in comingUp)
        {
            if (GeneratedPromoService.Subject(GeneratedPromoType.ComingUp, schedule, entry, 1) is not [UpNextListing listing])
            {
                continue;
            }

            named++;
            ScheduleEntry previous = schedule.Entries.Last(candidate => candidate.End <= entry.Start && candidate.Kind == ScheduleEntryKind.Program);
            ScheduleEntry next = schedule.Entries.First(candidate => candidate.Start >= entry.End && candidate.Kind == ScheduleEntryKind.Program);
            Assert.NotEqual(previous.Item!.SeriesName, listing.Title);
            Assert.NotEqual(next.Item!.SeriesName, listing.Title);
            Assert.True(listing.Start >= entry.GeneratedTargetAt!.Value - TimeSpan.FromSeconds(1));
            Assert.Contains(schedule.Entries, candidate => candidate.Start == listing.Start && candidate.Kind == ScheduleEntryKind.Program);
        }

        Assert.True(named > 0);
    }

    [Fact]
    public void An_open_target_names_the_first_show_from_then_that_is_not_either_side_of_the_promo()
    {
        ScheduleEntry Program(int minute, string series) => new()
        {
            Start = Monday.AddMinutes(minute), Duration = TimeSpan.FromMinutes(22), Kind = ScheduleEntryKind.Program,
            Item = TestMedia.Episode(series, 1, minute + 1, TimeSpan.FromMinutes(22), title: $"{series} episode")
        };
        var promo = new ScheduleEntry
        {
            Start = Monday.AddMinutes(22), Duration = TimeSpan.FromSeconds(8), Kind = ScheduleEntryKind.Promo,
            GeneratedTemplate = "x", GeneratedTargetAt = Monday.AddMinutes(45)
        };
        var schedule = new ChannelSchedule
        {
            ChannelId = "zone", ChannelName = "The Zone", Start = Monday, End = Monday.AddHours(3),
            Entries = [Program(0, "SpongeBob"), promo, Program(30, "Rugrats"), Program(60, "Rugrats"), Program(90, "SpongeBob"), Program(120, "Rocket Power")]
        };

        UpNextListing later = Assert.Single(GeneratedPromoService.Subject(GeneratedPromoType.ComingUp, schedule, promo, 1));
        Assert.Equal(("Rocket Power", Monday.AddMinutes(120)), (later.Title, later.Start));

        // An Eventually with no predicted show takes the first programme from its airtime, whatever it is.
        UpNextListing trailer = Assert.Single(GeneratedPromoService.Subject(GeneratedPromoType.Eventually, schedule, promo, 1));
        Assert.Equal(("Rugrats", Monday.AddMinutes(60)), (trailer.Title, trailer.Start));
    }

    [Fact]
    public async Task Mid_show_breaks_open_and_close_with_generated_youre_watching_and_back_to_the_show()
    {
        ChannelConfig channel = Channel(
            GeneratedPromoTemplates.ForType(GeneratedPromoType.YoureWatching),
            GeneratedPromoTemplates.ForType(GeneratedPromoType.BackToTheShow));
        channel.MidRoll = new MidRollConfig
        {
            Mode = MidRollMode.Auto,
            TargetSegment = TimeSpan.FromMinutes(10),
            MinSegment = TimeSpan.FromMinutes(5),
            MinBreak = TimeSpan.FromSeconds(20)
        };

        ChannelSchedule schedule = await BuildAsync(channel, Monday, TimeSpan.FromHours(3));

        ScheduleEntry watching = schedule.Entries.First(entry => entry.Kind == ScheduleEntryKind.YoureWatchingPromo);
        ScheduleEntry back = schedule.Entries.First(entry => entry.Kind == ScheduleEntryKind.BackToTheShowPromo);
        Assert.Equal("You're Watching", watching.GeneratedTemplate);
        Assert.Equal("Back to the Show", back.GeneratedTemplate);

        // Both name the show the break interrupts.
        string interrupted = schedule.Entries.Last(entry => entry.End <= watching.Start && entry.Kind == ScheduleEntryKind.Program).Item!.SeriesName!;
        Assert.Equal(interrupted, Assert.Single(GeneratedPromoService.Subject(GeneratedPromoType.YoureWatching, schedule, watching, 1)).Title);
        Assert.Equal(interrupted, Assert.Single(GeneratedPromoService.Subject(GeneratedPromoType.BackToTheShow, schedule, back, 1)).Title);
        Assert.All(schedule.Entries.Zip(schedule.Entries.Skip(1)), pair => Assert.Equal(pair.First.End, pair.Second.Start));
    }

    [Fact]
    public async Task Eventually_odds_of_zero_keep_generated_trailers_off_too()
    {
        ChannelConfig channel = Channel(GeneratedPromoTemplates.ForType(GeneratedPromoType.Eventually));
        channel.Promos!.Eventually.Chance = 0;

        ChannelSchedule schedule = await BuildAsync(channel, Monday, TimeSpan.FromHours(3));

        Assert.DoesNotContain(schedule.Entries, entry => entry.Kind == ScheduleEntryKind.EventuallyPromo);
    }

    [Theory]
    [InlineData(GeneratedPromoType.NowPlaying, 12, "You're watching SpongeBob!", new[] { "SpongeBob" })]
    [InlineData(GeneratedPromoType.ThisHasBeen, 12, "This has been SpongeBob!", new[] { "SpongeBob" })]
    [InlineData(GeneratedPromoType.YoureWatching, 12, "You're watching SpongeBob!", new[] { "SpongeBob" })]
    [InlineData(GeneratedPromoType.BackToTheShow, 12, "Now, back to SpongeBob!", new[] { "SpongeBob" })]
    [InlineData(GeneratedPromoType.ComingUp, 20, "Coming up tonight at 8:00, SpongeBob! Only on The Zone!", new[] { "SpongeBob", "Tonight at 8:00" })]
    [InlineData(GeneratedPromoType.ComingUp, 15, "Coming up today at 3:00, SpongeBob! Only on The Zone!", new[] { "SpongeBob", "Today at 3:00" })]
    [InlineData(GeneratedPromoType.Eventually, 24 + 9, "Don't miss SpongeBob, Tomorrow at 9:00! Only on The Zone!", new[] { "SpongeBob", "Tomorrow at 9:00" })]
    [InlineData(GeneratedPromoType.Eventually, 5 * 24 + 16, "Don't miss SpongeBob, Saturday at 4:00! Only on The Zone!", new[] { "SpongeBob", "Saturday at 4:00" })]
    public void Each_type_fills_its_text_block_and_voice_over(GeneratedPromoType type, int hoursFromMidnight, string script, string[] lines)
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.ForType(type);
        DateTimeOffset target = Midnight.AddHours(hoursFromMidnight);

        ResolvedGeneratedPromo promo = GeneratedPromoResolver.Resolve(
            template, "The Zone", [new UpNextListing("SpongeBob", target, "Help Wanted")], TimeZoneInfo.Utc, Monday.AddMinutes(10))!;

        Assert.Equal(script, promo.Script);
        int block = template.Layers.FindIndex(layer => layer.Kind == GeneratedPromoKinds.RequiredLayer(type));
        Assert.Equal(lines, promo.LayerLines[block]);
    }

    [Fact]
    public void Show_blocks_can_add_the_episode_title_and_drop_the_show_title()
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.ForType(GeneratedPromoType.NowPlaying);
        GeneratedLayer show = template.Layers.Single(layer => layer.Kind == GeneratedLayerKind.NowPlaying);
        show.Show = new GeneratedShowOptions { Title = false, EpisodeTitle = true };
        template.Voice!.PreMessage = null;

        ResolvedGeneratedPromo promo = GeneratedPromoResolver.Resolve(
            template, "The Zone", [new UpNextListing("SpongeBob", Monday, "Help Wanted")], TimeZoneInfo.Utc, Monday)!;

        Assert.Equal(["Help Wanted"], promo.LayerLines[template.Layers.IndexOf(show)]);
        Assert.Equal("You're watching SpongeBob!", promo.Script);
        Assert.Equal(TimeSpan.FromSeconds(6), GeneratedPromoResolver.Compose(promo).Duration);
    }

    [Fact]
    public void Slots_resolve_the_show_before_after_or_at_their_target()
    {
        ScheduleEntry Program(int minute, string series, bool intro = false) => new()
        {
            Start = Monday.AddMinutes(minute), Duration = TimeSpan.FromMinutes(minute == 30 ? 22 : 20), Kind = ScheduleEntryKind.Program,
            IsSlotIntro = intro, Item = TestMedia.Episode(series, 1, minute + 1, TimeSpan.FromMinutes(22), title: $"{series} episode")
        };
        ScheduleEntry Slot(int minute, DateTimeOffset? target = null, string? subject = null) => new()
        {
            Start = Monday.AddMinutes(minute), Duration = TimeSpan.FromSeconds(8), Kind = ScheduleEntryKind.Promo,
            GeneratedTemplate = "x", GeneratedTargetAt = target, GeneratedSubject = subject
        };
        var schedule = new ChannelSchedule
        {
            ChannelId = "zone", ChannelName = "The Zone", Start = Monday, End = Monday.AddHours(1),
            Entries = [Program(0, "SpongeBob"), Slot(22), Program(29, "Theme", intro: true), Program(30, "Rugrats")]
        };

        Assert.Equal("SpongeBob", Assert.Single(GeneratedPromoService.Subject(GeneratedPromoType.ThisHasBeen, schedule, Slot(22), 1)).Title);
        UpNextListing next = Assert.Single(GeneratedPromoService.Subject(GeneratedPromoType.NowPlaying, schedule, Slot(22), 1));
        Assert.Equal(("Rugrats", "Rugrats episode"), (next.Title, next.Episode));
        Assert.Equal("Rugrats", Assert.Single(GeneratedPromoService.Subject(GeneratedPromoType.ComingUp, schedule,
            Slot(10, Monday.AddMinutes(30), "Predicted"), 1)).Title);
        UpNextListing beyond = Assert.Single(GeneratedPromoService.Subject(GeneratedPromoType.Eventually, schedule,
            Slot(10, Monday.AddDays(3), "Rocket Power"), 1));
        Assert.Equal(("Rocket Power", Monday.AddDays(3)), (beyond.Title, beyond.Start));
        Assert.Empty(GeneratedPromoService.Subject(GeneratedPromoType.Eventually, schedule, Slot(10, Monday.AddDays(3)), 1));
    }

    [Theory]
    [InlineData(GeneratedPromoType.NowPlaying, "now_playing")]
    [InlineData(GeneratedPromoType.ThisHasBeen, "this_has_been")]
    [InlineData(GeneratedPromoType.ComingUp, "coming_up")]
    [InlineData(GeneratedPromoType.Eventually, "eventually")]
    [InlineData(GeneratedPromoType.YoureWatching, "youre_watching")]
    [InlineData(GeneratedPromoType.BackToTheShow, "back_to_the_show")]
    public void Each_type_round_trips_and_requires_its_own_text_block(GeneratedPromoType type, string layer)
    {
        var config = new SchedulerConfig { Channels = [Channel(GeneratedPromoTemplates.ForType(type))] };
        SchedulerConfig parsed = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config));
        GeneratedPromoTemplate template = Assert.Single(parsed.Channels[0].GeneratedPromos!.Templates);

        Assert.True(SchedulerConfigLoader.Validate(parsed).IsValid, string.Join("; ", SchedulerConfigLoader.Validate(parsed).Errors));
        Assert.Equal(type, template.Type);
        GeneratedLayer show = template.Layers.Single(candidate => candidate.Kind == GeneratedPromoKinds.RequiredLayer(type));
        Assert.Empty(template.Voice!.PreMessages);
        Assert.NotNull(show.Show);

        template.Layers.Remove(show);
        template.Layers.Add(new GeneratedLayer { Kind = GeneratedLayerKind.UpNext });
        Assert.Contains(SchedulerConfigLoader.Validate(parsed).Errors, error => error.EndsWith($"needs its '{layer}' text layer.", StringComparison.Ordinal));
    }
}
