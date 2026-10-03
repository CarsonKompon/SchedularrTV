using SchedularrTV.Core.Configuration;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class YamlCommentMergerTests
{
    private static string RoundTrip(string original)
    {
        SchedulerConfig config = SchedulerConfigLoader.Parse(original);
        return YamlCommentMerger.Merge(original, SchedulerConfigLoader.Serialize(config));
    }

    [Fact]
    public void Keeps_the_banner_above_the_first_key()
    {
        const string original = """
            # =====================
            # my channel file
            # =====================

            settings:
              time_zone: "America/New_York"
            """;

        string merged = RoundTrip(original);

        Assert.StartsWith("# =====================", merged, StringComparison.Ordinal);
        Assert.Contains("# my channel file", merged, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Template:Playhouse Disney")]
    [InlineData("'Template:Playhouse Disney'")]
    [InlineData("\"Template:Playhouse Disney\"")]
    public void Colon_mapping_keys_do_not_accumulate_and_existing_duplicates_are_removed(string key)
    {
        string original = string.Join('\n', "channels:", "- id: ch1", "  name: One", "  plugins:", "    archive-follow:",
            "      blocks:", "        # preserve the archive alias", $"        {key}: Playhouse",
            $"        {key}: Playhouse", "        Other: Other block");
        for (int save = 0; save < 4; save++)
        {
            original = RoundTrip(original);
            Assert.Single(original.Split('\n'), line => line.Contains("Template:Playhouse Disney", StringComparison.Ordinal));
            Assert.Contains("# preserve the archive alias", original);
            Assert.Equal("Playhouse", SchedulerConfigLoader.Parse(original).Channels[0].Follow!.Blocks["Template:Playhouse Disney"]);
        }
    }

    [Fact]
    public void Keeps_a_comment_attached_to_its_key()
    {
        const string original = """
            settings:
              # how far ahead playouts are generated
              horizon: 12h
            """;

        string merged = RoundTrip(original);

        Assert.Contains("  # how far ahead playouts are generated\n  horizon: 12h", merged.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void Keeps_trailing_comments()
    {
        const string original = """
            settings:
              time_zone: "America/New_York"   # channel-local clock
            """;

        string merged = RoundTrip(original);

        Assert.Contains("# channel-local clock", merged, StringComparison.Ordinal);
    }

    [Fact]
    public void Keeps_a_hand_written_duration_rather_than_normalising_it()
    {
        const string original = """
            settings:
              horizon: 12h
              rebuild_interval: 30m
            """;

        string merged = RoundTrip(original);

        Assert.Contains("horizon: 12h", merged, StringComparison.Ordinal);
        Assert.Contains("rebuild_interval: 30m", merged, StringComparison.Ordinal);
        Assert.DoesNotContain("12:00:00", merged, StringComparison.Ordinal);
    }

    [Fact]
    public void Keeps_a_quoted_time_of_day_as_written()
    {
        const string original = """
            settings:
              time_zone: "UTC"
            channels:
            - id: ch1
              name: One
              number: 1
              base_rotation:
                query: "kind == 'Movie'"
              anchors:
              - name: Prime time
                time: "20:00"
                show:
                  query: "kind == 'Movie'"
            """;

        string merged = RoundTrip(original);

        Assert.Contains("""time: "20:00" """.TrimEnd(), merged, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_a_newly_added_duration_in_the_friendly_form()
    {
        const string original = """
            settings:
              time_zone: "UTC"
            """;

        SchedulerConfig config = SchedulerConfigLoader.Parse(original);
        config.Settings.RebuildInterval = TimeSpan.FromMinutes(45);

        string merged = YamlCommentMerger.Merge(original, SchedulerConfigLoader.Serialize(config));

        Assert.Contains("rebuild_interval: 45m", merged, StringComparison.Ordinal);
    }

    [Fact]
    public void Comments_follow_a_channel_that_moved_in_the_document()
    {
        const string original = """
            settings:
              time_zone: "UTC"
            channels:
            - id: ch1
              name: One
              number: 1
              # the first channel's rotation
              base_rotation:
                query: "kind == 'Movie'"
            - id: ch2
              name: Two
              number: 2
              # the second channel's rotation
              base_rotation:
                query: "kind == 'Episode'"
            """;

        SchedulerConfig config = SchedulerConfigLoader.Parse(original);
        config.Channels.Reverse();

        string merged = YamlCommentMerger.Merge(original, SchedulerConfigLoader.Serialize(config)).Replace("\r\n", "\n");

        int second = merged.IndexOf("# the second channel's rotation", StringComparison.Ordinal);
        int first = merged.IndexOf("# the first channel's rotation", StringComparison.Ordinal);

        Assert.True(second >= 0 && first >= 0);
        Assert.True(second < first, "comments should travel with their channel, not stay at a fixed index");
    }

    [Fact]
    public void Drops_comments_for_keys_that_no_longer_exist()
    {
        const string original = """
            settings:
              time_zone: "UTC"
            channels:
            - id: ch1
              name: One
              number: 1
              # only relevant while the setting is here
              pad_to_nearest: 30m
              base_rotation:
                query: "kind == 'Movie'"
            """;

        SchedulerConfig config = SchedulerConfigLoader.Parse(original);
        config.Channels[0].PadToNearest = null;

        string merged = YamlCommentMerger.Merge(original, SchedulerConfigLoader.Serialize(config));

        Assert.DoesNotContain("only relevant while the setting is here", merged, StringComparison.Ordinal);
    }

    [Fact]
    public void The_merged_document_still_parses_to_the_same_configuration()
    {
        const string original = """
            # header
            settings:
              time_zone: "America/New_York"   # local clock
              horizon: 12h

            global_collections:
              # everything long enough to headline
              main_rotation:
                query: "kind == 'Episode' AND duration >= 600"
                order: shuffle

            channels:
            # the flagship
            - id: ch1
              name: "My Channel"
              number: 901
              pad_to_nearest: 30m
              base_rotation:
                collection: main_rotation
              interstitials:
                commercials:
                  base_query: tag:"Commercials"
                  max_item_duration: 3m
            """;

        SchedulerConfig before = SchedulerConfigLoader.Parse(original);
        string merged = RoundTrip(original);
        SchedulerConfig after = SchedulerConfigLoader.Parse(merged);

        Assert.Equal(before.Settings.Horizon, after.Settings.Horizon);
        Assert.Equal(before.Settings.TimeZone, after.Settings.TimeZone);
        Assert.Equal(before.Channels[0].PadToNearest, after.Channels[0].PadToNearest);
        Assert.Equal(
            before.Channels[0].Interstitials?.Commercials?.MaxItemDuration,
            after.Channels[0].Interstitials?.Commercials?.MaxItemDuration);
        Assert.Equal(before.GlobalCollections["main_rotation"].Query, after.GlobalCollections["main_rotation"].Query);

        Assert.Contains("# the flagship", merged, StringComparison.Ordinal);
        Assert.Contains("# everything long enough to headline", merged, StringComparison.Ordinal);
        Assert.Contains("pad_to_nearest: 30m", merged, StringComparison.Ordinal);
    }

    [Fact]
    public void A_commented_out_example_block_is_preserved()
    {
        const string original = """
            settings:
              time_zone: "UTC"
            channels:
            - id: ch1
              name: One
              number: 1
              base_rotation:
                query: "kind == 'Movie'"
              # anchors:
              #   - name: "Prime time"
              #     time: "20:00"
              pad_to_nearest: 30m
            """;

        string merged = RoundTrip(original);

        Assert.Contains("""#   - name: "Prime time" """.TrimEnd(), merged, StringComparison.Ordinal);
    }

    [Fact]
    public void Repeated_saves_do_not_duplicate_scalar_list_entries()
    {
        const string original = """
            settings:
              time_zone: "UTC"
              temporal_tags:
                include_month: true
            channels:
            - id: ch1
              name: One
              number: 1
              base_rotation:
                query: "kind == 'Movie'"
              blocks:
              - name: Weekend
                time: "08:00"
                end_time: "12:00"
                days: [Saturday, Sunday]
                shows:
                - query: "kind == 'Episode'"
              interstitials:
                commercials:
                  base_query: tag:"Commercials"
                  base_tags:
                  - Any Channel
                  - Any Childrens
                filler:
                  types:
                  - Bumpers
            """;

        string current = original;
        for (int i = 0; i < 3; i++)
        {
            current = YamlCommentMerger.Merge(current, SchedulerConfigLoader.Serialize(SchedulerConfigLoader.Parse(current)));
        }

        SchedulerConfig config = SchedulerConfigLoader.Parse(current);
        CommercialsConfig commercials = config.Channels[0].Interstitials!.Commercials!;

        Assert.Equal(["Any Channel", "Any Childrens"], commercials.BaseTags);
        Assert.Equal(["Bumpers"], config.Channels[0].Interstitials!.Filler!.Types);
        Assert.Equal([DayOfWeek.Saturday, DayOfWeek.Sunday], config.Channels[0].Blocks[0].Days);
        Assert.Single(config.Channels[0].Blocks[0].Shows);
        Assert.Single(config.Channels);
    }

    [Fact]
    public void A_comment_above_a_scalar_list_entry_survives()
    {
        const string original = """
            settings:
              time_zone: "UTC"
            channels:
            - id: ch1
              name: One
              number: 1
              base_rotation:
                query: "kind == 'Movie'"
              interstitials:
                commercials:
                  base_query: tag:"Commercials"
                  base_tags:
                  # the house brand
                  - Any Channel
                  - Any Childrens
            """;

        string merged = RoundTrip(original).Replace("\r\n", "\n");

        Assert.Contains("# the house brand", merged, StringComparison.Ordinal);
        Assert.Equal(1, merged.Split("- Any Channel").Length - 1);
    }

    [Fact]
    public void An_empty_original_still_yields_a_friendly_document()
    {
        SchedulerConfig config = new() { Settings = { TimeZone = "UTC" } };

        string merged = YamlCommentMerger.Merge("", SchedulerConfigLoader.Serialize(config));

        Assert.Contains("horizon: 2d", merged, StringComparison.Ordinal);
        Assert.Equal(config.Settings.Horizon, SchedulerConfigLoader.Parse(merged).Settings.Horizon);
    }
}
