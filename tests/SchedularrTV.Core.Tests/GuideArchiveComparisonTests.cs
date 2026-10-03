using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Service.Components.Shared;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>The guide's "Compare archive schedule" row: grey blocks of what the archive aired, under the channel's own.</summary>
public sealed class GuideArchiveComparisonTests
{
    // The guide shows a window around now, so the schedule has to be too.
    private static readonly DateTimeOffset Start = new(DateTimeOffset.Now.Year, DateTimeOffset.Now.Month, DateTimeOffset.Now.Day,
        DateTimeOffset.Now.Hour, 0, 0, DateTimeOffset.Now.Offset);

    private static ScheduleEntry Entry(int minute, ProgramOrigin origin, ScheduleEntryKind kind = ScheduleEntryKind.Program) => new()
    {
        Start = Start.AddMinutes(minute), Duration = TimeSpan.FromMinutes(10), Kind = kind,
        Item = TestMedia.Episode("Library Show", 1, minute + 1, TimeSpan.FromMinutes(10)), ProgramOrigin = origin
    };

    private static ChannelSchedule Schedule() => new()
    {
        ChannelId = "follow", ChannelName = "Follow Channel", ArchiveFollowEnabled = true,
        Start = Start, End = Start.AddHours(2),
        Entries = [Entry(0, ProgramOrigin.Archive), Entry(10, ProgramOrigin.ArchiveRandom),
            Entry(20, ProgramOrigin.Normal, ScheduleEntryKind.Interstitial), Entry(30, ProgramOrigin.Normal)],
        ArchiveDays = [new ArchiveGuideDay(Start, Start.AddHours(1), new DateOnly(2002, 9, 16),
            [new ArchiveGuideBlock(Start, Start.AddMinutes(30), ["Archive block"])
            {
                Show = "Original Show", Episode = "Pilot / Missing", OriginalStart = TimeSpan.FromHours(6),
                OriginalEnd = TimeSpan.FromHours(6.5)
            }])]
    };

    [Fact]
    public async Task The_comparison_row_shows_archived_slots_as_grey_blocks_on_the_guides_timeline()
    {
        ChannelSchedule schedule = Schedule();
        string html = await RenderAsync(new()
        {
            [nameof(GuideGrid.Schedules)] = new[] { schedule },
            [nameof(GuideGrid.CompareOpen)] = new[] { schedule.ChannelId }
        });

        Assert.Contains("guide-archive-row", html);
        Assert.Contains("Archived schedule", html);
        Assert.Contains("class=\"guide-faux", html);
        Assert.Contains("Original Show", html);
        Assert.Contains("Archive 2002-09-16", html);
        Assert.Contains("06:00 - 06:30", html);
        Assert.Contains("[Archive block]", html);
        Assert.Contains("aria-expanded=\"true\"", html);

        // The channel's own programmes are still drawn as normal.
        Assert.Contains("guide-prog archive-random", html);
    }

    [Fact]
    public async Task Follow_channels_get_the_toggle_closed_and_an_empty_row_says_so()
    {
        ChannelSchedule schedule = Schedule() with { ArchiveDays = [] };
        string closed = await RenderAsync(new()
        {
            [nameof(GuideGrid.Schedules)] = new[] { schedule, schedule with { ChannelId = "normal", ChannelName = "Normal Channel", ArchiveFollowEnabled = false } }
        });
        Assert.Contains("Compare archive schedule for Follow Channel", closed);
        Assert.Contains("aria-expanded=\"false\"", closed);
        Assert.DoesNotContain("Compare archive schedule for Normal Channel", closed);
        Assert.DoesNotContain("guide-archive-row", closed);

        string open = await RenderAsync(new()
        {
            [nameof(GuideGrid.Schedules)] = new[] { schedule },
            [nameof(GuideGrid.CompareOpen)] = new[] { schedule.ChannelId }
        });
        Assert.Contains("No archived schedule for this stretch", open);
    }

    [Fact]
    public async Task The_legend_names_the_archive_colours_only_when_a_channel_follows_an_archive()
    {
        string follow = await RenderAsync(new() { [nameof(GuideGrid.Schedules)] = new[] { Schedule() } });
        Assert.Contains("guide-legend", follow);
        Assert.Contains("Archived episode", follow);
        Assert.Contains("Archived schedule (compare)", follow);

        string normal = await RenderAsync(new()
        {
            [nameof(GuideGrid.Schedules)] = new[] { Schedule() with { ArchiveFollowEnabled = false, ArchiveDays = [] } }
        });
        Assert.Contains("Short, intro or outro", normal);
        Assert.DoesNotContain("Archived episode", normal);
    }

    private static async Task<string> RenderAsync(Dictionary<string, object?> parameters)
    {
        await using ServiceProvider provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<GuideGrid>(ParameterView.FromDictionary(parameters));
            return rendered.ToHtmlString();
        });
    }
}
