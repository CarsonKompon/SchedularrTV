using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Service;
using SchedularrTV.Service.Editor;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class ChannelCopyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"channel-copy-{Guid.NewGuid():N}");
    private readonly SchedulerConfigProvider _provider;
    private readonly ConfigEditor _editor;

    public ChannelCopyTests()
    {
        _provider = new SchedulerConfigProvider(Options.Create(new SchedulerServiceOptions
        {
            ConfigPath = Path.Combine(_directory, "schedule.yaml"), WatchConfig = false, AutoRebuild = false
        }), NullLogger<SchedulerConfigProvider>.Instance);
        var config = new SchedulerConfig
        {
            GlobalCollections = new() { ["Main"] = new() { Query = "kind == 'Episode'" } },
            Channels =
            [
                new() { Id = "destination", Name = "Destination", Number = 51, Logo = "/logos/destination.png",
                    BaseRotation = new() { Query = "kind == 'Movie'" }, Group = "Old group" },
                new()
                {
                    Id = "source", Name = "Source", Number = 50, Logo = "/logos/source.png", Group = "Cartoons",
                    TimeZone = "UTC", ScheduleStart = DateTimeOffset.Parse("2026-09-20T00:00:00Z"),
                    AutoTemporalTags = false, PadToNearest = TimeSpan.FromMinutes(30),
                    MaxBreak = TimeSpan.FromMinutes(5), StackToFill = true, MidRoll = new(),
                    BaseRotation = new() { Collection = "Main" },
                    InterstitialShows = new() { Query = "kind == 'Episode'" },
                    Interstitials = new() { MaxShowsPerSlot = 2, Shows = new() { Collection = "Main" } },
                    CommercialBaseTags = ["Brand"], PromoBaseTags = ["Promo brand"],
                    Promos = new() { Windows = [new() { Within = TimeSpan.FromHours(1), PromoTypes = ["next"] }] },
                    Follow = new()
                    {
                        Enabled = true, Directory = "archive", StartYear = 2000, EndYear = 2005,
                        Blocks = new() { ["Archived block"] = "Morning" },
                        Shows = new() { ["Archived show"] = "Library show", ["Disabled show"] = null },
                        Episodes = [new() { Show = "Archived show", Episode = "Pilot", MediaId = "episode-1" }]
                    },
                    Blocks = [new()
                    {
                        Name = "Morning", Time = TimeSpan.FromHours(6), EndTime = TimeSpan.FromHours(9),
                        Shows = [new() { Collection = "Main" }],
                        Interstitials = new() { MaxShowsPerSlot = 1 },
                        Anchors = [new() { Time = TimeSpan.FromHours(7), Show = new() { Collection = "Main" } }]
                    }],
                    Anchors = [new() { Time = TimeSpan.FromHours(12), Show = new() { Collection = "Main" } }]
                }
            ]
        };
        ConfigValidationResult saved = _provider.Save(SchedulerConfigLoader.Serialize(config));
        Assert.True(saved.IsValid, string.Join(Environment.NewLine, saved.Errors));
        _editor = new ConfigEditor(_provider);
    }

    [Fact]
    public void Copies_all_settings_from_working_source_preserving_identity_and_channel_order()
    {
        ChannelConfig source = _editor.FindChannel("source")!;
        source.Group = "Unsaved group";
        string sourceBefore = Serialize(source);
        string diskBefore = _provider.ReadRaw();
        var collections = _editor.Working.GlobalCollections;
        int changes = 0;
        _editor.Changed += () => changes++;

        ChannelConfig copied = _editor.CopyChannelSettings("DESTINATION", "SOURCE");

        Assert.Same(copied, _editor.Working.Channels[0]);
        Assert.Same(source, _editor.Working.Channels[1]);
        Assert.Equal("destination", copied.Id);
        Assert.Equal("Destination", copied.Name);
        Assert.Equal(51, copied.Number);
        Assert.Equal("schedularrtv.51", copied.TvgId);
        Assert.Equal("/logos/destination.png", copied.Logo);
        ChannelConfig expected = SchedulerConfigLoader.Parse(sourceBefore).Channels.Single();
        expected.Id = copied.Id;
        expected.Name = copied.Name;
        expected.Number = copied.Number;
        expected.Logo = copied.Logo;
        Assert.Equal(Serialize(expected), Serialize(copied));
        Assert.Equal(sourceBefore, Serialize(source));
        Assert.Same(collections, _editor.Working.GlobalCollections);
        Assert.Equal("Main", copied.BaseRotation!.Collection);
        Assert.Equal(diskBefore, _provider.ReadRaw());
        Assert.True(_editor.IsDirty);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Nested_settings_are_independent_in_both_directions()
    {
        ChannelConfig source = _editor.FindChannel("source")!;
        string sourceBefore = Serialize(source);
        ChannelConfig copied = _editor.CopyChannelSettings("destination", "source");
        copied.BaseRotation!.Collection = "Other";
        copied.Blocks[0].Shows[0].Collection = "Other";
        copied.Blocks[0].Anchors[0].Show!.Collection = "Other";
        copied.Blocks[0].Interstitials!.MaxShowsPerSlot = 0;
        copied.Anchors[0].Show!.Collection = "Other";
        copied.Interstitials!.Shows!.Collection = "Other";
        copied.InterstitialShows!.Query = "kind == 'Movie'";
        // The channel's tag-based break settings were moved into an ad break when the schedule was saved.
        copied.AdBreaks!.Add("Other");
        copied.Follow!.Blocks["archived block"] = null;
        copied.Follow.Shows["archived show"] = "Other";
        copied.Follow.Episodes[0].MediaId = "other-episode";
        Assert.Equal(sourceBefore, Serialize(source));
        Assert.Single(copied.Follow.Blocks);

        string copiedBefore = Serialize(copied);
        source.Follow!.Episodes.Clear();
        source.Blocks.Clear();
        source.MidRoll!.TargetSegment = TimeSpan.FromMinutes(25);
        Assert.Equal(copiedBefore, Serialize(copied));
    }

    [Fact]
    public void Nulls_and_empty_lists_replace_old_settings_and_keep_a_null_logo()
    {
        ChannelConfig source = _editor.FindChannel("source")!;
        source.Logo = null;

        ChannelConfig copied = _editor.CopyChannelSettings("source", "destination");

        Assert.Null(copied.Logo);
        Assert.Null(copied.Follow);
        Assert.Null(copied.Interstitials);
        Assert.Null(copied.Promos);
        Assert.Null(copied.CommercialBaseTags);
        Assert.Empty(copied.Blocks);
        Assert.Empty(copied.Anchors);
    }

    [Fact]
    public void Copy_can_be_discarded_or_saved_without_changing_the_source()
    {
        string original = _editor.ToYaml();
        string sourceBefore = Serialize(_editor.FindChannel("source")!);
        _editor.CopyChannelSettings("destination", "source");
        _editor.Reload();
        Assert.False(_editor.IsDirty);
        Assert.Equal(original, _editor.ToYaml());

        _editor.CopyChannelSettings("destination", "source");
        string copied = _editor.ToYaml();
        Assert.True(_editor.Save(), string.Join(Environment.NewLine, _editor.Errors));
        Assert.False(_editor.IsDirty);
        _editor.Reload();
        Assert.Equal(copied, _editor.ToYaml());
        Assert.Equal(sourceBefore, Serialize(_editor.FindChannel("source")!));
    }

    [Theory]
    [InlineData("destination", "DESTINATION")]
    [InlineData("destination", "missing")]
    [InlineData("missing", "source")]
    public void Invalid_choices_do_not_mutate_or_dirty_the_editor(string destination, string source)
    {
        string before = _editor.ToYaml();
        Assert.Throws<InvalidOperationException>(() => _editor.CopyChannelSettings(destination, source));
        Assert.Equal(before, _editor.ToYaml());
        Assert.False(_editor.IsDirty);
    }

    private static string Serialize(ChannelConfig channel) =>
        SchedulerConfigLoader.Serialize(new SchedulerConfig { Channels = [channel] });

    public void Dispose()
    {
        _provider.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}