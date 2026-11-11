using SchedularrTV.Core.Configuration;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>Channels and blocks keep a uid, so references to their ids and names follow a rename.</summary>
public sealed class ConfigIdentityTests
{
    private static SchedulerConfig Schedule()
    {
        var config = new SchedulerConfig
        {
            Channels =
            [
                new ChannelConfig
                {
                    Id = "ch48", Name = "Boomerang",
                    Blocks = [new BlockConfig { Name = "Mornings" }, new BlockConfig { Name = "Late" }],
                    Follow = new ArchiveFollowConfig { Blocks = new(StringComparer.OrdinalIgnoreCase) { ["Archived Mornings"] = "Mornings", ["Other"] = "Late" } }
                },
                new ChannelConfig
                {
                    Id = "ch46", Name = "Cartoon Network",
                    Blocks = [new BlockConfig { Name = "Boomerang", GeneratedPromos = new GeneratedPromosConfig { From = "ch48" } }],
                    Plugins = new(StringComparer.OrdinalIgnoreCase) { ["overlays"] = new Dictionary<object, object> { ["from"] = "ch48" } }
                }
            ]
        };
        config.Settings.GeneratedPromosVoicePacks.Add(new GeneratedVoicePack
        {
            Phrases =
            [
                new GeneratedVoicePhrase
                {
                    Kind = GeneratedVoicePhraseKind.ChannelName,
                    Entries = [new() { Key = GeneratedVoiceKeys.Channel("ch48") }, new() { Key = GeneratedVoiceKeys.Block("ch48", "Mornings") }]
                }
            ]
        });
        ConfigIdentity.EnsureUids(config);
        return config;
    }

    [Fact]
    public void Every_channel_and_block_gets_a_uid_of_its_own()
    {
        SchedulerConfig config = Schedule();

        Assert.All(config.Channels, channel => Assert.False(string.IsNullOrEmpty(channel.Uid)));
        Assert.Equal(2, config.Channels.Select(channel => channel.Uid).Distinct().Count());
        Assert.All(config.Channels.SelectMany(channel => channel.Blocks), block => Assert.False(string.IsNullOrEmpty(block.Uid)));
        Assert.False(ConfigIdentity.EnsureUids(config));
    }

    [Fact]
    public void Changing_a_channel_s_id_takes_what_refers_to_it_along()
    {
        SchedulerConfig config = Schedule();
        ConfigIdentity before = ConfigIdentity.Of(config);

        config.Channels[0].Id = "boomerang";
        Assert.True(ConfigIdentity.FollowRenames(config, before));

        Assert.Equal("boomerang", config.Channels[1].Blocks[0].GeneratedPromos!.From);
        Assert.Equal("boomerang", ((Dictionary<object, object>)config.Channels[1].Plugins!["overlays"]!)["from"]);
        List<GeneratedVoiceEntry> takes = config.Settings.GeneratedPromosVoicePacks[0].Phrases[0].Entries;
        Assert.Equal(GeneratedVoiceKeys.Channel("boomerang"), takes[0].Key);
        Assert.Equal(GeneratedVoiceKeys.Block("boomerang", "Mornings"), takes[1].Key);
        Assert.Empty(SchedulerConfigLoader.Validate(config).Errors.Where(error => error.Contains("there is no channel")));
    }

    [Fact]
    public void Renaming_a_block_keeps_archive_follow_and_its_voice_over_takes_mapped_to_it()
    {
        SchedulerConfig config = Schedule();
        ConfigIdentity before = ConfigIdentity.Of(config);

        config.Channels[0].Blocks[0].Name = "Saturday Mornings";
        Assert.True(ConfigIdentity.FollowRenames(config, before));

        Assert.Equal("Saturday Mornings", config.Channels[0].Follow!.Blocks["Archived Mornings"]);
        Assert.Equal("Late", config.Channels[0].Follow!.Blocks["Other"]);
        Assert.Equal(GeneratedVoiceKeys.Block("ch48", "Saturday Mornings"), config.Settings.GeneratedPromosVoicePacks[0].Phrases[0].Entries[1].Key);
    }

