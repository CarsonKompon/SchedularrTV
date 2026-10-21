using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SchedularrTV.Service.Editor;

/// <summary>
/// What a template package (a <c>.zip</c> exported from a generated promo, overlay or credit squeeze look) says about
/// itself, in its <c>schedularrtv-package.json</c>.
/// </summary>
public sealed class TemplatePackageManifest
{
    public int Format { get; set; } = TemplatePackages.Format;

    /// <summary>What the item is, e.g. <c>generated-promo</c>; an import only takes packages of its own kind.</summary>
    public string Kind { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? AppVersion { get; set; }

    public DateTimeOffset Created { get; set; }

    /// <summary>The program a package's template runs on the importing computer, if any, so it can be shown before importing.</summary>
    public string? Runs { get; set; }

    /// <summary>Font files in the package (e.g. <c>fonts/1/Inter-Bold.ttf</c>) its text is drawn with, kept on import.</summary>
    public List<string> Fonts { get; set; } = [];

    /// <summary>The template itself, its file paths pointing into the package (<see cref="TemplatePackages.Prefix"/>).</summary>
    public JsonElement Item { get; set; }
}

/// <summary>
/// One way to export something, offered in a choice before the download (e.g. a template with or without the voice pack
/// it uses): its button, a line under it (an estimated size, say), and what it writes.
/// </summary>
/// <param name="Export">Fills the package (and finishes it); returns the name for the download.</param>
public sealed record TemplateExportOption(string Label, string? Detail, Func<TemplatePackageWriter, Task<string>> Export, bool Primary = false);

/// <summary>
/// Template packages: zips holding a template and every file it uses (pictures, videos, songs, and an external
/// generator's folder), so templates can be shared between computers and keep working there.
/// </summary>
public sealed partial class TemplatePackages(string dataDirectory)
{
    public const int Format = 1;

    /// <summary>Marks a path as a file inside the package, e.g. <c>package:assets/1/logo.png</c>.</summary>
    public const string Prefix = "package:";

    public const string ManifestName = "schedularrtv-package.json";

    /// <summary>Generous for a template with a few videos and songs, but bounded.</summary>
    public const long MaxImportBytes = 4L * 1024 * 1024 * 1024;

    private static readonly TimeSpan KeepExports = TimeSpan.FromHours(2);

    public static JsonSerializerOptions Json { get; } = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private string ExportFolder => Path.Combine(Path.GetFullPath(dataDirectory), "exports");

    private string ImportFolder => Path.Combine(Path.GetFullPath(dataDirectory), "imports");

    /// <summary>A new, empty package; fill it, <see cref="TemplatePackageWriter.Finish"/> it, then dispose it and download it.</summary>
    public TemplatePackageWriter Create()
    {
        Clean(ExportFolder);
        Directory.CreateDirectory(ExportFolder);
        string id = Guid.NewGuid().ToString("N");
        return new TemplatePackageWriter(id, Path.Combine(ExportFolder, id + ".zip"));
    }

    /// <summary>A finished export's file; null for an unknown or malformed id, so no other file can be asked for.</summary>
    public string? Exported(string id)
    {
        if (!ExportId().IsMatch(id))
        {
            return null;
        }

        string path = Path.Combine(ExportFolder, id + ".zip");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Where the browser downloads an export from.</summary>
    public static string DownloadUrl(string id, string fileName) =>
        $"api/template-packages/{id}?name={Uri.EscapeDataString(fileName)}";

    /// <summary>A file name for the download, from the template's name.</summary>
    public static string FileName(string name) => TemplateAssets.SafeName(name, "template") + ".zip";

    /// <summary>
    /// Roughly how big a package of these files would be: each existing file counted once, at its size on disk (media
    /// isn't compressed in a package; fonts and program folders are, so they come out somewhat smaller).
    /// </summary>
    public static long EstimateBytes(IEnumerable<string?> files)
    {
        long total = 0;
        foreach (string full in files.OfType<string>().Select(path => path.Trim().Trim('"'))
                     .Where(path => path.Length > 0 && System.IO.Path.IsPathFullyQualified(path))
                     .Select(System.IO.Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(full)) total += new FileInfo(full).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return total;
    }

    /// <summary>A size for people: "about 12.3 MB", "under 1 MB".</summary>
    public static string DescribeSize(long bytes) => bytes switch
    {
        < 1024L * 1024 => "under 1 MB",
        < 1024L * 1024 * 1024 => $"about {bytes / 1024d / 1024:0.#} MB",
        _ => $"about {bytes / 1024d / 1024 / 1024:0.##} GB"
    };

    /// <summary>Reads an uploaded package (saved to a temporary file first, so it can be read in any order).</summary>
    public async Task<TemplatePackageReader> OpenAsync(Stream upload, CancellationToken cancellationToken = default)
    {
        Clean(ImportFolder);
        Directory.CreateDirectory(ImportFolder);
        string path = Path.Combine(ImportFolder, Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await upload.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            return TemplatePackageReader.Open(path);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    private static void Clean(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(folder))
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > KeepExports)
            {
                TryDelete(file);
            }
        }
    }

    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use or locked; the next clean-up gets it.
        }
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex ExportId();
}

/// <summary>A package being written. Every path handed to <see cref="Asset"/> is copied in once.</summary>
public sealed class TemplatePackageWriter : IDisposable
{
    private readonly ZipArchive _zip;
    private readonly Dictionary<string, string> _assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _fontFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _fonts = [];
    private bool _disposed;

