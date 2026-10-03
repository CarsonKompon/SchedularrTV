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
