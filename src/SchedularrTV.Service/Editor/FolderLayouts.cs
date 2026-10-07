using System.Globalization;
using System.Text.RegularExpressions;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Core.Temporal;
using SchedularrTV.Providers.FileSystem;

namespace SchedularrTV.Service.Editor;

/// <summary>Which of a channel's layouts a preview shows.</summary>
public enum FolderPreviewKind
{
    Breaks,
    Promos
}

/// <summary>How a folder in a layout preview relates to the library.</summary>
public enum FolderState
{
    /// <summary>A folder by this name is in the library.</summary>
    Found,

    /// <summary>Nothing by this name in the library yet.</summary>
    Missing,

    /// <summary>Stands for a name of your choosing ("any folder", "a show's name").</summary>
    Placeholder,

    /// <summary>Fixed by the library's settings (the promo folder, a root on disk), not checked.</summary>
    Fixed
}

/// <summary>One folder name a level accepts.</summary>
public sealed record FolderChoice(string Name, FolderState State, string? Hint = null);

/// <summary>One level of a layout: any one of its choices.</summary>
/// <param name="Caption">What the level is for, e.g. "brand".</param>
/// <param name="Optional">The level can be left out.</param>
public sealed record FolderLevel(string Caption, IReadOnlyList<FolderChoice> Choices, bool Optional = false, string? Note = null);

/// <summary>A folder layout the scheduler looks for: levels from the root down, then the clip.</summary>
public sealed record FolderLayout(string Title, string? Summary, IReadOnlyList<FolderLevel> Levels, string FileName, IReadOnlyList<string> Notes);

/// <summary>
/// Works out, from a channel's settings, the folder layouts its breaks and promos are drawn from, so the editor can
/// show where to put the files. The file system library turns every folder into a tag (and the levels under the promo
/// folder into show and promo type), and the scheduler matches those; the order shown is the conventional one.
/// </summary>
public static class FolderLayouts
{
    private const int MaxChoices = 8;

    /// <summary>The layouts of a channel's ad breaks: commercials (tier 2) and filler (tier 3).</summary>
    public static IReadOnlyList<FolderLayout> Breaks(SchedulerConfig config, ChannelConfig channel, CommercialFolders library,
        IReadOnlyList<MediaFolderOptions> roots, DateTimeOffset now)
    {
        InterstitialsConfig breaks = SchedulingEngine.ResolvedBreaks(config, channel);
        CommercialsConfig commercials = breaks.Commercials!;
        FillerTierConfig filler = breaks.Filler!;
        TemporalTagOptions temporal = config.Settings.TemporalTags;
        List<FolderLayout> layouts = [];

        layouts.Add(Clips("Commercials", "Tier 2: whole clips that fit the gap.",
            commercials.Query ?? commercials.BaseQuery, commercials.BaseTags, commercials.Types, commercials.AutoTemporalTags == true,
            commercials.Filter, library, roots, temporal, now, "advert.mp4"));

        if (filler.IsEnabled)
        {
            layouts.Add(Clips("Filler", "Tier 3: closes the last few seconds; may be trimmed.",
                filler.Query ?? filler.BaseQuery, filler.BaseTags, filler.Types, filler.AutoTemporalTags == true,
                filler.Filter, library, roots, temporal, now, "bumper.mp4"));
        }

        return layouts;
    }

