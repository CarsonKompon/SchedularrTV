using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class ShowTitleMatcherTests
{
    private static readonly string[] Titles =
    [
        "The Powerpuff Girls", "Ed, Edd n Eddy", "Tom & Jerry", "Squidbillies", "Pokémon", "Ben 10", "Ben 10: Alien Force",
        "Doug", "Dog City", "Codename: Kids Next Door"
    ];

    private static string? Find(string name) => ShowTitleMatcher.Find(name, Titles, title => title);

    [Theory]
    [InlineData("the powerpuff girls", "The Powerpuff Girls")]
    [InlineData("Powerpuff Girls", "The Powerpuff Girls")]
    [InlineData("Ed Edd and Eddy", "Ed, Edd n Eddy")]
    [InlineData("Tom and Jerry", "Tom & Jerry")]
    [InlineData("Squibillies", "Squidbillies")]
    [InlineData("Pokemon", "Pokémon")]
    [InlineData("Codename Kids Next Dor", "Codename: Kids Next Door")]
    [InlineData("Ben 10", "Ben 10")]
    public void Forgives_case_punctuation_articles_ampersands_accents_and_typos(string typed, string expected) =>
        Assert.Equal(expected, Find(typed));

    [Theory]
    [InlineData("Ben 11")]      // numbers must agree
    [InlineData("Dug")]         // short names don't get typo matching
    [InlineData("Courage the Cowardly Dog")]
    [InlineData("")]
    public void Leaves_unrelated_or_risky_names_unmatched(string typed) => Assert.Null(Find(typed));

    [Fact]
    public void Exact_title_wins_over_a_normalized_one()
    {
        string[] titles = ["Tom and Jerry", "Tom & Jerry"];
        Assert.Equal("Tom & Jerry", ShowTitleMatcher.Find("tom & jerry", titles, title => title));
    }

    [Fact]
    public void Variable_image_overrides_use_loose_matching_for_shows_and_blocks()
    {
        var image = new GeneratedVariableImage
        {
            DefaultPath = "default.png",
            Shows =
            [
                new() { Show = "Powerpuff Girls", Path = "ppg.png" },
                new() { Show = "Toonami", Block = true, Path = "toonami.png" },
                new() { Show = "Blank", Path = " " }
            ]
        };

        Assert.Equal("ppg.png", image.PathFor("The Powerpuff Girls", null));
        Assert.Equal("toonami.png", image.PathFor("Unassigned", "Toonammi"));
        Assert.Equal("default.png", image.PathFor("Blank", null));
    }
}
