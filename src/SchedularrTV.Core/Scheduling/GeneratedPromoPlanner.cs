using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;

namespace SchedularrTV.Core.Scheduling;

/// <summary>
/// The generated promos that apply somewhere, with the channel that owns them (its name fills <c>{channel}</c>)
/// and the block that defines them, if they belong to a block rather than a channel.
/// </summary>
public sealed record GeneratedPromoSource(GeneratedPromosConfig Promos, ChannelConfig Owner, BlockConfig? DefinedIn);

/// <summary>Chooses generated promo templates while a break is packed; rendering happens afterwards.</summary>
public static class GeneratedPromoPlanner
{
    public static MediaItem Placeholder(GeneratedPromoTemplate template, string? path = null) => new()
    {
        Id = $"generated:{template.Name}",
        Title = $"{template.Name} (generated)",
        Kind = MediaKind.Filler,
        Duration = template.Duration,
        Path = path
    };

    /// <summary>
    /// A block's own settings, else its channel's; <c>from</c> hands over to another channel's channel-level
    /// promos. Null when none apply, they are switched off, or <c>from</c> is broken.
    /// </summary>
    public static GeneratedPromoSource? Source(SchedulerConfig config, ChannelConfig channel, BlockConfig? block)
    {
        GeneratedPromosConfig? promos = block?.GeneratedPromos ?? channel.GeneratedPromos;
        ChannelConfig owner = channel;
        BlockConfig? definedIn = block?.GeneratedPromos is null ? null : block;
        HashSet<string> followed = new(StringComparer.OrdinalIgnoreCase);

        while (promos is { Enabled: true, From: { Length: > 0 } from })
        {
            ChannelConfig? source = config.Channels.FirstOrDefault(candidate => string.Equals(candidate.Id, from, StringComparison.OrdinalIgnoreCase));
            if (source is null || !followed.Add(source.Id))
            {
                return null;
            }

            promos = source.GeneratedPromos;
            owner = source;
            definedIn = null;
        }

        return promos is { Enabled: true } ? new GeneratedPromoSource(promos, owner, definedIn) : null;
    }

    /// <summary>The source for a scheduled entry, found again from the block name it was built in.</summary>
    public static GeneratedPromoSource? Source(SchedulerConfig config, ChannelConfig channel, string? blockName) =>
        Source(config, channel, blockName is null ? null
            : channel.Blocks.FirstOrDefault(block => string.Equals(block.Name, blockName, StringComparison.OrdinalIgnoreCase)));

    public static bool Has(SchedulerConfig config, ChannelConfig channel, BlockConfig? block, GeneratedPromoType type) =>
        Source(config, channel, block) is { } source
        && source.Promos.Templates.Any(template => template.Enabled && template.Type == type);

    /// <summary>A fitting template of the given type, or null to keep the library clip if there is one.</summary>
    public static GeneratedPromoTemplate? Choose(
        SchedulerConfig config,
        ChannelConfig channel,
        BlockConfig? block,
        GeneratedPromoType type,
        bool clipAvailable,
        TimeSpan available,
        DateTimeOffset at,
        bool endsAt = false,
        bool isNew = false)
    {
        if (Source(config, channel, block) is not { } source)
        {
            return null;
        }

        TimeZoneInfo zone = SchedulingEngine.ResolveTimeZone(string.IsNullOrWhiteSpace(channel.TimeZone) ? config.Settings.TimeZone : channel.TimeZone);
        List<GeneratedPromoTemplate> candidates = [.. source.Promos.Templates.Where(template =>
            template.Enabled && template.Type == type
            && template.Duration > TimeSpan.Zero
            && template.Duration <= available
            && GeneratedPromoKinds.Suits(template.Episodes, isNew)
            && GeneratedPromoEligibility.IsAvailable(template, endsAt ? at - template.Duration : at, zone)
            && (!clipAvailable || template.Usage == GeneratedPromoUsage.Mixed))];

        if (candidates.Count == 0)
        {
            return null;
        }

        // A template made for new episodes wins when the episode is new.
        if (isNew && candidates.Any(template => template.Episodes == GeneratedEpisodeFilter.New))
        {
            candidates = [.. candidates.Where(template => template.Episodes == GeneratedEpisodeFilter.New)];
        }

        // Up Next keeps its original seed so existing schedules do not reshuffle.
        string salt = type == GeneratedPromoType.UpNext ? "generated-up-next" : $"generated-{type}";
        DeterministicRandom random = DeterministicRandom.FromSeedText(config.Settings.Seed, channel.Id, at.ToString("O"), salt);
        GeneratedPromoTemplate chosen = candidates[random.Next(candidates.Count)];

        return !clipAvailable || random.Next(1_000) < (int)Math.Round(Math.Clamp(chosen.MixChance, 0, 1) * 1_000)
            ? chosen
            : null;
    }
}
