using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Service;
using SchedularrTV.Service.Editor;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class ConfigEditorSaveTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"editor-save-{Guid.NewGuid():N}");
    private readonly SchedulerConfigProvider _provider;
    private readonly ConfigEditor _editor;

    public ConfigEditorSaveTests()
    {
        _provider = new SchedulerConfigProvider(Options.Create(new SchedulerServiceOptions
        {
            ConfigPath = Path.Combine(_directory, "schedule.yaml"), WatchConfig = false, AutoRebuild = false
        }), NullLogger<SchedulerConfigProvider>.Instance);
        _provider.Save(SchedulerConfigLoader.Serialize(new SchedulerConfig
        {
            Channels = [new() { Id = "test", Name = "Saved", Number = 1, BaseRotation = new() { Query = "kind == 'Episode'" } }]
        }));
        _editor = new ConfigEditor(_provider);
    }

    [Fact]
    public async Task Save_does_not_block_caller_or_clear_edits_made_during_save()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        _provider.ConfigChanged += _ =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        _editor.FindChannel("test")!.Name = "Snapshot";
        _editor.MarkDirty();
        Task<bool> save = _editor.SaveAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(_editor.IsBusy);
            Assert.False(save.IsCompleted);
            Assert.False(await _editor.SaveAsync());
            _editor.FindChannel("test")!.Name = "Newer edit";
            _editor.MarkDirty();
            await _editor.ReloadAsync(); // A discard cannot race an in-flight save.
            Assert.Equal("Newer edit", _editor.FindChannel("test")!.Name);
        }
        finally { release.Set(); }

        Assert.True(await save);
        Assert.False(_editor.IsBusy);
        Assert.True(_editor.IsDirty);
        Assert.Equal("Snapshot", _provider.Current.Channels.Single().Name);
        Assert.Equal("Newer edit", _editor.FindChannel("test")!.Name);
    }

    [Fact]
    public async Task Invalid_save_notifies_and_remains_editable_then_can_be_discarded()
    {
        _editor.Working.Channels.Add(new ChannelConfig { Id = "test", Name = "Duplicate", Number = 1 });
        _editor.MarkDirty();
        int notifications = 0;
        _editor.Changed += () => notifications++;
        Assert.False(await _editor.SaveAsync());
        Assert.False(_editor.IsBusy);
        Assert.True(_editor.IsDirty);
        Assert.NotEmpty(_editor.Errors);
        Assert.True(notifications >= 2);

        var old = _editor.Working;
        await _editor.ReloadAsync();
        Assert.NotSame(old, _editor.Working);
        Assert.Single(_editor.Working.Channels);
        Assert.False(_editor.IsDirty);
        Assert.Empty(_editor.Errors);
    }

    [Fact]
    public async Task File_error_releases_busy_state_and_save_can_be_retried()
    {
        _editor.FindChannel("test")!.Name = "Changed";
        _editor.MarkDirty();
        using (var locked = new FileStream(_provider.ConfigPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(await _editor.SaveAsync());
            Assert.NotNull(_editor.SaveError);
            Assert.False(_editor.IsBusy);
            Assert.True(_editor.IsDirty);
        }
        Assert.True(await _editor.SaveAsync());
        Assert.Null(_editor.SaveError);
        Assert.False(_editor.IsDirty);
    }

    public void Dispose()
    {
        _provider.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