    [Fact]
    public void Swapped_names_follow_each_other_and_nothing_changed_is_nothing_followed()
    {
        SchedulerConfig config = Schedule();
        ConfigIdentity before = ConfigIdentity.Of(config);
        Assert.False(ConfigIdentity.FollowRenames(config, before));

        config.Channels[0].Blocks[0].Name = "Late";
        config.Channels[0].Blocks[1].Name = "Mornings";
        ConfigIdentity.FollowRenames(config, before);

        Assert.Equal("Late", config.Channels[0].Follow!.Blocks["Archived Mornings"]);
        Assert.Equal("Mornings", config.Channels[0].Follow!.Blocks["Other"]);
    }

    [Fact]
    public void A_name_cleared_while_typing_is_followed_from_what_it_was()
    {
        SchedulerConfig config = Schedule();
        ConfigIdentity before = ConfigIdentity.Of(config);

        config.Channels[0].Blocks[0].Name = "";
        ConfigIdentity.FollowRenames(config, before);
        ConfigIdentity cleared = ConfigIdentity.Of(config, before);
        Assert.Equal("Mornings", config.Channels[0].Follow!.Blocks["Archived Mornings"]);

        config.Channels[0].Blocks[0].Name = "Weekend Mornings";
        ConfigIdentity.FollowRenames(config, cleared);
        Assert.Equal("Weekend Mornings", config.Channels[0].Follow!.Blocks["Archived Mornings"]);
    }

    [Fact]
    public void A_duplicated_channel_gets_a_new_uid_and_the_original_keeps_its_own()
    {
        SchedulerConfig config = Schedule();
        ConfigIdentity before = ConfigIdentity.Of(config);
        string original = config.Channels[0].Uid!;

        // A copy put ahead of the original, carrying its uid.
        config.Channels.Insert(0, new ChannelConfig { Id = "ch48-copy", Name = "Copy", Uid = original });
        Assert.True(ConfigIdentity.EnsureUids(config, before));

        Assert.Equal(original, config.Channels.Single(channel => channel.Id == "ch48").Uid);
        Assert.NotEqual(original, config.Channels.Single(channel => channel.Id == "ch48-copy").Uid);
        // So it isn't mistaken for the original being renamed.
        Assert.False(ConfigIdentity.FollowRenames(config, before));
    }

    [Fact]
    public async Task A_renamed_channel_keeps_where_its_rotations_are_up_to_even_when_two_swap_ids()
    {
        var store = new SchedularrTV.Core.Ordering.InMemoryPlaybackStateStore();
        await store.SaveAsync(new SchedularrTV.Core.Ordering.PlaybackState { Key = "ch48|block|abc", Cursor = 7 });
        await store.SaveAsync(new SchedularrTV.Core.Ordering.PlaybackState { Key = "ch4|block|abc", Cursor = 3 });
        await store.SaveAsync(new SchedularrTV.Core.Ordering.PlaybackState { Key = "ch46|block|abc", Cursor = 2 });

        await SchedularrTV.Service.ChannelRenameCarrier.CarryAsync(store,
            new Dictionary<string, string> { ["ch48"] = "ch46", ["ch46"] = "ch48" });

        Assert.Equal(7, (await store.GetAsync("ch46|block|abc"))!.Cursor);
        Assert.Equal(2, (await store.GetAsync("ch48|block|abc"))!.Cursor);
        // A channel whose id merely starts the same way is left alone.
        Assert.Equal(3, (await store.GetAsync("ch4|block|abc"))!.Cursor);
    }

    [Fact]
    public void Channel_renames_are_told_by_uid()
    {
        SchedulerConfig config = Schedule();
        ConfigIdentity before = ConfigIdentity.Of(config);
        config.Channels[0].Id = "boomerang";

        Assert.Equal("boomerang", Assert.Single(ConfigIdentity.ChannelRenames(config, before)).Value);
        Assert.Empty(ConfigIdentity.ChannelRenames(config, ConfigIdentity.Of(config)));
    }

    [Fact]
    public void Uids_round_trip_through_the_schedule_file()
    {
        SchedulerConfig config = Schedule();
        SchedulerConfig loaded = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config));

        Assert.Equal(config.Channels.Select(channel => channel.Uid), loaded.Channels.Select(channel => channel.Uid));
        Assert.Equal(config.Channels[0].Blocks.Select(block => block.Uid), loaded.Channels[0].Blocks.Select(block => block.Uid));
    }
}
