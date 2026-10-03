using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SchedularrTV.Core.Configuration;

public sealed record ConfigValidationResult(bool IsValid, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

/// <summary>
/// Loads and validates the YAML schedule definition. Serialisation is symmetric so the future visual
/// editor can round-trip a config it did not author without losing fields.
/// </summary>
public static class SchedulerConfigLoader
{
    // Reading: `plugins:` binds to Plugins, and the features' old top-level keys still bind to their typed
    // properties so older files load. Normalize then moves the features' plugin sections into those properties.
    private static IDeserializer CreateDeserializer() =>
        new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithTypeConverter(new FlexibleTimeSpanConverter())
            .WithTypeConverter(new FlexibleEnumConverter())
            .WithAttributeOverride<SchedulerSettings>(settings => settings.PluginSections!, new YamlIgnoreAttribute())
            .WithAttributeOverride<ChannelConfig>(channel => channel.PluginSections!, new YamlIgnoreAttribute())
            .WithAttributeOverride<BlockConfig>(block => block.PluginSections!, new YamlIgnoreAttribute())
            .IgnoreUnmatchedProperties()
            .Build();

    // Writing: the features' typed properties go out under `plugins:` (PluginSections) instead of their old keys.
    private static ISerializer CreateSerializer() =>
        new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithTypeConverter(new FlexibleTimeSpanConverter())
            .WithTypeConverter(new FlexibleEnumConverter())
            .WithAttributeOverride<SchedulerSettings>(settings => settings.Plugins!, new YamlIgnoreAttribute())
            .WithAttributeOverride<SchedulerSettings>(settings => settings.Promos, new YamlIgnoreAttribute())
            .WithAttributeOverride<SchedulerSettings>(settings => settings.GeneratedPromosPath!, new YamlIgnoreAttribute())
            .WithAttributeOverride<SchedulerSettings>(settings => settings.GeneratedPromosKeepVideos, new YamlIgnoreAttribute())
            .WithAttributeOverride<SchedulerSettings>(settings => settings.GeneratedPromosKeepAudio, new YamlIgnoreAttribute())
            .WithAttributeOverride<SchedulerSettings>(settings => settings.GeneratedPromosLeadTime, new YamlIgnoreAttribute())
            .WithAttributeOverride<SchedulerSettings>(settings => settings.GeneratedPromosWatchedOnly, new YamlIgnoreAttribute())
            .WithAttributeOverride<ChannelConfig>(channel => channel.Plugins!, new YamlIgnoreAttribute())
            .WithAttributeOverride<ChannelConfig>(channel => channel.PromoBaseTags!, new YamlIgnoreAttribute())
            .WithAttributeOverride<ChannelConfig>(channel => channel.Promos!, new YamlIgnoreAttribute())
            .WithAttributeOverride<ChannelConfig>(channel => channel.GeneratedPromos!, new YamlIgnoreAttribute())
            .WithAttributeOverride<ChannelConfig>(channel => channel.Follow!, new YamlIgnoreAttribute())
            .WithAttributeOverride<BlockConfig>(block => block.Plugins!, new YamlIgnoreAttribute())
            .WithAttributeOverride<BlockConfig>(block => block.PromoBaseTags!, new YamlIgnoreAttribute())
            .WithAttributeOverride<BlockConfig>(block => block.Promos!, new YamlIgnoreAttribute())
            .WithAttributeOverride<BlockConfig>(block => block.GeneratedPromos!, new YamlIgnoreAttribute())
            .WithAttributeOverride<BlockConfig>(block => block.FollowOnly, new YamlIgnoreAttribute())
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
            .Build();

    public static SchedulerConfig Parse(string yaml)
    {
        SchedulerConfig config = CreateDeserializer().Deserialize<SchedulerConfig>(yaml)
            ?? throw new SchedulerConfigException("The configuration document was empty.");

        // Only a document that could have an old key is walked a second time.
        config.UsesOldLayout = OldKeyHints.Any(hint => yaml.Contains(hint, StringComparison.Ordinal))
            && FeatureSections.UsesOldKeys(new DeserializerBuilder().Build().Deserialize<object?>(yaml));

        Normalize(config);
        return config;
    }

    private static readonly string[] OldKeyHints = ["promos:", "promo_base_tags:", "generated_promos", "follow:", "follow_only:"];

    public static async Task<SchedulerConfig> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        string yaml = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Parse(yaml);
    }

    public static string Serialize(SchedulerConfig config) => CreateSerializer().Serialize(config);

    /// <summary>
    /// Turns a plugin's <c>plugins.&lt;id&gt;</c> section into its settings class by round-tripping it through YAML,
    /// so it binds with the same naming rules, durations and enums as the rest of the file.
    /// </summary>
    public static T? ConvertSection<T>(object section) where T : class =>
        SectionDeserializer.Deserialize<T>(SectionSerializer.Serialize(section));

    // Plugins read their sections per entry on every publish, so the builders are made once (they're thread-safe).
    private static readonly ISerializer SectionSerializer = CreateSerializer();
    private static readonly IDeserializer SectionDeserializer = CreateDeserializer();

    /// <summary>
    /// YAML maps under <c>plugins:</c> deserialise as object-keyed dictionaries; plain string-keyed ones keep JSON
    /// (used to fingerprint channels for selective rebuilds) and plugins able to read them.
    /// </summary>
    private static Dictionary<string, object?>? NormalizePluginSections(Dictionary<string, object?>? sections) =>
        sections is not { Count: > 0 }
            ? null
            : new Dictionary<string, object?>(
                sections.Select(pair => KeyValuePair.Create(pair.Key, Plain(pair.Value))),
                StringComparer.OrdinalIgnoreCase);

    private static object? Plain(object? value) => value switch
    {
        System.Collections.IDictionary map => Entries(map).ToDictionary(
            entry => Convert.ToString(entry.Key, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            entry => Plain(entry.Value),
            StringComparer.Ordinal),
        string text => text,
        System.Collections.IEnumerable list => list.Cast<object?>().Select(Plain).ToList(),
        _ => value
    };

    // A generic dictionary's plain IEnumerable yields KeyValuePairs, not DictionaryEntry; its IDictionary enumerator doesn't.
    private static IEnumerable<System.Collections.DictionaryEntry> Entries(System.Collections.IDictionary map)
    {
        System.Collections.IDictionaryEnumerator enumerator = map.GetEnumerator();
        while (enumerator.MoveNext())
        {
            yield return enumerator.Entry;
        }
    }

    /// <summary>
    /// Structural validation. Query syntax is checked here too so authors get errors at load time
    /// rather than mid-build.
    /// </summary>
    public static ConfigValidationResult Validate(SchedulerConfig config)
    {
        List<string> errors = [];
        List<string> warnings = [];

        if (config.Settings.Interstitials?.MaxShowsPerSlot < 0)
        {
            errors.Add("settings.interstitials.max_shows_per_slot: must be zero or greater.");
        }

        foreach ((string key, ContentSelector selector) in config.GlobalCollections)
        {
            ValidateSelector(selector, $"global_collections.{key}", config, errors);
        }

        if (config.Channels.Count == 0)
        {
            warnings.Add("No channels are defined.");
        }

        foreach (string duplicate in config.Channels.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1).Select(g => g.Key))
        {
            errors.Add($"Duplicate channel id '{duplicate}'.");
        }

        // The guide id is derived from the number, so a duplicate number silently merges two channels'
        // listings into one file.
        foreach (int duplicate in config.Channels.GroupBy(c => c.Number)
                     .Where(g => g.Count() > 1).Select(g => g.Key))
        {
            errors.Add($"Duplicate channel number {duplicate}.");
        }

        ValidateTimeZone(config.Settings.TimeZone, "settings.time_zone", errors);

        if (config.Settings.GeneratedPromosPath is { Length: > 0 } promoPath && !Path.IsPathFullyQualified(promoPath))
        {
            errors.Add($"settings.plugins.{BuiltInPluginIds.GeneratedPromos}.path: use a full path, e.g. D:\\SchedularrTV\\generated-promos.");
        }

        if (config.Settings.GeneratedPromosKeepVideos < TimeSpan.Zero || config.Settings.GeneratedPromosKeepAudio < TimeSpan.Zero)
        {
            errors.Add($"settings.plugins.{BuiltInPluginIds.GeneratedPromos}: cache times can't be negative (0 keeps files for good).");
        }

        if (config.Settings.GeneratedPromosLeadTime < TimeSpan.Zero)
        {
            errors.Add($"settings.plugins.{BuiltInPluginIds.GeneratedPromos}.lead_time: can't be negative (0 renders the whole playout ahead).");
        }

        foreach (ChannelConfig channel in config.Channels)
        {
            string scope = $"channels.{channel.Id}";

            ValidateTimeZone(channel.TimeZone, $"{scope}.time_zone", errors);

            if (channel.BaseRotation is null && !channel.Blocks.Any(block => !block.FollowOnly) && channel.Anchors.Count == 0 && channel.Follow is not { Enabled: true })
            {
                errors.Add($"{scope}: define at least a base_rotation, a block or an anchor.");
            }

            if (channel.BaseRotation is null)
            {
                warnings.Add($"{scope}: no base_rotation; unclaimed time will be padded instead of programmed.");
            }
            else
            {
                ValidateSelector(channel.BaseRotation, $"{scope}.base_rotation", config, errors);
            }

            if (channel.InterstitialShows is not null)
            {
                ValidateSelector(channel.InterstitialShows, $"{scope}.interstitial_shows", config, errors);
            }

            ValidateInterstitials(channel.Interstitials, $"{scope}.interstitials", config, errors);
            ValidateGeneratedPromos(channel.GeneratedPromos, $"{scope}.plugins.{BuiltInPluginIds.GeneratedPromos}", config, errors, warnings);

            string followScope = $"{scope}.plugins.{BuiltInPluginIds.ArchiveFollow}";
            if (channel.Follow is { Enabled: true } follow)
            {
                if (string.IsNullOrWhiteSpace(follow.Directory))
                {
                    errors.Add($"{followScope}: specify an archive directory.");
                }
                if (follow.StartYear < 1 || follow.EndYear > 9999 || follow.StartYear > follow.EndYear)
                {
                    errors.Add($"{followScope}: years must be between 1 and 9999, with start_year no later than end_year.");
                }
                foreach (string target in follow.Blocks.Values.OfType<string>().Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    int matches = channel.Blocks.Count(block => string.Equals(block.Name, target, StringComparison.OrdinalIgnoreCase));
                    if (matches != 1)
                    {
                        errors.Add($"{followScope}: mapped block '{target}' must identify exactly one channel block.");
                    }
                }
            }

            foreach (AnchorConfig anchor in channel.Anchors)
            {
                ValidateAnchor(anchor, $"{scope}.anchors[{anchor.Name ?? anchor.Time.ToString()}]", config, errors);
            }

            foreach (BlockConfig block in channel.Blocks)
            {
                string blockScope = $"{scope}.blocks.{block.Name}";

                if (!block.FollowOnly && block.Time is null && block.Schedule?.Date is null)
                {
                    errors.Add($"{blockScope}: a block requires 'time' (and usually 'end_time') or a 'schedule.date'.");
                }

                if (!block.FollowOnly && block.Recurrence == Recurrence.DaysOfWeek && block.Days.Count == 0)
                {
                    errors.Add($"{blockScope}: recurrence 'days_of_week' requires a non-empty 'days' list.");
                }

                if (!block.FollowOnly && block.Schedule is { Repeat: RepeatModifier.Yearly or RepeatModifier.Monthly, Date: null })
                {
                    errors.Add($"{blockScope}: 'schedule.repeat' requires 'schedule.date'.");
                }

                if (!block.FollowOnly && block.Shows.Count == 0 && block.Anchors.Count == 0)
                {
                    errors.Add($"{blockScope}: define 'shows' or 'anchors'.");
                }

                foreach (ContentSelector selector in block.Shows)
                {
                    ValidateSelector(selector, $"{blockScope}.shows", config, errors);
                }

                if (block.InterstitialShows is not null)
                {
                    ValidateSelector(block.InterstitialShows, $"{blockScope}.interstitial_shows", config, errors);
                }

                ValidateInterstitials(block.Interstitials, $"{blockScope}.interstitials", config, errors);
                ValidateGeneratedPromos(block.GeneratedPromos, $"{blockScope}.plugins.{BuiltInPluginIds.GeneratedPromos}", config, errors, warnings);

                foreach (AnchorConfig anchor in block.Anchors)
                {
                    ValidateAnchor(anchor, $"{blockScope}.anchors[{anchor.Name ?? anchor.Time.ToString()}]", config, errors);
                }

                if (!block.FollowOnly && block.EndTime is not null && block.Time is not null && block.EndTime == block.Time)
                {
                    errors.Add($"{blockScope}: 'end_time' must differ from 'time'.");
                }
            }
        }

        return new ConfigValidationResult(errors.Count == 0, errors, warnings);
    }

    private static void ValidateGeneratedPromos(
        GeneratedPromosConfig? generated, string scope, SchedulerConfig config, List<string> errors, List<string> warnings)
    {
        if (generated is null)
        {
            return;
        }

        if (generated.From is { Length: > 0 } from)
        {
            if (!config.Channels.Any(channel => string.Equals(channel.Id, from, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"{scope}.from: there is no channel '{from}'.");
            }
            else if (!FromResolves(config, from))
            {
                errors.Add($"{scope}.from: channels can't take their generated promos from each other in a loop.");
            }

            if (generated.Templates.Count > 0)
            {
                warnings.Add($"{scope}: templates are ignored while 'from' is set.");
            }
        }

        foreach (IGrouping<string, GeneratedPromoTemplate> duplicate in generated.Templates
                     .GroupBy(template => template.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1 || string.IsNullOrWhiteSpace(group.Key)))
        {
            errors.Add(string.IsNullOrWhiteSpace(duplicate.Key)
                ? $"{scope}: every template needs a name."
                : $"{scope}: template name '{duplicate.Key}' is used more than once.");
        }

        foreach (GeneratedPromoTemplate template in generated.Templates)
        {
            string templateScope = $"{scope}.templates.{template.Name}";
            if (template.Duration <= TimeSpan.Zero || template.Duration > TimeSpan.FromMinutes(2))
            {
                errors.Add($"{templateScope}: duration must be between 0 and 2 minutes.");
            }

            if (template.Width is < 16 or > 3840 || template.Height is < 16 or > 2160 || template.Fps is < 1 or > 60)
            {
                errors.Add($"{templateScope}: size must be 16x16 to 3840x2160 at 1 to 60 fps.");
            }
            else if (template.Width % 2 != 0 || template.Height % 2 != 0)
            {
                errors.Add($"{templateScope}: width and height must be even numbers.");
            }

            if (template.MixChance is < 0 or > 1)
            {
                errors.Add($"{templateScope}: mix_chance must be between 0 and 1.");
            }

            if (!IsColor(template.BackgroundColor))
            {
                errors.Add($"{templateScope}: background_color must be a hex colour such as #101828.");
            }

            if (template.Voice is { Enabled: true } voice
                && (string.IsNullOrWhiteSpace(voice.Provider) || string.IsNullOrWhiteSpace(voice.Voice)))
            {
                errors.Add($"{templateScope}.voice: choose a provider and a voice.");
            }

            if (template.Music is { } music
                && (music.Songs.Count == 0 || music.Volume is < 0 or > 1
                    || music.StartAt < TimeSpan.Zero || music.SongStarts?.Values.Any(start => start < TimeSpan.Zero) == true || music.FadeIn < TimeSpan.Zero || music.FadeOut < TimeSpan.Zero))
            {
                errors.Add($"{templateScope}.music: needs at least one song, a volume between 0 and 1, and non-negative start and fades.");
            }

            if (template.BackgroundClip is { } clip && (clip.Volume is < 0 or > 1 || clip.StartAt < TimeSpan.Zero))
            {
                errors.Add($"{templateScope}.background_clip: needs a volume between 0 and 1 and a non-negative start.");
            }

            if (template.Generator is { } generator)
            {
                if (string.IsNullOrWhiteSpace(generator.Command))
                {
                    errors.Add($"{templateScope}.generator: needs a command to run.");
                }

                if (generator.Shows is < 1 or > 10 || generator.Volume is < 0 or > 1
                    || generator.Timeout <= TimeSpan.Zero || generator.Timeout > TimeSpan.FromMinutes(30))
                {
                    errors.Add($"{templateScope}.generator: shows must be 1 to 10, volume 0 to 1, and the timeout up to 30 minutes.");
                }

                if (!GeneratorExtensions.Contains(generator.OutputExtension, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add($"{templateScope}.generator: output_extension must be one of {string.Join(", ", GeneratorExtensions)}.");
                }

                if (!string.IsNullOrWhiteSpace(generator.WorkingDirectory) && !Path.IsPathFullyQualified(generator.WorkingDirectory))
                {
                    errors.Add($"{templateScope}.generator: working_directory must be a full path.");
                }
            }

            GeneratedLayerKind required = GeneratedPromoKinds.RequiredLayer(template.Type);
            if (template.Generator is null && !template.Layers.Any(layer => layer.Kind == required))
            {
                errors.Add($"{templateScope}: this promo type needs its '{UnderscoredNamingConvention.Instance.Apply(required.ToString())}' text layer.");
            }

            foreach (GeneratedLayer layer in template.Layers)
            {
                string layerScope = $"{templateScope}.layers.{(string.IsNullOrWhiteSpace(layer.Name) ? layer.Kind.ToString() : layer.Name)}";
                if (layer.Width <= 0 || layer.Height <= 0 || layer.Opacity is < 0 or > 1)
                {
                    errors.Add($"{layerScope}: needs a positive size and an opacity between 0 and 1.");
                }

                if (layer.Start < TimeSpan.Zero || layer.End <= layer.Start || layer.TransitionDuration < TimeSpan.Zero)
                {
                    errors.Add($"{layerScope}: start must be non-negative and before end.");
                }

                if (!IsColor(layer.Color))
                {
                    errors.Add($"{layerScope}: color must be a hex colour such as #FFFFFF.");
                }

                if (layer.Kind == GeneratedLayerKind.Image && string.IsNullOrWhiteSpace(layer.Path))
                {
                    errors.Add($"{layerScope}: an image layer needs a path.");
                }

                if (layer.UpNext is { } upNext && (upNext.Offset < 0 || upNext.ShowsAhead is < 1 or > 10))
                {
                    errors.Add($"{layerScope}: offset must be zero or more and shows_ahead between 1 and 10.");
                }
            }
        }
    }

    private static readonly string[] GeneratorExtensions = [".mp4", ".mov", ".webm", ".mkv"];

    private static bool IsColor(string? value) =>
        value is { Length: 7 or 9 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);

    private static bool FromResolves(SchedulerConfig config, string channelId)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        for (string? next = channelId; next is { Length: > 0 };)
        {
            if (!seen.Add(next))
            {
                return false;
            }

            next = config.Channels.FirstOrDefault(channel => string.Equals(channel.Id, next, StringComparison.OrdinalIgnoreCase))
                ?.GeneratedPromos?.From;
        }

        return true;
    }

    private static void NormalizeGeneratedPromos(GeneratedPromosConfig? generated)
    {
        foreach (GeneratedPromoTemplate template in generated?.Templates ?? [])
        {
            if (template.Music is { } music)
            {
                music.Paths = [.. music.Songs];
                music.Path = null;

                // Starts for songs no longer listed are dropped; an empty map goes back to null so it isn't written.
                Dictionary<string, TimeSpan> starts = new(StringComparer.OrdinalIgnoreCase);
                if (music.SongStarts is not null)
                {
                    foreach ((string song, TimeSpan start) in music.SongStarts)
                    {
                        if (music.Paths.Contains(song, StringComparer.OrdinalIgnoreCase)) starts[song] = start;
                    }
                }

                music.SongStarts = starts.Count > 0 ? starts : null;
            }

            if (template.Voice is { } voice)
            {
                if (voice.PreMessages.Count == 0 && !string.IsNullOrWhiteSpace(voice.PreMessage)) voice.PreMessages = [voice.PreMessage];
                if (voice.PostMessages.Count == 0 && !string.IsNullOrWhiteSpace(voice.PostMessage)) voice.PostMessages = [voice.PostMessage];
                voice.PreMessage = null;
                voice.PostMessage = null;
            }
        }
    }

    private static void ValidateAnchor(AnchorConfig anchor, string scope, SchedulerConfig config, List<string> errors)
    {
        List<ContentSelector> selectors = anchor.AllSelectors().ToList();
        if (selectors.Count == 0)
        {
            errors.Add($"{scope}: an anchor requires 'show' or 'shows'.");
            return;
        }

        foreach (ContentSelector selector in selectors)
        {
            ValidateSelector(selector, scope, config, errors);
        }
    }

    private static void ValidateInterstitials(InterstitialsConfig? interstitials, string scope, SchedulerConfig config, List<string> errors)
    {
        if (interstitials is null)
        {
            return;
        }

        if (interstitials.Shows is not null)
        {
            ValidateSelector(interstitials.Shows, $"{scope}.shows", config, errors);
        }

        if (interstitials.MaxShowsPerSlot < 0)
        {
            errors.Add($"{scope}.max_shows_per_slot: must be zero or greater.");
        }

        if (interstitials.Commercials is { } commercials)
        {
            ValidateQuery(commercials.Query, $"{scope}.commercials.query", errors);
            ValidateQuery(commercials.BaseQuery, $"{scope}.commercials.base_query", errors);
            ValidateQuery(commercials.Filter, $"{scope}.commercials.filter", errors);
        }

        if (interstitials.Filler is { } filler)
        {
            ValidateQuery(filler.Query, $"{scope}.filler.query", errors);
            ValidateQuery(filler.BaseQuery, $"{scope}.filler.base_query", errors);
            ValidateQuery(filler.Filter, $"{scope}.filler.filter", errors);

            // Brand tags are inherited from the commercials tier, so 'types' alone is a complete
            // definition; only a tier with no source at all is an error.
            bool hasSource = !string.IsNullOrWhiteSpace(filler.Query)
                             || !string.IsNullOrWhiteSpace(filler.BaseQuery)
                             || filler.BaseTags is { Count: > 0 }
                             || filler.Types is { Count: > 0 };

            if (filler.IsEnabled && !hasSource)
            {
                errors.Add($"{scope}.filler: specify 'types', 'query' or 'base_tags', or set 'fallback: false' to disable the tier.");
            }
        }
    }

    /// <summary>
    /// A zone the machine does not know silently resolves to UTC at build time, shifting every block
    /// and anchor on the channel, so it is caught here instead.
    /// </summary>
    private static void ValidateTimeZone(string? id, string scope, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            errors.Add($"{scope}: unknown time zone '{id}'.");
        }
    }

    private static void ValidateQuery(string? query, string scope, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(query) && !Query.QueryParser.TryParse(query, out _, out string? error))
        {
            errors.Add($"{scope}: {error}");
        }
    }

    private static void ValidateSelector(ContentSelector selector, string scope, SchedulerConfig config, List<string> errors)
    {
        bool hasCollection = !string.IsNullOrWhiteSpace(selector.Collection);
        bool hasQuery = !string.IsNullOrWhiteSpace(selector.Query);
        bool hasInclude = selector.Include.Count > 0;

        if (!hasCollection && !hasQuery && !hasInclude)
        {
            errors.Add($"{scope} is empty: add titles with 'include', a 'query', or name a 'collection'.");
        }

        if (hasCollection && !config.GlobalCollections.ContainsKey(selector.Collection!))
        {
            errors.Add($"{scope}: unknown global collection '{selector.Collection}'.");
        }

        foreach (ContentInclude include in selector.Include)
        {
            if (include.IsEmpty)
            {
                errors.Add($"{scope}.include: an entry needs 'show', 'movie', 'collection' or 'query'.");
            }

            ValidateQuery(include.Query, $"{scope}.include.query", errors);
        }

        if (hasQuery && !Query.QueryParser.TryParse(selector.Query, out _, out string? queryError))
        {
            errors.Add($"{scope}.query: {queryError}");
        }

        if (!string.IsNullOrWhiteSpace(selector.Filter)
            && !Query.QueryParser.TryParse(selector.Filter, out _, out string? filterError))
        {
            errors.Add($"{scope}.filter: {filterError}");
        }
    }

    private static void Normalize(SchedulerConfig config)
    {
        // Rebuild with a case-insensitive comparer; YamlDotNet constructs an ordinal dictionary.
        config.GlobalCollections = new Dictionary<string, ContentSelector>(config.GlobalCollections, StringComparer.OrdinalIgnoreCase);
        config.Titles = new Dictionary<string, TitleDefaults>(config.Titles, StringComparer.OrdinalIgnoreCase);

        config.Settings.ExcludedLibraries = Unique(config.Settings.ExcludedLibraries) ?? [];
        config.Settings.Plugins = NormalizePluginSections(config.Settings.Plugins);
        FeatureSections.Extract(config.Settings);

        NormalizeInterstitials(config.Settings.Interstitials);
        NormalizePromos(config.Settings.Promos);

        foreach (TitleDefaults title in config.Titles.Values)
        {
            foreach (BumperBlock bumper in title.Bumpers)
            {
                NormalizeBumper(bumper);
            }
        }

        foreach (ContentSelector selector in config.GlobalCollections.Values)
        {
            NormalizeSelector(selector);
        }

        foreach (ChannelConfig channel in config.Channels)
        {
            channel.Plugins = NormalizePluginSections(channel.Plugins);
            FeatureSections.Extract(channel);

            if (channel.Follow is { } follow)
            {
                follow.Blocks = new Dictionary<string, string?>(follow.Blocks, StringComparer.OrdinalIgnoreCase);
                follow.Shows = new Dictionary<string, string?>(follow.Shows, StringComparer.OrdinalIgnoreCase);
            }

            channel.CommercialBaseTags = Unique(channel.CommercialBaseTags);
            channel.PromoBaseTags = Unique(channel.PromoBaseTags);
            NormalizeInterstitials(channel.Interstitials);
            NormalizePromos(channel.Promos);
            NormalizeGeneratedPromos(channel.GeneratedPromos);
            NormalizeSelector(channel.BaseRotation);
            NormalizeSelector(channel.InterstitialShows);

            foreach (AnchorConfig anchor in channel.Anchors)
            {
                NormalizeSelector(anchor.Show);
            }

            foreach (BlockConfig block in channel.Blocks)
            {
                // `days: [...]` without an explicit recurrence implies days_of_week.
                block.Recurrence ??= block.Days.Count > 0
                    ? Recurrence.DaysOfWeek
                    : block.Schedule?.Date is not null ? Recurrence.SpecificDate : Recurrence.Daily;

                block.Days = [.. block.Days.Distinct()];
                block.Plugins = NormalizePluginSections(block.Plugins);
                FeatureSections.Extract(block);
                block.CommercialBaseTags = Unique(block.CommercialBaseTags);
                block.PromoBaseTags = Unique(block.PromoBaseTags);
                NormalizeInterstitials(block.Interstitials);
                NormalizeGeneratedPromos(block.GeneratedPromos);
                NormalizeSelector(block.InterstitialShows);

                foreach (ContentSelector selector in block.Shows)
                {
                    NormalizeSelector(selector);
                }

                foreach (AnchorConfig anchor in block.Anchors)
                {
                    NormalizeSelector(anchor.Show);
                }
            }
        }
    }

    /// <summary>
    /// These lists are all OR-ed sets, so a repeated entry can never mean anything. Collapsing them
    /// on load keeps a hand-edited or round-tripped file from accumulating noise.
    /// </summary>
    private static List<string>? Unique(List<string>? values) =>
        values is null ? null : [.. values.Distinct(StringComparer.OrdinalIgnoreCase)];

    private static void NormalizeSelector(ContentSelector? selector)
    {
        if (selector is null)
        {
            return;
        }

        selector.ExcludeSeasons = [.. selector.ExcludeSeasons.Distinct().OrderBy(s => s)];

        foreach (ContentInclude include in selector.Include)
        {
            include.Seasons = [.. include.Seasons.Distinct()];
            include.Episodes = [.. include.Episodes.Distinct(StringComparer.OrdinalIgnoreCase)];
            FoldLegacyBumpers(include);
        }

        if (selector.Versions is { } versions)
        {
            versions.Prefer = [.. versions.Prefer.Distinct(StringComparer.OrdinalIgnoreCase)];
            versions.Exclude = [.. versions.Exclude.Distinct(StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>
    /// Turns the single-path <c>intro:</c>/<c>outro:</c> shorthand into blocks so the engine only
    /// has one shape to reason about. Writing the config back drops the shorthand.
    /// </summary>
    private static void FoldLegacyBumpers(ContentInclude include)
    {
        if (!string.IsNullOrWhiteSpace(include.Intro)
            && !include.Bumpers.Any(b => b.Position == BumperPosition.Intro && b.Clips.Any(c => PathIs(c.Path, include.Intro))))
        {
            include.Bumpers.Insert(0, new BumperBlock
            {
                Name = "Intro",
                Position = BumperPosition.Intro,
                Clips = [new BumperClip { Path = include.Intro! }]
            });
        }

        if (!string.IsNullOrWhiteSpace(include.Outro)
            && !include.Bumpers.Any(b => b.Position == BumperPosition.Outro && b.Clips.Any(c => PathIs(c.Path, include.Outro))))
        {
            include.Bumpers.Add(new BumperBlock
            {
                Name = "Outro",
                Position = BumperPosition.Outro,
                Clips = [new BumperClip { Path = include.Outro! }]
            });
        }

        include.Intro = null;
        include.Outro = null;

        foreach (BumperBlock bumper in include.Bumpers)
        {
            NormalizeBumper(bumper);
        }
    }

    private static void NormalizeBumper(BumperBlock bumper)
    {
        bumper.Seasons = [.. bumper.Seasons.Distinct().Order()];
        bumper.SkipWhenFileContains = [.. bumper.SkipWhenFileContains
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        foreach (BumperClip clip in bumper.Clips)
        {
            clip.Episodes = [.. clip.Episodes.Distinct(StringComparer.OrdinalIgnoreCase)];
        }
    }

    private static bool PathIs(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static void NormalizeInterstitials(InterstitialsConfig? interstitials)
    {
        if (interstitials is null)
        {
            return;
        }

        NormalizeSelector(interstitials.Shows);

        if (interstitials.Commercials is { } commercials)
        {
            commercials.BaseTags = Unique(commercials.BaseTags);
            commercials.Types = Unique(commercials.Types);
        }

        if (interstitials.Filler is { } filler)
        {
            filler.BaseTags = Unique(filler.BaseTags);
            filler.Types = Unique(filler.Types);
        }
    }

    private static void NormalizePromos(PromoConfig? promos)
    {
        if (promos is null)
        {
            return;
        }

        promos.UpNext.PromoTypes = [.. promos.UpNext.PromoTypes.Distinct(StringComparer.OrdinalIgnoreCase)];
        promos.NowPlaying.PromoTypes = [.. promos.NowPlaying.PromoTypes.Distinct(StringComparer.OrdinalIgnoreCase)];
        promos.ThisHasBeen.PromoTypes = [.. promos.ThisHasBeen.PromoTypes.Distinct(StringComparer.OrdinalIgnoreCase)];
        promos.YoureWatching.PromoTypes = [.. promos.YoureWatching.PromoTypes.Distinct(StringComparer.OrdinalIgnoreCase)];
        promos.BackToTheShow.PromoTypes = [.. promos.BackToTheShow.PromoTypes.Distinct(StringComparer.OrdinalIgnoreCase)];
        promos.Eventually.PromoTypes = [.. promos.Eventually.PromoTypes.Distinct(StringComparer.OrdinalIgnoreCase)];
        promos.Eventually.Chance = Math.Clamp(promos.Eventually.Chance, 0, 1);

        foreach (PromoWindow window in promos.Windows)
        {
            window.PromoTypes = [.. window.PromoTypes.Distinct(StringComparer.OrdinalIgnoreCase)];
        }
    }
}
