using System.Globalization;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Core.Temporal;

namespace SchedularrTV.Service.Editor;

/// <summary>A folder's job in an ad break, as the folder tree shows it.</summary>
/// <param name="Choice">The value of the tree's picker: <c>ads</c>, <c>bumpers</c>, <c>filler</c>, <c>skip</c> or <c>promo:UpNext</c>.</param>
public sealed record FolderJob(string Choice, string Label, string CssClass);

/// <summary>A job picked for a folder in the tree.</summary>
public sealed record FolderPick(CommercialFolder Folder, string Choice);

/// <summary>A folder the editor thinks has an obvious job, offered as a one-click suggestion.</summary>
public sealed record FolderSuggestion(string Path, string FolderName, string Choice, string Label);

/// <summary>A folder the ad break would look for that isn't there (yet), shown greyed out in the tree.</summary>
/// <param name="Name">Relative to the folder it's shown under, e.g. <c>Top Cat/8pm</c>.</param>
public sealed record PhantomFolder(string Name, string Hint);

/// <summary>
/// The ad break editor's folder logic: what each folder is used for (its own job, or the one it inherits from the
/// folder it's in), changing it, the suggestions, and the example folders a seasonal folder or promo folder implies.
/// </summary>
public static class AdBreakFolderJobs
{
    public const string None = "";

    public static readonly IReadOnlyList<(string Choice, string Label)> RoleChoices =
    [
        ("ads", "Ads"),
        ("bumpers", "Bumpers"),
        ("blockstart", "Block start"),
        ("blockend", "Block end"),
        ("filler", "Filler"),
        ("skip", "Skip (leave out)")
    ];

    /// <summary>A folder of promos sorted by show and type, every type found by its folder's name.</summary>
    public const string PromoRootChoice = "promos";

    public static IReadOnlyList<(string Choice, string Label)> PromoChoices { get; } =
    [
        (PromoRootChoice, "Promos: all types, by folder name"),
        .. Enum.GetValues<AdPromoType>().Select(type => (PromoChoice(type), $"Promo: {AdBreakPromo.FolderName(type)} only"))
    ];

    public static string PromoChoice(AdPromoType type) => $"promo:{type}";

    public static string RoleChoice(AdFolderRole role) => role.ToString().ToLowerInvariant();

    /// <summary>The job given to this exact folder, or null.</summary>
    public static FolderJob? Explicit(AdBreakConfig adBreak, string path)
    {
        if (adBreak.PromoRoots.Any(root => AdBreakPaths.Same(root.Path, path)))
        {
            return Job(PromoRootChoice);
        }

        if (adBreak.Promos.LastOrDefault(promo => AdBreakPaths.Same(promo.Path, path)) is { } promo)
        {
            return Job(PromoChoice(promo.Type));
        }

        return adBreak.Folders.LastOrDefault(folder => AdBreakPaths.Same(folder.Path, path)) is { } own
            ? Job(RoleChoice(own.Role))
            : null;
    }

    /// <summary>The job a folder takes from the nearest folder above it that has one, or null.</summary>
    public static FolderJob? Inherited(AdBreakConfig adBreak, CommercialFolder folder)
    {
        foreach (CommercialFolder ancestor in folder.Ancestors())
        {
            if (Explicit(adBreak, ancestor.Path) is { } job)
            {
                return job;
            }
        }

        return null;
    }

    public static FolderJob Job(string choice)
    {
        if (choice.StartsWith("promo:", StringComparison.Ordinal) && Enum.TryParse(choice[6..], out AdPromoType type))
        {
            return new FolderJob(choice, AdBreakPromo.FolderName(type), "job-promo");
        }

        return choice switch
        {
            PromoRootChoice => new FolderJob(choice, "Promos", "job-promo"),
            "ads" => new FolderJob(choice, "Ads", "job-ads"),
            "bumpers" => new FolderJob(choice, "Bumpers", "job-bumpers"),
            "blockstart" => new FolderJob(choice, "Block start", "job-bumpers"),
            "blockend" => new FolderJob(choice, "Block end", "job-bumpers"),
            "filler" => new FolderJob(choice, "Filler", "job-filler"),
            "skip" => new FolderJob(choice, "Skipped", "job-skip"),
            _ => new FolderJob(None, "Not used", "job-none")
        };
    }

