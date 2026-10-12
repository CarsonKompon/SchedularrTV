using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class InterstitialMigrationTests
{
    [Fact]
    public void Global_shorts_move_to_independent_channel_settings_and_do_not_return_after_removal()
    {
        var config = new SchedulerConfig();
        config.Settings.Interstitials = new()
        {
            Shows = new() { Query = "tag:'Shorts'" }, MaxShowsPerSlot = 2, ShowChance = 80,
            Commercials = new() { BaseTags = ["Brand"], Types = ["Ads"] }
        };
        config.Channels = [new() { Id = "one", Name = "One", Number = 1 }, new() { Id = "two", Name = "Two", Number = 2 },
            new() { Id = "three", Name = "Three", Number = 3, InterstitialShows = new() { Query = "tag:'Own'" }, Interstitials = new() { ShowChance = 10 } }];
        var loaded = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config));
        Assert.Null(loaded.Settings.Interstitials.Shows);
        Assert.Null(loaded.Settings.Interstitials.MaxShowsPerSlot);
        Assert.Null(loaded.Settings.Interstitials.ShowChance);
        Assert.NotSame(loaded.Channels[0].Interstitials!.Shows, loaded.Channels[1].Interstitials!.Shows);
        Assert.Equal("tag:'Own'", SchedulingEngine.ResolvedBreaks(loaded, loaded.Channels[2]).Shows!.Query);
        Assert.Equal(10, loaded.Channels[2].Interstitials!.ShowChance);
        loaded.Channels[0].Interstitials!.Shows = null;
        var reloaded = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(loaded));
        Assert.Null(SchedulingEngine.ResolvedBreaks(reloaded, reloaded.Channels[0]).Shows);
        Assert.Equal("tag:'Shorts'", SchedulingEngine.ResolvedBreaks(reloaded, reloaded.Channels[1]).Shows!.Query);
        Assert.Equal(["Brand"], SchedulingEngine.ResolvedBreaks(reloaded, reloaded.Channels[0]).Commercials!.BaseTags);
    }
}
