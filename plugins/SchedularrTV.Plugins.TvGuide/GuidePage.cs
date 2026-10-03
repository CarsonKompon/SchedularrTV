using System.Text.Json;

namespace SchedularrTV.TvGuide;

/// <summary>What the guide page needs to know about its guide (also what the editor's live preview sends it).</summary>
public sealed record GuidePageConfig(
    string Id,
    string Name,
    string Theme,
    double HoursShown,
    double ScrollSpeed,
    int RowHeight,
    double FontScale,
    bool ShowLogos,
    bool Clock24,
    IReadOnlyList<int> Hidden,
    string? Background,
    string? RowColor,
    string? TextColor,
    string? AccentColor,
    string Top,
    int TopHeight,
    FrameRect? Window,
    int? WindowChannel,
    bool CycleChannels,
    int CycleMinutes,
    string Message,
    string AdsQuery,
    bool Preview)
{
    public static GuidePageConfig For(GuideStation guide, bool preview) => new(
        guide.Id,
        guide.Name,
        guide.Theme.ToString(),
        guide.HoursShown,
        guide.ScrollSpeed,
        guide.RowHeight,
        guide.FontScale,
        guide.ShowLogos,
        guide.TwentyFourHourClock,
        guide.HiddenChannels,
        guide.Background,
        guide.RowColor,
        guide.TextColor,
        guide.AccentColor,
        guide.Top.ToString(),
        guide.TopHeight,
        guide.Window,
        guide.WindowChannel,
        guide.CycleChannels,
        guide.CycleMinutes,
        guide.Message,
        guide.AdsQuery,
        preview);
}

/// <summary>The guide page: one self-contained HTML file (in the plugin) with the guide's settings written in.</summary>
public static class GuidePage
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly Lazy<string> Template = new(() =>
    {
        using Stream stream = typeof(GuidePage).Assembly.GetManifestResourceStream("guide.html")
            ?? throw new InvalidOperationException("The guide page is missing from the plugin.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>
    /// The page for a guide. The settings go in as JSON, which escapes &lt; and &gt;, so nothing in them can end the
    /// script early.
    /// </summary>
    public static string Html(GuidePageConfig config) =>
        Template.Value.Replace("__CONFIG__", JsonSerializer.Serialize(config, Json), StringComparison.Ordinal);
}
