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
    /// <summary>The ad breaks in use for a block (its own, else the channel's), or null when the tag-based settings apply.</summary>
    public static IReadOnlyList<AdBreakConfig>? InUse(SchedulerConfig config, ChannelConfig channel, BlockConfig? block)
    {
        List<string>? names = block?.AdBreaks is { Count: > 0 } own ? own : channel.AdBreaks;
        if (names is not { Count: > 0 })
        {
            return null;
        }

        return [.. names.Select(name => config.AdBreaks.GetValueOrDefault(name)).OfType<AdBreakConfig>()];
    }

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

        CommercialsConfig commercials = resolved.Commercials!.Merge(null);
        commercials.Sources = ads;

        FillerTierConfig fillerTier = resolved.Filler!.Merge(null);
        fillerTier.Sources = filler;
        fillerTier.Fallback = filler.Count > 0 && (fillerTier.Fallback ?? true);

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
    internal static PromoConfig Promos(IReadOnlyList<AdBreakConfig> adBreaks)
    {
        PromoConfig defaults = new();
        List<(AdBreakConfig AdBreak, AdBreakPromo Promo)> promos = [.. adBreaks
            .SelectMany(adBreak => adBreak.Promos.Where(promo => promo.Enabled).Select(promo => (adBreak, promo)))];

        PromoSpotConfig Spot(AdPromoType type, PromoSpotConfig fallback)
        {
            List<(AdBreakConfig AdBreak, AdBreakPromo Promo)> matching = [.. promos.Where(entry => entry.Promo.Type == type)];
            AdBreakPromo? first = matching.Count > 0 ? matching[0].Promo : null;
            return new PromoSpotConfig
            {
                Enabled = true,
                PromoTypes = [],
                Tag = string.Empty,
                SkipWhenSameSeries = first?.SkipWhenSameSeries ?? fallback.SkipWhenSameSeries,
                Placement = first?.Placement ?? fallback.Placement,
                Folders = [.. matching.Select(entry => Folder(entry.AdBreak, entry.Promo))]
            };
        }

        List<PromoWindow> windows = [.. promos
            .Where(entry => entry.Promo.Type is AdPromoType.ComingUp or AdPromoType.LaterToday)
            .GroupBy(entry => entry.Promo.Within ?? AdBreakPromo.DefaultWithin(entry.Promo.Type))
            .OrderBy(group => group.Key)
            .Select(group => new PromoWindow
            {
                Within = group.Key,
                PromoTypes = [],
                IncludeClockVariants = true,
                Folders = [.. group.Select(entry => Folder(entry.AdBreak, entry.Promo))]
            })];

        List<(AdBreakConfig AdBreak, AdBreakPromo Promo)> eventually = [.. promos.Where(entry => entry.Promo.Type == AdPromoType.Eventually)];
        AdBreakPromo? firstEventually = eventually.Count > 0 ? eventually[0].Promo : null;

        return new PromoConfig
        {
            Enabled = true,
            FromAdBreaks = true,
            BaseQuery = null,
            BaseTags = null,
            AnyShowAlias = adBreaks.Select(adBreak => adBreak.AnyShowFolder).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                           ?? AdBreakConfig.DefaultAnyShowFolder,
            Lookahead = adBreaks.Select(adBreak => adBreak.Lookahead).FirstOrDefault(value => value is not null)
                        ?? (windows.Count > 0 ? TimeSpan.FromTicks(Math.Max(defaults.Lookahead.Ticks, windows.Max(window => window.Within.Ticks))) : defaults.Lookahead),
            MaxPerBreak = adBreaks.Select(adBreak => adBreak.MaxPromosPerBreak).FirstOrDefault(value => value is not null) ?? defaults.MaxPerBreak,
            Windows = windows,
            UpNext = Spot(AdPromoType.UpNext, defaults.UpNext),
            NowPlaying = Spot(AdPromoType.NowPlaying, defaults.NowPlaying),
            ThisHasBeen = Spot(AdPromoType.ThisHasBeen, defaults.ThisHasBeen),
            YoureWatching = Spot(AdPromoType.YoureWatching, defaults.YoureWatching),
            BackToTheShow = Spot(AdPromoType.BackToTheShow, defaults.BackToTheShow),
            Eventually = new EventuallyConfig
            {
                Enabled = true,
                PromoTypes = [],
                Tag = string.Empty,
                Chance = Math.Clamp(firstEventually?.Chance ?? defaults.Eventually.Chance, 0, 1),
                Horizon = firstEventually?.Horizon ?? defaults.Eventually.Horizon,
                Folders = [.. eventually.Select(entry => Folder(entry.AdBreak, entry.Promo))]
            }
        };
    }

    private static PromoFolderSource Folder(AdBreakConfig adBreak, AdBreakPromo promo) => new(
        promo.Path,
        Excluded(adBreak, promo.Path, null),
        promo.PerShow,
        promo.AnyShow,
        promo.Times,
        promo.Days,
        adBreak.AnyShowFolder ?? AdBreakConfig.DefaultAnyShowFolder);

    /// <summary>Folders inside <paramref name="folder"/> that the ad break gives a different job, and so are left out of it.</summary>
    private static List<string> Excluded(AdBreakConfig adBreak, string folder, AdFolderRole? role)
    {
        List<string> excluded = [.. adBreak.Folders
            .Where(other => other.Role != role && AdBreakPaths.IsUnder(other.Path, folder))
            .Select(other => other.Path)];

        excluded.AddRange(adBreak.Promos
            .Where(promo => (role is not null || !AdBreakPaths.Same(promo.Path, folder)) && AdBreakPaths.IsUnder(promo.Path, folder))
            .Select(promo => promo.Path));

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
    public static QueryExpression Pool(IReadOnlyList<AdPoolSource> sources, string? filter, DateTimeOffset moment, TemporalTagOptions options)
    {
        QueryExpression pool = QueryExpression.Or(sources.Select(source => Source(source, moment, options)));
        if (pool is NoneExpression || string.IsNullOrWhiteSpace(filter))
        {
            return pool;
        }

        return QueryExpression.And([pool, QueryParser.Parse(filter)]);
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

    /// <summary>
    /// Reads what a promo clip's folders say about it (the show it's for, and the day and start time when the promo
    /// folder uses them) into the attributes promo matching already reads: <c>show_id</c>, <c>promo_day</c>,
    /// <c>promo_clock</c>. Null when it isn't in any of the folders, or has no show and no "any show" fallback.
    /// </summary>
    public static MediaItem? Describe(MediaItem item, IReadOnlyList<PromoFolderSource> folders)
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
        foreach (string segment in AdBreakPaths.FoldersBetween(source.Folder, path))
        {
            if (source.Days && day is null && TryReadDay(segment, out DayOfWeek weekday))
            {
                day = weekday.ToString().ToLowerInvariant();
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
            ["promo_clock"] = clock ?? string.Empty
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
