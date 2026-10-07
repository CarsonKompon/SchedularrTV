using System.Collections.Concurrent;
using System.Text.Json;

namespace SchedularrTV.News;

/// <summary>What the news page needs to know about its show's look (sent with every state, so changes show at once).</summary>
public sealed record NewsPageConfig(
    string Id,
    string Name,
    string Theme,
    string Accent,
    bool ShowImages,
    bool ShowSidebar,
    bool ShowTicker,
    string? TickerMessage,
    string Location,
    string? TimeZone,
    bool TwelveHourClock,
    IReadOnlyList<string> Desks,
    bool Traffic,
    bool Preview)
{
    public static NewsPageConfig For(NewsShow show, bool traffic, bool preview) => new(
        show.Id,
        show.Name,
        show.Theme.ToString(),
        NewsStore.IsColour(show.AccentColor) ? show.AccentColor : "#d7263d",
        show.ShowImages,
        show.ShowSidebar,
        show.ShowTicker,
        show.TickerMessage,
        show.Location,
        show.TimeZone,
        show.TwelveHourClock,
        [.. show.Desks.Select(desk => desk.ToString().ToLowerInvariant())],
        traffic,
        preview);
}

/// <summary>The news page: one self-contained HTML file (in the plugin), every look in it.</summary>
public static class NewsPage
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly Lazy<string> Template = new(() =>
    {
        using Stream stream = typeof(NewsPage).Assembly.GetManifestResourceStream("news.html")
            ?? throw new InvalidOperationException("The news page is missing from the plugin.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>The page for a show. Its id goes in as JSON, which escapes &lt; and &gt;, so nothing in it can end the script.</summary>
    public static string Html(string id, bool preview) =>
        Template.Value.Replace("__BOOT__", JsonSerializer.Serialize(new { id, preview }, Json), StringComparison.Ordinal);
}

/// <summary>
/// The shows being edited, before they're saved, so the editor's preview can show the changes: each editor keeps its
/// draft here under a key of its own, and the preview page asks for it by that key.
/// </summary>
public sealed class NewsPreviews
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, NewsShow Show)> _drafts = new(StringComparer.Ordinal);

    public void Set(string key, NewsShow show)
    {
        NewsShow copy = show.Copy();
        NewsStore.Tidy(copy);
        copy.Id = key;
        _drafts[key] = (DateTimeOffset.UtcNow, copy);

        foreach (var stale in _drafts.Where(draft => DateTimeOffset.UtcNow - draft.Value.At > TimeSpan.FromHours(2)).ToList())
        {
            _drafts.TryRemove(stale.Key, out _);
        }
    }

    public NewsShow? Get(string key) => _drafts.TryGetValue(key, out var draft) ? draft.Show : null;
}
