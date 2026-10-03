using SchedularrTV.Core.Configuration;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class ConfigNormalizationTests
{
    private const string Damaged = """
        settings:
          time_zone: "UTC"
          promos:
            up_next:
              promo_types:
              - up_next
              - next
              - up_next
              - next
            windows:
            - within: 3h
              promo_types:
              - coming_up
              - later
              - coming_up
              - later
        global_collections:
          teletoon:
            include:
            - show: Space Ghost Coast to Coast
              seasons: [1, 2, 3, 1, 2, 3]
              episodes: [S01E01, S01E01]
        channels:
        - id: ch1
          name: One
          number: 1
          commercial_base_tags: [Any Channel, Any Childrens, Any Channel, Any Childrens]
          base_rotation:
            collection: teletoon
          interstitials:
            commercials:
              base_query: tag:"Commercials"
              base_tags: [Any Channel, Any Channel]
              types: [Ads, Bumpers, Ads]
            filler:
              types: [Bumpers, Bumpers]
          blocks:
          - name: Nights
            time: "21:00"
            end_time: "23:00"
            days: [Monday, Monday, Tuesday]
            shows:
            - collection: teletoon
        """;

    [Fact]
    public void Repeated_entries_are_collapsed_on_load()
    {
        SchedulerConfig config = SchedulerConfigLoader.Parse(Damaged);

        Assert.Equal(["up_next", "next"], config.Settings.Promos.UpNext.PromoTypes);
        Assert.Equal(["coming_up", "later"], config.Settings.Promos.Windows[0].PromoTypes);

        ContentInclude include = config.GlobalCollections["teletoon"].Include[0];
        Assert.Equal([1, 2, 3], include.Seasons);
        Assert.Equal(["S01E01"], include.Episodes);

        ChannelConfig channel = config.Channels[0];
        Assert.Equal(["Any Channel", "Any Childrens"], channel.CommercialBaseTags);
        Assert.Equal(["Any Channel"], channel.Interstitials!.Commercials!.BaseTags);
        Assert.Equal(["Ads", "Bumpers"], channel.Interstitials.Commercials.Types);
        Assert.Equal(["Bumpers"], channel.Interstitials.Filler!.Types);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Tuesday], channel.Blocks[0].Days);
    }

    [Fact]
    public void Saving_a_damaged_file_repairs_it()
    {
        SchedulerConfig config = SchedulerConfigLoader.Parse(Damaged);
        string repaired = YamlCommentMerger.Merge(Damaged, SchedulerConfigLoader.Serialize(config));

        Assert.Equal(1, repaired.Split("- coming_up").Length - 1);
        Assert.Equal(1, repaired.Split("- Any Childrens").Length - 1);
    }

    [Fact]
    public void Title_level_blocks_survive_a_save_and_reload()
    {
        const string yaml = """
            settings:
              time_zone: "UTC"
            titles:
              Looney Tunes:
                bumpers:
                - name: Opening Theme
                  position: intro
                  selection: longest_that_fits
                  clips:
                  - path: M:\Extras\Theme Long.mkv
                  - path: M:\Extras\Theme Short.mkv
                - name: Couch Gag
                  position: outro
                  clips:
                  - path: M:\Gags\Halloween.mkv
                    episodes: [S07E06]
            channels: []
            """;

        SchedulerConfig config = SchedulerConfigLoader.Parse(yaml);
        SchedulerConfig reloaded = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config));

        TitleDefaults title = reloaded.Titles["Looney Tunes"];
        Assert.Equal(2, title.Bumpers.Count);

        BumperBlock opening = title.Bumpers[0];
        Assert.Equal("Opening Theme", opening.Name);
        Assert.Equal(BumperPosition.Intro, opening.Position);
        Assert.Equal(BumperSelection.LongestThatFits, opening.Selection);
        Assert.Equal(2, opening.Clips.Count);

        BumperBlock gag = title.Bumpers[1];
        Assert.Equal(BumperPosition.Outro, gag.Position);
        Assert.Equal(["S07E06"], Assert.Single(gag.Clips).Episodes);
    }

    [Fact]
    public void The_single_path_intro_and_outro_shorthand_becomes_blocks()
    {
        const string yaml = """
            settings:
              time_zone: "UTC"
            global_collections:
              classics:
                include:
                - show: Looney Tunes
                  intro: M:\Extras\Show Intro.mkv
                  outro: M:\Extras\Show Outro.mkv
            channels: []
            """;

        SchedulerConfig config = SchedulerConfigLoader.Parse(yaml);
        ContentInclude include = config.GlobalCollections["classics"].Include[0];

        Assert.Equal(2, include.Bumpers.Count);

        BumperBlock intro = include.Bumpers[0];
        Assert.Equal(BumperPosition.Intro, intro.Position);
        Assert.Equal(@"M:\Extras\Show Intro.mkv", Assert.Single(intro.Clips).Path);

        BumperBlock outro = include.Bumpers[1];
        Assert.Equal(BumperPosition.Outro, outro.Position);
        Assert.Equal(@"M:\Extras\Show Outro.mkv", Assert.Single(outro.Clips).Path);

        // The shorthand is consumed, so saving writes blocks rather than both spellings.
        Assert.Null(include.Intro);
        Assert.Null(include.Outro);
    }

    [Fact]
    public void Normalization_leaves_a_clean_list_untouched()
    {
        SchedulerConfig config = SchedulerConfigLoader.Parse(Damaged);
        SchedulerConfig again = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config));

        Assert.Equal(
            config.Channels[0].Interstitials!.Commercials!.Types,
            again.Channels[0].Interstitials!.Commercials!.Types);
        Assert.Equal([1, 2, 3], again.GlobalCollections["teletoon"].Include[0].Seasons);
    }
}
