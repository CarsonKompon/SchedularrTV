using Microsoft.Extensions.Options;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class PiperTextToSpeechTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"piper-voices-{Guid.NewGuid():N}");

    public PiperTextToSpeechTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public void Voices_come_from_the_model_folder_with_each_speaker_listed_separately()
    {
        Model("en_US-ryan-high", """{"dataset":"ryan","audio":{"quality":"high"},"language":{"name_english":"English","region":"US","country_english":"United States"},"num_speakers":1,"speaker_id_map":{}}""");
        Model("en_GB-semaine-medium", """{"dataset":"semaine","audio":{"quality":"medium"},"language":{"name_english":"English","region":"GB","country_english":"Great Britain"},"num_speakers":2,"speaker_id_map":{"spike":1,"prudence":0},"phoneme_id_map":{"X":[1],"x":[2]}}""");
        Model("mystery", null);
        Model("broken", "{ not json");

        IReadOnlyList<TtsVoice> voices = PiperTextToSpeech.ListVoices(_directory);

        Assert.Equal(
        [
            ("broken", "Broken", "Other"),
            ("mystery", "Mystery", "Other"),
            ("en_GB-semaine-medium#prudence", "Semaine: Prudence, medium", "UK English"),
            ("en_GB-semaine-medium#spike", "Semaine: Spike, medium", "UK English"),
            ("en_US-ryan-high", "Ryan, high", "US English")
        ], voices.Select(voice => (voice.Id, voice.Name, voice.Language)));
        Assert.Equal("United States. Piper, high quality.", voices[^1].Description);
    }

    [Fact]
    public async Task Setup_problems_are_reported_and_unknown_voices_are_refused()
    {
        var missing = new PiperTextToSpeech(Options.Create(new PiperOptions
        {
            ExecutablePath = Path.Combine(_directory, "piper.exe"),
            VoicesPath = _directory
        }));
        Assert.Contains("isn't installed", missing.SetupProblem);
        await Assert.ThrowsAsync<InvalidOperationException>(() => missing.SynthesizeAsync("Hello", "en_US-ryan-high"));

        File.WriteAllText(Path.Combine(_directory, "piper.exe"), "");
        Assert.Contains("No Piper voices", missing.SetupProblem);

        Model("en_US-ryan-high", """{"dataset":"ryan","audio":{"quality":"high"},"language":{"name_english":"English","region":"US"}}""");
        Assert.Null(missing.SetupProblem);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => missing.SynthesizeAsync("Hello", "..\\elsewhere"));
        Assert.Contains("no Piper voice", error.Message);

        Assert.Equal(["--model", "m.onnx", "--speaker", "3", "--output_file", "o.wav"], PiperTextToSpeech.Arguments("m.onnx", 3, "o.wav"));
        Assert.Equal(["--model", "m.onnx", "--output_file", "o.wav"], PiperTextToSpeech.Arguments("m.onnx", null, "o.wav"));
    }

    [Fact]
    public async Task Installed_piper_speaks_a_read_out_including_a_named_speaker()
    {
        var installed = new PiperOptions();
        if (!File.Exists(installed.ExecutablePath) || !File.Exists(Path.Combine(installed.VoicesPath, "en_GB-semaine-medium.onnx")))
        {
            return;
        }

        var piper = new PiperTextToSpeech(Options.Create(installed));

        TtsAudio audio = await piper.SynthesizeAsync("Coming up next...\nRocket Power!", "en_GB-semaine-medium#spike");

        Assert.Equal("wav", audio.Extension);
        Assert.Equal("RIFF"u8.ToArray(), audio.Data[..4]);
        Assert.True(audio.Data.Length > 20_000, $"only {audio.Data.Length} bytes of audio");
        Assert.Contains(await piper.GetVoicesAsync(), voice => voice.Id == "en_GB-semaine-medium#spike");
    }

    private void Model(string id, string? json)
    {
        File.WriteAllText(Path.Combine(_directory, $"{id}.onnx"), "");
        if (json is not null)
        {
            File.WriteAllText(Path.Combine(_directory, $"{id}.onnx.json"), json);
        }
    }
}