    /// <summary>Gives a folder a job (or none), replacing whatever it had.</summary>
    public static void Set(AdBreakConfig adBreak, string path, string choice)
    {
        AdBreakPromo? previous = adBreak.Promos.FirstOrDefault(promo => AdBreakPaths.Same(promo.Path, path));
        adBreak.Folders.RemoveAll(folder => AdBreakPaths.Same(folder.Path, path));
        adBreak.Promos.RemoveAll(promo => AdBreakPaths.Same(promo.Path, path));
        adBreak.PromoRoots.RemoveAll(root => AdBreakPaths.Same(root.Path, path));

        if (choice == PromoRootChoice)
        {
            adBreak.PromoRoots.Add(new AdBreakPromoRoot { Path = path });
        }
        else if (choice.StartsWith("promo:", StringComparison.Ordinal) && Enum.TryParse(choice[6..], out AdPromoType type))
        {
            adBreak.Promos.Add(NewPromo(type, path, previous));
        }
        else if (choice is "ads" or "bumpers" or "blockstart" or "blockend" or "filler" or "skip")
        {
            adBreak.Folders.Add(new AdBreakFolder { Path = path, Role = Enum.Parse<AdFolderRole>(choice, ignoreCase: true) });
        }
    }

    /// <summary>A new promo with the usual settings for its type, keeping the folder options of the one it replaces.</summary>
    private static AdBreakPromo NewPromo(AdPromoType type, string path, AdBreakPromo? previous) => new()
    {
        Type = type,
        Path = path,
        PerShow = previous?.PerShow ?? true,
        AnyShow = previous?.AnyShow ?? true,
        Times = previous?.Times ?? type is AdPromoType.ComingUp or AdPromoType.LaterToday or AdPromoType.Eventually,
        Days = previous?.Days ?? type == AdPromoType.Eventually
    };

    // --- Suggestions ------------------------------------------------------------------------------------------

