namespace SchedularrTV.Core.Configuration;

/// <summary>Ids of the feature plugins that ship with the scheduler; also their keys under <c>plugins:</c> in the YAML.</summary>
public static class BuiltInPluginIds
{
    public const string Promos = "promos";

    public const string GeneratedPromos = "generated-promos";

    public const string ArchiveFollow = "archive-follow";

    public static bool IsFeature(string id) =>
        string.Equals(id, Promos, StringComparison.OrdinalIgnoreCase)
        || string.Equals(id, GeneratedPromos, StringComparison.OrdinalIgnoreCase)
        || string.Equals(id, ArchiveFollow, StringComparison.OrdinalIgnoreCase);
}

/// <summary><c>plugins.promos</c> on a channel or block.</summary>
public sealed class PromoPluginSection
{
    /// <summary>Brand folders promos come from here, without restating the promo settings.</summary>
    public List<string>? BaseTags { get; set; }

    /// <summary>Promo settings of this channel or block; absent uses the channel's, then the global ones.</summary>
    public PromoConfig? Override { get; set; }
}

/// <summary><c>settings.plugins.generated-promos</c>: where rendered promos go and how long they're kept.</summary>
public sealed class GeneratedPromoGlobalSection
{
    public string? Path { get; set; }

    public TimeSpan? KeepVideos { get; set; }

    public TimeSpan? KeepAudio { get; set; }

    public TimeSpan? LeadTime { get; set; }

    public bool? WatchedOnly { get; set; }

    /// <summary>Variable images shared by every template; see <see cref="SchedulerSettings.GeneratedPromosSharedImages"/>.</summary>
    public List<GeneratedSharedImage>? SharedImages { get; set; }
}

/// <summary><c>plugins.archive-follow</c> on a block.</summary>
public sealed class ArchiveFollowBlockSection
{
    /// <summary>The block only exists for Archive Follow to map archived slots onto.</summary>
    public bool FollowOnly { get; set; }
}

/// <summary>
/// The bundled features keep typed properties on the config classes (which the engine reads), but are written
/// under <c>plugins:</c> in the YAML. This converts between the two.
/// </summary>
internal static class FeatureSections
{
    private static readonly SchedulerSettings Defaults = new();

    // --- Writing ---------------------------------------------------------------------------------------------

    public static Dictionary<string, object?>? Compose(SchedulerSettings settings)
    {
        GeneratedPromoGlobalSection generated = new()
        {
            Path = string.IsNullOrWhiteSpace(settings.GeneratedPromosPath) ? null : settings.GeneratedPromosPath,
            KeepVideos = settings.GeneratedPromosKeepVideos == Defaults.GeneratedPromosKeepVideos ? null : settings.GeneratedPromosKeepVideos,
            KeepAudio = settings.GeneratedPromosKeepAudio == Defaults.GeneratedPromosKeepAudio ? null : settings.GeneratedPromosKeepAudio,
            LeadTime = settings.GeneratedPromosLeadTime == Defaults.GeneratedPromosLeadTime ? null : settings.GeneratedPromosLeadTime,
            WatchedOnly = settings.GeneratedPromosWatchedOnly == Defaults.GeneratedPromosWatchedOnly ? null : settings.GeneratedPromosWatchedOnly,
            SharedImages = settings.GeneratedPromosSharedImages is { Count: > 0 } shared ? shared : null
        };
        bool anyGenerated = generated.Path is not null || generated.KeepVideos is not null || generated.KeepAudio is not null
                            || generated.LeadTime is not null || generated.WatchedOnly is not null || generated.SharedImages is not null;

        return Merge(settings.Plugins,
            (BuiltInPluginIds.Promos, settings.Promos),
            (BuiltInPluginIds.GeneratedPromos, anyGenerated ? generated : null));
    }

    public static Dictionary<string, object?>? Compose(ChannelConfig channel) =>
        Merge(channel.Plugins,
            (BuiltInPluginIds.Promos, PromoSection(channel.PromoBaseTags, channel.Promos)),
            (BuiltInPluginIds.GeneratedPromos, channel.GeneratedPromos),
            (BuiltInPluginIds.ArchiveFollow, channel.Follow));

    public static Dictionary<string, object?>? Compose(BlockConfig block) =>
        Merge(block.Plugins,
            (BuiltInPluginIds.Promos, PromoSection(block.PromoBaseTags, block.Promos)),
            (BuiltInPluginIds.GeneratedPromos, block.GeneratedPromos),
            (BuiltInPluginIds.ArchiveFollow, block.FollowOnly ? new ArchiveFollowBlockSection { FollowOnly = true } : null));

    private static PromoPluginSection? PromoSection(List<string>? baseTags, PromoConfig? promos) =>
        baseTags is null && promos is null ? null : new PromoPluginSection { BaseTags = baseTags, Override = promos };

