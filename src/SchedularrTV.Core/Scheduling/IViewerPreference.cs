using SchedularrTV.Core.Configuration;

namespace SchedularrTV.Core.Scheduling;

/// <summary>
/// How much more (above 1) or less (below 1) often each show should come round on a channel, from what its
/// viewers watch. Used by viewer-aware scheduling (<see cref="ChannelConfig.ViewerAware"/>).
/// </summary>
public interface IViewerPreference
{
    /// <summary>
    /// Factors by show title (a series by its name, a film by its title), case-insensitive; shows left out are 1.
    /// Null when the channel doesn't use viewer-aware scheduling or hasn't passed its thresholds yet.
    /// </summary>
    IReadOnlyDictionary<string, double>? TitleFactors(ChannelConfig channel);
}

/// <summary>Turns viewer factors into the weights the planner's rotations use.</summary>
public static class ViewerWeights
{
    /// <summary>Weights are scaled by this, so a show can be rested below an ordinary weight of 1.</summary>
    public const int Scale = 4;

    /// <summary>A factor this close to 1 is treated as no change.</summary>
    public const double Neutral = 0.05;

    /// <summary>The factor for a title, 1 when it has none.</summary>
    public static double Of(IReadOnlyDictionary<string, double>? factors, string title) =>
        factors is not null && factors.TryGetValue(title, out double factor) && double.IsFinite(factor) && factor > 0 ? factor : 1;

    /// <summary>Whether any of <paramref name="titles"/> would be moved at all.</summary>
    public static bool Moves(IReadOnlyDictionary<string, double>? factors, IEnumerable<string> titles) =>
        factors is { Count: > 0 } && titles.Any(title => Math.Abs(Of(factors, title) - 1) > Neutral);

    /// <summary>A title's rotation weight: its configured weight, scaled and moved by its factor; never below 1.</summary>
    public static int Weight(int configured, double factor) =>
        (int)Math.Clamp(Math.Round(Math.Max(1, configured) * Scale * factor), 1, 10_000);
}
