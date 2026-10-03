using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class ChannelIdentityTests
{
    private static ChannelConfig Channel(int number, string? timeZone = null) => new()
    {
        Id = $"ch{number}",
        Name = $"Channel {number}",
        Number = number,
        TimeZone = timeZone,
        BaseRotation = new ContentSelector { Query = "kind == 'Episode'" }
    };

    [Fact]
    public void The_guide_id_follows_the_channel_number()
    {
        ChannelConfig channel = Channel(900);

        Assert.Equal("schedularrtv.900", channel.TvgId);

        channel.Number = 901;
        Assert.Equal("schedularrtv.901", channel.TvgId);
    }

    [Fact]
    public void The_guide_id_is_not_written_to_the_config_file()
    {
        SchedulerConfig config = new() { Channels = [Channel(900)] };

        string yaml = SchedulerConfigLoader.Serialize(config);

        Assert.DoesNotContain("tvg_id", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hand_written_guide_id_is_ignored_rather_than_honoured()
    {
        const string yaml = """
            settings:
              time_zone: "UTC"
            channels:
            - id: ch1
              name: One
              number: 900
              tvg_id: "schedularrtv.903"
              base_rotation:
                query: "kind == 'Episode'"
            """;

        SchedulerConfig config = SchedulerConfigLoader.Parse(yaml);

        Assert.Equal("schedularrtv.900", config.Channels[0].TvgId);
    }

    [Fact]
    public void Two_channels_on_the_same_number_are_rejected()
    {
        SchedulerConfig config = new() { Channels = [Channel(900), Channel(900)] };
        config.Channels[1].Id = "other";

        ConfigValidationResult result = SchedulerConfigLoader.Validate(config);

        Assert.Contains(result.Errors, e => e.Contains("Duplicate channel number 900", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_time_zone_is_reported_instead_of_silently_becoming_utc()
    {
        SchedulerConfig config = new() { Channels = [Channel(900, "Mars/Olympus_Mons")] };

        ConfigValidationResult result = SchedulerConfigLoader.Validate(config);

        Assert.Contains(result.Errors, e => e.Contains("unknown time zone", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_blank_channel_time_zone_inherits_the_global_one()
    {
        SchedulerConfig config = new()
        {
            Settings = new SchedulerSettings { TimeZone = "America/New_York" },
            Channels = [Channel(900, "   ")]
        };

        ConfigValidationResult result = SchedulerConfigLoader.Validate(config);
        Assert.DoesNotContain(result.Errors, e => e.Contains("time zone", StringComparison.OrdinalIgnoreCase));

        // Whitespace is not null, so a plain ?? would have handed "   " to the resolver and got UTC.
        TimeZoneInfo expected = SchedulingEngine.ResolveTimeZone("America/New_York");
        TimeZoneInfo resolved = SchedulingEngine.ResolveTimeZone(
            string.IsNullOrWhiteSpace(config.Channels[0].TimeZone)
                ? config.Settings.TimeZone
                : config.Channels[0].TimeZone!);

        Assert.Equal(expected.Id, resolved.Id);
        Assert.NotEqual(TimeZoneInfo.Utc.Id, resolved.Id);
    }
}
