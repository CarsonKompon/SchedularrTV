namespace SchedularrTV.Core.Tests;

/// <summary>Cleans up the temporary folders tests work in.</summary>
internal static class TestFolders
{
    /// <summary>
    /// Deletes a test's temporary folder. On Windows a file can stay locked for a moment after the process that wrote
    /// it (an ffmpeg render, say) has been stopped, so this tries again for a few seconds; a folder that still can't
    /// go is left to the temp folder's own clean-up rather than failing a test whose assertions all passed.
    /// </summary>
    public static void Delete(string folder)
    {
        for (int attempt = 0; attempt < 20 && Directory.Exists(folder); attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(250);
            }
        }
    }
}
