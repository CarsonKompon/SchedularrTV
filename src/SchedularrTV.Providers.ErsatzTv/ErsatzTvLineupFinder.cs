namespace SchedularrTV.Providers.ErsatzTv;

/// <summary>
/// Finds lineups that already exist near an ErsatzTV Next program. Next has no config folder of its own: it's given a
/// <c>lineup.json</c> when started (<c>ersatztv config/lineup.json</c>), conventionally in a <c>config</c> folder beside
/// it, which is where <c>ersatztv add-lineup</c> puts one.
/// </summary>
public static class ErsatzTvLineupFinder
{
    public const string LineupFileName = "lineup.json";

    /// <summary>Folders holding a <c>lineup.json</c> around the program, nearest first.</summary>
    public static IReadOnlyList<string> FoldersNear(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || Path.GetDirectoryName(Path.GetFullPath(executablePath)) is not { } programFolder)
        {
            return [];
        }

        List<string> candidates = [programFolder, Path.Combine(programFolder, "config"), Path.Combine(programFolder, "etv", "config")];
        if (Directory.GetParent(programFolder) is { } parent)
        {
            candidates.Add(Path.Combine(parent.FullName, "config"));
            candidates.Add(Path.Combine(parent.FullName, "etv", "config"));
        }

        // Any other folder right beside the program with a lineup in it (e.g. "lineup", "data").
        try
        {
            candidates.AddRange(Directory.EnumerateDirectories(programFolder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return [.. candidates
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(folder => File.Exists(Path.Combine(folder, LineupFileName)))];
    }
}
