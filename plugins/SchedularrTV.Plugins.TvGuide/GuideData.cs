using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Providers.ErsatzTv;
using SchedularrTV.Service;

namespace SchedularrTV.TvGuide;

/// <summary>One programme in the listings.</summary>
public sealed record GuideListing(DateTimeOffset Start, DateTimeOffset End, string Title, string? Subtitle, bool Movie);

/// <summary>One channel's row in the listings.</summary>
public sealed record GuideRow(int Number, string Name, string? Logo, IReadOnlyList<GuideListing> Listings);

/// <summary>An ad the top panel can play.</summary>
public sealed record GuideAd(string Title, string File, TimeSpan Duration);

/// <summary>What the guide page shows: the published schedules as listings, and the ads for its top panel.</summary>
public sealed class GuideData(IServiceProvider services, GuideSettings settings)
{
    private readonly Lock _adsGate = new();
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<GuideAd> Ads)> _ads = new(StringComparer.Ordinal);

    /// <summary>Every published channel's programmes from a moment ago until <paramref name="ahead"/>, in channel order.</summary>
    public IReadOnlyList<GuideRow> Listings(DateTimeOffset now, TimeSpan ahead)
    {
        IReadOnlyList<ChannelSchedule> schedules = services.GetService<ScheduleCoordinator>()?.Latest ?? [];
        return Rows(schedules, now, ahead);
    }

    public static IReadOnlyList<GuideRow> Rows(IEnumerable<ChannelSchedule> schedules, DateTimeOffset now, TimeSpan ahead)
    {
        DateTimeOffset from = now.AddMinutes(-1);
        DateTimeOffset to = now + ahead;
        List<GuideRow> rows = [];
        foreach (ChannelSchedule schedule in schedules.OrderBy(schedule => schedule.ChannelNumber))
        {
            List<GuideListing> listings = [];
            foreach (GuideEntry entry in XmltvPublisher.BuildGuide(schedule, GuideMode.Programs))
            {
                if (entry.Stop <= from || entry.Start >= to)
                {
                    continue;
                }

                MediaItem? item = entry.Entry.Item;
                bool series = item?.SeriesName is { Length: > 0 };
                string title = series ? item!.SeriesName! : item?.Title ?? entry.Entry.Source;
                string? subtitle = series && entry.Titles.Count > 0 ? string.Join(" / ", entry.Titles) : null;
                listings.Add(new GuideListing(entry.Start, entry.Stop, title, subtitle, item?.Kind == MediaKind.Movie));
            }

            rows.Add(new GuideRow(schedule.ChannelNumber, schedule.ChannelName, Logo(schedule.ChannelLogo), listings));
        }

        return rows;
    }

    // Logos uploaded in the app are served by the scheduler at a relative address; make it absolute for the page.
    private static string? Logo(string? logo) =>
        string.IsNullOrWhiteSpace(logo) ? null
        : logo.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || logo.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || logo.StartsWith('/') ? logo
        : "/" + logo;

    /// <summary>The ads a guide's top panel plays: library items matching its query, whose files are on this computer.</summary>
    public async Task<IReadOnlyList<GuideAd>> AdsAsync(GuideStation guide, CancellationToken cancellationToken)
    {
        string key = guide.AdsQuery.Trim();
        lock (_adsGate)
        {
            if (_ads.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(10))
            {
                return cached.Ads;
            }
        }

        IReadOnlyList<GuideAd> ads = [];
        if (key.Length > 0 && services.GetService<IMediaLibrary>() is { } library)
        {
            try
            {
                IReadOnlyList<MediaItem> items = await library.SearchAsync(QueryParser.Parse(key), cancellationToken).ConfigureAwait(false);
                ads = [.. items
                    .Where(item => item.Duration > TimeSpan.Zero && item.Duration <= TimeSpan.FromMinutes(5))
                    .Select(item => settings.LocalPath(item.Path) is { } file ? new GuideAd(item.Title, file, item.Duration) : null)
                    .OfType<GuideAd>()
                    .Where(ad => Playable(ad.File))
                    .Take(500)];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ads = [];
            }
        }

        lock (_adsGate)
        {
            _ads[key] = (DateTimeOffset.UtcNow, ads);
        }

        return ads;
    }

    /// <summary>Formats a browser can play by itself.</summary>
    public static bool Playable(string file) =>
        Path.GetExtension(file).ToLowerInvariant() is ".mp4" or ".m4v" or ".webm" or ".mov" or ".ogv";

    public static string ContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".webm" => "video/webm",
        ".ogv" => "video/ogg",
        ".mov" => "video/quicktime",
        _ => "video/mp4"
    };
}