    internal TemplatePackageWriter(string id, string path)
    {
        Id = id;
        Path = path;
        _zip = new ZipArchive(new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None), ZipArchiveMode.Create, leaveOpen: false);
    }

    public string Id { get; }

    public string Path { get; }

    public bool Finished { get; private set; }

    /// <summary>
    /// Copies the file into the package and returns the path to write in the template instead. Empty values, and
    /// paths that aren't existing files, come back as they were.
    /// </summary>
    public string? Asset(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        string trimmed = path.Trim().Trim('"');
        if (trimmed.StartsWith(TemplatePackages.Prefix, StringComparison.Ordinal) || !System.IO.Path.IsPathFullyQualified(trimmed) || !File.Exists(trimmed))
        {
            return path;
        }

        string full = System.IO.Path.GetFullPath(trimmed);
        if (_assets.TryGetValue(full, out string? existing))
        {
            return existing;
        }

        string entry = $"assets/{_assets.Count + 1}/{TemplateAssets.SafeName(System.IO.Path.GetFileName(full), "file")}";
        // Pictures, videos and songs are compressed already.
        _zip.CreateEntryFromFile(full, entry, CompressionLevel.NoCompression);
        string reference = TemplatePackages.Prefix + entry;
        _assets[full] = reference;
        return reference;
    }

    /// <summary>
    /// Copies every font file of each family (all its weights and styles) into the package, so text looks the same
    /// where it's imported. Families with no file found (or empty names) are skipped. Returns the families included.
    /// </summary>
    public IReadOnlyList<string> AddFonts(IEnumerable<string?> families)
    {
        List<string> included = [];
        foreach (string family in families.OfType<string>().Select(name => name.Trim()).Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyList<string> files = FontFiles.FilesFor(family);
            foreach (string file in files)
            {
                string full = System.IO.Path.GetFullPath(file);
                if (!_fontFiles.Add(full))
                {
                    continue;
                }

                string entry = $"fonts/{_fonts.Count + 1}/{TemplateAssets.SafeName(System.IO.Path.GetFileName(full), "font.ttf")}";
                _zip.CreateEntryFromFile(full, entry, CompressionLevel.Optimal);
                _fonts.Add(entry);
            }

            if (files.Count > 0)
            {
                included.Add(family);
            }
        }

        return included;
    }

    /// <summary>
    /// Copies a folder's files in under <paramref name="entry"/>, leaving out what its .gitignore files exclude.
    /// Returns how many files went in.
    /// </summary>
    public int AddFolder(string folder, string entry)
    {
        int count = 0;
        string root = System.IO.Path.GetFullPath(folder);
        foreach (string relative in GitIgnore.Files(root))
        {
            string source = System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            _zip.CreateEntryFromFile(source, $"{entry.TrimEnd('/')}/{relative}", CompressionLevel.Optimal);
            count++;
        }

        return count;
    }

    /// <summary>Writes the manifest: the template (its paths already swapped for <see cref="Asset"/>'s) and what it is.</summary>
    public void Finish<T>(string kind, string name, T item, string? runs = null)
    {
        var manifest = new TemplatePackageManifest
        {
            Kind = kind,
            Name = name,
            AppVersion = SchedularrTV.Service.Hosting.AppRuntime.Version,
            Created = DateTimeOffset.UtcNow,
            Runs = runs,
            Fonts = [.. _fonts],
            Item = JsonSerializer.SerializeToElement(item, TemplatePackages.Json)
        };
        ZipArchiveEntry entry = _zip.CreateEntry(TemplatePackages.ManifestName, CompressionLevel.Optimal);
        using (Stream stream = entry.Open())
        {
            JsonSerializer.Serialize(stream, manifest, TemplatePackages.Json);
        }

        Finished = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _zip.Dispose();
        if (!Finished)
        {
            TemplatePackages.TryDelete(Path);
        }
    }
}

/// <summary>An uploaded package being imported. Disposing it removes the uploaded copy.</summary>
public sealed class TemplatePackageReader : IDisposable
{
    private readonly ZipArchive _zip;
    private readonly string _path;
    private readonly Dictionary<string, string?> _kept = new(StringComparer.Ordinal);

