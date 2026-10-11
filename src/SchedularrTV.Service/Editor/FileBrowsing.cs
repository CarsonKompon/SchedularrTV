namespace SchedularrTV.Service.Editor;

/// <summary>
/// The kinds of file the editor's pickers ask for, and where they open. The pickers use the system's own file dialog
/// (<see cref="NativeFileDialog"/>); the scheduler reads these paths itself, so they are the server's.
/// </summary>
public static class FileBrowsing
{
    public static readonly string[] Audio = [".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma"];
    public static readonly string[] Images = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"];
    public static readonly string[] Videos = [".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".mpg", ".mpeg", ".ts"];

    /// <summary>Where a picker opens: beside the current file, else the last folder used, else a sensible default.</summary>
    public static string? StartFolder(string? current, string? last, Environment.SpecialFolder fallback)
    {
        if (!string.IsNullOrWhiteSpace(current) && Path.IsPathFullyQualified(current))
        {
            string? beside = Directory.Exists(current) ? current : Path.GetDirectoryName(current);
            if (beside is not null && Directory.Exists(beside))
            {
                return beside;
            }
        }

        if (last is not null && Directory.Exists(last))
        {
            return last;
        }

        string special = Environment.GetFolderPath(fallback);
        return Directory.Exists(special) ? special : null;
    }
}
