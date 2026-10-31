using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Service.Components.Shared;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class GuideProgramColorTests
{
    [Fact]
    public async Task Guide_renders_distinct_categories_without_merging_the_same_show_across_origins()
    {
        DateTimeOffset start = DateTimeOffset.Now;
        var item = TestMedia.Episode("Shared show", 1, 1, TimeSpan.FromMinutes(10));
        ScheduleEntry Entry(int minutes, ScheduleEntryKind kind, ProgramOrigin origin = ProgramOrigin.Normal) => new()
        {
            Start = start.AddMinutes(minutes), Duration = TimeSpan.FromMinutes(10), Kind = kind,
            Item = item, ProgramOrigin = origin, BlockName = "Same block"
        };
        var schedule = new ChannelSchedule
        {
            ChannelId = "test", ChannelName = "Test", Start = start, End = start.AddMinutes(90),
            Entries = [Entry(0, ScheduleEntryKind.Program), Entry(10, ScheduleEntryKind.Interstitial),
                Entry(20, ScheduleEntryKind.Program, ProgramOrigin.Anchor),
                Entry(30, ScheduleEntryKind.Program, ProgramOrigin.Archive),
                Entry(40, ScheduleEntryKind.Program, ProgramOrigin.ArchiveRandom) with { ContinuesSlot = true },
                // A show's intro and outro are part of its listing, not interstitials of their own.
                Entry(50, ScheduleEntryKind.Program) with { IsSlotIntro = true },
                Entry(60, ScheduleEntryKind.Program),
                Entry(70, ScheduleEntryKind.Program) with { IsSlotOutro = true },
                Entry(80, ScheduleEntryKind.Commercial)]
        };
        var services = new ServiceCollection().AddLogging();
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<GuideGrid>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(GuideGrid.Schedules)] = new[] { schedule } }));
            string html = rendered.ToHtmlString();
            Assert.Equal(6, html.Split("class=\"guide-prog ").Length - 1);
            // The opening show, and the one with its intro and outro folded in.
            Assert.Equal(2, html.Split("guide-prog normal").Length - 1);
            // Only the real interstitial.
            Assert.Equal(1, html.Split("guide-prog interstitial").Length - 1);
            Assert.Contains("guide-prog anchor", html);
            Assert.Contains("guide-prog archive", html);
            Assert.Contains("guide-prog archive-random", html);
            Assert.Contains("(Normal scheduling)", html);
            Assert.Contains("(Short / Interstitial)", html);
            Assert.Contains("(Anchor show)", html);
            Assert.Contains("(Archive follow)", html);
            Assert.Contains("(Archive follow: random episode)", html);
        });
    }
}