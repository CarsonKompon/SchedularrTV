using SchedularrTV.Core.Configuration;
using SchedularrTV.GeneratedPromos;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class PrerecordedVoiceTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero); // a Monday afternoon
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"prerecorded-{Guid.NewGuid():N}");

    public PrerecordedVoiceTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private GeneratedVoiceTake Take(string name)
    {
        string path = Path.Combine(_folder, name + ".wav");
        File.WriteAllBytes(path, [0]);
        return new GeneratedVoiceTake { Path = path };
    }

    private (GeneratedVoicePack Pack, GeneratedVoiceConfig Voice) Pack()
    {
        GeneratedVoicePhrase intro = new() { Name = "Coming up next", Takes = [Take("intro-long"), Take("intro-short")] };
        GeneratedVoicePhrase show = new()
        {
            Name = "Show", Kind = GeneratedVoicePhraseKind.ShowName,
            Entries = [new() { Key = "The Rugrats", Takes = [Take("rugrats")] }]
        };
        GeneratedVoicePhrase day = new()
        {
            Name = "Day", Kind = GeneratedVoicePhraseKind.Day,
            Entries = [new() { Key = "tonight", Takes = [Take("tonight")] }, new() { Key = "thursday", Takes = [Take("thursday")] }]
        };
        GeneratedVoicePhrase time = new()
        {
            Name = "Time", Kind = GeneratedVoicePhraseKind.Time,
            Entries = [new() { Key = "8", Takes = [Take("8")] }, new() { Key = ":30", Takes = [Take("30")] }, new() { Key = "pm", Takes = [Take("pm")] }]
        };
        GeneratedVoicePack pack = new() { Name = "Test", Phrases = [intro, show, day, time], Gap = TimeSpan.Zero };
        pack.Templates.Add(new GeneratedVoicePackTemplate { Name = "Short", Parts = [new() { Phrase = intro.Id }, new() { Phrase = show.Id }] });
        pack.Templates.Add(new GeneratedVoicePackTemplate
        {
            Name = "Long",
            Parts = [new() { Phrase = show.Id }, new() { Phrase = day.Id }, new() { Phrase = time.Id }]
        });
        GeneratedVoiceConfig voice = new()
        {
            Mode = GeneratedVoiceMode.Prerecorded,
            Prerecorded = new GeneratedPrerecordedVoiceConfig { Pack = pack.Id, Linked = pack }
        };
        return (pack, voice);
    }

    [Fact]
    public void Show_names_match_loosely_and_unrecorded_shows_skip_templates()
    {
        (_, GeneratedVoiceConfig voice) = Pack();
        PrerecordedVoiceRequest? request = PrerecordedVoice.Plan(voice, [new("Rugrats", At.AddHours(5.5))], TimeZoneInfo.Utc, At, "seed");
        Assert.NotNull(request);
        Assert.Equal(2, request!.Candidates.Count);

        Assert.Null(PrerecordedVoice.Plan(voice, [new("Hey Arnold!", At.AddHours(5.5))], TimeZoneInfo.Utc, At, "seed"));
    }

    [Fact]
    public void Time_is_said_from_hour_minutes_and_am_pm()
    {
        (GeneratedVoicePack pack, GeneratedVoiceConfig voice) = Pack();
        GeneratedVoicePhrase time = pack.Phrases[3];
        Assert.Equal(new[] { "8", ":30", "pm" }, PrerecordedVoice.TimeKeys(time, At.AddHours(5.5), TimeZoneInfo.Utc));
        Assert.Equal(new[] { "8", "pm" }, PrerecordedVoice.TimeKeys(time, At.AddHours(5), TimeZoneInfo.Utc));

        // 8:15 has no ":15" take, so only the template without a time can be said.
        PrerecordedVoiceRequest? request = PrerecordedVoice.Plan(voice, [new("Rugrats", At.AddHours(5.25))], TimeZoneInfo.Utc, At, "seed");
        Assert.Equal("Short", Assert.Single(request!.Candidates).Name);
    }

    [Fact]
    public void Whole_times_are_said_in_one_take_and_fall_back_to_parts()
    {
        (GeneratedVoicePack pack, _) = Pack();
        GeneratedVoicePhrase time = pack.Phrases[3];
        time.Combined = true;
        time.Entries.Add(new() { Key = "8:30 pm", Takes = [Take("eight-thirty-pm")] });

        IReadOnlyList<PrerecordedPart>? whole = PrerecordedVoice.Parts(time, 0, [new("Rugrats", At.AddHours(5.5))], TimeZoneInfo.Utc, At);
        Assert.Equal("8:30 PM", Assert.Single(whole!).Label);

        // 8:00 PM has no whole take, so it's said from its parts ("8" "PM").
        IReadOnlyList<PrerecordedPart>? parts = PrerecordedVoice.Parts(time, 0, [new("Rugrats", At.AddHours(5))], TimeZoneInfo.Utc, At);
        Assert.Equal(new[] { "8", "PM" }, parts!.Select(part => part.Label));

        // Without AM/PM in the take, the whole time is followed by the separate "pm" take.
        time.AmPmInTake = false;
        time.Entries.Add(new() { Key = "8:30", Takes = [Take("eight-thirty")] });
        whole = PrerecordedVoice.Parts(time, 0, [new("Rugrats", At.AddHours(5.5))], TimeZoneInfo.Utc, At);
        Assert.Equal(new[] { "8:30", "PM" }, whole!.Select(part => part.Label));
    }

    [Fact]
    public void Day_and_time_takes_fall_back_from_tonight_to_the_weekday()
    {
        GeneratedVoicePhrase dayTime = new()
        {
            Name = "When", Kind = GeneratedVoicePhraseKind.DayTime,
            Entries = [new() { Key = "monday at 8:30 pm", Takes = [Take("monday-830")] }, new() { Key = "thursday at noon", Takes = [Take("thursday-noon")] }]
        };

        IReadOnlyList<PrerecordedPart>? tonight = PrerecordedVoice.Parts(dayTime, 0, [new("Rugrats", At.AddHours(5.5))], TimeZoneInfo.Utc, At);
        Assert.Equal("Monday at 8:30 PM", Assert.Single(tonight!).Label);

        IReadOnlyList<PrerecordedPart>? noon = PrerecordedVoice.Parts(dayTime, 0, [new("Rugrats", At.AddDays(2).AddHours(21))], TimeZoneInfo.Utc, At);
        Assert.Equal("Thursday at noon", Assert.Single(noon!).Label);

        Assert.Null(PrerecordedVoice.Parts(dayTime, 0, [new("Rugrats", At.AddHours(5))], TimeZoneInfo.Utc, At));
        Assert.Contains("monday at 2:45 pm", GeneratedVoiceKeys.WholeTimes(false, true, 15).Select(time => GeneratedVoiceKeys.DayTime("monday", time)));
    }

    [Fact]
    public void Channel_names_say_the_block_while_in_one_and_the_channel_otherwise()
    {
        GeneratedVoicePhrase channel = new()
        {
            Name = "Channel", Kind = GeneratedVoicePhraseKind.ChannelName,
            Entries =
            [
                new() { Key = GeneratedVoiceKeys.Channel("nick"), Takes = [Take("nick")] },
                new() { Key = GeneratedVoiceKeys.Block("nick", "Saturday Morning"), Takes = [Take("saturday")] }
            ]
        };
        List<UpNextListing> shows = [new("Rugrats", At.AddHours(1))];

        Assert.EndsWith("nick.wav", Assert.Single(PrerecordedVoice.Parts(channel, 0, shows, TimeZoneInfo.Utc, At,
            new GeneratedVoiceContext("nick", "Saturday Morning"))!).Takes[0]);

        channel.UseBlockNames = true;
        Assert.EndsWith("saturday.wav", Assert.Single(PrerecordedVoice.Parts(channel, 0, shows, TimeZoneInfo.Utc, At,
            new GeneratedVoiceContext("nick", "Saturday Morning"))!).Takes[0]);

        // A block without its own recording, or no block, says the channel.
        Assert.EndsWith("nick.wav", Assert.Single(PrerecordedVoice.Parts(channel, 0, shows, TimeZoneInfo.Utc, At,
            new GeneratedVoiceContext("nick", "Primetime"))!).Takes[0]);
        Assert.Null(PrerecordedVoice.Parts(channel, 0, shows, TimeZoneInfo.Utc, At, new GeneratedVoiceContext("cartoon-network")));
        Assert.Null(PrerecordedVoice.Parts(channel, 0, shows, TimeZoneInfo.Utc, At));
    }

    [Fact]
    public void Days_fall_back_to_the_weekday()
    {
        Assert.Equal(new[] { "tonight", "today", "monday" }, PrerecordedVoice.DayKeys(At.AddHours(5), At, TimeZoneInfo.Utc));
        Assert.Equal(new[] { "thursday" }, PrerecordedVoice.DayKeys(At.AddDays(3), At, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Shorter_takes_are_swapped_in_when_time_is_tight()
    {
        (_, GeneratedVoiceConfig voice) = Pack();
        voice.Prerecorded!.Templates = [voice.Prerecorded.Linked!.Templates[0].Id];
        PrerecordedVoiceRequest request = PrerecordedVoice.Plan(voice, [new("Rugrats", At.AddHours(5))], TimeZoneInfo.Utc, At, "seed")!;
        TimeSpan Length(string path) => Path.GetFileNameWithoutExtension(path) switch
        {
            "intro-long" => TimeSpan.FromSeconds(3),
            "intro-short" => TimeSpan.FromSeconds(1),
            _ => TimeSpan.FromSeconds(2)
        };

        PrerecordedChoice tight = PrerecordedVoice.Choose(request, Length, TimeSpan.FromSeconds(3.5));
        Assert.EndsWith("intro-short.wav", tight.Takes[0]);
        Assert.Equal(TimeSpan.FromSeconds(3), tight.Length);

        PrerecordedChoice filled = PrerecordedVoice.Choose(request with { Fit = GeneratedVoiceFit.Fill }, Length, TimeSpan.FromSeconds(10));
        Assert.EndsWith("intro-long.wav", filled.Takes[0]);

        // Nothing fits: the shortest there is, for the render to speed up.
        PrerecordedChoice over = PrerecordedVoice.Choose(request, Length, TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(3), over.Length);
    }

    [Fact]
    public void Negative_pauses_overlap_parts_but_never_swallow_a_take()
    {
        (GeneratedVoicePack pack, GeneratedVoiceConfig voice) = Pack();
        pack.Gap = TimeSpan.FromMilliseconds(-100);
        voice.Prerecorded!.Templates = [pack.Templates[0].Id];
        PrerecordedVoiceRequest request = PrerecordedVoice.Plan(voice, [new("Rugrats", At.AddHours(5))], TimeZoneInfo.Utc, At, "seed")!;
        PrerecordedChoice choice = PrerecordedVoice.Choose(request, _ => TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromMilliseconds(1900), choice.Length);
        Assert.Equal(TimeSpan.FromMilliseconds(-100), Assert.Single(choice.Joints));

        // A part's own pause wins over the pack's.
        pack.Templates[0].Parts[1].Gap = TimeSpan.FromMilliseconds(250);
        request = PrerecordedVoice.Plan(voice, [new("Rugrats", At.AddHours(5))], TimeZoneInfo.Utc, At, "seed")!;
        Assert.Equal(TimeSpan.FromMilliseconds(2250), PrerecordedVoice.Choose(request, _ => TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10)).Length);

        // An overlap is held to 40% of the shorter take.
        Assert.Equal(TimeSpan.FromMilliseconds(-80), PrerecordedVoice.Joint(TimeSpan.FromMilliseconds(-300), TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Choice_is_fixed_by_the_promo()
    {
        (_, GeneratedVoiceConfig voice) = Pack();
        PrerecordedVoiceRequest request = PrerecordedVoice.Plan(voice, [new("Rugrats", At.AddHours(5.5))], TimeZoneInfo.Utc, At, "seed")!;
        PrerecordedChoice first = PrerecordedVoice.Choose(request, _ => TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
        PrerecordedChoice again = PrerecordedVoice.Choose(request, _ => TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
        Assert.Equal(first.Takes, again.Takes);
    }

    [Fact]
    public void Negative_pauses_survive_saving_from_the_editor()
    {
        (GeneratedVoicePack pack, _) = Pack();
        pack.Gap = TimeSpan.FromMilliseconds(-60);
        pack.Templates[0].Parts[1].Gap = TimeSpan.FromMilliseconds(-80);
        pack.Templates[0].Parts[0].Gap = TimeSpan.FromSeconds(-1.5);
        SchedulerConfig config = new();
        config.Settings.GeneratedPromosVoicePacks.Add(pack);

        // The visual editor saves through the comment merger, which writes durations in their friendly form (-80ms).
        string yaml = YamlCommentMerger.Merge(string.Empty, SchedulerConfigLoader.Serialize(config));
        GeneratedVoicePack read = Assert.Single(SchedulerConfigLoader.Parse(yaml).Settings.GeneratedPromosVoicePacks);
        Assert.Equal(TimeSpan.FromMilliseconds(-60), read.Gap);
        Assert.Equal(TimeSpan.FromMilliseconds(-80), read.Templates[0].Parts[1].Gap);
        Assert.Equal(TimeSpan.FromSeconds(-1.5), read.Templates[0].Parts[0].Gap);
        Assert.Equal("-80ms", DurationText.Format(TimeSpan.FromMilliseconds(-80)));
    }

    [Fact]
    public void A_channels_shows_come_from_its_rotation_blocks_and_collections()
    {
        SchedularrTV.Core.Media.MediaItem Episode(string id, string show) =>
            new() { Id = id, Title = $"{show} {id}", Kind = SchedularrTV.Core.Media.MediaKind.Episode, SeriesId = show, SeriesName = show };

        SchedulerConfig config = new();
        config.GlobalCollections["toons"] = new ContentSelector { Query = "show:'Doug'" };
        BlockConfig block = new() { Name = "Morning", Shows = [new ContentSelector { Collection = "toons" }, new ContentSelector { Collection = "missing" }] };
        ChannelConfig channel = new() { Id = "nick", Name = "Nick", BaseRotation = new ContentSelector { Query = "show:'Rugrats'" }, Blocks = [block] };
        List<SchedularrTV.Core.Media.MediaItem> library = [Episode("1", "Rugrats"), Episode("2", "Doug"), Episode("3", "Hey Arnold!")];

        Assert.Equal(new[] { "Doug", "Rugrats" }, SchedularrTV.Core.Scheduling.ContentSelectorQuery.Titles(config, SchedularrTV.Core.Scheduling.ContentSelectorQuery.Of(channel), library));
        Assert.Equal(new[] { "Doug" }, SchedularrTV.Core.Scheduling.ContentSelectorQuery.Titles(config, SchedularrTV.Core.Scheduling.ContentSelectorQuery.Of(block), library));

        // Movies are listed apart from shows.
        channel.BaseRotation = new ContentSelector { Query = "show:'Rugrats' OR title:'The Lion King'" };
        library.Add(new() { Id = "4", Title = "The Lion King", Kind = SchedularrTV.Core.Media.MediaKind.Movie });
        (IReadOnlyList<string> shows, IReadOnlyList<string> movies) = SchedularrTV.Core.Scheduling.ContentSelectorQuery.ShowsAndMovies(
            config, SchedularrTV.Core.Scheduling.ContentSelectorQuery.Of(channel), library);
        Assert.Equal(new[] { "Doug", "Rugrats" }, shows);
        Assert.Equal(new[] { "The Lion King" }, movies);
    }

    [Fact]
    public void A_show_named_again_says_more_then_even_more()
    {
        (GeneratedVoicePack pack, GeneratedVoiceConfig voice) = Pack();
        GeneratedVoicePhrase show = pack.Phrases[1];
        GeneratedVoiceEntry rugrats = show.Entries[0];
        rugrats.More = [Take("more-rugrats")];
        pack.Templates.Clear();
        pack.Templates.Add(new GeneratedVoicePackTemplate
        {
            Name = "Marathon",
            Parts = [new() { Phrase = show.Id }, new() { Phrase = pack.Phrases[0].Id }, new() { Phrase = show.Id }, new() { Phrase = show.Id }]
        });

        // "Even more" isn't recorded yet, so the third mention falls back to "more".
        PrerecordedVoiceRequest request = PrerecordedVoice.Plan(voice, [new("Rugrats", At.AddHours(5))], TimeZoneInfo.Utc, At, "seed")!;
        Assert.Equal(new[] { "Rugrats", "Coming up next", "More Rugrats", "More Rugrats" }, Assert.Single(request.Candidates).Parts.Select(part => part.Label));
        Assert.EndsWith("more-rugrats.wav", request.Candidates[0].Parts[2].Takes[0]);

        rugrats.EvenMore = [Take("even-more-rugrats")];
        request = PrerecordedVoice.Plan(voice, [new("Rugrats", At.AddHours(5))], TimeZoneInfo.Utc, At, "seed")!;
        Assert.Equal("Even more Rugrats", request.Candidates[0].Parts[3].Label);

        // Up Next slots that turn out to be the same show count as naming it again; a different show starts over.
        pack.Templates[0].Parts = [new() { Phrase = show.Id }, new() { Phrase = show.Id, Show = 1 }];
        request = PrerecordedVoice.Plan(voice, [new("Rugrats", At.AddHours(5)), new("Rugrats", At.AddHours(5.5))], TimeZoneInfo.Utc, At, "seed")!;
        Assert.Equal(new[] { "Rugrats", "More Rugrats" }, request.Candidates[0].Parts.Select(part => part.Label));

        show.Entries.Add(new() { Key = "Doug", Takes = [Take("doug")] });
        request = PrerecordedVoice.Plan(voice, [new("Rugrats", At.AddHours(5)), new("Doug", At.AddHours(5.5))], TimeZoneInfo.Utc, At, "seed")!;
        Assert.Equal(new[] { "Rugrats", "Doug" }, request.Candidates[0].Parts.Select(part => part.Label));
    }

    [Fact]
    public void Inline_templates_are_used_with_the_packs_or_on_their_own()
    {
        (GeneratedVoicePack pack, GeneratedVoiceConfig voice) = Pack();
        GeneratedVoicePackTemplate inline = new() { Name = "Just this promo", Parts = [new() { Phrase = pack.Phrases[1].Id }, new() { Phrase = pack.Phrases[0].Id }] };
        voice.Prerecorded!.InlineTemplates = [inline];

        PrerecordedVoiceRequest request = PrerecordedVoice.Plan(voice, [new("Rugrats", At.AddHours(5.5))], TimeZoneInfo.Utc, At, "seed")!;
        Assert.Equal(new[] { "Short", "Long", "Just this promo" }, request.Candidates.Select(candidate => candidate.Name));

        // A pick of the pack's templates leaves inline ones in.
        voice.Prerecorded.Templates = [pack.Templates[1].Id];
        request = PrerecordedVoice.Plan(voice, [new("Rugrats", At.AddHours(5.5))], TimeZoneInfo.Utc, At, "seed")!;
        Assert.Equal(new[] { "Long", "Just this promo" }, request.Candidates.Select(candidate => candidate.Name));

        // With the pack's turned off, only inline ones are said.
        voice.Prerecorded.UsePackTemplates = false;
        request = PrerecordedVoice.Plan(voice, [new("Rugrats", At.AddHours(5.5))], TimeZoneInfo.Utc, At, "seed")!;
        Assert.Equal("Just this promo", Assert.Single(request.Candidates).Name);
        Assert.Equal(new[] { "Rugrats", "Coming up next" }, request.Candidates[0].Parts.Select(part => part.Label));

        // An inline template that's off (or the pack's off with none inline) says nothing.
        inline.Enabled = false;
        Assert.Null(PrerecordedVoice.Plan(voice, [new("Rugrats", At.AddHours(5.5))], TimeZoneInfo.Utc, At, "seed"));
    }

    [Fact]
    public void Inline_templates_round_trip_through_the_schedule_file()
    {
        (GeneratedVoicePack pack, _) = Pack();
        GeneratedPromoTemplate template = new()
        {
            Name = "Promo",
            Voice = new GeneratedVoiceConfig
            {
                Mode = GeneratedVoiceMode.Prerecorded,
                Prerecorded = new GeneratedPrerecordedVoiceConfig
                {
                    Pack = pack.Id, UsePackTemplates = false,
                    InlineTemplates = [new() { Name = "Mine", Parts = [new() { Phrase = pack.Phrases[0].Id, Gap = TimeSpan.FromMilliseconds(-40) }] }]
                }
            }
        };
        SchedulerConfig config = new();
        config.Settings.GeneratedPromosVoicePacks.Add(pack);
        config.Channels.Add(new ChannelConfig { Id = "nick", Name = "Nick", Number = 1, BaseRotation = new() { Query = "*" }, GeneratedPromos = new() { Templates = [template] } });

        SchedulerConfig loaded = SchedulerConfigLoader.Parse(YamlCommentMerger.Merge(string.Empty, SchedulerConfigLoader.Serialize(config)));
        GeneratedPrerecordedVoiceConfig read = loaded.Channels[0].GeneratedPromos!.Templates[0].Voice!.Prerecorded!;
        Assert.False(read.PackTemplatesUsed);
        GeneratedVoicePackTemplate inline = Assert.Single(read.InlineTemplates!);
        Assert.Equal("Mine", inline.Name);
        Assert.Equal(pack.Phrases[0].Id, Assert.Single(inline.Parts).Phrase);
        Assert.Equal(TimeSpan.FromMilliseconds(-40), inline.Parts[0].Gap);
    }

    [Fact]
    public void Voice_packs_round_trip_through_the_schedule_file()
    {
        (GeneratedVoicePack pack, _) = Pack();
        pack.Phrases[1].Entries[0].More = [Take("more-rugrats")];
        pack.Phrases[1].Entries[0].EvenMore = [];
        SchedulerConfig config = new();
        config.Settings.GeneratedPromosVoicePacks.Add(pack);
        SchedulerConfig loaded = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config));
        GeneratedVoicePack read = Assert.Single(loaded.Settings.GeneratedPromosVoicePacks);
        Assert.EndsWith("more-rugrats.wav", Assert.Single(read.Phrases[1].Entries[0].More!).Path);
        Assert.Null(read.Phrases[1].Entries[0].EvenMore);
        Assert.Equal(pack.Id, read.Id);
        Assert.Equal(4, read.Phrases.Count);
        Assert.Equal(GeneratedVoicePhraseKind.Time, read.Phrases[3].Kind);
        Assert.Equal(":30", read.Phrases[3].Entries[1].Key);
        Assert.Equal(2, read.Templates.Count);
    }
}
