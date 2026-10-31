using System.Text.RegularExpressions;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;
using SchedularrTV.Core.Temporal;

namespace SchedularrTV.Core.Scheduling;

/// <summary>
/// Turns the ad break collections a channel (or block) uses into what the engine already knows how to fill a break
/// with: commercial and filler pools (matched by folder rather than by tag), bumpers for each end of the break, and a
/// promo configuration whose spots match clips by folder.
/// </summary>
public static class AdBreakCompiler
{
    /// <summary>
    /// The ad breaks in use for a block: its own, else the channel's, else the schedule's defaults. Once a schedule's
    /// settings have been moved into ad breaks, none means empty breaks; before that, null: the tag-based settings apply.
    /// </summary>
    public static IReadOnlyList<AdBreakConfig>? InUse(SchedulerConfig config, ChannelConfig channel, BlockConfig? block)
    {
        IReadOnlyList<string> names = Names(config, channel, block);
        if (names.Count == 0)
        {
            return config.Settings.AdBreaksMigrated == true ? [] : null;
        }

        return [.. names.Select(name => config.AdBreaks.GetValueOrDefault(name)).OfType<AdBreakConfig>()];
    }

    /// <summary>The names of the ad breaks a block uses: its own, else its channel's, else the schedule's defaults.</summary>
    public static IReadOnlyList<string> Names(SchedulerConfig config, ChannelConfig channel, BlockConfig? block = null) =>
        block?.AdBreaks is { Count: > 0 } own ? own
        : channel.AdBreaks is { Count: > 0 } channels ? channels
        : config.Settings.DefaultAdBreaks ?? [];

    /// <summary>The block-boundary folders, pooled across the block's own or inherited ad break collections.</summary>
    internal static IReadOnlyList<AdPoolSource> BlockSources(SchedulerConfig config, ChannelConfig channel, BlockConfig block, AdFolderRole role) =>
        [.. (InUse(config, channel, block) ?? []).SelectMany(adBreak => adBreak.Folders
            .Where(folder => folder.Role == role)
            .Select(folder => new AdPoolSource(folder.Path, Excluded(adBreak, folder.Path, role), adBreak.Seasonal)))];

    /// <summary>
    /// The breaks' settings with the ad breaks applied: the pools come from their folders, and their fine tuning sits
    /// between the channel's (and block's) and the global settings.
    /// </summary>
    internal static InterstitialsConfig Interstitials(
        SchedulerConfig config, ChannelConfig channel, BlockConfig? block, IReadOnlyList<AdBreakConfig> adBreaks)
    {
        InterstitialsConfig tuning = new();
        foreach (AdBreakConfig adBreak in adBreaks.Reverse())
        {
            tuning = new InterstitialsConfig
            {
                Commercials = Tuning(adBreak.Commercials),
                Filler = Tuning(adBreak.Filler)
            }.Merge(tuning);
        }

        InterstitialsConfig resolved = (block?.Interstitials ?? new InterstitialsConfig())
            .Merge(channel.Interstitials)
            .Merge(tuning)
            .Merge(config.Settings.Interstitials);

        List<AdPoolSource> ads = [];
        List<AdPoolSource> filler = [];
        List<AdPoolSource> opening = [];
        List<AdPoolSource> closing = [];

        foreach (AdBreakConfig adBreak in adBreaks)
        {
            foreach (AdBreakFolder folder in adBreak.Folders)
            {
                AdPoolSource source = new(folder.Path, Excluded(adBreak, folder.Path, folder.Role), adBreak.Seasonal);
                switch (folder.Role)
                {
                    case AdFolderRole.Ads:
                        ads.Add(source);
                        break;
                    case AdFolderRole.Filler:
                        filler.Add(source);
                        break;
                    case AdFolderRole.Bumpers:
                        switch (adBreak.BumperPlacement)
                        {
                            case BumperPlacement.Mixed:
                                ads.Add(source);
                                break;
                            case BumperPlacement.Start:
                                opening.Add(source);
                                break;
                            case BumperPlacement.End:
                                closing.Add(source);
                                break;
                            default:
                                opening.Add(source);
                                closing.Add(source);
                                break;
                        }

                        break;
                }
            }
        }

        List<CommercialsConfig> tagAds = [.. adBreaks.Select(adBreak => adBreak.TagRules?.Commercials).OfType<CommercialsConfig>()];
        List<FillerTierConfig> tagFiller = [.. adBreaks.Select(adBreak => adBreak.TagRules?.Filler).OfType<FillerTierConfig>()];

        CommercialsConfig commercials = resolved.Commercials!.Merge(null);
        commercials.Sources = ads;
        commercials.TagPools = tagAds;

        FillerTierConfig fillerTier = resolved.Filler!.Merge(null);
        fillerTier.Sources = filler;
        fillerTier.TagPools = tagFiller;
        fillerTier.Fallback = (filler.Count > 0 || tagFiller.Count > 0) && (fillerTier.Fallback ?? true);

        return new InterstitialsConfig
        {
            Shows = block?.InterstitialShows ?? channel.InterstitialShows ?? resolved.Shows,
            MaxShowsPerSlot = resolved.MaxShowsPerSlot,
            ShowChance = resolved.ShowChance,
            Commercials = commercials,
            Filler = fillerTier,
            OpeningBumpers = opening,
            ClosingBumpers = closing
        };
    }