    private TemplatePackageReader(string path, ZipArchive zip, TemplatePackageManifest manifest)
    {
        _path = path;
        _zip = zip;
        Manifest = manifest;
    }

    public TemplatePackageManifest Manifest { get; }

    internal static TemplatePackageReader Open(string path)
    {
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(path);
        }
        catch (InvalidDataException)
        {
            throw new InvalidOperationException("That isn't a zip file.");
        }

        try
        {
            ZipArchiveEntry entry = zip.GetEntry(TemplatePackages.ManifestName)
                ?? throw new InvalidOperationException("That zip isn't a SchedularrTV template package (it has no schedularrtv-package.json).");
            TemplatePackageManifest manifest;
            using (Stream stream = entry.Open())
            {
                manifest = JsonSerializer.Deserialize<TemplatePackageManifest>(stream, TemplatePackages.Json)
                    ?? throw new InvalidOperationException("The package's description is empty.");
            }

            if (manifest.Format > TemplatePackages.Format)
            {
                throw new InvalidOperationException("That package was made by a newer SchedularrTV; update to import it.");
            }

            if (manifest.Item.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("The package doesn't hold a template.");
            }

            return new TemplatePackageReader(path, zip, manifest);
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    /// <summary>The template, as exported (its paths still point into the package).</summary>
    public T Item<T>() where T : class =>
        Manifest.Item.Deserialize<T>(TemplatePackages.Json) ?? throw new InvalidOperationException("The package's template is empty.");

    /// <summary>
    /// For a path into the package, keeps that file in the data folder and returns its path there (null when the
    /// package doesn't have it). Any other value comes back as it was.
    /// </summary>
    public async Task<string?> AssetAsync(string? path, TemplateAssets assets, string category, CancellationToken cancellationToken = default)
    {
        if (path is null || !path.StartsWith(TemplatePackages.Prefix, StringComparison.Ordinal))
        {
            return path;
        }

        if (_kept.TryGetValue(path, out string? kept))
        {
            return kept;
        }

        string name = path[TemplatePackages.Prefix.Length..];
        ZipArchiveEntry? entry = name.StartsWith("assets/", StringComparison.Ordinal) ? _zip.GetEntry(name) : null;
        if (entry is null || entry.FullName.EndsWith('/'))
        {
            _kept[path] = null;
            return null;
        }

        await using (Stream stream = entry.Open())
        {
            kept = await assets.KeepAsync(category, entry.Name, stream, cancellationToken).ConfigureAwait(false);
        }

        _kept[path] = kept;
        return kept;
    }

    /// <summary>
    /// Keeps the package's fonts in the data folder (<c>assets/fonts</c>), where renderers find families that aren't
    /// installed. Returns the font files kept.
    /// </summary>
    public async Task<IReadOnlyList<string>> KeepFontsAsync(TemplateAssets assets, CancellationToken cancellationToken = default)
    {
        List<string> kept = [];
        foreach (string name in Manifest.Fonts.Distinct(StringComparer.Ordinal))
        {
            ZipArchiveEntry? entry = name.StartsWith("fonts/", StringComparison.Ordinal) ? _zip.GetEntry(name) : null;
            if (entry is null || entry.FullName.EndsWith('/'))
            {
                continue;
            }

            await using Stream stream = entry.Open();
            kept.Add(await assets.KeepAsync(FontFiles.Category, entry.Name, stream, cancellationToken).ConfigureAwait(false));
        }

        if (kept.Count > 0)
        {
            FontFiles.Changed();
        }

        return kept;
    }

    /// <summary>Whether the package has files under <paramref name="entry"/> (a folder in it).</summary>
    public bool HasFolder(string entry) =>
        _zip.Entries.Any(candidate => candidate.FullName.StartsWith(entry.TrimEnd('/') + "/", StringComparison.Ordinal));

    /// <summary>Unpacks the files under <paramref name="entry"/> into <paramref name="destination"/>; returns how many.</summary>
    public int ExtractFolder(string entry, string destination)
    {
        string prefix = entry.TrimEnd('/') + "/";
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;
        int count = 0;
        foreach (ZipArchiveEntry file in _zip.Entries)
        {
            if (!file.FullName.StartsWith(prefix, StringComparison.Ordinal) || file.FullName.EndsWith('/'))
            {
                continue;
            }

            string relative = file.FullName[prefix.Length..].Replace('\\', '/');
            string target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            // A crafted entry ("../../x") mustn't land outside the folder.
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"The package has an unsafe file path: {file.FullName}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            file.ExtractToFile(target, overwrite: true);
            count++;
        }

        return count;
    }

    public void Dispose()
    {
        _zip.Dispose();
        TemplatePackages.TryDelete(_path);
    }
}
