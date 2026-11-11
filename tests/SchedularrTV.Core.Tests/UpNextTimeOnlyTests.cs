using SchedularrTV.Core.Configuration;
using SchedularrTV.GeneratedPromos;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>Up Next text showing just the start times, on a 12- or 24-hour clock; and swapping text layers to Up Next and back.</summary>
public sealed class UpNextTimeOnlyTests
{
    private static readonly UpNextListing[] Listings =
    [
        new("Rugrats", new DateTimeOffset(2026, 11, 11, 19, 30, 0, TimeSpan.Zero), Runtime: TimeSpan.FromMinutes(30)),
        new("Doug", new DateTimeOffset(2026, 11, 11, 20, 0, 0, TimeSpan.Zero), Runtime: TimeSpan.FromMinutes(30))
    ];

    private static IReadOnlyList<string> Lines(GeneratedUpNextOptions options) =>
        GeneratedPromoResolver.UpNextLines(Listings, options, TimeZoneInfo.Utc, "Nick");

    [Fact]
    public void Start_times_read_as_they_always_have_unless_asked_otherwise()
    {
        Assert.Equal(new[] { "7:30  Rugrats", "8:00  Doug" }, Lines(new GeneratedUpNextOptions { IncludeTime = true }));
        Assert.Equal(new[] { "Rugrats", "Doug" }, Lines(new GeneratedUpNextOptions { TimeOnly = true }));
    }

    [Fact]
    public void Time_only_drops_the_show_names_and_the_clock_can_be_24_hour_or_show_am_pm()
    {
        Assert.Equal(new[] { "7:30", "8:00" }, Lines(new GeneratedUpNextOptions { IncludeTime = true, TimeOnly = true }));
        Assert.Equal(new[] { "7:30 PM", "8:00 PM" }, Lines(new GeneratedUpNextOptions { IncludeTime = true, TimeOnly = true, AmPm = true }));
        Assert.Equal(new[] { "19:30  Rugrats", "20:00  Doug" }, Lines(new GeneratedUpNextOptions { IncludeTime = true, TwentyFourHour = true, AmPm = true }));
    }

    [Fact]
    public void Am_pm_only_gives_just_the_am_or_pm_of_each_start_time()
    {
        var listings = new UpNextListing[]
        {
            new("Morning Show", new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero)),
            new("Rugrats", new DateTimeOffset(2026, 11, 11, 19, 30, 0, TimeSpan.Zero))
        };
        var options = new GeneratedUpNextOptions { IncludeTime = true, AmPmOnly = true, TimeOnly = true, TwentyFourHour = true };