    private static Dictionary<string, object?>? Merge(Dictionary<string, object?>? others, params (string Id, object? Section)[] features)
    {
        Dictionary<string, object?> merged = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string id, object? section) in features)
        {
            if (section is not null)
            {
                merged[id] = section;
            }
        }

        if (others is not null)
        {
            foreach ((string id, object? section) in others)
            {
                if (!BuiltInPluginIds.IsFeature(id))
                {
                    merged[id] = section;
                }
            }
        }

        return merged.Count == 0 ? null : merged;
    }

    // --- Reading ---------------------------------------------------------------------------------------------

    /// <summary>Moves the feature sections out of <see cref="SchedulerSettings.Plugins"/> into the typed properties.</summary>
    public static void Extract(SchedulerSettings settings)
    {
        if (Take(settings.Plugins, BuiltInPluginIds.Promos, out object? promos))
        {
            settings.Promos = Convert<PromoConfig>(promos) ?? new PromoConfig();
        }

        if (Take(settings.Plugins, BuiltInPluginIds.GeneratedPromos, out object? raw) && Convert<GeneratedPromoGlobalSection>(raw) is { } generated)
        {
            settings.GeneratedPromosPath = generated.Path ?? settings.GeneratedPromosPath;
            settings.GeneratedPromosKeepVideos = generated.KeepVideos ?? settings.GeneratedPromosKeepVideos;
            settings.GeneratedPromosKeepAudio = generated.KeepAudio ?? settings.GeneratedPromosKeepAudio;
            settings.GeneratedPromosLeadTime = generated.LeadTime ?? settings.GeneratedPromosLeadTime;
            settings.GeneratedPromosWatchedOnly = generated.WatchedOnly ?? settings.GeneratedPromosWatchedOnly;
            settings.GeneratedPromosSharedImages = generated.SharedImages ?? settings.GeneratedPromosSharedImages;
        }

        settings.Plugins = Empty(settings.Plugins);
    }

    public static void Extract(ChannelConfig channel)
    {
        if (Take(channel.Plugins, BuiltInPluginIds.Promos, out object? promos))
        {
            PromoPluginSection? section = Convert<PromoPluginSection>(promos);
            channel.PromoBaseTags = section?.BaseTags;
            channel.Promos = section?.Override;
        }

        if (Take(channel.Plugins, BuiltInPluginIds.GeneratedPromos, out object? generated))
        {
            channel.GeneratedPromos = Convert<GeneratedPromosConfig>(generated);
        }

        if (Take(channel.Plugins, BuiltInPluginIds.ArchiveFollow, out object? follow))
        {
            channel.Follow = Convert<ArchiveFollowConfig>(follow);
        }

        channel.Plugins = Empty(channel.Plugins);
    }

    public static void Extract(BlockConfig block)
    {
        if (Take(block.Plugins, BuiltInPluginIds.Promos, out object? promos))
        {
            PromoPluginSection? section = Convert<PromoPluginSection>(promos);
            block.PromoBaseTags = section?.BaseTags;
            block.Promos = section?.Override;
        }

        if (Take(block.Plugins, BuiltInPluginIds.GeneratedPromos, out object? generated))
        {
            block.GeneratedPromos = Convert<GeneratedPromosConfig>(generated);
        }

        if (Take(block.Plugins, BuiltInPluginIds.ArchiveFollow, out object? follow))
        {
            block.FollowOnly = Convert<ArchiveFollowBlockSection>(follow)?.FollowOnly ?? false;
        }

        block.Plugins = Empty(block.Plugins);
    }

    private static bool Take(Dictionary<string, object?>? sections, string id, out object? section)
    {
        section = null;
        if (sections is null)
        {
            return false;
        }

        string? key = sections.Keys.FirstOrDefault(key => string.Equals(key, id, StringComparison.OrdinalIgnoreCase));
        if (key is null)
        {
            return false;
        }

        section = sections[key];
        sections.Remove(key);
        return true;
    }

    private static T? Convert<T>(object? section) where T : class =>
        section is null ? null : SchedulerConfigLoader.ConvertSection<T>(section);

    private static Dictionary<string, object?>? Empty(Dictionary<string, object?>? sections) =>
        sections is { Count: > 0 } ? sections : null;

    // --- Old layout --------------------------------------------------------------------------------------------

    private static readonly string[] OldSettingsKeys =
        ["promos", "generated_promos_path", "generated_promos_keep_videos", "generated_promos_keep_audio", "generated_promos_lead_time", "generated_promos_watched_only"];

    private static readonly string[] OldChannelKeys = ["promos", "promo_base_tags", "generated_promos", "follow"];

    private static readonly string[] OldBlockKeys = ["promos", "promo_base_tags", "generated_promos", "follow_only"];

    /// <summary>Whether a document still uses the keys these features had before they moved under <c>plugins:</c>.</summary>
    public static bool UsesOldKeys(object? document)
    {
        if (Map(document) is not { } root)
        {
            return false;
        }

        if (Map(Value(root, "settings")) is { } settings && OldSettingsKeys.Any(key => Has(settings, key)))
        {
            return true;
        }

        foreach (object? item in List(Value(root, "channels")))
        {
            if (Map(item) is not { } channel)
            {
                continue;
            }

            if (OldChannelKeys.Any(key => Has(channel, key)))
            {
                return true;
            }

            foreach (object? blockItem in List(Value(channel, "blocks")))
            {
                if (Map(blockItem) is { } block && OldBlockKeys.Any(key => Has(block, key)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static System.Collections.IDictionary? Map(object? value) => value as System.Collections.IDictionary;

    private static IEnumerable<object?> List(object? value) =>
        value is System.Collections.IList list ? list.Cast<object?>() : [];

    private static object? Value(System.Collections.IDictionary map, string key)
    {
        foreach (System.Collections.DictionaryEntry entry in map)
        {
            if (string.Equals(entry.Key?.ToString(), key, StringComparison.Ordinal))
            {
                return entry.Value;
            }
        }

        return null;
    }

    private static bool Has(System.Collections.IDictionary map, string key)
    {
        foreach (System.Collections.DictionaryEntry entry in map)
        {
            if (string.Equals(entry.Key?.ToString(), key, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
