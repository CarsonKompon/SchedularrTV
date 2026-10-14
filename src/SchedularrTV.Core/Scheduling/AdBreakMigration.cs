using System.Text.Json;
using SchedularrTV.Core.Configuration;

namespace SchedularrTV.Core.Scheduling;

/// <summary>
/// Moves the older, tag-based break and promo settings (a channel's or block's commercials, filler, brands, seasonal
/// matching and promos, and the global defaults they fell back on) into ad breaks, and points the channels and blocks
/// at them. Each ad break keeps the settings as tag rules, so breaks are filled exactly as before.
/// </summary>
/// <remarks>
/// Channels whose settings come out the same share one ad break ("Default" for the global defaults). A block that
/// changed them gets its own. The first run covers every channel and marks the schedule as moved
/// (<see cref="SchedulerSettings.AdBreaksMigrated"/>); later runs only pick up tag-based settings that appear again,
/// from an import or pasted YAML, say.
/// </remarks>
public static class AdBreakMigration
{
    public const string DefaultName = "Default";

    private static readonly JsonSerializerOptions FingerprintJson = new() { WriteIndented = false };

    /// <summary>Moves what there is to move. True when the schedule changed.</summary>
    public static bool Migrate(SchedulerConfig config)
    {
        bool everything = config.Settings.AdBreaksMigrated != true;
        bool changed = false;

        Dictionary<string, string> byFingerprint = new(StringComparer.Ordinal);
        foreach ((string name, AdBreakConfig existing) in config.AdBreaks)
        {
            byFingerprint.TryAdd(Fingerprint(existing), name);
        }

        string defaultFingerprint = Fingerprint(FromTagSettings(config, new ChannelConfig { Id = string.Empty, Name = string.Empty }, null));

        foreach (ChannelConfig channel in config.Channels)
        {
            bool channelHasAdBreaks = channel.AdBreaks is { Count: > 0 };
            bool moveChannel = !channelHasAdBreaks && (everything || HasTagSettings(channel));
            List<BlockConfig> blocks = [.. channel.Blocks.Where(block => block.AdBreaks is not { Count: > 0 } && HasTagSettings(block))];

            if (!moveChannel && blocks.Count == 0)
            {
                if (channelHasAdBreaks && HasTagSettings(channel))
                {
                    // Already on ad breaks: whatever tag-based settings are left can't be seen or used any more.
                    Clear(channel);
                    changed = true;
                }

                continue;
            }

            // Everything is worked out before anything is cleared: a block's settings fall back on the channel's.
            AdBreakConfig? channelBreak = moveChannel ? FromTagSettings(config, channel, null) : null;
            List<(BlockConfig Block, AdBreakConfig AdBreak)> blockBreaks = [.. blocks.Select(block => (block, FromTagSettings(config, channel, block)))];

            if (channelBreak is not null)
            {
                string fingerprint = Fingerprint(channelBreak);
                string name = Register(config, byFingerprint, fingerprint, channelBreak,
                    fingerprint == defaultFingerprint ? DefaultName : channel.Name);
                channel.AdBreaks = [name];
            }

            string? channelFingerprint = channelBreak is null ? null : Fingerprint(channelBreak);
            foreach ((BlockConfig block, AdBreakConfig adBreak) in blockBreaks)
            {
                string fingerprint = Fingerprint(adBreak);

                // A block that comes out the same as its channel just uses the channel's.
                if (fingerprint != channelFingerprint || channel.AdBreaks is not { Count: > 0 })
                {
                    block.AdBreaks = [Register(config, byFingerprint, fingerprint, adBreak, $"{channel.Name} – {block.Name}")];
                }

                Clear(block);
            }

            if (moveChannel)
            {
                Clear(channel);
            }

            changed = true;
        }

        if (everything)
        {
            config.Settings.AdBreaksMigrated = true;
            changed = true;
        }

        return changed;
    }

    /// <summary>Whether a channel has tag-based break or promo settings of its own.</summary>
    public static bool HasTagSettings(ChannelConfig channel) =>
        channel.Interstitials?.Commercials is not null || channel.Interstitials?.Filler is not null
        || channel.CommercialBaseTags is not null || channel.PromoBaseTags is not null || channel.Promos is not null
        || channel.AutoTemporalTags is not null;

    /// <summary>Whether a block has tag-based break or promo settings of its own.</summary>
    public static bool HasTagSettings(BlockConfig block) =>
        block.Interstitials?.Commercials is not null || block.Interstitials?.Filler is not null
        || block.CommercialBaseTags is not null || block.PromoBaseTags is not null || block.Promos is not null
        || block.AutoTemporalTags is not null;

