using SchedularrTV.Service.Rendering;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class GlyphFallbackTests
{
    // A font with plain ASCII only (letters, digits, punctuation and space).
    private static bool Ascii(int codepoint) => codepoint is >= 0x20 and < 0x7F;

    private static Func<int, bool> AsciiWithout(params char[] missing) => codepoint => Ascii(codepoint) && !missing.Contains((char)codepoint);

    [Fact]
    public void Missing_ampersands_are_said_as_and()
    {
        Func<int, bool> font = AsciiWithout('&');
        Assert.Equal("Tom and Jerry", GlyphFallback.Apply("Tom & Jerry", font));
        Assert.Equal("R and B", GlyphFallback.Apply("R&B", font));
        Assert.Equal("TOM AND JERRY", GlyphFallback.Apply("TOM & JERRY", font));
        Assert.Equal("AT and T", GlyphFallback.Apply("AT&T", font));

        // A font with "&" keeps it.
        Assert.Equal("Tom & Jerry", GlyphFallback.Apply("Tom & Jerry", Ascii));
    }

    [Fact]
    public void Accents_the_font_lacks_are_dropped_and_ones_it_has_kept()
    {
        Assert.Equal("Pokemon", GlyphFallback.Apply("Pokémon", Ascii));
        Assert.Equal("Cafe Creme", GlyphFallback.Apply("Café Crème", Ascii));
        Assert.Equal("Pokémon", GlyphFallback.Apply("Pokémon", codepoint => Ascii(codepoint) || codepoint == 'é'));
        Assert.Equal("fish", GlyphFallback.Apply("ﬁsh", Ascii));
    }

    [Fact]
    public void Typographic_characters_fall_back_to_plain_ones()
    {
        Assert.Equal("It's \"Rugrats\" - tonight...", GlyphFallback.Apply("It’s “Rugrats” — tonight…", Ascii));
        Assert.Equal("Strasse", GlyphFallback.Apply("Straße", Ascii));
        Assert.Equal("BrandTM", GlyphFallback.Apply("Brand™", Ascii));

        // Nothing the font can draw instead: left for the font's own mark.
        Assert.Equal("☃", GlyphFallback.Apply("☃", Ascii));
        // Invisible characters it lacks are dropped.
        Assert.Equal("ab", GlyphFallback.Apply("a​b", Ascii));
    }
}
