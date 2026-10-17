using SchedularrTV.Core.Configuration;
using SchedularrTV.GeneratedPromos;
using SchedularrTV.Plugins.Speech;
using SchedularrTV.Service.Writing;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class CuratedPromoTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static Transcript Transcript(params (double Start, double End, string Text)[] lines) =>
        new([.. lines.Select(line => new TranscriptSegment(S(line.Start), S(line.End), line.Text))]);

    private static CuratedPromoRequest Request(GeneratedCuratedClips? config = null, double duration = 15) => new(
        0, config ?? new GeneratedCuratedClips(), new UpNextListing("SpongeBob SquarePants", Noon.AddHours(7), "Help Wanted"),
        "episode.mkv", "YTV", GeneratedPromoType.ComingUp, TimeZoneInfo.Utc, Noon, S(duration), S(0.35),
        "Coming up tonight at 7, SpongeBob SquarePants!", "variety");

    [Fact]
    public void Moments_skip_titles_credits_notes_and_lines_of_the_wrong_length()
    {
        Transcript transcript = Transcript(
            (10, 12, "Who lives in a pineapple?"),        // opening titles
            (100, 102.5, "I didn't know that!"),
            (200, 201, "[Music]"),
            (300, 320, "A very long speech that goes on and on and on for far too long to be a clip."),
            (400, 402, "Oh, barnacles."),
            (1290, 1292, "Credits line"));                // credits

        IReadOnlyList<CuratedMoment> moments = CuratedPromo.Moments(transcript, new GeneratedCuratedClips(), TimeSpan.FromMinutes(22));

        Assert.Equal(["I didn't know that!", "Oh, barnacles."], moments.Select(moment => moment.Text));
        Assert.Equal([1, 2], moments.Select(moment => moment.Number));
    }

    [Fact]
    public void Moments_only_come_from_the_allowed_stretch_of_the_episode()
    {
        Transcript transcript = Transcript((60, 62, "Theme song words!"), (300, 302, "Middle bit!"), (580, 582, "Credits words!"));
        TimeSpan runtime = TimeSpan.FromMinutes(10);

        Assert.Equal(["Theme song words!", "Middle bit!", "Credits words!"],
            CuratedPromo.Moments(transcript, new GeneratedCuratedClips { FromPercent = 0, UntilPercent = 100 }, runtime).Select(moment => moment.Text));
        Assert.Equal(["Middle bit!"],
            CuratedPromo.Moments(transcript, new GeneratedCuratedClips { FromPercent = 20, UntilPercent = 80 }, runtime).Select(moment => moment.Text));
        // Backwards allows the whole episode.
        Assert.Equal(3, CuratedPromo.Moments(transcript, new GeneratedCuratedClips { FromPercent = 80, UntilPercent = 20 }, runtime).Count);
    }

    [Fact]
    public void Moments_carry_what_is_said_around_them()
    {
        Transcript transcript = Transcript(
            (296, 298, "Who broke the vase?"),
            (298.5, 301, "Me! I'm in charge!"),
            (302, 304, "Since when?"),
            (400, 402, "Way later on, alone."));

        IReadOnlyList<CuratedMoment> moments = CuratedPromo.Moments(transcript, new GeneratedCuratedClips(), TimeSpan.FromMinutes(22));
        CuratedMoment charge = Assert.Single(moments, moment => moment.Text == "Me! I'm in charge!");

        Assert.Equal("Who broke the vase?", charge.Before);
        Assert.Equal("Since when?", charge.After);
        Assert.Null(Assert.Single(moments, moment => moment.Text.StartsWith("Way later")).After);

        AiTextJob job = CuratedPromo.Job(Request(), [charge]);
        Assert.Contains(job.Facts, fact => fact.Label == "Around moment " + charge.Number && fact.Value.Contains("Who broke the vase?"));
    }

    [Theory]
    [InlineData("Who's really in charge? Maybe Pepper Ann knows.", "Pepper")]
    [InlineData("Who's really in charge? Maybe the vase knows.", null)]
    [InlineData("Help Wanted is a classic. SpongeBob's day gets weird on YTV.", null)]
    [InlineData("I think it's fine, OK? Tonight on YTV.", null)]
    public void Names_it_was_not_given_are_caught(string spoken, string? name)
    {
        IReadOnlyList<CuratedMoment> moments = [new(1, S(100), S(102), "Who broke the vase?")];
        AiTextJob job = CuratedPromo.Job(Request(), moments);

        Assert.Equal(name, CuratedPromo.UnknownName(spoken, job));
    }

    [Fact]
    public void A_sentence_split_by_speech_recognition_is_one_moment_and_one_that_can_not_be_joined_is_left_out()
    {
        Transcript transcript = Transcript(
            (100, 101.6, "I need a..."), (102, 103.2, "teacher's aid!"),
            (300, 302, "This sentence goes on and on"), (302.3, 306.5, "and on, far too long to join up with it."));

        IReadOnlyList<CuratedMoment> moments = CuratedPromo.Moments(transcript, new GeneratedCuratedClips(), TimeSpan.FromMinutes(22));

        CuratedMoment aid = Assert.Single(moments, moment => moment.Text.StartsWith("I need"));
        Assert.Equal("I need a... teacher's aid!", aid.Text);
        Assert.Equal(S(103.2), aid.End);
        Assert.DoesNotContain(moments, moment => moment.Text == "This sentence goes on and on");
    }

    [Fact]
    public void A_clip_described_as_another_moments_line_plays_that_moment()
    {
        IReadOnlyList<CuratedMoment> moments = [new(9, S(100), S(102), "What did you say?"), new(12, S(200), S(203), "I need a teacher's aid!")];
        AiTextJob job = CuratedPromo.Job(Request(), moments);

        (string text, string? problem) = job.Format!.Accept(
            "VO: Who needs a teacher's aid?\nCLIP 9: I need a teacher's aid!\nVO: SpongeBob SquarePants, tonight on YTV!", job);

        Assert.Null(problem);
        Assert.Contains("CLIP 12", text);
        Assert.DoesNotContain("CLIP 9", text);
    }

    [Fact]
    public void Short_lines_run_on_into_the_next_one()
    {
        Transcript transcript = Transcript((100, 100.5, "Wait."), (100.7, 102, "What did you do?"));

        CuratedMoment moment = Assert.Single(CuratedPromo.Moments(transcript, new GeneratedCuratedClips(), TimeSpan.FromMinutes(22)));

        Assert.Equal("Wait. What did you do?", moment.Text);
        Assert.Equal(S(100), moment.Start);
        Assert.Equal(S(102), moment.End);
    }

    [Fact]
    public void Plans_are_read_line_by_line()
    {
        IReadOnlyList<CuratedBeat> beats = CuratedPromo.Beats("""
            VO: Did you know?
            CLIP 2
            VO 1: "Well, now you do!"
            Voice-over over moment 3: And more.
            Here's a note that isn't a beat
            """);

        Assert.Equal(
            [new CuratedBeat("Did you know?", null), new CuratedBeat(null, 2), new CuratedBeat("Well, now you do!", 1), new CuratedBeat("And more.", 3)],
            beats);
        Assert.True(beats[1].ClipSpeaks);
    }

    [Fact]
    public void A_plan_is_kept_when_it_names_the_show_and_uses_listed_moments()
    {
        IReadOnlyList<CuratedMoment> moments = [new(1, S(100), S(102), "I didn't know that!"), new(2, S(400), S(402), "Oh, barnacles.")];
        AiTextJob job = CuratedPromo.Job(Request(), moments);

        (string text, string? problem) = job.Format!.Accept("""
            <think>hmm</think>
            VO: Did you know there's a new adventure tonight?
            CLIP 1
            CLIP 9
            VO 2: Well, now you do!
            VO: SpongeBob SquarePants, tonight at 7 on YTV!
            """, job);

        Assert.Null(problem);
        Assert.Equal("""
            VO: Did you know there's a new adventure tonight?
            CLIP 1
            VO 2: Well, now you do!
            VO: SpongeBob SquarePants, tonight at 7 on YTV!
            """.Replace("\r\n", "\n"), text);
    }

    [Theory]
    [InlineData("VO: Big things tonight!\nVO: Only on YTV!", "moments")]
    [InlineData("VO 1: Big things tonight on YTV!", "SpongeBob")]
    [InlineData("VO 1: SpongeBob SquarePants, 99 jokes tonight on YTV!", "99")]
    public void A_plan_is_set_aside_when_it_breaks_the_rules(string reply, string mentioned)
    {
        IReadOnlyList<CuratedMoment> moments = [new(1, S(100), S(102), "I didn't know that!")];
        AiTextJob job = CuratedPromo.Job(Request(), moments);

        (_, string? problem) = job.Format!.Accept(reply, job);

        Assert.NotNull(problem);
        Assert.Contains(mentioned, problem);
    }

    [Fact]
    public void A_closer_naming_the_show_means_the_AI_does_not_have_to()
    {
        IReadOnlyList<CuratedMoment> moments = [new(1, S(100), S(102), "I didn't know that!")];
        CuratedPromoRequest request = Request() with { Closer = "{show}, coming up {when}, only on {channel}!" };
        AiTextJob job = CuratedPromo.Job(request, moments);

        (string text, string? problem) = job.Format!.Accept("VO: Did you know?\nCLIP 1", job);

        Assert.Null(problem);
        Assert.Empty(job.MustSay);
        IReadOnlyList<CuratedBeat> beats = CuratedPromo.WithFixedLines(CuratedPromo.Beats(text), request);
        Assert.Equal(3, beats.Count);
        Assert.True(beats[1].ClipSpeaks);
        Assert.StartsWith("SpongeBob SquarePants, coming up tonight at 7", beats[^1].Voice);
        Assert.EndsWith("only on YTV!", beats[^1].Voice);
    }

    [Fact]
    public void A_line_repeating_a_moment_plays_the_moment_instead()
    {
        IReadOnlyList<CuratedMoment> moments = [new(1, S(100), S(102), "I didn't know that, Patrick!"), new(2, S(200), S(202), "Oh, barnacles.")];
        AiTextJob job = CuratedPromo.Job(Request(), moments);

        (string text, string? problem) = job.Format!.Accept(
            "VO: Did you know?\nVO: I didn't know that, Patrick!\nVO 2: Oh, barnacles.\nVO: SpongeBob SquarePants, tonight on YTV!", job);

        Assert.Null(problem);
        Assert.Equal("VO: Did you know?\nCLIP 1\nCLIP 2\nVO: SpongeBob SquarePants, tonight on YTV!", text);
    }

    [Fact]
    public void Stage_directions_are_not_read_out_and_pauses_are_real()
    {
        IReadOnlyList<CuratedBeat> beats = CuratedPromo.Beats(
            "VO: Big news. The announcer pauses to let the silence hang. It's here!\nVO 2: (laughs) No way.\nPAUSE 1.5\nVO: Bye.");

        Assert.Equal(
            [new CuratedBeat("Big news.", null), CuratedBeat.Silence(S(1)), new CuratedBeat("It's here!", null), new CuratedBeat("No way.", 2),
                CuratedBeat.Silence(S(1.5)), new CuratedBeat("Bye.", null)],
            beats);
        Assert.Equal("Big news. It's here! No way. Bye.", CuratedPromo.Spoken(beats));
    }

    [Fact]
    public void Pauses_are_placed_as_silence()
    {
        IReadOnlyList<CuratedBeat> beats = [new("One.", null), CuratedBeat.Silence(S(1)), new("Two.", null)];

        CuratedTimeline timeline = CuratedPromo.Layout(beats, [S(1), S(1)], [], Request());

        Assert.Equal(timeline.Lines[0].At + S(1) + TimeSpan.FromMilliseconds(150) + S(1) + TimeSpan.FromMilliseconds(150), timeline.Lines[1].At);
    }

    [Fact]
    public void A_pause_after_a_clip_keeps_its_picture_running_silently()
    {
        IReadOnlyList<CuratedMoment> moments = [new(5, S(100), S(102), "Me!")];
        IReadOnlyList<CuratedBeat> beats = [new("Who's in charge?", null), new(null, 5), CuratedBeat.Silence(S(1)), new("Maybe.", null)];

        CuratedTimeline timeline = CuratedPromo.Layout(beats, [S(1), S(1)], moments, Request());

        Assert.Equal(2, timeline.Clips.Count);
        CuratedClip bite = timeline.Clips[0];
        CuratedClip held = timeline.Clips[1];
        Assert.Equal(bite.At + bite.Length, held.At);
        Assert.Equal(bite.SourceStart + bite.Length, held.SourceStart);
        Assert.False(held.WithSound);
        Assert.Equal(held.At + held.Length, timeline.Lines[1].At);
    }

    [Fact]
    public void Every_line_can_have_a_clip_under_it()
    {
        IReadOnlyList<CuratedMoment> moments = [new(1, S(100), S(102), "Hi!"), new(2, S(200), S(202), "Unused!")];
        IReadOnlyList<CuratedBeat> beats = [new("Hello.", null), new(null, 1), new("Bye.", null)];

        CuratedTimeline plain = CuratedPromo.Layout(beats, [S(1), S(1)], moments, Request());
        CuratedTimeline filled = CuratedPromo.Layout(beats, [S(1), S(1)], moments, Request(new GeneratedCuratedClips { ClipUnderEveryLine = true }));

        Assert.Single(plain.Clips);
        Assert.Equal(3, filled.Clips.Count);
        Assert.Equal(filled.Lines[0].At, filled.Clips[0].At);
        Assert.Equal(S(200) - TimeSpan.FromMilliseconds(150), filled.Clips[0].SourceStart);
        Assert.Equal(filled.Lines[1].At, filled.Clips[2].At);
    }

    [Fact]
    public void Directions_are_picked_per_promo_and_the_old_single_prompt_still_works()
    {
        var config = new GeneratedCuratedClips { Directions = ["Mayhem Mondays.", "  ", "Calm, then chaos."] };
        HashSet<string?> picked = [.. Enumerable.Range(0, 40).Select(seed => CuratedPromo.Direction(config, $"seed {seed}"))];
        Assert.Equal(["Calm, then chaos.", "Mayhem Mondays."], picked.Order());

        var old = new GeneratedCuratedClips { Writer = new AiTextConfig { Prompt = "Tease the mystery." } };
        Assert.Equal("Tease the mystery.", CuratedPromo.Direction(old, "seed"));
        Assert.Null(CuratedPromo.Direction(new GeneratedCuratedClips(), "seed"));
    }

    [Fact]
    public void Fixed_lines_fill_their_tokens()
    {
        CuratedPromoRequest request = Request();

        Assert.Equal("Help Wanted at 7 on YTV", CuratedPromo.Fixed("{episode} at {time} on {channel}", request));
        Assert.Null(CuratedPromo.Fixed("  ", request));
        Assert.Equal("{unknown} SpongeBob SquarePants", CuratedPromo.Fixed("{unknown} {show_name}", request));
    }

    [Fact]
    public void Picking_first_keeps_only_listed_moments_and_the_writer_sees_just_those()
    {
        IReadOnlyList<CuratedMoment> moments = [new(1, S(100), S(102), "Hi!"), new(2, S(200), S(202), "Clouds? Clouds!"), new(3, S(300), S(302), "Bye!")];
        AiTextJob pick = CuratedPromo.PickJob(Request(), moments);

        (string text, string? problem) = pick.Format!.Accept("I'd use 3, then 9, then 2 and 3 again.", pick);

        Assert.Null(problem);
        Assert.Equal("3, 2", text);
        IReadOnlyList<CuratedMoment> picked = CuratedPromo.Picked(text, moments);
        Assert.Equal([3, 2], picked.Select(moment => moment.Number));

        AiTextJob write = CuratedPromo.WriteJob(Request(), picked);
        Assert.Equal(["Moment 3", "Moment 2"], write.Facts.Where(fact => fact.Label.StartsWith("Moment ")).Select(fact => fact.Label));
        Assert.Contains(write.Guidance, line => line.Contains("in this order: 3, 2"));
        Assert.NotNull(pick.Format!.Accept("none of them", pick).Problem);
    }

    [Fact]
    public void Clips_can_only_speak_when_allowed()
    {
        IReadOnlyList<CuratedMoment> moments = [new(1, S(100), S(102), "Hi!"), new(2, S(200), S(202), "Bye!")];
        AiTextJob job = CuratedPromo.Job(Request(new GeneratedCuratedClips { LetClipsSpeak = false }), moments);

        (string text, string? problem) = job.Format!.Accept("VO: Ready?\nCLIP 1\nVO 2: SpongeBob SquarePants on YTV!", job);

        Assert.Null(problem);
        Assert.DoesNotContain("CLIP", text);
    }

    [Fact]
    public void Layout_places_lines_and_clips_one_after_another()
    {
        IReadOnlyList<CuratedMoment> moments = [new(1, S(100), S(102), "I didn't know that!")];
        IReadOnlyList<CuratedBeat> beats = [new("Did you know?", null), new(null, 1), new("Well, now you do!", 1)];

        CuratedTimeline timeline = CuratedPromo.Layout(beats, [S(1.5), S(2)], moments, Request());

        Assert.Equal(2, timeline.Lines.Count);
        Assert.Equal(S(0.35), timeline.Lines[0].At);
        Assert.Equal(2, timeline.Clips.Count);
        CuratedClip bite = timeline.Clips[0];
        Assert.Equal(timeline.Lines[0].At + S(1.5) + TimeSpan.FromMilliseconds(150), bite.At);
        Assert.True(bite.WithSound);
        Assert.Equal(1, bite.Volume);
        Assert.Equal(S(100) - TimeSpan.FromMilliseconds(150), bite.SourceStart);
        // The clip runs through the gap after it, so the picture never drops out.
        Assert.Equal(bite.At + bite.Length, timeline.Lines[1].At);
        CuratedClip under = timeline.Clips[1];
        Assert.Equal(timeline.Lines[1].At, under.At);
        Assert.Equal(0.15, under.Volume);
    }

    [Fact]
    public void Layout_speeds_up_then_drops_middle_clips_to_fit()
    {
        IReadOnlyList<CuratedMoment> moments = [new(1, S(100), S(104), "One!"), new(2, S(200), S(204), "Two!"), new(3, S(300), S(304), "Three!")];
        IReadOnlyList<CuratedBeat> beats = [new("Opening line.", null), new(null, 1), new(null, 2), new(null, 3), new("Sign-off line.", null)];

        CuratedTimeline timeline = CuratedPromo.Layout(beats, [S(2), S(2)], moments, Request(duration: 10));

        Assert.Equal(["Opening line.", "Sign-off line."], timeline.Lines.Select(line => line.Text));
        TimeSpan end = timeline.Lines[^1].At + timeline.Lines[^1].Length;
        Assert.True(end <= S(10), $"Ends at {end}");
        Assert.True(timeline.Clips.Count < 3);
    }

    [Fact]
    public void A_curated_layer_is_resolved_with_its_episode_and_drawn_as_its_clips()
    {
        string episode = Path.GetTempFileName();
        try
        {
            var template = new GeneratedPromoTemplate
            {
                Name = "Curated",
                Type = GeneratedPromoType.NowPlaying,
                Duration = S(10),
                Voice = new GeneratedVoiceConfig { Voice = "en_us_006" },
                Layers =
                [
                    new() { Kind = GeneratedLayerKind.NowPlaying },
                    new() { Name = "Clips", Kind = GeneratedLayerKind.Video, Width = 1920, Height = 1080,
                        Video = new GeneratedVideoOptions { Episode = "promoted", Curated = new GeneratedCuratedClips() } }
                ]
            };
            var show = new UpNextListing("SpongeBob SquarePants", Noon, "Help Wanted", episode, TimeSpan.FromMinutes(11));

            ResolvedGeneratedPromo promo = GeneratedPromoResolver.Resolve(template, "YTV", [show], TimeZoneInfo.Utc, Noon)!;

            CuratedPromoRequest curation = Assert.IsType<CuratedPromoRequest>(promo.Curation);
            Assert.Equal(1, curation.LayerIndex);
            Assert.Equal(episode, curation.EpisodePath);
            Assert.Equal(promo.Script, curation.Fallback);

            // Uncurated, it shows no clip (by default), or one clip when set to.
            Assert.Empty(GeneratedPromoResolver.Compose(promo).Layers.OfType<PromoVideoLayer>());
            template.Layers[1].Video!.Curated!.WhenNotCurated = GeneratedCurationFallback.OneClip;
            Assert.Single(GeneratedPromoResolver.Compose(promo).Layers.OfType<PromoVideoLayer>());

            CuratedClip[] clips = [new(episode, S(100), S(1), S(2), true, 1), new(episode, S(300), S(4), S(3), false, 0)];
            PromoComposition composition = GeneratedPromoResolver.Compose(promo with { CuratedClips = clips });
            PromoVideoLayer[] cuts = [.. composition.Layers.OfType<PromoVideoLayer>()];
            Assert.Equal(2, cuts.Length);
            Assert.Equal(S(1), cuts[0].Start);
            Assert.Equal(S(3), cuts[0].End);
            Assert.Equal(S(100), cuts[0].SourceStart);
            Assert.Equal(PromoTransition.None, cuts[0].In);
            PromoMusic sound = Assert.Single(composition.LayerAudio);
            Assert.Equal(S(1), sound.Delay);
            Assert.False(sound.DuckUnderVoice);
        }
        finally
        {
            File.Delete(episode);
        }
    }

    [Fact]
    public void Templates_without_curation_keep_their_render_keys()
    {
        var template = new GeneratedPromoTemplate
        {
            Name = "Plain",
            Voice = new GeneratedVoiceConfig { Voice = "en_us_006" },
            Layers = [new() { Kind = GeneratedLayerKind.UpNext }]
        };
        var show = new UpNextListing("Rugrats", Noon);

        ResolvedGeneratedPromo promo = GeneratedPromoResolver.Resolve(template, "Nick", [show], TimeZoneInfo.Utc, Noon)!;

        Assert.Null(promo.Curation);
        // Render keys serialize the template: a video layer that isn't curated mustn't gain a field.
        Assert.DoesNotContain("Curated", System.Text.Json.JsonSerializer.Serialize(new GeneratedVideoOptions { Episode = "promoted" }), StringComparison.Ordinal);
    }
}
