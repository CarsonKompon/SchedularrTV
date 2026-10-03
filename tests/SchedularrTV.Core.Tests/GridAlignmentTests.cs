using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class GridAlignmentTests
{
    private static readonly DateTimeOffset Anchor = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static SchedulerConfig Config(ChannelConfig channel) => new()
    {
        Settings = new SchedulerSettings { TimeZone = "UTC" },
        Channels = [channel]
    };

    private static ChannelConfig Channel(TimeSpan? pad, DateTimeOffset? anchor = null) => new()
    {
        Id = "ch1",
        Name = "One",
        Number = 900,
        TimeZone = "UTC",
        PadToNearest = pad,
        ScheduleStart = anchor,
        BaseRotation = new ContentSelector { Query = "kind == 'Episode'" }
    };

    [Fact]
    public void A_moment_inside_a_slot_aligns_back_to_that_slot()
    {
        ChannelConfig channel = Channel(TimeSpan.FromMinutes(30), Anchor);
        DateTimeOffset now = new(2026, 3, 4, 21, 10, 44, TimeSpan.Zero);

        Assert.Equal(
            new DateTimeOffset(2026, 3, 4, 21, 0, 0, TimeSpan.Zero),
            SchedulingEngine.AlignToGrid(Config(channel), channel, now));
    }

    [Fact]
    public void The_next_boundary_is_the_end_of_the_current_slot()
    {
        ChannelConfig channel = Channel(TimeSpan.FromMinutes(30), Anchor);
        DateTimeOffset now = new(2026, 3, 4, 21, 10, 44, TimeSpan.Zero);

        Assert.Equal(
            new DateTimeOffset(2026, 3, 4, 21, 30, 0, TimeSpan.Zero),
            SchedulingEngine.SlotBoundaryAfter(Config(channel), channel, now));
    }

    [Fact]
    public void A_moment_exactly_on_a_boundary_advances_to_the_next_one()
    {
        ChannelConfig channel = Channel(TimeSpan.FromMinutes(30), Anchor);
        DateTimeOffset now = new(2026, 3, 4, 21, 30, 0, TimeSpan.Zero);

        Assert.Equal(
            new DateTimeOffset(2026, 3, 4, 22, 0, 0, TimeSpan.Zero),
            SchedulingEngine.SlotBoundaryAfter(Config(channel), channel, now));
    }

    [Fact]
    public void The_anchor_shifts_the_whole_grid()
    {
        // A grid anchored at :10 puts boundaries on :10 and :40, not :00 and :30.
        ChannelConfig channel = Channel(
            TimeSpan.FromMinutes(30),
            new DateTimeOffset(2026, 1, 1, 0, 10, 0, TimeSpan.Zero));

        DateTimeOffset now = new(2026, 3, 4, 21, 15, 0, TimeSpan.Zero);

        Assert.Equal(
            new DateTimeOffset(2026, 3, 4, 21, 10, 0, TimeSpan.Zero),
            SchedulingEngine.AlignToGrid(Config(channel), channel, now));

        Assert.Equal(
            new DateTimeOffset(2026, 3, 4, 21, 40, 0, TimeSpan.Zero),
            SchedulingEngine.SlotBoundaryAfter(Config(channel), channel, now));
    }

    [Fact]
    public void Without_a_grid_the_moment_is_left_alone()
    {
        ChannelConfig channel = Channel(pad: null);
        DateTimeOffset now = new(2026, 3, 4, 21, 10, 44, TimeSpan.Zero);

        Assert.Equal(now, SchedulingEngine.AlignToGrid(Config(channel), channel, now));
        Assert.Equal(now, SchedulingEngine.SlotBoundaryAfter(Config(channel), channel, now));
    }

    [Fact]
    public void An_anchor_in_the_future_does_not_align_backwards()
    {
        ChannelConfig channel = Channel(TimeSpan.FromMinutes(30), new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        DateTimeOffset now = new(2026, 3, 4, 21, 10, 44, TimeSpan.Zero);

        Assert.Equal(now, SchedulingEngine.AlignToGrid(Config(channel), channel, now));
    }
}
