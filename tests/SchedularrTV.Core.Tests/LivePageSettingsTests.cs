using Microsoft.Extensions.Configuration;
using SchedularrTV.LiveCapture;
using SchedularrTV.Plugins;
using SchedularrTV.Radio;
using SchedularrTV.Streamers;
using SchedularrTV.TvGuide;
using SchedularrTV.WebChannels;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>What every live page plugin's settings share (<see cref="LivePageSettings"/>), and the hidden browser's pacing.</summary>
public sealed class LivePageSettingsTests
{
    private static FakeContext Context(Dictionary<string, string?>? plugin = null, Dictionary<string, string?>? app = null) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(plugin ?? []).Build(),
            new ConfigurationBuilder().AddInMemoryCollection(app ?? new Dictionary<string, string?> { ["Server:Port"] = "5252" }).Build());

    [Fact]
    public void Each_plugin_keeps_its_own_defaults()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), new StreamerSettings(Context()).KeepAlive);
        Assert.Equal(TimeSpan.FromSeconds(90), new RadioSettings(Context()).KeepAlive);
        Assert.Equal(TimeSpan.FromSeconds(90), new GuideSettings(Context()).KeepAlive);
        Assert.Equal(TimeSpan.FromSeconds(90), new WebChannelSettings(Context()).KeepAlive);

        if (!SchedularrTV.Core.Platform.HardwareProfile.IsLowPower)
        {
            Assert.Equal(15, new StreamerSettings(Context()).CaptureFrameRate);
            Assert.Equal(20, new RadioSettings(Context()).CaptureFrameRate);
            Assert.Equal(24, new GuideSettings(Context()).CaptureFrameRate);
            Assert.Equal(15, new WebChannelSettings(Context()).CaptureFrameRate);
        }
    }

    [Fact]
    public void Shared_settings_are_read_the_same_way_everywhere()
    {
        var settings = new GuideSettings(Context(new Dictionary<string, string?>
        {
            ["FrameRate"] = "99", ["KeepAliveSeconds"] = "\"12\"", ["VideoDecoding"] = "CUDA", ["PaceAnimations"] = "False",
            ["StreamAddress"] = "http://tv.example:5252/"
        }));

        Assert.Equal(30, settings.CaptureFrameRate);
        Assert.Equal(TimeSpan.FromSeconds(12), settings.KeepAlive);
        Assert.Equal("cuda", settings.HardwareDecoding);
        Assert.False(settings.PaceAnimations);
        Assert.Equal("http://tv.example:5252/plugins/tv-guide/live/a%20b.ts", settings.LiveUrl("a b"));
        Assert.Equal("http://localhost:5252/plugins/tv-guide/view/a%20b", settings.PageUrl("a b"));

        // Unknown or "off" means the processor; pacing is on unless switched off.
        var plain = new RadioSettings(Context(new Dictionary<string, string?> { ["VideoDecoding"] = "off" }));
        Assert.Null(plain.HardwareDecoding);
        Assert.True(plain.PaceAnimations);
        LiveStreamSpec spec = plain.Spec(new RadioStation { Id = "late", Name = "Late" });
        Assert.True(spec.PaceAnimations);
        Assert.Equal(plain.CaptureFrameRate, spec.CaptureFrameRate);
        Assert.NotEqual(spec.Fingerprint, (spec with { PaceAnimations = false }).Fingerprint);
    }

    [Fact]
    public void Web_channels_still_fall_back_to_the_plugin_they_replaced()
    {
        var settings = new WebChannelSettings(Context(app: new Dictionary<string, string?>
        {
            ["Server:Port"] = "5252", [$"Plugins:{WebChannelSettings.LegacyPluginId}:FrameRate"] = "9"
        }));

        Assert.Equal(9, settings.CaptureFrameRate);
    }

    [Fact]
    public void The_pacing_script_runs_at_the_capture_rate()
    {
        string script = HeadlessBrowser.PacingScript(15);

        Assert.Contains("const interval = 1000 / 15;", script, StringComparison.Ordinal);
        Assert.Contains("window.requestAnimationFrame =", script, StringComparison.Ordinal);
        Assert.Contains("document.getAnimations()", script, StringComparison.Ordinal);
        Assert.Contains("1000 / 60;", HeadlessBrowser.PacingScript(500), StringComparison.Ordinal);
    }

    private sealed class FakeContext(IConfiguration configuration, IConfiguration app) : IPluginContext
    {
        public PluginManifest Manifest { get; } = new() { Id = "live", Name = "Live" };
        public string? PluginDirectory => null;
        public string DataDirectory => Path.GetTempPath();
        public string AppDataDirectory => Path.GetTempPath();
        public IConfiguration Configuration => configuration;
        public IConfiguration AppConfiguration => app;
        public bool IsLoaded(string pluginId) => false;
        public string StaticFilesPath => "/plugins/live/";
    }
}
