using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Service.Components.Shared;
using SchedularrTV.Service.Editor;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class GuideInteractionTests
{
    [Fact]
    public async Task Zoom_reveals_break_overlays_without_splitting_programmes_and_scroll_renders_later_items()
    {
        DateTimeOffset start = DateTimeOffset.Now;
        var item = TestMedia.Episode("First programme", 1, 1, TimeSpan.FromMinutes(20));
        ScheduleEntry Entry(int offset, int duration, ScheduleEntryKind kind) => new()
        {
            Start = start.AddMinutes(offset), Duration = TimeSpan.FromMinutes(duration), Kind = kind, Item = item
        };
        var schedule = new ChannelSchedule
        {
            ChannelId = "one", ChannelName = "One", Start = start, End = start.AddHours(25),
            Entries = [Entry(0, 10, ScheduleEntryKind.Program) with { SegmentNumber = 1, SegmentCount = 2 },
                Entry(10, 2, ScheduleEntryKind.Commercial), Entry(12, 1, ScheduleEntryKind.Filler),
                Entry(13, 10, ScheduleEntryKind.Program) with { SegmentNumber = 2, SegmentCount = 2, InPoint = TimeSpan.FromMinutes(10) },
                Entry(23, 7, ScheduleEntryKind.Commercial),
                Entry(1440, 30, ScheduleEntryKind.Program) with { Item = TestMedia.Episode("Tomorrow programme", 1, 1, TimeSpan.FromMinutes(30)) }]
        };
        Assert.Equal(2, GuideBreakSpan.FromSchedule(schedule).Count);
        Assert.Equal(2, GuideBreakSpan.FromSchedule(schedule)[0].Clips);
        using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            InteractiveGuide? guide = null;
            var rendered = await renderer.RenderComponentAsync<InteractiveGuide>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(GuideGrid.Schedules)] = new[] { schedule },
                [nameof(InteractiveGuide.Capture)] = (Action<InteractiveGuide>)(value => guide = value)
            }));
            string initial = rendered.ToHtmlString();
            Assert.Single(initial.Split("class=\"guide-prog ").Skip(1));
            Assert.DoesNotContain("class=\"guide-ad-break\"", initial);
            Assert.DoesNotContain("Tomorrow programme", initial);

            guide!.ZoomAt(1, 0, 200);
            string zoomed = rendered.ToHtmlString();
            Assert.Equal(2, zoomed.Split("class=\"guide-ad-break\"").Length - 1);
            Assert.Single(zoomed.Split("class=\"guide-prog ").Skip(1));
            Assert.Contains($"{start:HH:mm} – {start.AddMinutes(30):HH:mm}", System.Net.WebUtility.HtmlDecode(zoomed));

            await guide.ViewportChanged(24 * 420, 600);
            Assert.Contains("Tomorrow programme", rendered.ToHtmlString());
            Assert.DoesNotContain("First programme", rendered.ToHtmlString());
            guide.ZoomAt(-1, 24 * 420, 200);
            Assert.Contains("Tomorrow programme", rendered.ToHtmlString());
        });
    }

    public sealed class InteractiveGuide : GuideGrid
    {
        [Parameter] public Action<InteractiveGuide>? Capture { get; set; }
        protected override void OnInitialized() { base.OnInitialized(); Capture?.Invoke(this); }
    }
}
