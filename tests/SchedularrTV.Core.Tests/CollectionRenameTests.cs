using SchedularrTV.Core.Configuration;
using SchedularrTV.Service.Editor;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class CollectionRenameTests
{
    private static SchedulerConfig Config()
    {
        SchedulerConfig config = new();
        config.GlobalCollections["Kids"] = new ContentSelector { Query = "collection:'Kids'" };
        config.GlobalCollections["Specials"] = new ContentSelector { Collection = "Kids" };
        config.GlobalHolidays.Add(new ChannelHoliday { Name = "Christmas", Fallback = "kids" });
        config.Channels.Add(new ChannelConfig
        {
            Id = "ch",
            Name = "Channel",
            BaseRotation = new ContentSelector { Collection = "Kids" },
            Blocks = [new BlockConfig { Name = "Mornings", Shows = [new ContentSelector { Collection = "Kids" }, new ContentSelector { Collection = "Other" }] }],
            Anchors = [new AnchorConfig { Name = "Late", Time = TimeSpan.FromHours(22), Show = new ContentSelector { Collection = "Kids" } }],
            Holidays = [new ChannelHoliday { Name = "Easter", Fallback = "Kids" }]
        });
        return config;
    }

    [Fact]
    public void A_renamed_collection_is_renamed_everywhere_it_is_used()
    {
        SchedulerConfig config = Config();
        ContentSelector kids = config.GlobalCollections["Kids"];

        Assert.Null(CollectionNames.Rename(config, "Kids", " Cartoons ", out int references));

        Assert.False(config.GlobalCollections.ContainsKey("Kids"));
        Assert.Same(kids, config.GlobalCollections["Cartoons"]);
        // The rotation, the block's show, the anchor, the other collection and both holidays (named in any case).
        Assert.Equal(6, references);
        ChannelConfig channel = config.Channels[0];
        Assert.Equal("Cartoons", channel.BaseRotation!.Collection);
        Assert.Equal(new[] { "Cartoons", "Other" }, channel.Blocks[0].Shows.Select(show => show.Collection));
        Assert.Equal("Cartoons", channel.Anchors[0].Show!.Collection);
        Assert.Equal("Cartoons", config.GlobalCollections["Specials"].Collection);
        Assert.Equal("Cartoons", config.GlobalHolidays[0].Fallback);
        Assert.Equal("Cartoons", channel.Holidays[0].Fallback);
    }

    [Fact]
    public void A_rename_that_would_clash_or_is_blank_is_refused()
    {
        SchedulerConfig config = Config();
        Assert.NotNull(CollectionNames.Rename(config, "Kids", "specials", out _));
        Assert.NotNull(CollectionNames.Rename(config, "Kids", "  ", out _));
        Assert.NotNull(CollectionNames.Rename(config, "Missing", "New", out _));
        Assert.True(config.GlobalCollections.ContainsKey("Kids"));

        // Only the case changes: allowed.
        Assert.Null(CollectionNames.Rename(config, "Kids", "KIDS", out _));
        Assert.Equal("KIDS", config.Channels[0].BaseRotation!.Collection);
    }
}
