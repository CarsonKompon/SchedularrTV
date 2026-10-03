using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class ConfigLoaderTests
{
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void Untimed_follow_only_overrides_round_trip_and_do_not_count_as_normal_programming(bool hasBaseRotation)
  {
    var config = new SchedulerConfig
    {
      Channels = [new ChannelConfig
      {
        Id = "archive", Name = "Archive",
        BaseRotation = hasBaseRotation ? new ContentSelector { Query = "*" } : null,
        Blocks = [new BlockConfig { Name = "Playhouse", FollowOnly = true, CommercialBaseTags = ["Playhouse"] }]
      }]
    };
    config = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config));
    BlockConfig block = Assert.Single(config.Channels[0].Blocks);
    Assert.True(block.FollowOnly);
    Assert.Null(block.Time);
    Assert.Null(block.EndTime);
    Assert.Equal(hasBaseRotation, SchedulerConfigLoader.Validate(config).IsValid);
  }

    private const string SpecYaml = """
        settings:
          time_zone: "UTC"
          horizon: 48h
          filler:
            base_query: 'tag:"Commercials"'
            tolerance: 500ms

        global_collections:
          adult_swim_shows:
            query: "collection:'Adult Swim at Home'"
            order: shuffle

        channels:
          - id: "cartoon-network"
            name: "Cartoon Network / Adult Swim"
            number: 1
            auto_temporal_tags: true

            base_rotation:
              collection: adult_swim_shows
              order: shuffle

            interstitial_shows:
              query: "collection:'Shorts & Pilots'"
              order: shuffle

            blocks:
              - name: "SpongeBob Morning Anchor"
                time: "08:00"
                days: ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"]
                shows:
                  - query: "show:'SpongeBob SquarePants'"
                    order: chronological

              - name: "Christmas Marathon"
                time: "00:00"
                end_time: "23:59"
                schedule:
                  date: "12-25"
                  repeat: yearly
                shows:
                  - collection: adult_swim_shows
                    filter: "title CONTAINS 'christmas' OR plot CONTAINS 'xmas'"
                    order: shuffle

              - name: "Teletoon at Night"
                time: "21:00"
                end_time: "03:00"
                recurrence: daily
                commercial_base_tags: ["Adult Swim", "Any Channel"]
                anchors:
                  - time: "22:00"
                    show:
                      query: "show:'Clone High'"
                      order: chronological
                shows:
                  - collection: adult_swim_shows
                    order: shuffle
        """;

    [Fact]
    public void Parses_the_specification_example()
    {
        SchedulerConfig config = SchedulerConfigLoader.Parse(SpecYaml);

        Assert.Equal(TimeSpan.FromHours(48), config.Settings.Horizon);
        Assert.Single(config.GlobalCollections);

        ChannelConfig channel = Assert.Single(config.Channels);
        Assert.Equal("cartoon-network", channel.Id);
        Assert.True(channel.AutoTemporalTags);
        Assert.Equal(3, channel.Blocks.Count);

        BlockConfig anchorSlot = channel.Blocks[0];
        Assert.True(anchorSlot.IsAnchorSlot);
        Assert.Equal(TimeSpan.FromHours(8), anchorSlot.Time);
        Assert.Equal(5, anchorSlot.Days.Count);
        Assert.Equal(PlaybackOrder.Chronological, anchorSlot.Shows[0].Order);

        BlockConfig christmas = channel.Blocks[1];
        Assert.Equal("12-25", christmas.Schedule?.Date);
        Assert.Equal(RepeatModifier.Yearly, christmas.Schedule?.Repeat);

        BlockConfig night = channel.Blocks[2];
        Assert.Equal(TimeSpan.FromHours(21), night.Time);
        Assert.Equal(TimeSpan.FromHours(3), night.EndTime);
        Assert.Equal(["Adult Swim", "Any Channel"], night.CommercialBaseTags);
        Assert.Equal(TimeSpan.FromHours(22), Assert.Single(night.Anchors).Time);
    }

    [Fact]
    public void The_specification_example_validates_cleanly()
    {
        ConfigValidationResult result = SchedulerConfigLoader.Validate(SchedulerConfigLoader.Parse(SpecYaml));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Theory]
    [InlineData("21:00", 21, 0, 0)]
    [InlineData("00:03:30", 0, 3, 30)]
    [InlineData("90s", 0, 1, 30)]
    [InlineData("5m", 0, 5, 0)]
    [InlineData("2h", 2, 0, 0)]
    public void Parses_friendly_duration_forms(string text, int hours, int minutes, int seconds) =>
        Assert.Equal(new TimeSpan(hours, minutes, seconds), FlexibleTimeSpanConverter.Parse(text));

    [Theory]
    [InlineData("season_episode")]
    [InlineData("Season, Episode")]
    [InlineData("SeasonEpisode")]
    [InlineData("season-episode")]
    public void Playback_order_accepts_the_spec_spellings(string spelling)
    {
        string yaml = $"""
            global_collections:
              c:
                query: "*"
            channels:
              - id: a
                name: A
                base_rotation:
                  collection: c
                  order: "{spelling}"
            """;

        SchedulerConfig config = SchedulerConfigLoader.Parse(yaml);
        Assert.Equal(PlaybackOrder.SeasonEpisode, config.Channels[0].BaseRotation!.Order);
    }

    [Fact]
    public void Reports_an_unknown_global_collection_reference()
    {
        const string yaml = """
            channels:
              - id: a
                name: A
                base_rotation:
                  collection: missing
            """;

        ConfigValidationResult result = SchedulerConfigLoader.Validate(SchedulerConfigLoader.Parse(yaml));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("missing", StringComparison.Ordinal));
    }

    [Fact]
    public void Reports_an_invalid_query_at_load_time()
    {
        const string yaml = """
            channels:
              - id: a
                name: A
                base_rotation:
                  query: "tag:"
            """;

        ConfigValidationResult result = SchedulerConfigLoader.Validate(SchedulerConfigLoader.Parse(yaml));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("query", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_a_selector_with_no_source_at_all()
    {
        const string yaml = """
            channels:
              - id: a
                name: A
                base_rotation:
                  order: shuffle
            """;

        ConfigValidationResult result = SchedulerConfigLoader.Validate(SchedulerConfigLoader.Parse(yaml));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("include", StringComparison.Ordinal));
    }

    [Fact]
    public void A_selector_may_union_a_collection_a_query_and_picked_titles()
    {
        const string yaml = """
            global_collections:
              c:
                query: "*"
            channels:
              - id: a
                name: A
                base_rotation:
                  collection: c
                  query: "kind == 'Movie'"
                  include:
                    - show: "Rugrats"
                      seasons: [1, 2]
            """;

        SchedulerConfig config = SchedulerConfigLoader.Parse(yaml);
        ContentSelector selector = config.Channels[0].BaseRotation!;

        Assert.True(SchedulerConfigLoader.Validate(config).IsValid);
        Assert.Equal("Rugrats", Assert.Single(selector.Include).Show);
        Assert.Equal([1, 2], selector.Include[0].Seasons);
    }

    [Fact]
    public void Parses_the_three_tier_interstitial_block()
    {
        const string yaml = """
            channels:
              - id: "cartoon-network"
                name: "Cartoon Network"
                auto_temporal_tags: true

                base_rotation:
                  query: "*"

                interstitials:
                  shows:
                    query: "collection:'Shorts & Pilots'"
                    order: "shuffle"

                  commercials:
                    query: "tag:'Commercials' AND (tag:'Adult Swim' OR tag:'Any Channel')"
                    strict_fit: true

                  filler:
                    query: "tag:'Bumpers' OR tag:'StationID' OR tag:'MusicVideo'"
                    trimmable: true
                    fallback: true
            """;

        SchedulerConfig config = SchedulerConfigLoader.Parse(yaml);
        InterstitialsConfig interstitials = Assert.Single(config.Channels).Interstitials!;

        Assert.Equal("collection:'Shorts & Pilots'", interstitials.Shows!.Query);
        Assert.Equal(PlaybackOrder.Shuffle, interstitials.Shows.Order);

        Assert.True(interstitials.Commercials!.IsStrictFit);
        Assert.Contains("Any Channel", interstitials.Commercials.Query, StringComparison.Ordinal);

        Assert.True(interstitials.Filler!.IsTrimmable);
        Assert.True(interstitials.Filler.IsEnabled);

        Assert.True(SchedulerConfigLoader.Validate(config).IsValid);
    }

    [Fact]
    public void Interstitial_tiers_inherit_from_the_channel_and_settings()
    {
        const string yaml = """
            settings:
              interstitials:
                commercials:
                  base_query: 'tag:"Commercials"'
                  granularity: 250ms
                filler:
                  query: "tag:'Bumpers'"

            channels:
              - id: a
                name: A
                commercial_base_tags: ["Adult Swim"]
                base_rotation:
                  query: "*"
                blocks:
                  - name: Night
                    time: "21:00"
                    end_time: "23:00"
                    commercial_base_tags: ["Toonami"]
                    shows:
                      - query: "*"
            """;

        SchedulerConfig config = SchedulerConfigLoader.Parse(yaml);
        ChannelConfig channel = config.Channels[0];

        InterstitialsConfig atChannel = SchedulingEngine.ResolveInterstitials(config, channel, null);
        InterstitialsConfig inBlock = SchedulingEngine.ResolveInterstitials(config, channel, channel.Blocks[0]);

        Assert.Equal(["Adult Swim"], atChannel.Commercials!.BaseTags);
        Assert.Equal(["Toonami"], inBlock.Commercials!.BaseTags);

        // Everything not restated by the block still comes from settings.
        Assert.Equal("tag:\"Commercials\"", inBlock.Commercials.BaseQuery);
        Assert.Equal(TimeSpan.FromMilliseconds(250), inBlock.Commercials.Granularity);
        Assert.Equal("tag:'Bumpers'", inBlock.Filler!.Query);
    }

    [Fact]
    public void An_enabled_filler_tier_without_a_pool_is_rejected()
    {
        const string yaml = """
            channels:
              - id: a
                name: A
                base_rotation:
                  query: "*"
                interstitials:
                  filler:
                    trimmable: true
            """;

        ConfigValidationResult result = SchedulerConfigLoader.Validate(SchedulerConfigLoader.Parse(yaml));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("filler", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("config/schedule.example.yaml")]
    public void The_shipped_config_files_parse_and_validate(string path)
    {
        SchedulerConfig config = SchedulerConfigLoader.Parse(File.ReadAllText(path));
        ConfigValidationResult result = SchedulerConfigLoader.Validate(config);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.NotEmpty(config.Channels);
    }

    [Fact]
    public void Round_trips_through_serialization()
    {
        SchedulerConfig original = SchedulerConfigLoader.Parse(SpecYaml);
        SchedulerConfig round = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(original));

        Assert.Equal(original.Channels.Count, round.Channels.Count);
        Assert.Equal(original.Channels[0].Blocks.Count, round.Channels[0].Blocks.Count);
        Assert.Equal(original.Channels[0].Blocks[2].EndTime, round.Channels[0].Blocks[2].EndTime);
    }
}
