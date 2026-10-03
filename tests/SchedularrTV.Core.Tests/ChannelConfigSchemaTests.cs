using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SchedularrTV.Providers.ErsatzTv;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class ChannelConfigSchemaTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "channel-schema-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void An_unversioned_config_gets_the_current_version_first_and_keeps_everything_else()
    {
        JsonObject upgraded = ChannelConfigSchema.Upgrade(Parse("""
            { "playout": { "folder": "./playout" },
              "normalization": { "video": { "format": "hevc", "accel": "qsv", "width": 1280 }, "audio": { "format": "aac" } } }
            """))!;

        Assert.Equal(ChannelConfigSchema.Current, (string?)upgraded["version"]);
        Assert.Equal("version", upgraded.First().Key);
        Assert.Equal("hevc", (string?)upgraded["normalization"]!["video"]!["format"]);
        Assert.Equal("qsv", (string?)upgraded["normalization"]!["video"]!["accel"]);
        Assert.Equal(1280, (int?)upgraded["normalization"]!["video"]!["width"]);
        Assert.Null(upgraded["normalization"]!["video"]!["mode"]);
    }

    [Fact]
    public void A_null_format_meant_copy_and_now_says_so()
    {
        JsonObject upgraded = ChannelConfigSchema.Upgrade(Parse("""
            { "version": "https://ersatztv.org/channel/version/0.0.2",
              "normalization": { "video": { "format": null }, "audio": { "format": null, "normalize_loudness": true } } }
            """))!;

        JsonNode video = upgraded["normalization"]!["video"]!;
        JsonNode audio = upgraded["normalization"]!["audio"]!;
        Assert.Equal(ChannelConfigSchema.Current, (string?)upgraded["version"]);
        Assert.Equal("copy", (string?)video["mode"]);
        Assert.False(((JsonObject)video).ContainsKey("format"));
        Assert.Equal("copy", (string?)audio["mode"]);
        Assert.False((bool)audio["normalize_loudness"]!);
    }

    [Theory]
    [InlineData("https://ersatztv.org/channel/version/0.1.0")]
    [InlineData("https://ersatztv.org/channel/version/0.1.7")]
    [InlineData("https://ersatztv.org/channel/version/0.2.0")]
    [InlineData("something else")]
    public void Current_newer_and_unrecognised_versions_are_left_alone(string version)
    {
        Assert.Null(ChannelConfigSchema.Upgrade(Parse($$"""{ "version": "{{version}}", "playout": { "folder": "./playout" } }""")));
    }

    [Fact]
    public async Task Files_are_upgraded_once_with_the_original_kept()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "channel.json");
        const string original = """{ "playout": { "folder": "./playout" } }""";
        await File.WriteAllTextAsync(path, original);

        Assert.True(await ChannelConfigSchema.UpgradeFileAsync(path, NullLogger.Instance, CancellationToken.None));
        Assert.False(await ChannelConfigSchema.UpgradeFileAsync(path, NullLogger.Instance, CancellationToken.None));

        Assert.Equal(ChannelConfigSchema.Current, (string?)JsonNode.Parse(await File.ReadAllTextAsync(path))!["version"]);
        Assert.Equal(original, await File.ReadAllTextAsync(path + ".bak"));
    }

    [Fact]
    public async Task A_file_that_isnt_json_is_left_alone()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "channel.json");
        await File.WriteAllTextAsync(path, "{ not json");

        Assert.False(await ChannelConfigSchema.UpgradeFileAsync(path, NullLogger.Instance, CancellationToken.None));
        Assert.Equal("{ not json", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void New_channel_configs_are_written_at_the_current_version()
    {
        Assert.Null(ChannelConfigSchema.Upgrade(Parse(LineupPublisher.DefaultChannelConfig)));
        Assert.Equal(ChannelConfigSchema.Current, (string?)Parse(LineupPublisher.DefaultChannelConfig)["version"]);
    }
}
