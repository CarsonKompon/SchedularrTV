using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Plugins.Promos;

namespace SchedularrTV.GeneratedPromos;

/// <summary>Makes a channel's generated promos for other plugins on request (a credit squeeze's panel, say).</summary>
public sealed class GeneratedPromoMaker(GeneratedPromoService promos, SchedularrTV.Service.Images.AutomaticImages? automaticImages = null) : IPromoMaker
{
    public bool CanMake(SchedulerConfig config, ChannelConfig channel, string? blockName, GeneratedPromoType type) =>
        Candidates(config, channel, blockName, type).Templates.Count > 0;

    public async Task<MadePromo?> MakeAsync(SchedulerConfig config, ChannelConfig channel, ChannelSchedule schedule, string? blockName, GeneratedPromoType type,
        DateTimeOffset airsAt, CancellationToken cancellationToken = default)
    {
        (GeneratedPromoSource? source, List<GeneratedPromoTemplate> templates) = Candidates(config, channel, blockName, type);
        schedule = UpNextPromo.WithPreferredNames(schedule, config);
        TimeZoneInfo zone = SchedulingEngine.ResolveTimeZone(string.IsNullOrWhiteSpace(channel.TimeZone) ? config.Settings.TimeZone : channel.TimeZone);
        templates.RemoveAll(template => !GeneratedPromoEligibility.IsAvailable(template, airsAt, zone));
        if (source is null || templates.Count == 0)
        {
            return null;
        }

        // The same airing always gets the same template.
        DeterministicRandom random = DeterministicRandom.FromSeedText(config.Settings.Seed, channel.Id, airsAt.ToString("O"), $"made-{type}");
        GeneratedPromoTemplate template = templates[random.Next(templates.Count)];
        int needed = GeneratedPromoResolver.ListingsNeeded(template);
        IReadOnlyList<UpNextListing> listings = type switch
        {
            GeneratedPromoType.ThisHasBeen or GeneratedPromoType.YoureWatching or GeneratedPromoType.BackToTheShow
                or GeneratedPromoType.RightBack =>
                UpNextPromo.Previous(schedule, airsAt) is { } previous ? [previous] : [],

            // The next new episode the schedule airs.
            GeneratedPromoType.NewEpisode => UpNextPromo.NextNew(schedule, airsAt, Array.Empty<string>()) is { } fresh ? [fresh] : [],

            // Something later today that isn't the next programme.
            GeneratedPromoType.ComingUp => UpNextPromo.Listings(schedule, airsAt, 4, grouping: template.UpcomingGrouping) is { Count: > 1 } upcoming ? [upcoming[^1]] : [],

            // Something well after it.
            GeneratedPromoType.Eventually => UpNextPromo.Listings(schedule, airsAt + TimeSpan.FromHours(6), 1, grouping: template.UpcomingGrouping),
            _ => UpNextPromo.Listings(schedule, airsAt, needed, grouping: template.UpcomingGrouping)
        };
        if (listings.Count == 0
            || (GeneratedPromoEligibility.HasProgrammeQuery(template)
                && !GeneratedPromoEligibility.SuitsProgramme(template, UpNextPromo.ItemFor(schedule, listings[0]))))
        {
            return null;
        }

        GeneratedImageSubjects subjects = GeneratedImageSubjects.FromSchedule(schedule, airsAt, template: template);
        var where = new GeneratedVoiceContext(channel.Id, blockName, channel.Name);
        // Variants limited to some programmes check the one this promo is about.
        SchedularrTV.Core.Media.MediaItem? programme = template.HasVariants ? UpNextPromo.ItemFor(schedule, listings[0]) : null;

        // Made now, so variable images' automatic pictures are waited for first.
        if (automaticImages?.Lookup(queue: true) is { } lookup)
        {
            _ = GeneratedPromoResolver.Resolve(template, source.Owner.Name, listings, zone, airsAt, subjects, lookup, where, programme);
            await lookup.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (GeneratedPromoResolver.Resolve(template, source.Owner.Name, listings, zone, airsAt, subjects,
                automaticImages?.Lookup(queue: false), where, programme) is not { } promo)
        {
            return null;
        }

        string path = await promos.MadeOrRenderAsync(promo with { Folder = GeneratedPromoService.Folder(source, template) }, cancellationToken).ConfigureAwait(false);
        UpNextListing subject = listings[0];
        return new MadePromo(path, template.Duration, subject.Title, subject.Episode, subject.Start);
    }

    private static (GeneratedPromoSource? Source, List<GeneratedPromoTemplate> Templates) Candidates(SchedulerConfig config, ChannelConfig channel,
        string? blockName, GeneratedPromoType type)
    {
        GeneratedPromoSource? source = GeneratedPromoPlanner.Source(config, channel, blockName);
        List<GeneratedPromoTemplate> templates = source is null
            ? new List<GeneratedPromoTemplate>()
            : source.Promos.Templates.Where(template => template.Enabled && template.Type == type && template.Duration > TimeSpan.Zero).ToList();
        return (source, templates);
    }
}