    /// <summary>
    /// An ad break that fills breaks as the tag-based settings in force for this channel (or block) do: their pools as
    /// tag rules, their fine tuning as the ad break's own.
    /// </summary>
    public static AdBreakConfig FromTagSettings(SchedulerConfig config, ChannelConfig channel, BlockConfig? block)
    {
        InterstitialsConfig resolved = SchedulingEngine.ResolveTagBasedInterstitials(config, channel, block);
        CommercialsConfig commercials = resolved.Commercials!;
        FillerTierConfig filler = resolved.Filler!;
        PromoConfig promos = block?.Promos ?? channel.Promos ?? config.Settings.Promos;
        List<string>? promoBrands = PromoBreakContributor.ResolveBaseTags(block, channel, promos, commercials);

        return new AdBreakConfig
        {
            Commercials = new CommercialsConfig
            {
                StrictFit = commercials.StrictFit,
                Order = commercials.Order,
                MinItemDuration = commercials.MinItemDuration,
                MaxItemDuration = commercials.MaxItemDuration,
                Tolerance = commercials.Tolerance,
                Granularity = commercials.Granularity,
                MaxRepeatsPerBreak = commercials.MaxRepeatsPerBreak
            },
            Filler = new FillerTierConfig
            {
                Trimmable = filler.Trimmable,
                StopBeforeEnd = filler.StopBeforeEnd,
                // On, so a global "off" can't switch off filler this channel had on.
                Fallback = filler.IsEnabled ? true : null,
                Loop = filler.Loop,
                Order = filler.Order,
                MinItemDuration = filler.MinItemDuration,
                MaxItemDuration = filler.MaxItemDuration,
                MinTrimmedDuration = filler.MinTrimmedDuration
            },
            TagRules = new AdBreakTagRules
            {
                Commercials = new CommercialsConfig
                {
                    Query = commercials.Query,
                    BaseQuery = commercials.BaseQuery,
                    BaseTags = Copy(commercials.BaseTags),
                    Types = Copy(commercials.Types),
                    AutoTemporalTags = commercials.AutoTemporalTags,
                    Filter = commercials.Filter
                },
                // A switched-off filler tier has no pool to keep.
                Filler = !filler.IsEnabled ? null : new FillerTierConfig
                {
                    Query = filler.Query,
                    BaseQuery = filler.BaseQuery,
                    BaseTags = Copy(filler.BaseTags),
                    Types = Copy(filler.Types),
                    AutoTemporalTags = filler.AutoTemporalTags,
                    Filter = filler.Filter
                },
                Promos = SchedulerConfigLoader.ConvertSection<PromoConfig>(promos) ?? new PromoConfig(),
                PromoBaseTags = Copy(promoBrands)
            }
        };
    }

    /// <summary>The name of the ad break with this content: an existing one, or one added under a free name.</summary>
    private static string Register(
        SchedulerConfig config, Dictionary<string, string> byFingerprint, string fingerprint, AdBreakConfig adBreak, string wanted)
    {
        if (byFingerprint.TryGetValue(fingerprint, out string? existing))
        {
            return existing;
        }

        string baseName = string.IsNullOrWhiteSpace(wanted) ? "Ad break" : wanted.Trim();
        string name = baseName;
        for (int i = 2; config.AdBreaks.ContainsKey(name); i++)
        {
            name = $"{baseName} ({i})";
        }

        config.AdBreaks[name] = adBreak;
        byFingerprint[fingerprint] = name;
        return name;
    }

    /// <summary>What an ad break does, for telling ones that would fill breaks the same way apart.</summary>
    private static string Fingerprint(AdBreakConfig adBreak)
    {
        return JsonSerializer.Serialize(adBreak, FingerprintJson);
    }

    private static void Clear(ChannelConfig channel)
    {
        channel.Interstitials = WithoutPools(channel.Interstitials);
        channel.CommercialBaseTags = null;
        channel.PromoBaseTags = null;
        channel.Promos = null;
        channel.AutoTemporalTags = null;
    }

    private static void Clear(BlockConfig block)
    {
        block.Interstitials = WithoutPools(block.Interstitials);
        block.CommercialBaseTags = null;
        block.PromoBaseTags = null;
        block.Promos = null;
        block.AutoTemporalTags = null;
    }

    /// <summary>Keeps only the interstitial shows (which stay on the channel or block); null when there are none.</summary>
    private static InterstitialsConfig? WithoutPools(InterstitialsConfig? interstitials)
    {
        if (interstitials is null)
        {
            return null;
        }

        interstitials.Commercials = null;
        interstitials.Filler = null;
        return interstitials.Shows is null && interstitials.MaxShowsPerSlot is null && interstitials.ShowChance is null ? null : interstitials;
    }

    private static List<string>? Copy(List<string>? values) => values is null ? null : [.. values];
}
