using SchedularrTV.Core.Configuration;
using SchedularrTV.Service.Writing;

namespace SchedularrTV.Overlays;

/// <summary>What an AI writer is asked for an AI-written text layer: a caption for the programme it's over.</summary>
public static class OverlayWriting
{
    public const string Source = "overlays";

    /// <summary>Only programmes starting this soon are queued for writing; later ones are written as they get closer.</summary>
    public static readonly TimeSpan Lookahead = TimeSpan.FromHours(36);

    private const int OverviewLength = 320;

    /// <summary>A description for the editor's "Try it", in place of a real episode's.</summary>
    public const string SampleOverview = "Two friends throw a surprise party for their boss, and everything that can go wrong does.";

    public static bool Writes(OverlayLayer layer) => layer.Kind == OverlayLayerKinds.Text && layer.Writer is not null;

    /// <param name="tokens">What the overlay's {tokens} read as on this programme.</param>
    /// <param name="overview">What the episode is about, if known.</param>
    /// <param name="identity">The programme (its library id), so each episode gets its own caption and repeats share it.</param>
    public static AiTextJob Job(
        Overlay overlay,
        OverlayLayer layer,
        IReadOnlyDictionary<string, string> tokens,
        string? overview,
        string identity,
        DateTimeOffset due)
    {
        AiTextConfig writer = layer.Writer ?? new AiTextConfig();
        string channel = tokens.GetValueOrDefault("channel") ?? string.Empty;
        List<AiTextFact> facts = [new("Channel", channel)];

        if (tokens.GetValueOrDefault("title") is { Length: > 0 } title)
        {
            string details = string.Join(", ", new[]
            {
                tokens.GetValueOrDefault("episode") is { Length: > 0 } episode ? $"episode \"{episode}\"" : null,
                tokens.GetValueOrDefault("season_episode") is { Length: > 0 } number ? number : null,
                tokens.GetValueOrDefault("year") is { Length: > 0 } year ? $"from {year}" : null
            }.OfType<string>());
            facts.Add(new AiTextFact("On now", details.Length > 0 ? $"{title}, {details}" : title));
        }

        if (writer.UseDescriptions && !string.IsNullOrWhiteSpace(overview))
        {
            facts.Add(new AiTextFact("What this episode is about", Shorten(overview)));
        }

        if (tokens.GetValueOrDefault("next") is { Length: > 0 } next)
        {
            string at = tokens.GetValueOrDefault("next_time") is { Length: > 0 } time ? $" at {time}" : string.Empty;
            facts.Add(new AiTextFact("On next", next + at));
        }

        return new AiTextJob
        {
            Config = writer,
            Use = AiTextUse.Shown,
            Channel = channel,
            Purpose = "an on-screen caption shown over the programme" +
                (string.IsNullOrWhiteSpace(writer.Prompt) ? ": a short, fun line about what's on" : string.Empty),
            Fallback = OverlayRenderer.Fill(layer.Text, tokens),
            Facts = facts,
            MaxWords = 8,
            Seed = $"{identity}|{overlay.Name}|{layer.Name}",
            Source = Source,
            Due = due
        };
    }

    private static string Shorten(string text)
    {
        string flat = string.Join(' ', text.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length <= OverviewLength)
        {
            return flat;
        }

        int cut = flat.LastIndexOf(". ", OverviewLength, StringComparison.Ordinal);
        return cut > OverviewLength / 2 ? flat[..(cut + 1)] : flat[..OverviewLength].TrimEnd() + "…";
    }
}
