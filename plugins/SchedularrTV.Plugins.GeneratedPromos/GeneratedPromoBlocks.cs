using SchedularrTV.Core.Configuration;

namespace SchedularrTV.GeneratedPromos;

/// <summary>
/// For templates set to name other blocks (<see cref="GeneratedPromoTemplate.NameOtherBlocks"/>): shows coming up in a
/// block other than the one the promo airs in become that block's name.
/// </summary>
public static class GeneratedPromoBlocks
{
    /// <summary>Whether the promo type talks about shows still to come (rather than the one on now or just ended).</summary>
    public static bool LooksAhead(GeneratedPromoType type) => type is GeneratedPromoType.UpNext or GeneratedPromoType.ComingUp
        or GeneratedPromoType.Eventually or GeneratedPromoType.NewEpisode;

    /// <summary>
    /// <paramref name="listings"/> with each show in another block than <paramref name="here"/> (null: the promo airs
    /// outside any block) standing as that block: its name as the title, where and when its first show starts, and each
    /// run of shows in a row in the same block named once. Shows outside any block, and those in this block, stay as
    /// they are.
    /// </summary>
    public static IReadOnlyList<UpNextListing> AsBlocks(IReadOnlyList<UpNextListing> listings, string? here)
    {
        List<UpNextListing> result = new(listings.Count);
        bool lastStandsIn = false;
        foreach (UpNextListing listing in listings)
        {
            string? block = string.IsNullOrWhiteSpace(listing.Block) ? null : listing.Block.Trim();
            if (block is null || string.Equals(block, here?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                result.Add(listing);
                lastStandsIn = false;
                continue;
            }

            if (lastStandsIn && string.Equals(result[^1].Block?.Trim(), block, StringComparison.OrdinalIgnoreCase))
            {
                // The same block's next show: the block runs on.
                result[^1] = result[^1] with { End = listing.End ?? result[^1].End };
                continue;
            }

            // Its first show's file and length stay, for a background clip of what's on first.
            result.Add(new UpNextListing(block, listing.Start, Path: listing.Path, Runtime: listing.Runtime, Block: listing.Block) { End = listing.End });
            lastStandsIn = true;
        }

        return result;
    }
}
