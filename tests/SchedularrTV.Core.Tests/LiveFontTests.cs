using SchedularrTV.LiveCapture;
using SchedularrTV.News;
using SchedularrTV.Radio;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class LiveFontTests
{
    [Theory]
    [InlineData("Sans", "Inter")]
    [InlineData("Mono", "JetBrains Mono")]
    [InlineData("Segment", LiveFonts.Segmented)]
    [InlineData("Theme", null)]
    public void Old_presets_carry_over_as_families(string preset, string? family) =>
        Assert.Equal(family, LiveFonts.FromPreset(preset));

    [Fact]
    public void Installed_fonts_become_a_stack_and_web_fonts_bring_their_stylesheet()
    {
        Assert.Equal("\"Comic Sans MS\", \"Segoe UI\", Arial, sans-serif", LiveFonts.Stack("Comic Sans MS"));
        Assert.Null(LiveFonts.StylesheetUrl("Comic Sans MS"));
        Assert.StartsWith("\"Oswald\", \"Arial Narrow\"", LiveFonts.Stack("oswald"));
        Assert.Equal("https://fonts.googleapis.com/css2?family=Oswald:wght@400;500;600;700&display=swap", LiveFonts.StylesheetUrl("Oswald"));
        Assert.Null(LiveFonts.Stack(null));
        Assert.Null(LiveFonts.StylesheetUrl(LiveFonts.Segmented));
    }

    [Theory]
    [InlineData("  Arial  ", "Arial")]
    [InlineData("Evil\"; } body { x", "Evil  body  x")]
    [InlineData("   ", null)]
    public void Names_are_tidied_so_they_cannot_break_out_of_css(string name, string? expected) =>
        Assert.Equal(expected, LiveFonts.Clean(name));

    [Fact]
    public void Saved_presets_are_moved_to_a_family_when_tidied()
    {
        var station = new RadioStation { Id = "r", Name = "Radio", Font = RadioFont.Slab };
        RadioStore.Tidy(station);
        Assert.Equal("Roboto Slab", station.FontFamily);
        Assert.Equal(RadioFont.Theme, station.Font);

        var show = new NewsShow { Id = "n", Name = "News", Font = NewsFont.Rounded };
        NewsStore.Tidy(show);
        Assert.Equal("Nunito", show.FontFamily);
        Assert.Equal(NewsFont.Theme, show.Font);
    }

    [Fact]
    public void Retro_fm_stays_segmented_unless_another_lettering_is_chosen()
    {
        var station = new RadioStation { Id = "r", Name = "Radio", Preset = RadioPreset.RetroFm };
        Assert.True(RadioPageConfig.For(station, preview: false).Segmented);

        station.FontFamily = "Arial";
        Assert.False(RadioPageConfig.For(station, preview: false).Segmented);

        station.Preset = RadioPreset.NowPlaying;
        station.FontFamily = LiveFonts.Segmented;
        Assert.True(RadioPageConfig.For(station, preview: false).Segmented);
    }
}