    /// <summary>
    /// The promo settings the ad breaks add up to. A spot no ad break has a folder for stays switched on but finds no
    /// clips, so a generated promo can still take it.
    /// </summary>
    internal static PromoConfig Promos(IReadOnlyList<AdBreakConfig> adBreaks, TemporalTagOptions temporal)
    {
        PromoConfig defaults = new();

        // Every folder each kind of promo comes from: the single-type promo folders, and every type of each folder
        // sorted by show and type. Options (placement, how far ahead…) come from a single-type folder where there is one.
        List<(AdPromoType Type, TimeSpan? Within, PromoFolderSource Source, AdBreakPromo? Options)> promos =
        [
            .. adBreaks.SelectMany(adBreak => adBreak.Promos
                .Where(promo => promo.Enabled)
                .Select(promo => (promo.Type, promo.Within, Folder(adBreak, promo, temporal), (AdBreakPromo?)promo))),
            .. adBreaks.SelectMany(adBreak => adBreak.PromoRoots
                .Where(root => root.Enabled)
                .SelectMany(root => Enum.GetValues<AdPromoType>().Select(type =>
                    (type, (TimeSpan?)null, RootFolder(adBreak, root, type, temporal), (AdBreakPromo?)null))))
        ];

        // Tag-based promos (moved from the older settings) keep matching as they did, alongside the folders. The first
        // ad break that has them supplies them. Only read: their lists are copied where the build needs its own.
        AdBreakTagRules? tagged = adBreaks.Select(adBreak => adBreak.TagRules).FirstOrDefault(rules => rules?.Promos is not null);
        PromoConfig? tagPromos = tagged?.Promos;

        PromoSpotConfig Spot(AdPromoType type, PromoSpotConfig fallback, PromoSpotConfig? byTag)
        {
            var matching = promos.Where(entry => entry.Type == type).ToList();
            AdBreakPromo? first = matching.Select(entry => entry.Options).FirstOrDefault(options => options is not null);
            return new PromoSpotConfig
            {
                Enabled = byTag?.Enabled ?? true,
                PromoTypes = byTag is null ? [] : [.. byTag.PromoTypes],
                Tag = byTag?.Tag ?? string.Empty,
                SkipWhenSameSeries = first?.SkipWhenSameSeries ?? byTag?.SkipWhenSameSeries ?? fallback.SkipWhenSameSeries,
                Placement = first?.Placement ?? byTag?.Placement ?? fallback.Placement,
                Folders = [.. matching.Select(entry => entry.Source)]
            };
        }

        List<PromoWindow> windows = tagPromos is null
            ? []
            : [.. (tagPromos.Windows.Count > 0 ? tagPromos.Windows : PromoConfig.DefaultWindows).Select(window => new PromoWindow
            {
                Within = window.Within,
                PromoTypes = [.. window.PromoTypes],
                IncludeClockVariants = window.IncludeClockVariants
            })];

        int tagWindows = windows.Count;
        windows.AddRange(promos
            .Where(entry => entry.Type is AdPromoType.ComingUp or AdPromoType.LaterToday)
            .GroupBy(entry => entry.Within ?? AdBreakPromo.DefaultWithin(entry.Type))
            .OrderBy(group => group.Key)
            .Select(group => new PromoWindow
            {
                Within = group.Key,
                PromoTypes = [],
                IncludeClockVariants = true,
                Folders = [.. group.Select(entry => entry.Source)]
            }));

        var eventually = promos.Where(entry => entry.Type == AdPromoType.Eventually).ToList();
        AdBreakPromo? firstEventually = eventually.Select(entry => entry.Options).FirstOrDefault(options => options is not null);
        EventuallyConfig eventuallyDefaults = tagPromos?.Eventually ?? defaults.Eventually;
        PromoConfig fallbacks = tagPromos ?? defaults;

        return new PromoConfig
        {
            Enabled = tagPromos?.Enabled ?? true,
            FromAdBreaks = true,
            BaseQuery = tagPromos?.BaseQuery,
            BaseTags = tagged?.PromoBaseTags,
            AnyShowAlias = adBreaks.Select(adBreak => adBreak.AnyShowFolder).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                           ?? tagPromos?.AnyShowAlias
                           ?? AdBreakConfig.DefaultAnyShowFolder,
            AnyMovieAlias = adBreaks.Select(adBreak => adBreak.AnyMovieFolder).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                            ?? tagPromos?.AnyMovieAlias
                            ?? AdBreakConfig.DefaultAnyMovieFolder,
            // Far enough ahead for every folder window; tag-based ones keep the lookahead they had.
            Lookahead = adBreaks.Select(adBreak => adBreak.Lookahead).FirstOrDefault(value => value is not null)
                        ?? TimeSpan.FromTicks(Math.Max(fallbacks.Lookahead.Ticks,
                            windows.Count > tagWindows ? windows.Skip(tagWindows).Max(window => window.Within.Ticks) : 0)),
            MaxPerBreak = adBreaks.Select(adBreak => adBreak.MaxPromosPerBreak).FirstOrDefault(value => value is not null) ?? fallbacks.MaxPerBreak,
            Windows = windows,
            UpNext = Spot(AdPromoType.UpNext, defaults.UpNext, tagPromos?.UpNext),
            NowPlaying = Spot(AdPromoType.NowPlaying, defaults.NowPlaying, tagPromos?.NowPlaying),
            ThisHasBeen = Spot(AdPromoType.ThisHasBeen, defaults.ThisHasBeen, tagPromos?.ThisHasBeen),
            YoureWatching = Spot(AdPromoType.YoureWatching, defaults.YoureWatching, tagPromos?.YoureWatching),
            BackToTheShow = Spot(AdPromoType.BackToTheShow, defaults.BackToTheShow, tagPromos?.BackToTheShow),
            Eventually = new EventuallyConfig
            {
                Enabled = eventuallyDefaults.Enabled,
                PromoTypes = tagPromos is null ? [] : [.. eventuallyDefaults.PromoTypes],
                Tag = tagPromos is null ? string.Empty : eventuallyDefaults.Tag,
                Chance = Math.Clamp(firstEventually?.Chance ?? eventuallyDefaults.Chance, 0, 1),
                Horizon = firstEventually?.Horizon ?? eventuallyDefaults.Horizon,
                Folders = [.. eventually.Select(entry => entry.Source)]
            }
        };
    }

