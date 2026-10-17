using System.Text.Json;
using SchedularrTV.Service.Hosting;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class UpdateCheckerTests
{
    [Theory]
    [InlineData("v1.1.0", "1.0.0", true)]
    [InlineData("v1.0.0", "1.0.0", false)]
    [InlineData("v0.9.0", "1.0.0", false)]
    [InlineData("v1.0", "1.0.0", false)]
    [InlineData("v0.1.0", "0.1.0-nightly.42", true)]
    [InlineData("v0.2.0", "0.1.0-nightly.42", true)]
    [InlineData("v0.0.9", "0.1.0-nightly.42", false)]
    [InlineData("v1.0.0", "dev", false)]
    [InlineData("nightly", "1.0.0", false)]
    public void A_release_is_newer_when_its_number_is_higher_or_it_finishes_a_nightly(string tag, string running, bool newer)
    {
        Assert.Equal(newer, UpdateChecker.IsNewer(tag, running));
    }

    [Theory]
    [InlineData("0.2.1-nightly.58", "0.2.1-nightly.57", 1)]
    [InlineData("0.2.1-nightly.58", "0.2.1-nightly.9", 1)]
    [InlineData("0.2.1-nightly.3", "0.2.0", 1)]
    [InlineData("0.2.1-nightly.3", "0.2.1", -1)]
    [InlineData("0.2.1", "0.2.1-dev", 1)]
    [InlineData("v0.2.1", "0.2.1+abc123", 0)]
    [InlineData("nightly", "0.2.1", 0)]
    public void Versions_order_by_number_then_release_then_pre_release_parts(string left, string right, int expected)
    {
        Assert.Equal(expected, Math.Sign(UpdateChecker.Compare(left, right)));
    }

    [Fact]
    public void A_nightly_is_offered_when_its_titled_version_is_newer_and_brings_its_files()
    {
        using JsonDocument release = JsonDocument.Parse("""
            { "tag_name": "nightly", "name": "Nightly build 0.2.1-nightly.58", "prerelease": true,
              "html_url": "https://github.com/owner/repo/releases/tag/nightly",
              "assets": [
                { "name": "SchedularrTV-nightly-build-win-x64.zip", "browser_download_url": "https://example.test/win.zip", "size": 1234,
                  "digest": "sha256:ABCDEF" },
                { "name": "SchedularrTV-nightly-plugin-local-ai.zip", "browser_download_url": "https://example.test/ai.zip", "size": 99 }
              ] }
            """);
        using JsonDocument untitled = JsonDocument.Parse("""{ "tag_name": "nightly", "name": "Nightly build", "prerelease": true }""");

        AvailableUpdate update = UpdateChecker.NewerNightly(release.RootElement, "0.2.1-nightly.57")!;

        Assert.Equal("0.2.1-nightly.58", update.Version);
        Assert.Equal(UpdateChannel.Nightly, update.Channel);
        Assert.Equal("nightly 58 (0.2.1)", update.Label);
        Assert.Equal(2, update.Assets.Count);
        Assert.Equal("abcdef", update.Assets[0].Sha256);
        Assert.Null(update.Assets[1].Sha256);
        Assert.Null(UpdateChecker.NewerNightly(release.RootElement, "0.2.1-nightly.58"));
        Assert.Null(UpdateChecker.NewerNightly(release.RootElement, "0.2.1"));
        Assert.Null(UpdateChecker.NewerNightly(untitled.RootElement, "0.1.0"));
    }

    [Fact]
    public void The_download_for_this_system_and_each_plugin_is_picked_by_name()
    {
        ReleaseAsset[] assets =
        [
            new("SchedularrTV-v0.3.0-all-plugins.zip", "u0", 1),
            new("SchedularrTV-v0.3.0-build-linux-x64.tar.gz", "u1", 1),
            new("SchedularrTV-v0.3.0-build-win-x64.zip", "u2", 1),
            new("SchedularrTV-v0.3.0-plugin-generated-promos.zip", "u3", 1),
            new("SchedularrTV-v0.3.0-plugin-local-ai.zip", "u4", 1)
        ];
        ReleaseAsset[] older = [new("SchedularrTV-v0.2.0-osx-arm64.zip", "u5", 1), new("SchedularrTV-v0.2.0-plugin-osx-arm64.zip", "u6", 1)];

        Assert.Equal("u2", UpdateInstaller.AppAsset(assets, "win-x64")!.Url);
        Assert.Equal("u1", UpdateInstaller.AppAsset(assets, "linux-x64")!.Url);
        Assert.Null(UpdateInstaller.AppAsset(assets, "osx-arm64"));
        Assert.Equal("u5", UpdateInstaller.AppAsset(older, "osx-arm64")!.Url);
        Assert.Equal("u4", UpdateInstaller.PluginAsset(assets, "local-ai")!.Url);
        Assert.Null(UpdateInstaller.PluginAsset(assets, "tts-piper"));
    }

    [Fact]
    public void An_update_request_round_trips_and_names_the_folder_it_replaces()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"update-request-{Guid.NewGuid():N}");
        try
        {
            var request = new SchedularrTV.Hosting.UpdateRequest
            {
                Version = "0.3.0",
                Downloads = [new() { Kind = SchedularrTV.Hosting.UpdateDownload.PluginKind, Name = "p.zip", Url = "u", PluginId = "local-ai", TargetDirectory = "/data/plugins/local-ai" }],
                RelaunchArguments = ["--data-dir", "/data"]
            };
            string path = SchedularrTV.Hosting.UpdateRequest.PathFor(folder);
            request.Save(path);

            SchedularrTV.Hosting.UpdateRequest loaded = SchedularrTV.Hosting.UpdateRequest.Load(path)!;
            Assert.Equal("0.3.0", loaded.Version);
            Assert.Equal("local-ai", Assert.Single(loaded.Downloads).PluginId);
            Assert.Equal(["--data-dir", "/data"], loaded.RelaunchArguments);
        }
        finally
        {
            TestFolders.Delete(folder);
        }

        string app = Path.Combine(Path.GetTempPath(), "Apps", "SchedularrTV.app");
        Assert.Equal(app, SchedularrTV.Hosting.UpdateRequest.AppRootFor(Path.Combine(app, "Contents", "MacOS", "SchedularrTV")));
        string plain = Path.Combine(Path.GetTempPath(), "SchedularrTV");
        Assert.Equal(plain, SchedularrTV.Hosting.UpdateRequest.AppRootFor(Path.Combine(plain, "SchedularrTV.exe")));
    }

    [Fact]
    public void Only_published_full_releases_count()
    {
        using JsonDocument release = JsonDocument.Parse("""
            { "tag_name": "v2.0.0", "name": "Big one", "html_url": "https://github.com/owner/repo/releases/tag/v2.0.0",
              "draft": false, "prerelease": false, "published_at": "2026-10-03T12:00:00Z" }
            """);
        using JsonDocument preRelease = JsonDocument.Parse("""{ "tag_name": "v3.0.0", "prerelease": true }""");

        AvailableUpdate update = UpdateChecker.Newer(release.RootElement, "1.0.0")!;

        Assert.Equal(("v2.0.0", "Big one", "https://github.com/owner/repo/releases/tag/v2.0.0"), (update.Tag, update.Name, update.Url));
        Assert.Null(UpdateChecker.Newer(release.RootElement, "2.0.0"));
        Assert.Null(UpdateChecker.Newer(preRelease.RootElement, "1.0.0"));
    }
}
