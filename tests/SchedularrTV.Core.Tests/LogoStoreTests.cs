using SchedularrTV.Service.Editor;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class LogoStoreTests
{
    [Theory]
    [InlineData("http://localhost:5252/logos/ch8.png?v=123", "ch8.png")]
    [InlineData("/logos/ch8-1a2b3c4d.webp", "ch8-1a2b3c4d.webp")]
    [InlineData("https://example.com/images/ch8.png", null)]
    [InlineData("/logos/../settings.json", null)]
    [InlineData("/logos/sub/ch8.png", null)]
    [InlineData("/logos/ch8.txt", null)]
    [InlineData(null, null)]
    public void Only_files_in_the_logo_folder_count_as_stored(string? logo, string? expected)
    {
        Assert.Equal(expected, LogoStore.StoredFileName(logo));
    }
}