        Assert.Equal(new[] { "AM", "PM" }, GeneratedPromoResolver.UpNextLines(listings, options, TimeZoneInfo.Utc, "Nick"));
        Assert.DoesNotContain("AmPmOnly", System.Text.Json.JsonSerializer.Serialize(new GeneratedUpNextOptions()));
    }

    [Fact]
    public void Start_times_can_be_shown_in_other_time_zones_with_their_labels()
    {
        // 6:30 PM in New York is 3:30 PM in Los Angeles (both on daylight time in July).
        var listings = new UpNextListing[] { new("Rugrats", new DateTimeOffset(2026, 7, 10, 22, 30, 0, TimeSpan.Zero)) };
        List<GeneratedUpNextZone> zones = [new() { TimeZone = "America/New_York" }, new() { TimeZone = "America/Los_Angeles", Label = "Pacific" }];

        Assert.Equal(new[] { "6:30 ET", "3:30 Pacific" },
            GeneratedPromoResolver.UpNextLines(listings, new GeneratedUpNextOptions { IncludeTime = true, TimeOnly = true, Zones = zones }, TimeZoneInfo.Utc, "Nick"));
        Assert.Equal(new[] { "6:30 ET / 3:30 Pacific  Rugrats" },
            GeneratedPromoResolver.UpNextLines(listings, new GeneratedUpNextOptions { IncludeTime = true, Zones = zones }, TimeZoneInfo.Utc, "Nick"));
        Assert.Equal(new[] { "PM ET", "PM Pacific" },
            GeneratedPromoResolver.UpNextLines(listings, new GeneratedUpNextOptions { IncludeTime = true, AmPmOnly = true, Zones = zones }, TimeZoneInfo.Utc, "Nick"));
    }

    [Fact]
    public void A_time_zone_that_isn_t_known_falls_back_to_the_channel_s_and_labels_can_be_left_off()
    {
        var listings = new UpNextListing[] { new("Doug", new DateTimeOffset(2026, 7, 10, 20, 0, 0, TimeSpan.Zero)) };
        var options = new GeneratedUpNextOptions
        {
            IncludeTime = true, TimeOnly = true, Zones = [new() { TimeZone = "Not/AZone", Label = "" }]
        };

        Assert.Equal(new[] { "8:00" }, GeneratedPromoResolver.UpNextLines(listings, options, TimeZoneInfo.Utc, "Nick"));
        Assert.Equal("PT", GeneratedUpNextZone.ShortName("Pacific Standard Time"));
        Assert.Null(GeneratedUpNextZone.ShortName("Europe/Paris"));
    }

    [Fact]
    public void Start_and_end_time_tokens_use_the_same_clock()
    {
        var options = new GeneratedUpNextOptions { ShowsAhead = 2, TwentyFourHour = true, Prefix = "From {start_time} to {end_time}\n" };
        Assert.Equal(new[] { "From 19:30 to 20:30", "Rugrats", "Doug" }, Lines(options));
    }

    [Fact]
    public void Unused_clock_settings_are_left_out_so_render_keys_hold()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new GeneratedUpNextOptions { IncludeTime = true });
        Assert.DoesNotContain("TimeOnly", json);
        Assert.DoesNotContain("TwentyFourHour", json);
        Assert.DoesNotContain("AmPm", json);
    }

    [Fact]
    public void A_text_layer_swaps_to_up_next_and_back_keeping_everything_else()
    {
        var text = new GeneratedLayer
        {
            Kind = GeneratedLayerKind.Text, Name = "Text 2", Text = "Only on {channel}", X = 40, FontSize = 72, Color = "#FFCC00",
            Effects = [LayerEffect.New(LayerEffectKind.Outline)]
        };
        var template = new GeneratedPromoTemplate { Layers = [text] };

        Assert.True(GeneratedPromoEditing.SwapText(template, text));
        Assert.Equal(GeneratedLayerKind.UpNext, text.Kind);
        Assert.NotNull(text.UpNext);
        Assert.Equal("Up Next", text.Name);
        Assert.Equal((40f, 72f, "#FFCC00"), (text.X, text.FontSize, text.Color));
        Assert.Single(text.Effects!);

        text.UpNext!.IncludeTime = true;
        Assert.True(GeneratedPromoEditing.SwapText(template, text));
        Assert.Equal(GeneratedLayerKind.Text, text.Kind);
        Assert.Equal("Only on {channel}", text.Text);
        Assert.Equal("Text", text.Name);

        // Its Up Next settings were kept for swapping back.
        GeneratedPromoEditing.SwapText(template, text);
        Assert.True(text.UpNext!.IncludeTime);
    }

    [Fact]
    public void A_named_layer_keeps_its_name_and_other_kinds_dont_swap()
    {
        var layer = new GeneratedLayer { Kind = GeneratedLayerKind.UpNext, Name = "Lower third" };
        var template = new GeneratedPromoTemplate { Layers = [layer] };

        GeneratedPromoEditing.SwapText(template, layer);
        Assert.Equal("Lower third", layer.Name);
        Assert.Equal("Up next", layer.Text);

        var shape = new GeneratedLayer { Kind = GeneratedLayerKind.Rect };
        Assert.False(GeneratedPromoEditing.SwapText(template, shape));
        Assert.Equal(GeneratedLayerKind.Rect, shape.Kind);
    }
}