    /// <summary>The layouts of a channel's promos: the fixed spots and lookahead promos, and Eventually trailers.</summary>
    public static IReadOnlyList<FolderLayout> Promos(SchedulerConfig config, ChannelConfig channel, CommercialFolders library,
        IReadOnlyList<MediaFolderOptions> roots, IReadOnlyList<string> exampleShows)
    {
        (PromoConfig promos, List<string>? brands) = SchedulingEngine.ResolvedPromos(config, channel);
        if (!promos.Enabled)
        {
            return [];
        }

        MediaFolderOptions[] clipRoots = [.. roots.Where(root => !root.IsLibrary)];
        string promoFolder = clipRoots.Select(root => root.PromoFolder).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "Promos";
        List<string> notes = [];
        List<FolderLevel> start = Start(promos.BaseQuery, null, brands, library, clipRoots, notes, "Leave the brand folder out to use promos from every brand.");
        FolderLevel promoLevel = new("promo folder", [new FolderChoice(promoFolder, FolderState.Fixed, "Set per media folder in App settings")]);

        List<FolderChoice> shows = [.. exampleShows.Take(3).Select(show => new FolderChoice(show, FolderState.Placeholder, "A show on this channel"))];
        if (exampleShows.Count == 0)
        {
            shows.Add(new FolderChoice("‹show name›", FolderState.Placeholder, "The series name, as the library has it"));
        }

        if (!string.IsNullOrWhiteSpace(promos.AnyShowAlias))
        {
            shows.Add(new FolderChoice(promos.AnyShowAlias, FolderState.Fixed, "Stands in for any show"));
        }

        FolderLevel showLevel = new("show", shows, Note: "Named exactly as the series is");

        // Each spot and lookahead window, by the folder name that reads as its promo type.
        List<FolderChoice> types = [];
        void Spot(PromoSpotConfig spot)
        {
            if (spot.Enabled && TypeFolder(spot.PromoTypes, spot.Tag) is { } name)
            {
                types.Add(new FolderChoice(name, FolderState.Fixed));
            }
        }

        Spot(promos.UpNext);
        Spot(promos.NowPlaying);
        Spot(promos.ThisHasBeen);
        Spot(promos.YoureWatching);
        Spot(promos.BackToTheShow);
        IReadOnlyList<PromoWindow> windows = promos.Windows.Count > 0 ? promos.Windows : PromoConfig.DefaultWindows;
        foreach (PromoWindow window in windows)
        {
            if (TypeFolder(window.PromoTypes, null) is { } name && !types.Any(type => type.Name == name))
            {
                types.Add(new FolderChoice(name, FolderState.Fixed, $"Within {Span(window.Within)} of the show"));
            }
        }

        List<FolderLayout> layouts = [];
        if (types.Count > 0)
        {
            List<FolderLevel> levels = [.. start, promoLevel, showLevel, new FolderLevel("promo type", types)];
            if (windows.Any(window => window.IncludeClockVariants))
            {
                levels.Add(new FolderLevel("start time", [new FolderChoice("4pm", FolderState.Placeholder, "When the show starts; preferred at that time")],
                    Optional: true, Note: "Lookahead promos only"));
            }

            layouts.Add(new FolderLayout("Promos", "Up Next, idents and lookahead promos for the show they name.", levels, "promo.mp4", notes));
        }

        if (promos.Eventually.Enabled && TypeFolder(promos.Eventually.PromoTypes, promos.Eventually.Tag) is { } eventually)
        {
            List<FolderLevel> levels =
            [
                .. start, promoLevel, showLevel,
                new FolderLevel("promo type", [new FolderChoice(eventually, FolderState.Fixed)]),
                new FolderLevel("day", [new FolderChoice("Saturday", FolderState.Placeholder, "The day it airs")], Optional: true),
                new FolderLevel("start time", [new FolderChoice("4pm", FolderState.Placeholder, "When it airs")], Optional: true)
            ];
            layouts.Add(new FolderLayout("Eventually trailers",
                $"Mixed in among the ads for a show airing in the next {Span(promos.Eventually.Horizon)}.", levels, "trailer.mp4", notes));
        }

        return layouts;
    }

