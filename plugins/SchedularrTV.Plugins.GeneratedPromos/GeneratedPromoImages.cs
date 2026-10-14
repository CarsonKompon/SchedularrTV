using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.GeneratedPromos;

/// <summary>The shows around the promo, independent of the show it is promoting.</summary>
public sealed record GeneratedImageSubjects(UpNextListing? Current, IReadOnlyList<UpNextListing> Upcoming)
{
    public static GeneratedImageSubjects FromSchedule(ChannelSchedule schedule, DateTimeOffset at, DateTimeOffset? after = null)
    {
        UpNextListing? current = UpNextPromo.Previous(schedule, at);
        // Listings already excludes mid-show continuations, intros, outros and stacked episodes.
        IReadOnlyList<UpNextListing> upcoming = UpNextPromo.Listings(schedule, after ?? at, 10);
        return new(current, upcoming);
    }
}

public static class GeneratedPromoImages
{
    public static IReadOnlyDictionary<string, string> Resolve(GeneratedPromoTemplate template,
        IReadOnlyList<UpNextListing> promoted, GeneratedImageSubjects? subjects = null)
    {
        Dictionary<string, string> images = new(StringComparer.OrdinalIgnoreCase);
        foreach (GeneratedVariableImage image in template.VariableImages ?? [])
        {
            UpNextListing? show = image.Context switch
            {
                GeneratedImageContext.CurrentlyWatching => subjects is not null ? subjects.Current
                    : template.Type is GeneratedPromoType.YoureWatching or GeneratedPromoType.BackToTheShow or GeneratedPromoType.ThisHasBeen
                        ? promoted.FirstOrDefault() : null,
                GeneratedImageContext.UpNext => (subjects?.Upcoming
                    ?? (template.Type is GeneratedPromoType.UpNext or GeneratedPromoType.NowPlaying ? promoted : []))
                    .ElementAtOrDefault(Math.Max(0, image.Offset)),
                _ => promoted.FirstOrDefault()
            };
            string? path = show is null ? null : image.Shows.FirstOrDefault(candidate =>
                string.Equals(candidate.Show.Trim(), show.Title.Trim(), StringComparison.OrdinalIgnoreCase))?.Path;
            images[image.Name] = string.IsNullOrWhiteSpace(path) ? image.DefaultPath ?? string.Empty : path;
        }
        return images;
    }
}
