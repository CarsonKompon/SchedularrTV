using SchedularrTV.GeneratedPromos;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class PreviewFrameCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"preview-frames-{Guid.NewGuid():N}");
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    public PreviewFrameCacheTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private string Frame(string name, int bytes, TimeSpan age)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, Now - age);
        return path;
    }

    private string Strip(string name, int frames, int bytes, TimeSpan age)
    {
        string path = Path.Combine(_directory, name);
        Directory.CreateDirectory(path);
        for (int index = 0; index < frames; index++)
        {
            File.WriteAllBytes(Path.Combine(path, $"{index + 1:00000}.jpg"), new byte[bytes]);
        }

        Directory.SetLastWriteTimeUtc(path, Now - age);
        return path;
    }

    [Fact]
    public void Frames_unused_for_a_day_go()
    {
        string old = Frame("old.jpg", 10, TimeSpan.FromDays(2));
        string recent = Frame("recent.jpg", 10, TimeSpan.FromMinutes(5));
        string oldStrip = Strip("old-strip", 3, 10, TimeSpan.FromDays(3));

        PreviewFrameCache.Trim(_directory, Now);

        Assert.False(File.Exists(old));
        Assert.False(Directory.Exists(oldStrip));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void Over_budget_the_least_recently_used_go_until_well_under()
    {
        string oldest = Strip("a", 4, 100, TimeSpan.FromHours(5));
        string older = Frame("b.jpg", 400, TimeSpan.FromHours(4));
        string newer = Frame("c.jpg", 400, TimeSpan.FromHours(1));
        string newest = Strip("d", 4, 100, TimeSpan.FromMinutes(1));

        // 1,600 bytes against a 1,000 byte budget: trimmed to 800 or less, oldest first.
        long freed = PreviewFrameCache.Trim(_directory, Now, maxBytes: 1000, trimTo: 800);

        Assert.Equal(800, freed);
        Assert.False(Directory.Exists(oldest));
        Assert.False(File.Exists(older));
        Assert.True(File.Exists(newer));
        Assert.True(Directory.Exists(newest));
    }

    [Fact]
    public void Using_an_entry_keeps_it()
    {
        string frame = Frame("kept.jpg", 10, TimeSpan.FromDays(2));
        PreviewFrameCache.Touch(frame);

        PreviewFrameCache.Trim(_directory, DateTime.UtcNow);

        Assert.True(File.Exists(frame));
    }
}
