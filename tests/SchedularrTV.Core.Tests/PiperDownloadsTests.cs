using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class PiperDownloadsTests
{
    [Theory]
    [InlineData("WINDOWS", Architecture.X64, "piper_windows_amd64.zip")]
    [InlineData("WINDOWS", Architecture.Arm64, "piper_windows_amd64.zip")]
    [InlineData("LINUX", Architecture.X64, "piper_linux_x86_64.tar.gz")]
    [InlineData("LINUX", Architecture.Arm64, "piper_linux_aarch64.tar.gz")]
    [InlineData("LINUX", Architecture.Arm, "piper_linux_armv7l.tar.gz")]
    [InlineData("OSX", Architecture.Arm64, "piper_macos_aarch64.tar.gz")]
    [InlineData("OSX", Architecture.X64, "piper_macos_x64.tar.gz")]
    [InlineData("LINUX", Architecture.Wasm, null)]
    public void The_right_piper_download_is_picked_for_each_computer(string os, Architecture architecture, string? expected)
    {
        Assert.Equal(expected, PiperDownloads.ProgramAsset(OSPlatform.Create(os), architecture));
    }

    [Fact]
    public void The_voice_catalogue_lists_downloadable_voices_by_language()
    {
        const string catalogue = """
            {
              "en_US-lessac-medium": {
                "key": "en_US-lessac-medium", "name": "lessac", "quality": "medium", "num_speakers": 1,
                "language": { "code": "en_US", "name_english": "English", "country_english": "United States" },
                "files": {
                  "en/en_US/lessac/medium/en_US-lessac-medium.onnx": { "size_bytes": 63201294 },
                  "en/en_US/lessac/medium/en_US-lessac-medium.onnx.json": { "size_bytes": 4885 },
                  "en/en_US/lessac/medium/MODEL_CARD": { "size_bytes": 300 }
                }
              },
              "en_GB-vctk-medium": {
                "name": "vctk", "quality": "medium", "num_speakers": 109,
                "language": { "code": "en_GB", "name_english": "English", "country_english": "Great Britain" },
                "files": {
                  "en/en_GB/vctk/medium/en_GB-vctk-medium.onnx": { "size_bytes": 76952753 },
                  "en/en_GB/vctk/medium/en_GB-vctk-medium.onnx.json": {}
                }
              },
              "broken": { "name": "broken", "files": { "README": {} } }
            }
            """;
        using JsonDocument json = JsonDocument.Parse(catalogue);

        IReadOnlyList<PiperCatalogVoice> voices = PiperDownloads.ParseCatalog(json.RootElement);

        Assert.Equal(["English (Great Britain)", "English (United States)"], voices.Select(voice => voice.Language));
        PiperCatalogVoice lessac = voices[1];
        Assert.Equal("en_US-lessac-medium", lessac.Key);
        Assert.Equal(63201294, lessac.SizeBytes);
        Assert.Equal("en/en_US/lessac/medium/en_US-lessac-medium.onnx.json", lessac.ConfigPath);
        Assert.Equal(109, voices[0].Speakers);
        Assert.Equal(
            "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/lessac/medium/en_US-lessac-medium.onnx",
            PiperDownloads.FileUrl(lessac.ModelPath));
    }

    [Fact]
    public void Installed_voices_are_the_models_in_the_folder_and_can_be_removed()
    {
        string folder = Path.Combine(Path.GetTempPath(), "piper-voices-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "en_US-lessac-medium.onnx"), "");
            File.WriteAllText(Path.Combine(folder, "en_US-lessac-medium.onnx.json"), "{}");
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "");

            Assert.Equal(["en_US-lessac-medium"], PiperDownloads.InstalledVoices(folder));

            PiperDownloads.RemoveVoice("en_US-lessac-medium", folder);
            Assert.Empty(PiperDownloads.InstalledVoices(folder));
            Assert.Throws<ArgumentException>(() => PiperDownloads.RemoveVoice("../escape", folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Relative_paths_are_taken_from_the_data_folder_and_blank_means_the_default()
    {
        string data = Path.Combine(Path.GetTempPath(), "data");
        var paths = new PiperPaths(data);

        Assert.Equal("fallback", paths.Resolve("  ", "fallback"));
        Assert.Equal(Path.GetFullPath(Path.Combine(data, "piper", "voices")), paths.Resolve(Path.Combine("piper", "voices"), "fallback"));
    }
}
