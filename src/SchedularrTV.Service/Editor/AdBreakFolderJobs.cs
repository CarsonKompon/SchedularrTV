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
        ("filler", "Filler"),
        ("skip", "Skip (leave out)")
    ];

    public static IReadOnlyList<(string Choice, string Label)> PromoChoices { get; } =
        [.. Enum.GetValues<AdPromoType>().Select(type => (PromoChoice(type), $"Promo: {AdBreakPromo.FolderName(type)}"))];

    public static string PromoChoice(AdPromoType type) => $"promo:{type}";

    public static string RoleChoice(AdFolderRole role) => role.ToString().ToLowerInvariant();

    /// <summary>The job given to this exact folder, or null.</summary>
    public static FolderJob? Explicit(AdBreakConfig adBreak, string path)
    {
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
            "ads" => new FolderJob(choice, "Ads", "job-ads"),
            "bumpers" => new FolderJob(choice, "Bumpers", "job-bumpers"),
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

        if (choice.StartsWith("promo:", StringComparison.Ordinal) && Enum.TryParse(choice[6..], out AdPromoType type))
        {
            adBreak.Promos.Add(NewPromo(type, path, previous));
        }
        else if (choice is "ads" or "bumpers" or "filler" or "skip")
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
        ["filler"] = AdFolderRole.Filler,
        ["fillers"] = AdFolderRole.Filler,
        ["loops"] = AdFolderRole.Filler
    };

    /// <summary>
    /// Folders named like an ad break job ("Ads", "Bumpers", "Up Next"…) that don't have one yet. Looked for beside and
    /// inside the folders the ad break already uses, or everywhere while it uses none.
    /// </summary>
    public static IReadOnlyList<FolderSuggestion> Suggest(AdBreakConfig adBreak, IReadOnlyList<CommercialFolder> roots, int max = 12)
    {
        List<string> used = [.. adBreak.Folders.Select(folder => folder.Path), .. adBreak.Promos.Select(promo => promo.Path)];
        List<CommercialFolder> all = [.. roots.SelectMany(root => root.Descendants())];

        IEnumerable<CommercialFolder> scope = used.Count == 0
            ? all
            : all.Where(folder => used.Any(path =>
                AdBreakPaths.IsUnder(folder.Path, path)
                || (Parent(all, path) is { } parent && (AdBreakPaths.IsUnder(folder.Path, parent.Path) || AdBreakPaths.Same(folder.Path, parent.Path)))));

        List<FolderSuggestion> suggestions = [];
        foreach (CommercialFolder folder in scope)
        {
            if (Explicit(adBreak, folder.Path) is not null || Inherited(adBreak, folder) is { Choice: "skip" or "filler" or "bumpers" } || folder.Parent is null)
            {
                continue;
            }

            if (AdBreakPromo.FromFolderName(folder.Name) is { } type
                && !adBreak.Promos.Any(promo => promo.Type == type && AdBreakPaths.Same(promo.Path, folder.Path))
                && Inherited(adBreak, folder) is null or { Choice: "ads" })
            {
                suggestions.Add(new FolderSuggestion(folder.Path, folder.Name, PromoChoice(type), $"{AdBreakPromo.FolderName(type)} promos"));
            }
            else if (RoleNames.TryGetValue(new string([.. folder.Name.Where(char.IsLetterOrDigit)]), out AdFolderRole role)
                     && Inherited(adBreak, folder) is null)
            {
                suggestions.Add(new FolderSuggestion(folder.Path, folder.Name, RoleChoice(role), role.ToString()));
            }

            if (suggestions.Count >= max)
            {
                break;
            }
        }

        return suggestions;
    }

    private static CommercialFolder? Parent(IReadOnlyList<CommercialFolder> all, string path) =>
        all.FirstOrDefault(folder => AdBreakPaths.Same(folder.Path, path)) is { } found ? found.Parent ?? found : null;

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
        if (job is { Choice: "ads" or "bumpers" or "filler" } && adBreak.Seasonal)
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