    /// <summary>Show names from a channel's own picks, for the promo preview's examples.</summary>
    public static IReadOnlyList<string> ExampleShows(ChannelConfig channel)
    {
        List<ContentSelector> selectors = [.. channel.Blocks.SelectMany(block => block.Shows)];
        if (channel.BaseRotation is { } rotation)
        {
            selectors.Add(rotation);
        }

        return [.. selectors
            .SelectMany(selector => selector.Include)
            .Select(include => include.Show)
            .OfType<string>()
            .Where(show => !string.IsNullOrWhiteSpace(show))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static FolderLayout Clips(string title, string summary, string? pool, List<string>? brands, List<string>? kinds, bool temporal,
        string? filter, CommercialFolders library, IReadOnlyList<MediaFolderOptions> roots, TemporalTagOptions temporalOptions,
        DateTimeOffset now, string file)
    {
        List<string> notes = [];
        MediaFolderOptions[] clipRoots = [.. roots.Where(root => !root.IsLibrary)];
        List<FolderLevel> levels = Start(pool, filter, brands, library, clipRoots, notes,
            brands is { Count: > 0 } ? null : "Any brand folder will do: none is chosen.");

        levels.Add(kinds is { Count: > 0 }
            ? new FolderLevel("clip folder", Choices(kinds, library.Types))
            : new FolderLevel("clip folder", [new FolderChoice("‹any folder›", FolderState.Placeholder, "None chosen, so any (or none)")], Optional: true));

        if (temporal)
        {
            TemporalTags tags = TemporalTagResolver.Resolve(now, temporalOptions);
            List<string> when = [temporalOptions.AnyTimeTag, .. tags.All.Where(tag => tag != tags.ExactDate && tag != tags.FullDate)];
            levels.Add(new FolderLevel("when it airs", Choices(when, library.Occasions),
                Note: $"Today's; another month, season, holiday or date ({tags.ExactDate}) folder is used on its days"));
        }

        return new FolderLayout(title, summary, levels, file, notes);
    }

    /// <summary>The root (from the pool query, else the media folders on disk) and the brand level.</summary>
    private static List<FolderLevel> Start(string? pool, string? filter, List<string>? brands, CommercialFolders library,
        IReadOnlyList<MediaFolderOptions> roots, List<string> notes, string? anyBrandNote)
    {
        List<FolderLevel> levels = [];
        string? rootTag = SimpleTag(pool);
        MediaFolderOptions[] matching = rootTag is null
            ? [.. roots]
            : [.. roots.Where(root => root.Tags.Contains(rootTag, StringComparer.OrdinalIgnoreCase))];

        List<FolderChoice> rootChoices = [.. matching.Take(MaxChoices).Select(root => new FolderChoice(root.Path, FolderState.Fixed,
            root.Tags.Count > 0 ? $"Tagged {string.Join(", ", root.Tags)}" : null))];
        if (rootChoices.Count == 0)
        {
            rootChoices.Add(rootTag is null
                ? new FolderChoice("‹a media folder›", FolderState.Placeholder, "Add one under App settings → Commercials library")
                : new FolderChoice(rootTag, library.Roots.Contains(rootTag, StringComparer.OrdinalIgnoreCase) ? FolderState.Found : FolderState.Missing,
                    $"A media folder tagged {rootTag}, or a folder named {rootTag}"));
        }

        levels.Add(new FolderLevel("root", rootChoices));
        if (!string.IsNullOrWhiteSpace(pool) && rootTag is null)
        {
            notes.Add($"Clips must also match the query {pool}.");
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            notes.Add($"…and the filter {filter}.");
        }

        levels.Add(brands is { Count: > 0 }
            ? new FolderLevel("brand", Choices(brands, library.Brands))
            : new FolderLevel("brand", [new FolderChoice("‹any brand›", FolderState.Placeholder)], Optional: true, Note: anyBrandNote));
        return levels;
    }

    private static List<FolderChoice> Choices(IEnumerable<string> names, IReadOnlyList<string> known)
    {
        List<string> distinct = [.. names.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase)];
        List<FolderChoice> choices = [.. distinct.Take(MaxChoices).Select(name =>
            new FolderChoice(name, known.Contains(name, StringComparer.OrdinalIgnoreCase) ? FolderState.Found : FolderState.Missing))];
        if (distinct.Count > MaxChoices)
        {
            choices.Add(new FolderChoice($"+{distinct.Count - MaxChoices} more", FolderState.Placeholder));
        }

        return choices;
    }

    /// <summary>The tag of a query that is just one tag (<c>tag:"Commercials"</c>), else null.</summary>
    public static string? SimpleTag(string? query) =>
        query is not null && Regex.Match(query.Trim(), "^tag\\s*[:=]\\s*(?:\"([^\"]+)\"|'([^']+)'|([^\\s\"']+))$", RegexOptions.IgnoreCase) is { Success: true } match
            ? match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value
            : null;

    /// <summary>
    /// The folder name that reads as one of <paramref name="types"/>: the spot's tag when it does ("You're Watching"
    /// reads as youre_watching), else the first type written out ("later_today" → "Later Today").
    /// </summary>
    public static string? TypeFolder(IReadOnlyList<string> types, string? tag)
    {
        if (!string.IsNullOrWhiteSpace(tag) && (types.Count == 0 || types.Contains(Normalise(tag), StringComparer.OrdinalIgnoreCase)))
        {
            return tag;
        }

        return types.FirstOrDefault(type => !string.IsNullOrWhiteSpace(type)) is { } first
            ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(first.Replace('_', ' '))
            : tag;
    }

    /// <summary>How the file system library reads a promo type folder (as <c>FileSystemMediaLibrary.NormalisePromoType</c>).</summary>
    private static string Normalise(string folder)
    {
        string stripped = folder.ToLowerInvariant().Replace("'", string.Empty).Replace("’", string.Empty);
        return string.Join('_', stripped.Split([.. stripped.Where(c => !char.IsLetterOrDigit(c)).Distinct()],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string Span(TimeSpan span) =>
        span.TotalDays >= 1 ? $"{span.TotalDays:0.#} days" : span.TotalHours >= 1 ? $"{span.TotalHours:0.#} hours" : $"{span.TotalMinutes:0} minutes";
}
