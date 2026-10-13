using System.Security.Cryptography;

namespace SchedularrTV.Service.Editor;

/// <summary>
/// Copies of the pictures, videos and songs that templates use (generated promos, overlays, credit squeezes), kept in
/// the data folder so a template keeps working when the original file is moved or deleted. Files are filed by their
/// content, so choosing the same file twice (or importing it in two packages) keeps one copy.
/// </summary>
public sealed class TemplateAssets(string dataDirectory)
{
    public const string FolderName = "assets";

    private const string ProgramsFolder = "programs";

    /// <summary><c>&lt;data folder&gt;/assets</c>.</summary>
    public string Root { get; } = Path.Combine(Path.GetFullPath(dataDirectory), FolderName);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Whether <paramref name="path"/> is already a kept copy (or anything else inside <see cref="Root"/>).</summary>
    public bool IsKept(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path.Trim()))
        {
            return false;
        }

        string full = Path.GetFullPath(path.Trim());
        return full.StartsWith(Path.TrimEndingDirectorySeparator(Root) + Path.DirectorySeparatorChar, PathComparison);
    }

    /// <summary>
    /// Keeps a copy of the file at <paramref name="path"/> under <paramref name="category"/> and returns the copy's
    /// path. A path that's already kept, isn't a full path, or isn't an existing file comes back unchanged.
    /// </summary>
    public async Task<string> KeepAsync(string category, string path, CancellationToken cancellationToken = default)
    {
        string trimmed = path.Trim().Trim('"');
        if (trimmed.Length == 0 || !Path.IsPathFullyQualified(trimmed) || !File.Exists(trimmed) || IsKept(trimmed))
        {
            return path;
        }

        await using var source = new FileStream(trimmed, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        return await KeepAsync(category, Path.GetFileName(trimmed), source, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Keeps <paramref name="content"/> as a file named <paramref name="fileName"/> and returns its path.</summary>
    public async Task<string> KeepAsync(string category, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        string folder = Path.Combine(Root, SafeName(category, "files"));
        Directory.CreateDirectory(folder);
        string partial = Path.Combine(folder, $".{Guid.NewGuid():N}.partial");
        try
        {
            string hash;
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                hash = Convert.ToHexStringLower(sha.GetHashAndReset())[..16];
            }

            // A folder per content keeps the original file name, which is what the editor shows.
            string target = Path.Combine(folder, hash, SafeName(fileName, "file"));
            if (File.Exists(target))
            {
                File.Delete(partial);
                return target;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                File.Move(partial, target);
            }
            catch (IOException) when (File.Exists(target))
            {
                // Kept by someone else at the same moment: same content, so theirs will do.
                File.Delete(partial);
            }

            return target;
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>A new, empty folder for an imported template's program, named after the template.</summary>
    public string NewProgramFolder(string name)
    {
        string folder = Path.Combine(Root, ProgramsFolder, $"{SafeName(name, "program")}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>A file or folder name with nothing that could leave the folder it's put in.</summary>
    public static string SafeName(string? name, string fallback)
    {
        string bare = Path.GetFileName((name ?? string.Empty).Replace('\\', '/').TrimEnd('/'));
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new string([.. bare.Select(c => invalid.Contains(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c)])
            .Trim().TrimEnd('.');
        if (safe.Length > 100)
        {
            string extension = Path.GetExtension(safe);
            safe = safe[..(100 - Math.Min(extension.Length, 20))] + extension[..Math.Min(extension.Length, 20)];
        }

        return safe.Length == 0 || safe is "." or ".." ? fallback : safe;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind; harmless.
        }
    }
}
