using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Query;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Service.Editor;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class ShowAliasTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static MediaItem Episode(int season, int episode = 1) => TestMedia.Episode("Total Drama", season, episode, TimeSpan.FromMinutes(22));
    private static SchedulerConfig Config() => new()
    {
        Titles = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Total Drama"] = new()
            {
                Aliases = [new() { Name = "Drama", Preferred = true }, new() { Name = "TD" }],
                Seasons = new()
                {
                    [1] = new() { Aliases = [new() { Name = "Total Drama Island", Preferred = true }] },
                    [2] = new() { Aliases = [new() { Name = "Total Drama Action", Preferred = true }] }
                }
            }
        }
    };

    [Fact]
    public void Aliases_round_trip_and_season_preference_overrides_show_preference()
    {
        SchedulerConfig config = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(Config()));
        Assert.Equal("Total Drama Island", ShowAliases.PreferredName(config, Episode(1)));
        Assert.Equal("Total Drama Action", ShowAliases.PreferredName(config, Episode(2)));
        Assert.Equal("Drama", ShowAliases.PreferredName(config, Episode(3)));
        Assert.Equal(["Drama", "TD", "Total Drama Island"], ShowAliases.Names(config, Episode(1)));
        config.Titles["Total Drama"].Aliases.ForEach(alias => alias.Preferred = false);
        Assert.Equal("Total Drama", ShowAliases.PreferredName(config, Episode(3)));
    }

    [Fact]
    public void Invalid_aliases_are_reported()
    {
        SchedulerConfig config = Config();
        config.Titles["Total Drama"].Aliases.Add(new() { Name = " drama ", Preferred = true });
        var errors = SchedulerConfigLoader.Validate(config).Errors;
        Assert.Contains(errors, error => error.Contains("only one alias"));
        Assert.Contains(errors, error => error.Contains("names must be unique"));
    }

    [Theory]
    [InlineData(1, "Total Drama Island", true)]
    [InlineData(2, "Total Drama Island", false)]
    [InlineData(2, "TD", true)]
    public async Task Promo_folders_match_show_aliases_and_only_the_target_seasons_alias(int season, string folderName, bool matches)
    {
        const string root = @"D:\Promos\Up Next";
        MediaItem clip = new() { Id = "promo", Title = "Promo", Path = $@"{root}\{folderName}\clip.mp4", Duration = TimeSpan.FromSeconds(15) };
        var config = Config();
        var channel = new ChannelConfig { Id = "test", Name = "Test" };
        var session = new ScheduleBuildSession(config, new FakeMediaLibrary([Episode(season), clip]), new InMemoryPlaybackStateStore(), channel, TimeZoneInfo.Utc, Noon);
        PromoTarget? target = await PromoBreakContributor.SampleTargetAsync(session,
            [new ContentSelector { Query = "kind:Episode" }], Noon, "fallback", default);
        Assert.NotNull(target);
        var promos = new PromoConfig { FromAdBreaks = true };
        var spot = new PromoSpotConfig
        {
            Enabled = true,
            Folders = [new(root, [], PerShow: true, AnyShow: false, Times: false, Days: false, AnyShowFolder: "Any Show")]
        };
        MediaItem? selected = await new PromoSelector(session, promos, null).SelectSpotAsync(spot, target.ShowIds, Noon, TimeSpan.FromMinutes(1), default);
        Assert.Equal(matches, selected is not null);
    }

    [Fact]
    public async Task Outlook_keeps_canonical_titles_separate_from_aliases_and_respects_season_filters()
    {
        var config = Config();
        var channel = new ChannelConfig { Id = "test", Name = "Test", BaseRotation = new() { Include = [new() { Show = "Total Drama", Seasons = [2] }] } };
        var session = new ScheduleBuildSession(config, new FakeMediaLibrary([Episode(1), Episode(2)]), new InMemoryPlaybackStateStore(), channel, TimeZoneInfo.Utc, Noon);
        ChannelOutlook outlook = await session.GetOutlookAsync(TimeSpan.FromDays(1), default);
        Assert.Equal(["Total Drama"], outlook.Rotation);
        Assert.Contains("Total Drama Action", outlook.RotationPromoNames!);
        Assert.DoesNotContain("Total Drama Island", outlook.RotationPromoNames!);
    }

    [Fact]
    public void Preferred_names_reach_voice_text_and_image_subjects_without_changing_identity()
    {
        MediaItem item = Episode(1);
        var original = new ChannelSchedule
        {
            ChannelId = "test", ChannelName = "Test", Start = Noon, End = Noon.AddHours(1),
            Entries = [new() { Start = Noon, Duration = item.Duration, Kind = ScheduleEntryKind.Program, Item = item }]
        };
        ChannelSchedule named = UpNextPromo.WithPreferredNames(original, Config());
        var listings = UpNextPromo.Listings(named, Noon, 1);
        Assert.Equal("Total Drama Island", listings[0].Title);
        Assert.Contains("Total Drama Island", UpNextPromo.Script(listings, TimeZoneInfo.Utc));
        Assert.Equal("Total Drama Island", GeneratedImageSubjects.FromSchedule(named, Noon).Upcoming[0].Title);
        var template = new GeneratedPromoTemplate
        {
            Name = "Aliases",
            Layers = [new() { Kind = GeneratedLayerKind.UpNext, UpNext = new() { ShowsAhead = 1 } }],
            VariableImages = [new() { Name = "art", DefaultPath = "default.png", Shows = [new() { Show = "Total Drama Island", Path = "island.png" }] }]
        };
        var resolved = GeneratedPromoResolver.Resolve(template, "Test", listings, TimeZoneInfo.Utc, Noon)!;
        Assert.Equal("island.png", resolved.VariableImages["art"]);
        Assert.Equal("Total Drama", UpNextPromo.ItemFor(named, listings[0])!.SeriesName);
        Assert.Equal("Total Drama", UpNextPromo.Listings(original, Noon, 1)[0].Title);
    }

    [Fact]
    public void Predictions_beyond_the_built_schedule_use_the_show_preference()
    {
        var schedule = new ChannelSchedule
        {
            ChannelId = "test", ChannelName = "Test", Start = Noon, End = Noon.AddHours(1),
            Entries = [new() { Start = Noon, Duration = TimeSpan.FromSeconds(15), Kind = ScheduleEntryKind.EventuallyPromo, GeneratedSubject = "Total Drama" }]
        };
        var named = UpNextPromo.WithPreferredNames(schedule, Config());
        Assert.Equal("Drama", UpNextPromo.At(named, Noon.AddDays(1), named.Entries[0].GeneratedSubject)!.Title);
    }

    [Fact]
    public void Collection_picks_union_seasons_and_episodes_and_do_not_narrow_a_whole_show()
    {
        var show = new LibraryShow("Total Drama", "Total Drama", null, 4, [1, 2], TimeSpan.FromHours(2), [], 0, []);
        var target = new ContentSelector();
        ShowCollectionEditor.Add(target, show, 1, "S01E01");
        ShowCollectionEditor.Add(target, show, 2);
        ShowCollectionEditor.Add(target, show, 2);
        Assert.Equal(2, target.Include.Count);
        var query = ContentSelectorQuery.Expression(new(), target);
        Assert.True(QueryEvaluator.Matches(Episode(1), query));
        Assert.False(QueryEvaluator.Matches(Episode(1, 2), query));
        Assert.True(QueryEvaluator.Matches(Episode(2, 2), query));
        ShowCollectionEditor.Add(target, show);
        int count = target.Include.Count;
        ShowCollectionEditor.Add(target, show, 1, "S01E02");
        Assert.Equal(count, target.Include.Count);
        Assert.True(QueryEvaluator.Matches(Episode(3), ContentSelectorQuery.Expression(new(), target)));
    }
}
