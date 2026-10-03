using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class TimelineBuilderTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Follow_only_blocks_never_create_regular_windows_or_anchors(bool anchorSlot)
    {
        var block = new BlockConfig
        {
            Name = "Archive settings",
            FollowOnly = true,
            Time = TimeSpan.FromHours(8),
            EndTime = anchorSlot ? null : TimeSpan.FromHours(10),
            Anchors = [new AnchorConfig { Time = TimeSpan.FromHours(9), Show = new ContentSelector { Query = "*" } }]
        };
        ChannelConfig channel = Channel(block);
        var start = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var blocks = TimelineBuilder.BuildBlockOccurrences(channel, Utc, start, start.AddDays(1));
        Assert.False(block.IsAnchorSlot);
        Assert.Empty(blocks);
        Assert.Empty(TimelineBuilder.BuildAnchorOccurrences(channel, blocks, Utc, start, start.AddDays(1)));
    }

    private static ChannelConfig Channel(params BlockConfig[] blocks) => new()
    {
        Id = "ch",
        Name = "Channel",
        Blocks = [.. blocks]
    };

    [Fact]
    public void A_night_block_crossing_midnight_produces_a_continuous_window()
    {
        ChannelConfig channel = Channel(new BlockConfig
        {
            Name = "Teletoon at Night",
            Time = TimeSpan.FromHours(21),
            EndTime = TimeSpan.FromHours(3),
            Recurrence = Recurrence.Daily,
            Shows = [new ContentSelector { Query = "*" }]
        });

        var start = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);
        IReadOnlyList<BlockOccurrence> occurrences = TimelineBuilder.BuildBlockOccurrences(channel, Utc, start, start.AddDays(1));

        // The window that began the previous evening plus the one starting tonight.
        Assert.Equal(2, occurrences.Count);
        Assert.Equal(start, occurrences[0].Start);
        Assert.Equal(start.AddHours(3), occurrences[0].End);
        Assert.Equal(start.AddHours(21), occurrences[1].Start);
    }

    [Fact]
    public void Days_of_week_blocks_only_occur_on_listed_days()
    {
        ChannelConfig channel = Channel(new BlockConfig
        {
            Name = "SNICK",
            Time = TimeSpan.FromHours(20),
            EndTime = TimeSpan.FromHours(22),
            Days = [DayOfWeek.Saturday],
            Shows = [new ContentSelector { Query = "*" }]
        });

        var saturday = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);
        var sunday = saturday.AddDays(1);

        Assert.Single(TimelineBuilder.BuildBlockOccurrences(channel, Utc, saturday, saturday.AddDays(1)));
        Assert.Empty(TimelineBuilder.BuildBlockOccurrences(channel, Utc, sunday, sunday.AddDays(1)));
    }

    [Theory]
    [InlineData("12-25", RepeatModifier.Yearly, 2026, 12, 25, true)]
    [InlineData("12-25", RepeatModifier.Yearly, 2027, 12, 25, true)]
    [InlineData("12-25", RepeatModifier.Yearly, 2026, 12, 24, false)]
    [InlineData("15", RepeatModifier.Monthly, 2026, 3, 15, true)]
    [InlineData("15", RepeatModifier.Monthly, 2026, 4, 15, true)]
    [InlineData("15", RepeatModifier.Monthly, 2026, 4, 16, false)]
    [InlineData("2026-03-15", RepeatModifier.None, 2026, 3, 15, true)]
    [InlineData("2026-03-15", RepeatModifier.None, 2027, 3, 15, false)]
    public void Date_specs_honour_the_repeat_modifier(string spec, RepeatModifier repeat, int year, int month, int day, bool expected) =>
        Assert.Equal(expected, TimelineBuilder.MatchesDateSpec(spec, repeat, new DateTime(year, month, day)));

    [Fact]
    public void A_specific_date_block_outranks_a_daily_block()
    {
        ChannelConfig channel = Channel(
            new BlockConfig
            {
                Name = "Daily",
                Time = TimeSpan.FromHours(0),
                EndTime = TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59),
                Recurrence = Recurrence.Daily,
                Shows = [new ContentSelector { Query = "*" }]
            },
            new BlockConfig
            {
                Name = "Christmas Marathon",
                Time = TimeSpan.FromHours(8),
                EndTime = TimeSpan.FromHours(20),
                Schedule = new BlockSchedule { Date = "12-25", Repeat = RepeatModifier.Yearly },
                Shows = [new ContentSelector { Query = "*" }]
            });

        var christmas = new DateTimeOffset(2026, 12, 25, 0, 0, 0, TimeSpan.Zero);
        IReadOnlyList<BlockOccurrence> occurrences = TimelineBuilder.BuildBlockOccurrences(channel, Utc, christmas, christmas.AddDays(1));

        BlockOccurrence atNoon = Assert.Single(occurrences, o => o.Start <= christmas.AddHours(12) && o.End > christmas.AddHours(12));
        Assert.Equal("Christmas Marathon", atNoon.Block.Name);

        // The daily block is clipped either side rather than dropped.
        Assert.Equal(2, occurrences.Count(o => o.Block.Name == "Daily"));
    }

    [Fact]
    public void Nested_block_anchors_only_fire_inside_their_parent_window()
    {
        var block = new BlockConfig
        {
            Name = "Teletoon at Night",
            Time = TimeSpan.FromHours(21),
            EndTime = TimeSpan.FromHours(3),
            Recurrence = Recurrence.Daily,
            Anchors =
            [
                new AnchorConfig { Name = "Clone High", Time = TimeSpan.FromHours(22), Show = new ContentSelector { Query = "*" } },
                new AnchorConfig { Name = "Never", Time = TimeSpan.FromHours(12), Show = new ContentSelector { Query = "*" } }
            ],
            Shows = [new ContentSelector { Query = "*" }]
        };

        ChannelConfig channel = Channel(block);
        var start = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);

        IReadOnlyList<BlockOccurrence> blocks = TimelineBuilder.BuildBlockOccurrences(channel, Utc, start, start.AddDays(1));
        IReadOnlyList<AnchorOccurrence> anchors = TimelineBuilder.BuildAnchorOccurrences(channel, blocks, Utc, start, start.AddDays(1));

        AnchorOccurrence fired = Assert.Single(anchors);
        Assert.Equal("Clone High", fired.Name);
        Assert.Equal(start.AddHours(22), fired.Start);
    }

    [Fact]
    public void A_block_without_an_end_time_is_treated_as_an_anchor_slot()
    {
        ChannelConfig channel = Channel(new BlockConfig
        {
            Name = "SpongeBob Morning Anchor",
            Time = TimeSpan.FromHours(8),
            Days = [DayOfWeek.Monday],
            Shows = [new ContentSelector { Query = "show:'SpongeBob SquarePants'" }]
        });

        var monday = new DateTimeOffset(2026, 1, 12, 0, 0, 0, TimeSpan.Zero);

        Assert.Empty(TimelineBuilder.BuildBlockOccurrences(channel, Utc, monday, monday.AddDays(1)));

        AnchorOccurrence anchor = Assert.Single(TimelineBuilder.BuildAnchorOccurrences(channel, [], Utc, monday, monday.AddDays(1)));
        Assert.Equal(monday.AddHours(8), anchor.Start);
    }
}
