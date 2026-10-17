using SchedularrTV.Core.Configuration;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>Promos, generated promos and Archive Follow are written under <c>plugins:</c>, and older files still load.</summary>
public class FeatureSectionTests
{
    private const string OldLayout = """
        settings:
          time_zone: UTC
          generated_promos_path: D:\Promos
          generated_promos_lead_time: 2h
          promos:
            lookahead: 3h
        channels:
        - id: cn
          name: Cartoon Network
          number: 1
          promo_base_tags: [Cartoon Network]
          promos:
            max_per_break: 2
          generated_promos:
            templates:
            - name: Classic
          follow:
            enabled: true
            directory: D:\Archive
          plugins:
            acme.screen-bug:
              image: D:\Logos\cn.png
          blocks:
          - name: Late
            follow_only: true
            promo_base_tags: [Adult Swim]
            generated_promos:
              from: cn
        """;

    [Fact]
    public void The_old_keys_still_load_and_are_flagged_for_migration()
    {
        SchedulerConfig config = SchedulerConfigLoader.Parse(OldLayout);

        Assert.True(config.UsesOldLayout);
        AssertFeatures(config);
    }

    [Fact]
    public void Saving_writes_the_features_under_plugins_and_reloads_identically()
    {
        string saved = SchedulerConfigLoader.Serialize(SchedulerConfigLoader.Parse(OldLayout));

        // The old keys at their old depths: settings and channel fields are indented 2, block fields 4.
        foreach (string old in (string[])["\n  generated_promos_", "\n  promos:", "\n  promo_base_tags:", "\n  generated_promos:", "\n  follow:",
                     "\n    follow_only:", "\n    promo_base_tags:", "\n    generated_promos:"])
        {
            Assert.DoesNotContain(old, saved.Replace("\r\n", "\n"));
        }

        Assert.Contains("archive-follow:", saved);
        Assert.Contains("generated-promos:", saved);

        SchedulerConfig reloaded = SchedulerConfigLoader.Parse(saved);
        Assert.False(reloaded.UsesOldLayout);
        AssertFeatures(reloaded);
    }

    [Fact]
    public void The_new_layout_wins_over_an_old_key_left_beside_it()
    {
        const string yaml = """
            channels:
            - id: cn
              name: Cartoon Network
              promo_base_tags: [Old]
              plugins:
                promos:
                  base_tags: [New]
            """;

        Assert.Equal(["New"], SchedulerConfigLoader.Parse(yaml).Channels[0].PromoBaseTags);
    }

    [Fact]
    public void An_installed_plugins_section_is_not_mixed_up_with_the_features()
    {
        ChannelConfig channel = SchedulerConfigLoader.Parse(OldLayout).Channels[0];

        string key = Assert.Single(channel.Plugins!.Keys);
        Assert.Equal("acme.screen-bug", key);
    }

    [Fact]
    public void A_blocks_keep_hours_setting_is_written_under_archive_follow_and_read_back()
    {
        SchedulerConfig config = SchedulerConfigLoader.Parse("""
            channels:
              - id: cn
                name: Cartoon Network
                number: 2
                blocks:
                  - name: Toonami
                    time: '17:00'
                    end_time: '19:00'
                    shows: [{ include: [{ show: Dragon Ball Z }] }]
                    plugins:
                      archive-follow:
                        keep_hours: true
            """);
        BlockConfig block = Assert.Single(config.Channels[0].Blocks);
        Assert.True(block.KeepsHoursOverArchive);
        Assert.False(block.FollowOnly);

        string saved = SchedulerConfigLoader.Serialize(config);
        Assert.Contains("keep_hours: true", saved);
        Assert.DoesNotContain("follow_only", saved);
        Assert.DoesNotContain("keeps_hours_over_archive", saved);
        Assert.True(Assert.Single(SchedulerConfigLoader.Parse(saved).Channels[0].Blocks).KeepsHoursOverArchive);
    }

    private static void AssertFeatures(SchedulerConfig config)
    {
        Assert.Equal(@"D:\Promos", config.Settings.GeneratedPromosPath);
        Assert.Equal(TimeSpan.FromHours(2), config.Settings.GeneratedPromosLeadTime);
        Assert.Equal(TimeSpan.FromHours(3), config.Settings.Promos.Lookahead);

        ChannelConfig channel = config.Channels[0];
        Assert.Equal(["Cartoon Network"], channel.PromoBaseTags);
        Assert.Equal(2, channel.Promos!.MaxPerBreak);
        Assert.Equal("Classic", Assert.Single(channel.GeneratedPromos!.Templates).Name);
        Assert.True(channel.Follow!.Enabled);
        Assert.Equal(@"D:\Archive", channel.Follow.Directory);
        Assert.Equal(@"D:\Logos\cn.png", Assert.IsAssignableFrom<IDictionary<string, object?>>(channel.Plugins!["acme.screen-bug"])["image"]);

        BlockConfig block = Assert.Single(channel.Blocks);
        Assert.True(block.FollowOnly);
        Assert.Equal(["Adult Swim"], block.PromoBaseTags);
        Assert.Equal("cn", block.GeneratedPromos!.From);
    }
}
