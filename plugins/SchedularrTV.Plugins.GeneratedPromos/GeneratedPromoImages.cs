using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.GeneratedPromos;

/// <summary>The shows around the promo, independent of the show it is promoting.</summary>
public sealed record GeneratedImageSubjects(UpNextListing? Current, IReadOnlyList<UpNextListing> Upcoming)
{
    public static GeneratedImageSubjects FromSchedule(ChannelSchedule schedule, DateTimeOffset at, DateTimeOffset? after = null,
        GeneratedPromoTemplate? template = null)
    {
        UpNextListing? current = UpNextPromo.Previous(schedule, at);
        GeneratedUpcomingGrouping grouping = template?.UpcomingGrouping ?? GeneratedUpcomingGrouping.Timeslot;
        int needed = Math.Max(10, template is null ? 1 : GeneratedPromoResolver.ListingsNeeded(template));
        IReadOnlyList<UpNextListing> upcoming = UpNextPromo.Listings(schedule, after ?? at, needed, grouping: grouping);
        TimeSpan within = (template?.BackgroundRules ?? []).Where(rule => rule.MatchShows != false)
            .Select(rule => rule.Within ?? TimeSpan.Zero).DefaultIfEmpty().Max();
        if (within > TimeSpan.Zero)
        {
            upcoming = [.. upcoming.Concat(UpNextPromo.Listings(schedule, after ?? at, int.MaxValue, at + within, grouping))
                .DistinctBy(show => (show.Title, show.Start)).OrderBy(show => show.Start)];
        }
        return new(current, upcoming);
    }
}

public static class GeneratedPromoImages
{
    /// <summary>
    /// Each variable image's file, keyed by its name (resolved for the variable's own show context) and by every
    /// <c>name:selector</c> the template uses (resolved for that show instead), from image layers and generator arguments.
    /// Only the uses there are get entries, so templates that don't choose a show per use keep their render keys.
    /// </summary>
    /// <param name="automatic">Finds automatic pictures for shows without an override; without it, the default is used.</param>
    public static IReadOnlyDictionary<string, string> Resolve(GeneratedPromoTemplate template,
        IReadOnlyList<UpNextListing> promoted, GeneratedImageSubjects? subjects = null, IAutomaticImageLookup? automatic = null)
    {
        Dictionary<string, string> images = new(StringComparer.OrdinalIgnoreCase);
        List<GeneratedVariableImage> variables = template.VariableImages ?? [];
        foreach (GeneratedVariableImage image in variables)
        {
            images[image.Name] = PathFor(image, Show(template, promoted, subjects, image.Context, image.Offset), automatic);
        }

        foreach ((string name, string selector) in Uses(template))
        {
            if (variables.FirstOrDefault(image => string.Equals(image.Name, name, StringComparison.OrdinalIgnoreCase)) is { } image
                && GeneratedImageSelectors.TryParse(selector, out GeneratedImageContext context, out int offset))
            {
                images[GeneratedImageSelectors.Reference(image.Name, selector)] = PathFor(image, Show(template, promoted, subjects, context, offset), automatic);
            }
        }

        return images;
    }

    /// <summary>
    /// For each image layer showing an upcoming show (by its own choice or its variable's usual show): how many shows
    /// ahead it reaches, 1 being the next show.
    /// </summary>
    public static IEnumerable<int> UpcomingShown(GeneratedPromoTemplate template)
    {
        foreach (GeneratedLayer layer in template.Layers)
        {
            // A video of an upcoming episode counts the same way.
            if (layer is { Kind: GeneratedLayerKind.Video, Video.Episode: { } episode }
                && GeneratedImageSelectors.TryParse(episode, out GeneratedImageContext videoContext, out int videoOffset))
            {
                if (videoContext == GeneratedImageContext.UpNext)
                {
                    yield return Math.Max(0, videoOffset) + 1;
                }

                continue;
            }

            if (layer.Kind != GeneratedLayerKind.Image || layer.VariableImage is not { Length: > 0 } name
                || template.VariableImages?.FirstOrDefault(image => string.Equals(image.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } image)
            {
                continue;
            }

            (GeneratedImageContext context, int offset) = (image.Context, image.Offset);
            if (layer.VariableImageShow is { Length: > 0 } selector && GeneratedImageSelectors.TryParse(selector, out GeneratedImageContext chosen, out int ahead))
            {
                (context, offset) = (chosen, ahead);
            }

            if (context == GeneratedImageContext.UpNext)
            {
                yield return Math.Max(0, offset) + 1;
            }
        }
    }

    /// <summary>Every variable image use that picks its own show: from image layers, and from generator arguments.</summary>
    public static IEnumerable<(string Name, string Selector)> Uses(GeneratedPromoTemplate template)
    {
        foreach (GeneratedLayer layer in template.Layers)
        {
            if (layer.VariableImage is { Length: > 0 } name && layer.VariableImageShow is { Length: > 0 } selector)
            {
                yield return (name, selector);
            }
        }

        if (template.Generator is not { } generator)
        {
            yield break;
        }

        foreach (string argument in generator.Arguments.Concat(generator.PerShow).Concat(generator.ClipArguments))
        {
            foreach (System.Text.RegularExpressions.Match use in GeneratedImageSelectors.Placeholder.Matches(argument))
            {
                if (use.Groups[2].Success)
                {
                    yield return (use.Groups[1].Value, use.Groups[2].Value);
                }
            }
        }
    }

    /// <summary>The show a variable image (or a video layer's episode) is resolved for in this context, or null when the promo has none there.</summary>
    public static UpNextListing? Show(GeneratedPromoTemplate template, IReadOnlyList<UpNextListing> promoted, GeneratedImageSubjects? subjects,
        GeneratedImageContext context, int offset) => context switch
    {
        GeneratedImageContext.CurrentlyWatching => subjects is not null ? subjects.Current
            : template.Type is GeneratedPromoType.YoureWatching or GeneratedPromoType.BackToTheShow or GeneratedPromoType.ThisHasBeen
                ? promoted.FirstOrDefault() : null,
        GeneratedImageContext.UpNext => (subjects?.Upcoming
            ?? (template.Type is GeneratedPromoType.UpNext or GeneratedPromoType.NowPlaying ? promoted : []))
            .ElementAtOrDefault(Math.Max(0, offset)),
        _ => promoted.FirstOrDefault()
    };

    private static string PathFor(GeneratedVariableImage image, UpNextListing? show, IAutomaticImageLookup? automatic) =>
        image.PathFor(show?.Title, show?.Block, automatic);
}
