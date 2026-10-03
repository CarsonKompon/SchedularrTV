using SchedularrTV.Core.Temporal;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class TemporalTagTests
{
    [Fact]
    public void Resolves_the_spec_example_for_january_tenth()
    {
        TemporalTags tags = TemporalTagResolver.Resolve(new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal("January", tags.Month);
        Assert.Equal("Winter", tags.Season);
        Assert.Equal("01-10", tags.ExactDate);
        Assert.Equal("2026-01-10", tags.FullDate);
        Assert.Contains("January", tags.All);
        Assert.Contains("Winter", tags.All);
        Assert.Contains("01-10", tags.All);
    }

    [Theory]
    [InlineData(12, "Winter")]
    [InlineData(1, "Winter")]
    [InlineData(2, "Winter")]
    [InlineData(3, "Spring")]
    [InlineData(5, "Spring")]
    [InlineData(6, "Summer")]
    [InlineData(8, "Summer")]
    [InlineData(9, "Fall")]
    [InlineData(11, "Fall")]
    public void Maps_months_to_meteorological_seasons(int month, string expected) =>
        Assert.Equal(expected, TemporalTagResolver.GetSeason(month));

    [Fact]
    public void Detects_christmas_and_halloween_windows()
    {
        TemporalTags christmas = TemporalTagResolver.Resolve(new DateTimeOffset(2026, 12, 20, 0, 0, 0, TimeSpan.Zero));
        TemporalTags halloween = TemporalTagResolver.Resolve(new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero));
        TemporalTags neither = TemporalTagResolver.Resolve(new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.Zero));

        Assert.Contains("Christmas", christmas.Holidays);
        Assert.Contains("Halloween", halloween.Holidays);
        Assert.Empty(neither.Holidays);
    }

    [Fact]
    public void Holiday_windows_may_wrap_the_year_boundary()
    {
        var newYear = new HolidayDefinition { Name = "New Year", Start = "12-26", End = "01-02" };

        Assert.True(TemporalTagResolver.IsWithin(newYear, new DateTime(2026, 12, 31)));
        Assert.True(TemporalTagResolver.IsWithin(newYear, new DateTime(2026, 1, 1)));
        Assert.False(TemporalTagResolver.IsWithin(newYear, new DateTime(2026, 6, 1)));
    }

    [Fact]
    public void Resolves_thanksgiving_as_the_fourth_thursday_of_november()
    {
        DateTime resolved = TemporalTagResolver.ResolveNthWeekday(
            2026,
            new NthWeekdayRule { Month = 11, DayOfWeek = DayOfWeek.Thursday, Ordinal = 4 });

        Assert.Equal(new DateTime(2026, 11, 26), resolved);
    }

    [Fact]
    public void Temporal_tags_can_be_switched_off_individually()
    {
        var options = new TemporalTagOptions { IncludeMonth = false, IncludeHolidays = false, IncludeFullDate = false };
        TemporalTags tags = TemporalTagResolver.Resolve(new DateTimeOffset(2026, 12, 25, 0, 0, 0, TimeSpan.Zero), options);

        Assert.DoesNotContain("December", tags.All);
        Assert.DoesNotContain("Christmas", tags.All);
        Assert.Contains("Winter", tags.All);
    }
}