    private static readonly Dictionary<string, AdFolderRole> RoleNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ads"] = AdFolderRole.Ads,
        ["adverts"] = AdFolderRole.Ads,
        ["advertisements"] = AdFolderRole.Ads,
        ["commercials"] = AdFolderRole.Ads,
        ["bumpers"] = AdFolderRole.Bumpers,
        ["bumps"] = AdFolderRole.Bumpers,
        ["idents"] = AdFolderRole.Bumpers,
        ["ids"] = AdFolderRole.Bumpers,
        ["stationids"] = AdFolderRole.Bumpers,
        ["stingers"] = AdFolderRole.Bumpers,
        ["blockstart"] = AdFolderRole.BlockStart,
        ["blockintro"] = AdFolderRole.BlockStart,
        ["blockopening"] = AdFolderRole.BlockStart,
        ["blockend"] = AdFolderRole.BlockEnd,
        ["blockoutro"] = AdFolderRole.BlockEnd,
        ["blockclosing"] = AdFolderRole.BlockEnd,
        ["filler"] = AdFolderRole.Filler,
        ["fillers"] = AdFolderRole.Filler,
        ["loops"] = AdFolderRole.Filler
    };

    /// <summary>
    /// Folders named like an ad break job ("Bumpers", "Filler", "Up Next"…) that don't have one yet: inside
    /// <paramref name="within"/> when given, else inside the folders the ad break uses (everywhere while it uses none).
    /// A folder that already plays as ads isn't suggested as ads again.
    /// </summary>
    public static IReadOnlyList<FolderSuggestion> Suggest(
        AdBreakConfig adBreak, IReadOnlyList<CommercialFolder> roots, int max = 12, string? within = null, TemporalTagOptions? temporal = null)
    {
        temporal ??= new TemporalTagOptions();
        List<string> used = within is not null
            ? [within]
            : [.. adBreak.Folders.Select(folder => folder.Path), .. adBreak.Promos.Select(promo => promo.Path), .. adBreak.PromoRoots.Select(root => root.Path)];
        List<CommercialFolder> all = [.. roots.SelectMany(root => root.Descendants())];

        List<CommercialFolder> scope = used.Count == 0
            ? [.. all.Where(folder => folder.Parent is not null)]
            : [.. all.Where(folder => used.Any(path => AdBreakPaths.IsUnder(folder.Path, path)))];

        // Promos sorted by show and type (Promos/Looney Tunes/Back To The Show) are offered as one folder, and nothing
        // inside it is suggested on its own.
        List<FolderSuggestion> suggestions = [];
        List<CommercialFolder> promoRoots = [];
        foreach (CommercialFolder candidate in PromoRootCandidates(scope, within ?? (used.Count == 1 ? used[0] : null), temporal))
        {
            if (Explicit(adBreak, candidate.Path) is not null || Inherited(adBreak, candidate) is { CssClass: "job-promo" } or { Choice: "skip" })
            {
                continue;
            }

            promoRoots.Add(candidate);
            List<string> types = [.. candidate.Descendants()
                .Select(folder => AdBreakPromo.FromFolderName(folder.Name))
                .OfType<AdPromoType>()
                .Distinct()
                .Select(AdBreakPromo.FolderName)];
            suggestions.Add(new FolderSuggestion(candidate.Path, candidate.Name, PromoRootChoice,
                $"Promos by show ({string.Join(", ", types.Take(4))}{(types.Count > 4 ? "…" : "")})"));
        }

        foreach (CommercialFolder folder in scope)
        {
            if (promoRoots.Any(root => AdBreakPaths.IsUnder(folder.Path, root.Path)))
            {
                continue;
            }

            FolderJob? inherited = Inherited(adBreak, folder);
            if (Explicit(adBreak, folder.Path) is not null || inherited is { Choice: "skip" or "filler" or "bumpers" or "blockstart" or "blockend" } || inherited?.CssClass == "job-promo")
            {
                continue;
            }

            if (AdBreakPromo.FromFolderName(folder.Name) is { } type)
            {
                suggestions.Add(new FolderSuggestion(folder.Path, folder.Name, PromoChoice(type), $"{AdBreakPromo.FolderName(type)} promos"));
            }
            else if (RoleNames.TryGetValue(new string([.. folder.Name.Where(char.IsLetterOrDigit)]), out AdFolderRole role)
                     && (inherited is null || (inherited.Choice == "ads" && role != AdFolderRole.Ads)))
            {
                suggestions.Add(new FolderSuggestion(folder.Path, folder.Name, RoleChoice(role), Job(RoleChoice(role)).Label));
            }

            if (suggestions.Count >= max)
            {
                break;
            }
        }

        return suggestions;
    }

    private static readonly HashSet<string> PromoFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "promos", "promo", "promotions", "promotional", "network promos", "channel promos"
    };

    private static bool IsTypeFolder(CommercialFolder folder) => AdBreakPromo.FromFolderName(folder.Name) is not null;

    /// <summary>A folder named for a time of year, a weekday, a start time, or "any time"/"any show"/"any movie": a qualifier, not a show.</summary>
    private static bool IsQualifier(CommercialFolder folder, TemporalTagOptions temporal) =>
        SeasonalFolders.IsSeasonal(folder.Name, temporal)
        || string.Equals(folder.Name, temporal.AnyTimeTag, StringComparison.OrdinalIgnoreCase)
        || string.Equals(folder.Name, AdBreakConfig.DefaultAnyShowFolder, StringComparison.OrdinalIgnoreCase)
        || string.Equals(folder.Name, AdBreakConfig.DefaultAnyMovieFolder, StringComparison.OrdinalIgnoreCase)
        || AdBreakCompiler.TryReadDay(folder.Name, out _)
        || AdBreakCompiler.TryReadClock(folder.Name, out _);

    /// <summary>Whether a folder has a folder named for a promo type in it, directly or under qualifiers (dates, days…).</summary>
    private static bool HasTypeChild(CommercialFolder folder, TemporalTagOptions temporal) =>
        folder.Children.Any(child => IsTypeFolder(child) || (IsQualifier(child, temporal) && HasTypeChild(child, temporal)));

    /// <summary>
    /// Folders that look like promos sorted by show and type, worked out from where the folders named for promo types
    /// sit: under show folders (<c>Promos/Looney Tunes/Back To The Show</c>, the show folder having others like it or
    /// a "Promos" folder above it), or side by side in one folder (<c>Promos/Up Next</c>, <c>Promos/Now Playing</c>).
    /// Date, weekday and time folders in between are looked through. Never <paramref name="exclude"/> itself.
    /// </summary>
    public static IReadOnlyList<CommercialFolder> PromoRootCandidates(IEnumerable<CommercialFolder> scope, string? exclude, TemporalTagOptions temporal)
    {
        List<CommercialFolder> found = [];

        bool Usable(CommercialFolder? folder) =>
            folder is not null && (exclude is null || AdBreakPaths.IsUnder(folder.Path, exclude));

        CommercialFolder? Above(CommercialFolder? folder)
        {
            // Past any date, weekday or time folders.
            while (folder is not null && IsQualifier(folder, temporal))
            {
                folder = folder.Parent;
            }

            return folder;
        }

        foreach (CommercialFolder type in scope.Where(IsTypeFolder))
        {
            CommercialFolder? parent = Above(type.Parent);
            if (parent is null || IsTypeFolder(parent))
            {
                continue;
            }

            CommercialFolder? container = null;
            CommercialFolder? grandparent = Above(parent.Parent);

            bool parentIsShow = grandparent is not null
                && (PromoFolderNames.Contains(grandparent.Name)
                    || grandparent.Children.Count(sibling => !ReferenceEquals(sibling, parent) && !IsTypeFolder(sibling) && HasTypeChild(sibling, temporal)) > 0);

            if (parentIsShow && Usable(grandparent))
            {
                container = grandparent;
            }
            else if (Usable(parent) && (PromoFolderNames.Contains(parent.Name) || parent.Children.Count(IsTypeFolder) >= 2))
            {
                // Types side by side: Promos/Up Next/Top Cat.
                container = parent;
            }

            if (container is not null && !found.Contains(container))
            {
                found.Add(container);
            }
        }

        // Folders named "Promos" with promo type folders somewhere inside.
        foreach (CommercialFolder folder in scope.Where(folder => PromoFolderNames.Contains(folder.Name) && Usable(folder)))
        {
            if (!found.Contains(folder) && folder.Descendants().Skip(1).Any(IsTypeFolder))
            {
                found.Add(folder);
            }
        }

        // The outermost only: one inside another is covered by it.
        return [.. found.Where(folder => !found.Any(other => !ReferenceEquals(other, folder) && AdBreakPaths.IsUnder(folder.Path, other.Path)))];
    }

    /// <summary>
    /// A new ad break drawing on everything in <paramref name="root"/>: the root plays as ads, and each suggestion
    /// in <paramref name="accepted"/> gives a folder inside it its own job (unless one above it already took a job
    /// that covers it). Seasonal when asked.
    /// </summary>
    public static AdBreakConfig FromRoot(CommercialFolder root, IEnumerable<FolderSuggestion> accepted, bool seasonal)
    {
        AdBreakConfig adBreak = new() { Seasonal = seasonal, Root = root.Path };
        Set(adBreak, root.Path, "ads");

        // Outermost first, so a promo folder's own subfolders aren't given jobs inside it.
        foreach (FolderSuggestion suggestion in accepted.OrderBy(suggestion => AdBreakPaths.Key(suggestion.Path).Length))
        {
            bool covered = adBreak.Promos.Any(promo => AdBreakPaths.IsUnder(suggestion.Path, promo.Path))
                           || adBreak.PromoRoots.Any(promoRoot => AdBreakPaths.IsUnder(suggestion.Path, promoRoot.Path))
                           || adBreak.Folders.Any(folder => folder.Role != AdFolderRole.Ads && AdBreakPaths.IsUnder(suggestion.Path, folder.Path));
            if (!covered)
            {
                Set(adBreak, suggestion.Path, suggestion.Choice);
            }
        }

        return adBreak;
    }

    /// <summary>Whether a folder has month, season, holiday, date or "Any Time" folders inside it.</summary>
    public static bool HasSeasonalFolders(CommercialFolder root, TemporalTagOptions temporal) =>
        root.Descendants().Any(folder => folder.Parent is not null
            && (SeasonalFolders.IsSeasonal(folder.Name, temporal) || string.Equals(folder.Name, temporal.AnyTimeTag, StringComparison.OrdinalIgnoreCase)));

    // --- Example folders --------------------------------------------------------------------------------------

    /// <summary>
    /// The folders the ad break would look for inside <paramref name="folder"/> that aren't there: seasonal ones for an
    /// ads, bumpers or filler folder (when seasonal), show, time and day folders for a promo folder.
    /// </summary>
    public static IReadOnlyList<PhantomFolder> Phantoms(
        AdBreakConfig adBreak, CommercialFolder folder, TemporalTagOptions temporal, DateTimeOffset now, IReadOnlyList<string> shows)
    {
        HashSet<string> existing = new(folder.Children.Select(child => child.Name), StringComparer.OrdinalIgnoreCase);
        List<PhantomFolder> phantoms = [];

        // A folder that's really there is shown as itself, so only the missing ones are added.
        void Add(string name, string hint)
        {
            if ((name.Contains('/') || !existing.Contains(name)) && !phantoms.Any(phantom => phantom.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                phantoms.Add(new PhantomFolder(name, hint));
            }
        }

        FolderJob? job = Explicit(adBreak, folder.Path);
        if (job is { Choice: "ads" or "bumpers" or "filler" or "blockstart" or "blockend" } && adBreak.Seasonal)
        {
            string month = CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(now.Month);
            string nextMonth = CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(now.AddMonths(1).Month);
            Add(temporal.AnyTimeTag is { Length: > 0 } anyTime ? anyTime : "Any Time", "Plays all year (same as leaving it loose)");
            Add(month, "Plays all this month");
            Add(nextMonth, $"Plays all of {nextMonth}");
            Add(TemporalTagResolver.GetSeason(now.Month), "Plays all season");
            foreach (string holiday in UpcomingHolidays(temporal, now).Take(2))
            {
                Add(holiday, "Plays in the run-up to the holiday");
            }

            Add(now.ToString("MM-dd", CultureInfo.InvariantCulture), "Plays on that date only");
        }
        else if (job is { Choice: PromoRootChoice } && adBreak.PromoRoots.LastOrDefault(root => AdBreakPaths.Same(root.Path, folder.Path)) is { } promoRoot)
        {
            // Sorted by show then type, or type then show: either reads the same.
            string show = shows.FirstOrDefault() ?? "Show Name";
            Add(show, "A show's folder, named as the library names it");
            Add($"{show}/Up Next", "Its Up Next promos (any type's name works: Now Playing, Back To The Show…)");
            Add($"{show}/You're Watching", "Its You're Watching promos");
            if (promoRoot.Times)
            {
                Add($"{show}/Up Next/8pm", "Only for an 8pm start");
            }

            if (promoRoot.Days)
            {
                Add($"{show}/Eventually/Saturday", "Only for a Saturday airing");
            }

            if (promoRoot.AnyShow)
            {
                Add($"{adBreak.AnyShowFolder ?? AdBreakConfig.DefaultAnyShowFolder}/Now Playing", "Now Playing clips for any show");
            }

            Add($"{adBreak.AnyMovieFolder ?? AdBreakConfig.DefaultAnyMovieFolder}/Now Playing", "Now Playing clips for any movie (used instead of any show's when a movie is on)");

            if (adBreak.Seasonal)
            {
                Add($"Christmas/{show}/Up Next", "Seasonal: only in the run-up to Christmas");
            }
        }
        else if (job is not null && adBreak.Promos.LastOrDefault(promo => AdBreakPaths.Same(promo.Path, folder.Path)) is { } promo)
        {
            string anyShow = adBreak.AnyShowFolder ?? AdBreakConfig.DefaultAnyShowFolder;
            List<string> examples = [.. shows.Take(2)];
            if (examples.Count == 0)
            {
                examples.Add("Show Name");
            }

            string? time = promo.Times ? "8pm" : null;
            string? day = promo.Days ? "Saturday" : null;
            string Inner(string prefix)
            {
                string path = prefix;
                if (day is not null) path = path.Length == 0 ? day : $"{path}/{day}";
                if (time is not null) path = path.Length == 0 ? time : $"{path}/{time}";
                return path;
            }

            if (promo.PerShow)
            {
                foreach (string show in examples)
                {
                    Add(show, "Clips for this show only (named as the library names it)");
                    if (day is not null || time is not null)
                    {
                        Add(Inner(show), Hint(day, time));
                    }
                }

                if (promo.AnyShow)
                {
                    Add(anyShow, "Clips for any show");
                }

                Add(adBreak.AnyMovieFolder ?? AdBreakConfig.DefaultAnyMovieFolder, "Clips for any movie (used instead of any show's when a movie is on)");
            }
            else if (day is not null || time is not null)
            {
                Add(Inner(string.Empty), Hint(day, time));
            }
        }

        return phantoms;
    }

    private static string Hint(string? day, string? time) =>
        (day, time) switch
        {
            ({ } d, { } t) => $"Only for a show airing {d} at {t}",
            ({ } d, null) => $"Only for a show airing on a {d}",
            (null, { } t) => $"Only for a show starting at {t} (also 8:30pm, 830pm…)",
            _ => string.Empty
        };

    /// <summary>Holidays running now or coming up next, soonest first.</summary>
    public static IEnumerable<string> UpcomingHolidays(TemporalTagOptions temporal, DateTimeOffset now)
    {
        IReadOnlyList<HolidayDefinition> holidays = temporal.Holidays.Count > 0 ? temporal.Holidays : TemporalTagOptions.DefaultHolidays;
        DateTime today = now.Date;
        return holidays
            .Select(holiday => (holiday.Name, Days: Enumerable.Range(0, 366).FirstOrDefault(offset => TemporalTagResolver.IsWithin(holiday, today.AddDays(offset)), -1)))
            .Where(entry => entry.Days >= 0 && !string.IsNullOrWhiteSpace(entry.Name))
            .OrderBy(entry => entry.Days)
            .Select(entry => entry.Name);
    }

    /// <summary>What a seasonal folder name means, for the badge beside a real folder; null for an ordinary folder.</summary>
    public static string? SeasonalNote(string name, TemporalTagOptions temporal, DateTimeOffset now)
    {
        if (string.Equals(name, temporal.AnyTimeTag, StringComparison.OrdinalIgnoreCase))
        {
            return "all year";
        }

        if (!SeasonalFolders.IsSeasonal(name, temporal))
        {
            return null;
        }

        return SeasonalFolders.IsActive(name, now, temporal) ? "seasonal · playing now" : "seasonal · not now";
    }
}
