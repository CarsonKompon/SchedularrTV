using System.Collections.Frozen;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.CreditSqueeze;
using SchedularrTV.Providers.Jellyfin;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class CreditSqueezeTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static MediaItem Episode(string series, int number, TimeSpan? credits = null) => new()
    {
        Id = $"{series}-{number}",
        Title = $"{series} episode {number}",
        Kind = MediaKind.Episode,
        Duration = TimeSpan.FromMinutes(22),
        SeriesId = series,
        SeriesName = series,
        Path = $"/media/{series}/{number}.mkv",
        Attributes = credits is { } at
            ? new Dictionary<string, string> { [MediaItem.CreditsStartAttribute] = at.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) }.ToFrozenDictionary()
            : FrozenDictionary<string, string>.Empty
    };

    private static ScheduleEntry Programme(MediaItem item, DateTimeOffset start, TimeSpan? duration = null, bool outro = false) => new()
    {
        Start = start, Duration = duration ?? item.Duration, Kind = ScheduleEntryKind.Program, Item = item, IsSlotOutro = outro, BlockName = "Day"
    };

    private static ScheduleEntry Ads(DateTimeOffset start, TimeSpan length) => new()
    {
        Start = start, Duration = length, Kind = ScheduleEntryKind.Commercial, Item = TestMedia.Filler("ads", length)
    };

    /// <summary>
    /// 12:00 SpongeBob (credits marked at 21:00), ads, 12:30 Rugrats (no marker) and its 30-second outro, ads, 1:00 Hey
    /// Arnold, 1:30 Rocket Power.
    /// </summary>
    private static List<ScheduleEntry> Day()
    {
        MediaItem outro = new() { Id = "rugrats-outro", Title = "Rugrats outro", Duration = TimeSpan.FromSeconds(30), Path = "/media/Rugrats/extras/outro.mp4" };
        return
        [
            Programme(Episode("SpongeBob", 1, TimeSpan.FromMinutes(21)), Noon),
            Ads(Noon.AddMinutes(22), TimeSpan.FromMinutes(8)),
            Programme(Episode("Rugrats", 1), Noon.AddMinutes(30)),
            Programme(outro, Noon.AddMinutes(52), outro: true),
            Ads(Noon.AddMinutes(52.5), TimeSpan.FromMinutes(7.5)),
            Programme(Episode("Hey Arnold", 1), Noon.AddMinutes(60)),
            Programme(Episode("Rocket Power", 1), Noon.AddMinutes(90))
        ];
    }

    private static List<SqueezePlan> Plan(List<ScheduleEntry> entries, CreditSqueezeSection section) =>
        SqueezePlanner.Plan(entries, "toons", "Toons", TimeZoneInfo.Utc, _ => section, item => item.CreditsStart);

    [Fact]
    public void Marked_credits_and_shows_outros_are_squeezed_with_whats_next()
    {
        List<SqueezePlan> plans = Plan(Day(), new CreditSqueezeSection());

        Assert.Equal(2, plans.Count);
        SqueezePlan credits = plans[0];
        Assert.Equal((0, TimeSpan.FromMinutes(21), false, TimeSpan.FromMinutes(1)), (credits.Index, credits.From, credits.WholeEntry, credits.Job.Length));
        Assert.Equal(("Rugrats", "Rugrats episode 1", "12:30 PM"), (credits.Job.Next.Title, credits.Job.Next.Episode, credits.Job.Next.When));
        Assert.Equal(new[] { "Hey Arnold", "Rocket Power" }, credits.Job.Later.Select(later => later.Title));
        Assert.Equal("1 PM", credits.Job.Later[0].When);

        SqueezePlan outro = plans[1];
        Assert.Equal((3, true, TimeSpan.FromSeconds(30), "Hey Arnold"), (outro.Index, outro.WholeEntry, outro.Job.Length, outro.Job.Next.Title));
    }

    [Fact]
    public void A_squeeze_takes_the_credits_place_and_the_timeline_stays_whole()
    {
        List<ScheduleEntry> day = Day();
        List<SqueezePlan> plans = Plan(day, new CreditSqueezeSection());

        List<ScheduleEntry> squeezed = SqueezePlanner.Apply(day, plans, plan => $"/renders/{plan.Index}.mp4");

        Assert.Equal(day.Count + 1, squeezed.Count);
        Assert.Equal((TimeSpan.FromMinutes(21), true), (squeezed[0].Duration, squeezed[0].IsTrimmed));
        Assert.Equal(("/renders/0.mp4", Noon.AddMinutes(21), TimeSpan.FromMinutes(1), true), (squeezed[1].Item!.Path, squeezed[1].Start, squeezed[1].Duration, squeezed[1].IsSlotOutro));
        Assert.Equal(TimeSpan.Zero, squeezed[1].InPoint);
        Assert.Equal("/renders/3.mp4", squeezed[4].Item!.Path);
        Assert.All(squeezed.Zip(squeezed.Skip(1)), pair => Assert.Equal(pair.First.End, pair.Second.Start));

        // Not made yet: the credits play as they are.
        Assert.Equal(day, SqueezePlanner.Apply(day, plans, _ => null));
    }

    [Fact]
    public void Each_show_can_have_its_own_odds_for_its_outro()
    {
        var section = new CreditSqueezeSection { CreditsChance = 0, Outros = { ["rugrats"] = 0 } };

        Assert.Empty(Plan(Day(), section));
        Assert.Equal(0, section.OutroChanceFor("Rugrats"));
        Assert.Equal(1, section.OutroChanceFor("Hey Arnold"));
    }

    [Fact]
    public void An_episode_followed_by_a_squeezed_outro_keeps_its_own_credits()
    {
        List<ScheduleEntry> day = Day();
        day[2] = Programme(Episode("Rugrats", 1, TimeSpan.FromMinutes(21)), Noon.AddMinutes(30));

        Assert.DoesNotContain(Plan(day, new CreditSqueezeSection()), plan => plan.Index == 2);
        Assert.Contains(Plan(day, new CreditSqueezeSection { Outros = { ["Rugrats"] = 0 } }), plan => plan.Index == 2);
    }

    [Theory]
    [InlineData(0.5)]   // starts before the part that plays has been on a minute
    [InlineData(21.95)] // too short to bother
    [InlineData(10)]    // twelve minutes of "credits": not credits
    public void Unlikely_credits_are_left_alone(double minutes)
    {
        List<ScheduleEntry> day = Day();
        day[0] = Programme(Episode("SpongeBob", 1, TimeSpan.FromMinutes(minutes)), Noon);

        Assert.DoesNotContain(Plan(day, new CreditSqueezeSection()), plan => plan.Index == 0);
    }

    [Fact]
    public void Nothing_is_squeezed_without_a_programme_after_it()
    {
        List<ScheduleEntry> day = [.. Day().Take(2)];

        Assert.Empty(Plan(day, new CreditSqueezeSection()));
    }

    [Fact]
    public void The_odds_are_kept_and_the_same_every_time()
    {
        List<ScheduleEntry> entries = [.. Enumerable.Range(0, 400).Select(index => Programme(Episode("Show", index), Noon.AddMinutes(30 * index)))];

        int squeezed = entries.Count(entry => SqueezePlanner.Rolls("toons", entry, "credits", 0.25));

        Assert.InRange(squeezed, 60, 140);
        Assert.Equal(squeezed, entries.Count(entry => SqueezePlanner.Rolls("toons", entry, "credits", 0.25)));
    }

    [Fact]
    public void The_credits_play_in_their_box_with_their_sound()
    {
        SqueezeJob job = Plan(Day(), new CreditSqueezeSection { Animate = false, CreditsVolume = 0.8 })[0].Job;

        List<string> arguments = [.. SqueezeRenderer.Arguments(job, "frame.png", "out.mp4", 1280, 720, animate: false)];
        string graph = arguments[arguments.IndexOf("-filter_complex") + 1];

        // Side by side (the default): the credits against the left edge, 60% of the width.
        Rect box = job.Layout().Credits;
        Assert.Equal(new Rect(0, 144, 768, 432), box);
        Assert.Equal("1260", arguments[arguments.IndexOf("-ss") + 1]);
        Assert.Equal("/media/SpongeBob/1.mkv", arguments[arguments.IndexOf("-ss") + 5]);
        Assert.Contains("scale=768:432:force_original_aspect_ratio=decrease", graph, StringComparison.Ordinal);
        Assert.Contains("overlay=x='0+(768-w)/2':y='144+(432-h)/2'", graph, StringComparison.Ordinal);
        Assert.Contains("[1:a]asetpts=PTS-STARTPTS,volume=0.8,aresample=48000[a]", graph, StringComparison.Ordinal);
        Assert.Contains("[bg]null[panelled]", graph, StringComparison.Ordinal);

        // Shrinking into place is worked out per frame, at the size asked for; side by side, they grow back out at the end.
        Rect big = job.Layout(1920, 1080).Credits;
        string animated = string.Join(' ', SqueezeRenderer.Arguments(job, "frame.png", "out.mp4", 1920, 1080, animate: true));
        Assert.Contains("eval=frame", animated, StringComparison.Ordinal);
        Assert.Contains($"({big.Width}-1920)", animated, StringComparison.Ordinal);
        Assert.Contains("min(if(gte(t,", animated, StringComparison.Ordinal);

        // Framed: two boxes on a background, shrinking in only.
        SqueezeJob framed = job with { Style = SqueezeStyle.Framed };
        Assert.Equal(new Rect(48, 176, 686, 386), framed.Layout().Credits);
        Assert.DoesNotContain("min(if(", string.Join(' ', SqueezeRenderer.Arguments(framed, "frame.png", "out.mp4", 1280, 720, animate: true)),
            StringComparison.Ordinal);

        // Silent credits: no sound track at all.
        Assert.DoesNotContain("[a]", string.Join(' ', SqueezeRenderer.Arguments(job, "frame.png", "out.mp4", 1280, 720, animate: false, creditsAudio: false)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_promo_plays_in_the_panel_once_the_credits_are_in_place_over_their_sound()
    {
        SqueezeJob job = Plan(Day(), new CreditSqueezeSection())[0].Job with { PromoPath = "/promos/up-next.mp4", PromoLength = TimeSpan.FromSeconds(15), Side = CreditsSide.Right };
        SqueezeLayout layout = job.Layout();

        List<string> arguments = [.. SqueezeRenderer.Arguments(job, "frame.png", "out.mp4", 1280, 720, animate: false)];
        string graph = arguments[arguments.IndexOf("-filter_complex") + 1];

        Assert.True(layout.Credits.X >= layout.Panel.Right);
        Assert.Equal("/promos/up-next.mp4", arguments[arguments.IndexOf("/media/SpongeBob/1.mkv") + 2]);
        Assert.Contains("[2:v]setpts=PTS-STARTPTS+1.2/TB", graph, StringComparison.Ordinal);
        Assert.Contains($"overlay=x='{layout.Picture.X}+({layout.Picture.Width}-w)/2'", graph, StringComparison.Ordinal);
        Assert.Contains("volume='if(between(t,1.2,16.2),0.15,1)':eval=frame", graph, StringComparison.Ordinal);
        Assert.Contains("adelay=1200:all=1", graph, StringComparison.Ordinal);
        Assert.Contains("amix=inputs=2:duration=first", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void The_panels_promo_is_picked_from_what_the_schedule_airs_nearby()
    {
        List<ScheduleEntry> day = Day();
        MediaItem upNext = new() { Id = "rugrats-next", Title = "Rugrats up next", Duration = TimeSpan.FromSeconds(15), Path = "/promos/rugrats-next.mp4" };
        MediaItem placeholder = new() { Id = "generated:Coming Up", Title = "Coming Up (generated)", Duration = TimeSpan.FromSeconds(10) };
        day[1] = Ads(Noon.AddMinutes(22), TimeSpan.FromMinutes(7) + TimeSpan.FromSeconds(35));
        day.Insert(2, new ScheduleEntry { Start = Noon.AddMinutes(29).AddSeconds(35), Duration = TimeSpan.FromSeconds(15), Kind = ScheduleEntryKind.UpNextPromo, Item = upNext });
        day.Insert(3, new ScheduleEntry
        {
            Start = Noon.AddMinutes(29).AddSeconds(50), Duration = TimeSpan.FromSeconds(10), Kind = ScheduleEntryKind.Promo, Item = placeholder, GeneratedTemplate = "Coming Up"
        });

        Assert.Equal((upNext, false), SqueezePlanner.PickPromo(day, 0, 4, [SqueezePromo.UpNext], "toons"));
        Assert.Equal(((MediaItem?)null, false), SqueezePlanner.PickPromo(day, 0, 4, [SqueezePromo.Eventually], "toons"));
        Assert.Equal(((MediaItem?)null, false), SqueezePlanner.PickPromo(day, 0, 4, [], "toons"));

        // A generated promo not made yet: the squeeze waits for it.
        Assert.Equal(((MediaItem?)null, true), SqueezePlanner.PickPromo(day, 0, 4, [SqueezePromo.ComingUp], "toons"));
        Assert.Equal("/promos/rugrats-next.mp4", Plan(day, new CreditSqueezeSection { Promos = [SqueezePromo.UpNext] })[0].Job.PromoPath);
    }

    [Fact]
    public void The_frame_is_a_picture_of_the_whole_screen()
    {
        SqueezeJob job = Plan(Day(), new CreditSqueezeSection())[0].Job;

        byte[] png = SqueezeRenderer.DrawFrame(job, 1280, 720, art: null);

        Assert.True(png.Length > 1000);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);

        // Every style and side draws.
        foreach (SqueezeStyle style in Enum.GetValues<SqueezeStyle>())
        {
            foreach (CreditsSide side in Enum.GetValues<CreditsSide>())
            {
                Assert.True(SqueezeRenderer.DrawFrame(job with { Style = style, Side = side }, 640, 360, art: null).Length > 500);
                Assert.True(SqueezeRenderer.DrawFrame(job with
                {
                    Style = style, Side = side, CreditsFit = SqueezeFit.Crop, CreditsText = true, EndingShow = "SpongeBob", CreditsLine = "Thanks for watching",
                    PanelFit = SqueezeFit.Squeeze, PanelText = false, HeadingFont = "No Such Font"
                }, 640, 360, art: null).Length > 500);
            }
        }
    }

    [Theory]
    [InlineData(CreditsSide.Top, 0)]
    [InlineData(CreditsSide.Bottom, 360)]
    public void Side_by_side_on_the_top_or_bottom_puts_the_words_beside_the_picture(CreditsSide side, int bandTop)
    {
        SqueezeLayout layout = SqueezeLayout.Of(SqueezeStyle.SideBySide, side, 50);

        Assert.Equal(new Rect(320, bandTop, 640, 360), layout.Credits);
        Assert.True(layout.TextBeside);
        Assert.True(layout.Text.X > layout.Picture.Right);
        Assert.True(layout.Picture.Y >= layout.Panel.Y && layout.Picture.Bottom <= layout.Panel.Bottom);
        Assert.All(new[] { layout.Credits, layout.Picture, layout.Text }, rect => Assert.True(rect.Width % 2 == 0 && rect.Height % 2 == 0));
    }

    [Fact]
    public void Each_side_is_letterboxed_squeezed_or_cropped_and_can_go_without_text()
    {
        SqueezeJob job = Plan(Day(), new CreditSqueezeSection { Animate = false })[0].Job;

        // Squeezed or cropped, the credits fill their whole column.
        SqueezeJob squeezed = job with { CreditsFit = SqueezeFit.Squeeze };
        Assert.Equal(new Rect(0, 0, 768, 720), squeezed.Layout().Credits);
        string graph = string.Join(' ', SqueezeRenderer.Arguments(squeezed, "frame.png", "out.mp4", 1280, 720, animate: false));
        Assert.Contains("scale=768:720,setsar=1", graph, StringComparison.Ordinal);

        SqueezeJob cropped = job with { CreditsFit = SqueezeFit.Crop };
        graph = string.Join(' ', SqueezeRenderer.Arguments(cropped, "frame.png", "out.mp4", 1280, 720, animate: false));
        Assert.Contains("scale=768:720:force_original_aspect_ratio=increase:force_divisible_by=2,crop=768:720", graph, StringComparison.Ordinal);

        // Shrinking while cropped: trimmed by a black picture the size of the box at each moment.
        graph = string.Join(' ', SqueezeRenderer.Arguments(cropped, "frame.png", "out.mp4", 1280, 720, animate: true));
        Assert.Contains("color=c=black:s=1280x720", graph, StringComparison.Ordinal);
        Assert.Contains("[cmask][cover]overlay", graph, StringComparison.Ordinal);

        // With text, the credits leave room for it under them.
        SqueezeLayout captioned = (cropped with { CreditsText = true }).Layout();
        Assert.Equal(new Rect(0, 0, 768, 600), captioned.Credits);
        Assert.True(captioned.Caption.Y >= captioned.Credits.Bottom && !captioned.Caption.IsEmpty);
        Assert.True((job with { CreditsText = true }).Layout().Caption.Y >= job.Layout().Credits.Y);

        // The up-next side as just its video: the whole panel, no words.
        SqueezeLayout bare = (job with { PanelText = false }).Layout();
        Assert.Equal(bare.Panel, bare.Picture);
        Assert.True(bare.Text.IsEmpty);
        Assert.True((job with { Style = SqueezeStyle.Framed, PanelText = false }).Layout() is { } framed && framed.Picture.Height == framed.Credits.Height);

        // The promo is fitted the same way.
        SqueezeJob promo = job with { PromoPath = "/promos/up-next.mp4", PromoLength = TimeSpan.FromSeconds(15), PanelFit = SqueezeFit.Crop, PanelText = false };
        graph = string.Join(' ', SqueezeRenderer.Arguments(promo, "frame.png", "out.mp4", 1280, 720, animate: false));
        Assert.Contains("scale=512:720:force_original_aspect_ratio=increase:force_divisible_by=2,crop=512:720", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void A_background_video_loops_under_the_frame()
    {
        SqueezeJob job = Plan(Day(), new CreditSqueezeSection { BackgroundPath = "/backgrounds/loop.mp4" })[0].Job;

        List<string> arguments = [.. SqueezeRenderer.Arguments(job, "frame.png", "out.mp4", 1280, 720, animate: false)];
        string graph = arguments[arguments.IndexOf("-filter_complex") + 1];

        Assert.Equal("/backgrounds/loop.mp4", job.BackgroundPath);
        Assert.Equal("-1", arguments[arguments.IndexOf("-stream_loop") + 1]);
        Assert.Contains("[2:v]setpts=PTS-STARTPTS,scale=1280:720:force_original_aspect_ratio=increase,crop=1280:720", graph, StringComparison.Ordinal);
        Assert.Contains("[bgv][0:v]overlay=0:0", graph, StringComparison.Ordinal);

        // A picture is drawn into the frame instead.
        Assert.DoesNotContain("-stream_loop", SqueezeRenderer.Arguments(job with { BackgroundPath = "/backgrounds/still.png" }, "frame.png", "out.mp4", 1280, 720, animate: false));
    }

    [Fact]
    public void A_show_can_have_its_own_layout_here_or_on_every_channel()
    {
        CreditSqueezeSection everywhere = new() { Enabled = false };
        everywhere.Shows["spongebob"] = new SqueezeLayoutChoice { Style = SqueezeStyle.Framed, Side = CreditsSide.Right, SplitPercent = 70 };
        CreditSqueezeSection channel = new() { Side = CreditsSide.Bottom };

        SqueezeJob job = SqueezePlanner.Plan(Day(), "toons", "Toons", TimeZoneInfo.Utc, _ => channel, item => item.CreditsStart, everywhere)[0].Job;
        Assert.Equal((SqueezeStyle.Framed, CreditsSide.Right, 70), (job.Style, job.Side, job.SplitPercent));

        // The channel's own wins over every channel's.
        channel.Shows["SpongeBob"] = new SqueezeLayoutChoice { Side = CreditsSide.Top, SplitPercent = 40 };
        job = SqueezePlanner.Plan(Day(), "toons", "Toons", TimeZoneInfo.Utc, _ => channel, item => item.CreditsStart, everywhere)[0].Job;
        Assert.Equal((SqueezeStyle.SideBySide, CreditsSide.Top, 40), (job.Style, job.Side, job.SplitPercent));

        // Other shows (Rugrats' outro) use the channel's.
        SqueezeJob outro = SqueezePlanner.Plan(Day(), "toons", "Toons", TimeZoneInfo.Utc, _ => channel, item => item.CreditsStart, everywhere)[1].Job;
        Assert.Equal(CreditsSide.Bottom, outro.Side);
    }

    [Fact]
    public void A_shows_preview_uses_its_outro_its_credits_or_the_end_of_an_episode()
    {
        CreditSqueezeSection section = new();
        TimeSpan twenty = TimeSpan.FromSeconds(20);

        var rugrats = SqueezePlanner.PreviewFor(Day(), "Rugrats", section, null, "toons", "Toons", TimeZoneInfo.Utc, item => item.CreditsStart, twenty);
        var spongebob = SqueezePlanner.PreviewFor(Day(), "SpongeBob", section, null, "toons", "Toons", TimeZoneInfo.Utc, item => item.CreditsStart, twenty);
        var arnold = SqueezePlanner.PreviewFor(Day(), "Hey Arnold", section, null, "toons", "Toons", TimeZoneInfo.Utc, item => item.CreditsStart, twenty);

        Assert.Equal(SqueezePlanner.PreviewSource.Outro, rugrats?.Source);
        Assert.Equal((SqueezePlanner.PreviewSource.Credits, TimeSpan.FromMinutes(21), twenty), (spongebob?.Source, spongebob?.Plan.From, spongebob?.Plan.Job.Length));
        Assert.Equal((SqueezePlanner.PreviewSource.LastSeconds, TimeSpan.FromMinutes(22) - twenty), (arnold?.Source, arnold?.Plan.From));

        // Nothing follows the last programme.
        Assert.Null(SqueezePlanner.PreviewFor(Day(), "Rocket Power", section, null, "toons", "Toons", TimeZoneInfo.Utc, item => item.CreditsStart, twenty));
        Assert.Equal(new[] { "Hey Arnold", "Rugrats", "SpongeBob" }, SqueezePlanner.ShowsOn(Day()));
    }

    [Theory]
    [InlineData("Credits", 20.5, 20.5)]
    [InlineData("End Credits", 21, 21)]
    [InlineData("Outro", 21.5, 21.5)]
    [InlineData("Opening Credits", 21, null)]
    [InlineData("Credits", 2, null)]
    [InlineData("Act 3", 21, null)]
    public void A_chapter_named_for_the_credits_marks_them(string name, double minutes, double? expected)
    {
        TimeSpan? found = JellyfinMediaLibrary.CreditsChapter(TimeSpan.FromMinutes(22),
            [(TimeSpan.Zero, "Intro"), (TimeSpan.FromMinutes(minutes), name)]);

        Assert.Equal(expected is { } at ? TimeSpan.FromMinutes(at) : null, found);
    }
}
