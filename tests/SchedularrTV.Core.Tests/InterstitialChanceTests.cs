using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class InterstitialChanceTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 18, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Rolls_are_stable_and_follow_the_chance()
    {
        Assert.True(SchedulingEngine.UsesShort("seed", 0, 100));
        Assert.False(SchedulingEngine.UsesShort("seed", 0, 0));
        Assert.Equal(SchedulingEngine.UsesShort("break-a", 3, 70), SchedulingEngine.UsesShort("break-a", 3, 70));

        int used = Enumerable.Range(0, 2000).Count(i => SchedulingEngine.UsesShort($"break-{i}", 0, 70));
        Assert.InRange(used, 1250, 1550);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(70)]
    [InlineData(0)]
    public async Task Shorts_are_used_at_the_chance_set(int chance)
    {
        int shorts = Count(await BuildAsync(chance));
        int always = Count(await BuildAsync(100));

        switch (chance)
        {
            case 100:
                Assert.Equal(24, shorts);
                break;
            case 0:
                Assert.Equal(0, shorts);
                break;
            default:
                Assert.InRange(shorts, 1, always - 1);
                break;
        }
    }

    [Fact]
    public async Task Unset_uses_the_default_and_rebuilds_agree()
    {
        ChannelSchedule first = await BuildAsync(null);
        ChannelSchedule second = await BuildAsync(null);

        Assert.InRange(Count(first), 1, 23);
        Assert.Equal(first.Entries.Select(entry => (entry.Start, entry.Kind)), second.Entries.Select(entry => (entry.Start, entry.Kind)));
    }

    private static async Task<ChannelSchedule> BuildAsync(int? chance)
    {
        var config = new SchedulerConfig();
        config.Settings.TimeZone = "UTC";
        var channel = new ChannelConfig
        {
            Id = "test", Name = "Test", PadToNearest = TimeSpan.FromMinutes(30),
            BaseRotation = new() { Include = [new ContentInclude { Show = "Main" }], Order = PlaybackOrder.Chronological },
            Interstitials = new()
            {
                ShowChance = chance,
                MaxShowsPerSlot = 1,
                Shows = new() { Include = [new ContentInclude { Show = "Shorts" }], Order = PlaybackOrder.Random },
                Commercials = new() { Query = "tag:'Ads'", MaxRepeatsPerBreak = 100 },
                Filler = new() { Fallback = false }
            }
        };

        List<MediaItem> items = [.. Enumerable.Range(1, 30).Select(number => TestMedia.Episode("Main", 1, number, TimeSpan.FromMinutes(22)))];
        items.Add(TestMedia.Episode("Shorts", 1, 1, TimeSpan.FromMinutes(2)));
        items.Add(TestMedia.Filler("ad", TimeSpan.FromSeconds(30), "Ads"));
        return await new SchedulingEngine(new FakeMediaLibrary(items), new InMemoryPlaybackStateStore())
            .BuildAsync(config, channel, Start, Start.AddHours(12));
    }

    private static int Count(ChannelSchedule schedule) => schedule.Entries.Count(entry => entry.Kind == ScheduleEntryKind.Interstitial);
}