    private static PromoFolderSource Folder(AdBreakConfig adBreak, AdBreakPromo promo, TemporalTagOptions temporal) => new(
        promo.Path,
        Excluded(adBreak, promo.Path, null),
        promo.PerShow,
        promo.AnyShow,
        promo.Times,
        promo.Days,
        adBreak.AnyShowFolder ?? AdBreakConfig.DefaultAnyShowFolder,
        Seasonal: adBreak.Seasonal ? temporal : null,
        AnyMovieFolder: adBreak.AnyMovieFolder);

    private static PromoFolderSource RootFolder(AdBreakConfig adBreak, AdBreakPromoRoot root, AdPromoType type, TemporalTagOptions temporal) => new(
        root.Path,
        Excluded(adBreak, root.Path, null),
        PerShow: true,
        root.AnyShow,
        root.Times,
        root.Days,
        adBreak.AnyShowFolder ?? AdBreakConfig.DefaultAnyShowFolder,
        type,
        adBreak.Seasonal ? temporal : null,
        adBreak.AnyMovieFolder);

    /// <summary>Folders inside <paramref name="folder"/> that the ad break gives a different job, and so are left out of it.</summary>
    private static List<string> Excluded(AdBreakConfig adBreak, string folder, AdFolderRole? role)
    {
        List<string> excluded = [.. adBreak.Folders
            .Where(other => other.Role != role && AdBreakPaths.IsUnder(other.Path, folder))
            .Select(other => other.Path)];

        excluded.AddRange(adBreak.Promos
            .Where(promo => (role is not null || !AdBreakPaths.Same(promo.Path, folder)) && AdBreakPaths.IsUnder(promo.Path, folder))
            .Select(promo => promo.Path));

        excluded.AddRange(adBreak.PromoRoots
            .Where(root => root.Enabled && AdBreakPaths.IsUnder(root.Path, folder))
            .Select(root => root.Path));

        return [.. excluded.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>An ad break's fine tuning, without the pool settings that its folders replace.</summary>
    private static CommercialsConfig? Tuning(CommercialsConfig? commercials) => commercials is null ? null : new CommercialsConfig
    {
        Filter = commercials.Filter,
        StrictFit = commercials.StrictFit,
        Order = commercials.Order,
        MinItemDuration = commercials.MinItemDuration,
        MaxItemDuration = commercials.MaxItemDuration,
        Tolerance = commercials.Tolerance,
        Granularity = commercials.Granularity,
        MaxRepeatsPerBreak = commercials.MaxRepeatsPerBreak
    };

    private static FillerTierConfig? Tuning(FillerTierConfig? filler) => filler is null ? null : new FillerTierConfig
    {
        Filter = filler.Filter,
        Trimmable = filler.Trimmable,
        StopBeforeEnd = filler.StopBeforeEnd,
        Fallback = filler.Fallback,
        Loop = filler.Loop,
        Order = filler.Order,
        MinItemDuration = filler.MinItemDuration,
        MaxItemDuration = filler.MaxItemDuration,
        MinTrimmedDuration = filler.MinTrimmedDuration
    };

    // --- Queries ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Every clip in the sources' folders that may play at <paramref name="moment"/>; nothing when there are none.
    /// <paramref name="filter"/> is AND-ed on.
    /// </summary>
    public static QueryExpression Pool(
        IReadOnlyList<AdPoolSource> sources, string? filter, DateTimeOffset moment, TemporalTagOptions options, IEnumerable<QueryExpression>? tagPools = null)
    {
        QueryExpression pool = QueryExpression.Or(sources.Select(source => Source(source, moment, options)).Concat(tagPools ?? []));
        if (pool is NoneExpression || string.IsNullOrWhiteSpace(filter))
        {
            return pool;
        }

        return QueryExpression.And([pool, QueryParser.Parse(filter)]);
    }

    /// <summary>
    /// What <see cref="Pool"/> is drawn from, the same on every day: the folders (and what each leaves out, and whether
    /// it's seasonal), the filter and the tag pools. A pool's shuffle state is kept under this rather than under the
    /// day's query, which names today's date.
    /// </summary>
    public static string PoolIdentity(IReadOnlyList<AdPoolSource> sources, string? filter, IEnumerable<string>? tagPools = null)
    {
        List<string> parts = [.. sources.Select(source =>
            $"{AdBreakPaths.Key(source.Folder)}|{string.Join(",", source.Excluded.Select(AdBreakPaths.Key))}|{source.Seasonal}")];
        parts.Add($"filter:{filter}");
        parts.AddRange((tagPools ?? []).Select(pool => $"tags:{pool}"));
        return string.Join("||", parts);
    }

    private static QueryExpression Source(AdPoolSource source, DateTimeOffset moment, TemporalTagOptions options)
    {
        List<QueryExpression> clauses = [PathMatches(AdBreakPaths.Inside(source.Folder))];
        clauses.AddRange(source.Excluded.Select(excluded => (QueryExpression)new NotExpression(PathMatches(AdBreakPaths.Inside(excluded)))));

        if (source.Seasonal)
        {
            clauses.AddRange(Seasonal(source.Folder, moment, options));
        }

        return QueryExpression.And(clauses);
    }

    /// <summary>
    /// Leaves out clips under a month, season or holiday folder that isn't today's, and under a date folder that isn't
    /// today. A clip under no such folder (or under <c>Any Time</c>) always plays.
    /// </summary>
    private static IEnumerable<QueryExpression> Seasonal(string folder, DateTimeOffset moment, TemporalTagOptions options)
    {
        string below = $"^{AdBreakPaths.Pattern(folder)}{AdBreakPaths.Separator}(?:[^\\\\/]+{AdBreakPaths.Separator})*";

        HashSet<string> active = new(SeasonalFolders.Active(moment, options), StringComparer.OrdinalIgnoreCase);
        List<string> inactive = [.. SeasonalFolders.Names(options).Where(name => !active.Contains(name))];
        if (inactive.Count > 0)
        {
            string names = string.Join('|', inactive.Select(Regex.Escape));
            yield return new NotExpression(PathMatches($"{below}(?:{names}){AdBreakPaths.Separator}"));
        }

        string anyDate = $"{below}(?:\\d{{4}}-)?\\d{{1,2}}-\\d{{1,2}}{AdBreakPaths.Separator}";
        string today = $"{below}(?:{moment.Year}-)?0?{moment.Month}-0?{moment.Day}{AdBreakPaths.Separator}";
        yield return QueryExpression.Or([new NotExpression(PathMatches(anyDate)), PathMatches(today)]);
    }

    private static FieldExpression PathMatches(string pattern) => new("path", ComparisonOperator.Matches, [pattern]);

    // --- Promo folders --------------------------------------------------------------------------------------------

    /// <summary>A query for every clip in the promo folders (before they're matched to a show).</summary>
    public static QueryExpression PromoPool(IReadOnlyList<PromoFolderSource> folders) =>
        QueryExpression.Or(folders.Select(folder => QueryExpression.And(
            [PathMatches(AdBreakPaths.Inside(folder.Folder)),
             .. folder.Excluded.Select(excluded => (QueryExpression)new NotExpression(PathMatches(AdBreakPaths.Inside(excluded))))])));

    /// <summary>Set to <c>true</c> on promo clips filed for new episodes (a <c>New</c> folder, or a <c>new_episode</c> tag).</summary>
    public const string NewEpisodeAttribute = "promo_new";

    /// <summary>Folder names that file a promo as being for new episodes: New, New Episode(s), Premiere(s), All New.</summary>
    public static bool IsNewEpisodeFolder(string segment)
    {
        string name = new(segment.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return name is "new" or "newepisode" or "newepisodes" or "allnew" or "premiere" or "premieres";
    }

    /// <summary>Whether a promo clip was made for a new episode rather than a repeat.</summary>
    public static bool IsNewEpisodeClip(MediaItem clip) =>
        (clip.Attributes.TryGetValue(NewEpisodeAttribute, out string? flag) && string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase))
        || clip.Tags.Any(tag => string.Equals(tag, "new_episode", StringComparison.OrdinalIgnoreCase) || string.Equals(tag, "new episode", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads what a promo clip's folders say about it (the show it's for, the day and start time when the promo
    /// folder uses them, and whether it's filed for new episodes) into the attributes promo matching already reads:
    /// <c>show_id</c>, <c>promo_day</c>, <c>promo_clock</c>, <c>promo_new</c>. Null when it isn't in any of the folders, or has no show and no "any show" fallback. For a
    /// folder sorted by type, also null unless the clip is under a folder named for that type; for a seasonal one,
    /// null when it's under a month, season, holiday or date folder that isn't <paramref name="seasonAt"/>'s.
    /// </summary>
    public static MediaItem? Describe(MediaItem item, IReadOnlyList<PromoFolderSource> folders, DateTimeOffset? seasonAt = null)
    {
        if (item.Path is not { } path)
        {
            return null;
        }

        // The most specific folder wins, should two of them nest.
        PromoFolderSource? source = folders
            .Where(folder => AdBreakPaths.IsUnder(path, folder.Folder) && !folder.Excluded.Any(excluded => AdBreakPaths.IsUnder(path, excluded)))
            .OrderByDescending(folder => AdBreakPaths.Key(folder.Folder).Length)
            .FirstOrDefault();
        if (source is null)
        {
            return null;
        }

        string? show = null;
        string? day = null;
        string? clock = null;
        bool fresh = false;
        bool typed = source.Type is null;
        foreach (string segment in AdBreakPaths.FoldersBetween(source.Folder, path))
        {
            // A "New" folder holds the versions made for a new episode (a drop's first airing).
            if (IsNewEpisodeFolder(segment))
            {
                fresh = true;
                continue;
            }

            // In a folder sorted by type, the type's folder is the one named for it; another type's means another spot.
            if (source.Type is { } wanted && AdBreakPromo.FromFolderName(segment) is { } found)
            {
                if (found != wanted)
                {
                    return null;
                }

                typed = true;
                continue;
            }

            if (source.Seasonal is { } temporal)
            {
                if (string.Equals(segment, temporal.AnyTimeTag, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (SeasonalFolders.IsSeasonal(segment, temporal))
                {
                    if (seasonAt is { } moment && !SeasonalFolders.IsActive(segment, moment, temporal))
                    {
                        return null;
                    }

                    continue;
                }
            }

            // A weekday, or a group of them (Weekdays, Weekends).
            if (source.Days && day is null && PromoDays.TryRead(segment, out string days))
            {
                day = days;
            }
            else if (source.Times && clock is null && TryReadClock(segment, out string? normalized))
            {
                clock = normalized;
            }
            else if (source.PerShow && show is null)
            {
                show = segment;
            }
        }

        if (!typed)
        {
            return null;
        }

        if (show is null || string.Equals(show, source.AnyShowFolder, StringComparison.OrdinalIgnoreCase))
        {
            if (!source.AnyShow && show is null)
            {
                return null;
            }

            show = source.AnyShowFolder;
        }

        // Whatever the library read from the clip's folders is replaced (blank reads as unset): these folders say it better.
        Dictionary<string, string> attributes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["show_id"] = show,
            ["promo_day"] = day ?? string.Empty,
            ["promo_clock"] = clock ?? string.Empty,
            [NewEpisodeAttribute] = fresh ? "true" : string.Empty
        };

        return item.WithChapters(item.Chapters, attributes);
    }

    /// <summary>Recognises a weekday folder (letters only, so a number is never read as a day).</summary>
    public static bool TryReadDay(string folderName, out DayOfWeek day)
    {
        string trimmed = folderName.Trim();
        return Enum.TryParse(trimmed, ignoreCase: true, out day) && trimmed.All(char.IsLetter);
    }

    private static readonly Regex ClockPattern = new(@"^(\d{1,2})(?::?(\d{2}))?\s*(am|pm|a\.m\.|p\.m\.)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Recognises a start time folder (<c>4pm</c>, <c>8:30 PM</c>, <c>830pm</c>, <c>20:30</c>) and spells it the way
    /// promo matching compares them: <c>4pm</c> for the hour (any start in it), <c>830pm</c> for an exact time.
    /// </summary>
    public static bool TryReadClock(string folderName, out string? clock)
    {
        clock = null;
        Match match = ClockPattern.Match(folderName.Trim());
        if (!match.Success)
        {
            return false;
        }

        int hour = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        int? minute = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
        string suffix = match.Groups[3].Value.ToLowerInvariant().Replace(".", string.Empty);

        // A bare number is only a time with minutes (20:30, 2030), so a show called "24" isn't read as one.
        if (suffix.Length == 0 && minute is null)
        {
            return false;
        }

        if (suffix.Length == 0)
        {
            if (hour > 23)
            {
                return false;
            }

            suffix = hour < 12 ? "am" : "pm";
            hour %= 12;
            if (hour == 0)
            {
                hour = 12;
            }
        }
        else if (hour is < 1 or > 12)
        {
            return false;
        }

        if (minute is > 59)
        {
            return false;
        }

        clock = minute is { } minutes ? $"{hour}{minutes:D2}{suffix}" : $"{hour}{suffix}";
        return true;
    }
}
