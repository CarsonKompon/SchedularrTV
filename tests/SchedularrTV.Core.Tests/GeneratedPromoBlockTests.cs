using SchedularrTV.Core.Configuration;
using SchedularrTV.GeneratedPromos;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class GeneratedPromoBlockTests
{
    private static readonly DateTimeOffset Eight = new(2026, 10, 22, 20, 0, 0, TimeSpan.Zero);

    private static readonly UpNextListing[] Coming =
    [
        new("Rugrats", Eight, Block: "Nicktoons"),
        new("Dragon Ball Z", Eight.AddMinutes(30), Block: "Toonami") { End = Eight.AddMinutes(60) },
        new("Gundam Wing", Eight.AddMinutes(60), Block: "Toonami") { End = Eight.AddMinutes(90) },
        new("Infomercial", Eight.AddMinutes(90)),
        new("Sailor Moon", Eight.AddMinutes(120), Block: "Toonami")
    ];

    [Fact]
    public void Shows_in_other_blocks_stand_as_their_block_named_once_for_a_run()
    {
        IReadOnlyList<UpNextListing> named = GeneratedPromoBlocks.AsBlocks(Coming, "Nicktoons");

        Assert.Equal(new[] { "Rugrats", "Toonami", "Infomercial", "Toonami" }, named.Select(listing => listing.Title));
        Assert.Equal(Eight.AddMinutes(30), named[1].Start);
        Assert.Equal(Eight.AddMinutes(90), named[1].End);
        Assert.Null(named[1].Episode);

        // Aired outside any block, every block's shows stand as their block.
        Assert.Equal(new[] { "Nicktoons", "Toonami", "Infomercial", "Toonami" }, GeneratedPromoBlocks.AsBlocks(Coming, null).Select(listing => listing.Title));
    }

    [Fact]
    public void An_up_next_promo_set_to_name_other_blocks_says_the_block()
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext();
        var where = new GeneratedVoiceContext("nick", "Nicktoons", "Nick");

        string Said(GeneratedPromoTemplate promo) => string.Join('\n',
            GeneratedPromoResolver.Resolve(promo, "Nick", [.. Coming.Skip(1)], TimeZoneInfo.Utc, Eight.AddMinutes(25), where: where)!.LayerLines.SelectMany(lines => lines));

        Assert.Contains("Dragon Ball Z", Said(template), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Toonami", Said(template), StringComparison.OrdinalIgnoreCase);

        template.NameOtherBlocks = true;
        Assert.Contains("Toonami", Said(template), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Dragon Ball Z", Said(template), StringComparison.OrdinalIgnoreCase);

        // In the same block, the shows are named as usual.
        where = new GeneratedVoiceContext("nick", "Toonami", "Nick");
        Assert.Contains("Dragon Ball Z", Said(template), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_block_override_gives_the_block_its_picture()
    {
        var images = new GeneratedVariableImage
        {
            Name = "variable_image_01", DefaultPath = "default.png",
            Shows = [new GeneratedShowImage { Show = "Toonami", Block = true, Path = "toonami.png" }, new GeneratedShowImage { Show = "Dragon Ball Z", Path = "dbz.png" }]
        };
        UpNextListing standIn = GeneratedPromoBlocks.AsBlocks(Coming, "Nicktoons")[1];

        Assert.Equal("toonami.png", images.PathFor(standIn.Title, standIn.Block));
    }
}
