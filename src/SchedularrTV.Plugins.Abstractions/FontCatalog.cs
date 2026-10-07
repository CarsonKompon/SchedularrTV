namespace SchedularrTV.Plugins.Editor;

/// <summary>
/// The font families installed on the computer running the scheduler (including fonts installed for the current user
/// only), for font pickers. Pages drawn by the scheduler's browser and its renderer can use any of them.
/// </summary>
public interface IFontCatalog
{
    /// <summary>Installed families, sorted by name.</summary>
    IReadOnlyList<string> Families { get; }
}
