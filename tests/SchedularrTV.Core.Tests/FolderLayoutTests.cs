using SchedularrTV.Core.Configuration;
using SchedularrTV.Providers.FileSystem;
using SchedularrTV.Service.Editor;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class FolderLayoutTests
{
    private static readonly CommercialFolders Library = new(["Commercials"], ["Cartoon Network", "Any Channel"], ["Ads", "Bumpers"], ["Any Time", "October"]);

    private static readonly MediaFolderOptions[] Roots = [new() { Path = @"D:\Media\Commercials", Tags = ["Commercials"] }];

    [Theory]
    [InlineData("tag:\"Commercials\"", "Commercials")]
    [InlineData("tag:'Adverts'", "Adverts")]
    [InlineData("tag:Promo", "Promo")]
    [InlineData("tag:\"A\" AND tag:\"B\"", null)]
    [InlineData(null, null)]
    public void A_single_tag_query_names_the_root(string? query, string? expected) =>
        Assert.Equal(expected, FolderLayouts.SimpleTag(query));

    [Theory]
    [InlineData(new[] { "youre_watching" }, "You're Watching", "You're Watching")]
    [InlineData(new[] { "later_today", "tonight" }, null, "Later Today")]
    [InlineData(new[] { "up_next" }, "Next Up", "Up Next")]
    public void Promo_type_folders_read_as_their_type(string[] types, string? tag, string expected) =>
        Assert.Equal(expected, FolderLayouts.TypeFolder(types, tag));

    [Fact]
    public void Breaks_follow_the_channels_brands_types_and_time_tags()
    {
        ChannelConfig channel = new()
        {
            Id = "ch", Name = "Toons", CommercialBaseTags = ["Cartoon Network", "Toon Disney"], AutoTemporalTags = true,
            Interstitials = new InterstitialsConfig { Commercials = new CommercialsConfig { BaseQuery = "tag:\"Commercials\"", Types = ["Ads"] } }
        };
        SchedulerConfig config = new() { Channels = [channel] };

        IReadOnlyList<FolderLayout> layouts = FolderLayouts.Breaks(config, channel, Library, Roots, new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));

        FolderLayout commercials = layouts[0];
        Assert.Equal(@"D:\Media\Commercials", commercials.Levels[0].Choices.Single().Name);
        Assert.Equal(FolderState.Found, commercials.Levels[1].Choices.Single(choice => choice.Name == "Cartoon Network").State);
        Assert.Equal(FolderState.Missing, commercials.Levels[1].Choices.Single(choice => choice.Name == "Toon Disney").State);
        Assert.Equal("Ads", commercials.Levels[2].Choices.Single().Name);
        Assert.Contains(commercials.Levels[3].Choices, choice => choice.Name == "October");
        Assert.Contains(commercials.Levels[3].Choices, choice => choice.Name == "Any Time");

        // Filler inherits the pool, never the brands.
        FolderLayout filler = layouts[1];
        Assert.Equal(FolderState.Placeholder, filler.Levels[1].Choices.Single().State);
    }

    [Fact]
    public void Promos_go_under_the_promo_folder_by_show_and_type()
    {
        ChannelConfig channel = new()
        {
            Id = "ch", Name = "Toons", CommercialBaseTags = ["Cartoon Network"],
            BaseRotation = new ContentSelector { Include = [new ContentInclude { Show = "Dexter's Laboratory" }] }
        };
        SchedulerConfig config = new() { Channels = [channel] };

        IReadOnlyList<FolderLayout> layouts = FolderLayouts.Promos(config, channel, Library, Roots, FolderLayouts.ExampleShows(channel));

        FolderLayout promos = layouts[0];
        Assert.Equal("Cartoon Network", promos.Levels[1].Choices.Single().Name);
        Assert.Equal("Promos", promos.Levels[2].Choices.Single().Name);
        Assert.Contains(promos.Levels[3].Choices, choice => choice.Name == "Dexter's Laboratory");
        Assert.Contains(promos.Levels[3].Choices, choice => choice.Name == "Any Show");
        Assert.Contains(promos.Levels[4].Choices, choice => choice.Name == "Up Next");
        Assert.Contains(promos.Levels[4].Choices, choice => choice.Name == "Coming Up");
        Assert.Contains(layouts, layout => layout.Title == "Eventually trailers");
    }
}
