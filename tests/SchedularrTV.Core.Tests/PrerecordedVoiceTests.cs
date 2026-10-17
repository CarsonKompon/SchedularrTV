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
    public void Voice_packs_round_trip_through_the_schedule_file()
    {
        (GeneratedVoicePack pack, _) = Pack();
        SchedulerConfig config = new();
        config.Settings.GeneratedPromosVoicePacks.Add(pack);
        SchedulerConfig loaded = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config));
        GeneratedVoicePack read = Assert.Single(loaded.Settings.GeneratedPromosVoicePacks);
        Assert.Equal(pack.Id, read.Id);
        Assert.Equal(4, read.Phrases.Count);
        Assert.Equal(GeneratedVoicePhraseKind.Time, read.Phrases[3].Kind);
        Assert.Equal(":30", read.Phrases[3].Entries[1].Key);
        Assert.Equal(2, read.Templates.Count);
    }
}
