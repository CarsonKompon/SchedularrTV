namespace SchedularrTV.Core.Media;

/// <summary>What chapter names say about a programme, whichever source the chapters came from.</summary>
public static class ChapterMarkers
{
    /// <summary>
    /// Where the chapter named for the end credits ("Credits", "End Credits", "Outro", "Ending", "ED") starts, among
    /// chapters (start, name) of an item this long: in its second half, so "Opening Credits" never counts.
    /// </summary>
    public static TimeSpan? CreditsStart(TimeSpan runtime, IEnumerable<(TimeSpan Start, string? Name)> chapters)
    {
        foreach ((TimeSpan start, string? named) in chapters)
        {
            string name = named?.Trim() ?? string.Empty;
            bool credits = name.Contains("credit", StringComparison.OrdinalIgnoreCase) || name.Contains("outro", StringComparison.OrdinalIgnoreCase)
                           || name.Equals("ending", StringComparison.OrdinalIgnoreCase) || name.Equals("ED", StringComparison.Ordinal);
            if (credits && !name.Contains("open", StringComparison.OrdinalIgnoreCase) && runtime > TimeSpan.Zero && start > runtime / 2 && start < runtime)
            {
                return start;
            }
        }

        return null;
    }
}
