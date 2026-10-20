using SkiaSharp;

namespace SchedularrTV.Service.Editor;

/// <summary>
/// The font files behind font family names: the computer's installed fonts (system-wide and per-user), plus fonts
/// kept in the data folder (<c>assets/fonts</c>) from imported template packages, which renderers use when a family
/// isn't installed. Lets a template package carry the fonts its text is drawn with.
/// </summary>
public static class FontFiles
{
    private static readonly string[] Extensions = [".ttf", ".otf", ".ttc", ".otc"];
    private static readonly Lock Gate = new();
    private static Dictionary<string, List<string>>? _installed;
    private static Dictionary<string, List<string>>? _kept;
    private static string? _keptFolder;

    /// <summary>Where kept fonts live: <c>&lt;data folder&gt;/assets/fonts</c>. Set as the scheduler starts.</summary>
    public static string? KeptFolder
    {
        get => _keptFolder;
        set
        {
            lock (Gate)
            {
                _keptFolder = value;
                _kept = null;
            }
        }
    }

    /// <summary>The <see cref="TemplateAssets"/> category fonts are kept under.</summary>
    public const string Category = "fonts";

    /// <summary>Has the kept fonts read again, after some were added.</summary>
    public static void Changed()
    {
        lock (Gate)
        {
            _kept = null;
        }
    }

    /// <summary>Every font file of the family (all its weights and styles), installed or kept; empty when there's none.</summary>
    public static IReadOnlyList<string> FilesFor(string? family)
    {
        if (string.IsNullOrWhiteSpace(family))
        {
            return [];
        }

        string name = family.Trim();
        List<string> found = [];
        if (Installed().TryGetValue(name, out List<string>? installed)) found.AddRange(installed);
        if (Kept().TryGetValue(name, out List<string>? kept)) found.AddRange(kept);
        return [.. found.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The families of the kept fonts, sorted; font pickers offer them alongside the installed ones.</summary>
    public static IReadOnlyList<string> KeptFamilies() => [.. Kept().Keys.Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// A typeface for the family from the kept fonts, closest to the weight asked for; null when none is kept. The
    /// caller owns (disposes) it.
    /// </summary>
    public static SKTypeface? KeptTypeface(string? family, bool bold)
        => KeptTypeface(family, bold ? 700 : 400, false);

    public static SKTypeface? KeptTypeface(string? family, int weight, bool italic)
    {
        if (string.IsNullOrWhiteSpace(family) || !Kept().TryGetValue(family.Trim(), out List<string>? files))
        {
            return null;
        }

        SKTypeface? best = null;
        int bestScore = int.MaxValue;
        int wanted = Math.Clamp(weight, 100, 900);
        foreach (string file in files)
        {
            SKTypeface? typeface = Load(file);
            if (typeface is null)
            {
                continue;
            }

            // Match the requested slant first, then the nearest available weight.
            bool slanted = typeface.FontSlant != SKFontStyleSlant.Upright;
            int score = Math.Abs(typeface.FontWeight - wanted) + (slanted == italic ? 0 : 1000);
            if (score < bestScore)
            {
                best?.Dispose();
                best = typeface;
                bestScore = score;
            }
            else
            {
                typeface.Dispose();
            }
        }

        return best;
    }

    /// <summary>Shared style resolution for installed fonts and fonts bundled with templates. Caller owns the result.</summary>
    public static SKTypeface Typeface(string family, int weight, bool italic)
    {
        using var style = new SKFontStyle(Math.Clamp(weight, 100, 900), (int)SKFontStyleWidth.Normal,
            italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        var face = SKTypeface.FromFamilyName(family, style);
        if (KeptTypeface(family, weight, italic) is { } kept)
        {
            int Score(SKTypeface candidate) => Math.Abs(candidate.FontWeight - weight)
                + ((candidate.FontSlant != SKFontStyleSlant.Upright) == italic ? 0 : 1000);
            if (!IsFamily(face, family) || Score(kept) < Score(face!))
            {
                face?.Dispose();
                return kept;
            }
            kept.Dispose();
        }
        return face ?? SKTypeface.FromFamilyName(null, style);
    }

    /// <summary>Whether <paramref name="typeface"/> really is <paramref name="family"/> (not the fallback a missing family gets).</summary>
    public static bool IsFamily(SKTypeface? typeface, string? family) =>
        typeface is not null && family is not null && string.Equals(typeface.FamilyName, family.Trim(), StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, List<string>> Installed()
    {
        lock (Gate)
        {
            // Read once: listing every installed font is slow, and it's only needed when exporting.
            return _installed ??= Index(SystemFolders());
        }
    }

    private static Dictionary<string, List<string>> Kept()
    {
        lock (Gate)
        {
            return _kept ??= _keptFolder is { } folder ? Index([folder]) : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, List<string>> Index(IEnumerable<string> folders)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                    .Where(file => Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string file in files)
            {
                using SKTypeface? typeface = Load(file);
                if (typeface?.FamilyName is not { Length: > 0 } family)
                {
                    continue;
                }

                if (!index.TryGetValue(family, out List<string>? list))
                {
                    index[family] = list = [];
                }

                list.Add(file);
            }
        }

        return index;
    }

    private static SKTypeface? Load(string file)
    {
        try
        {
            return SKTypeface.FromFile(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static IEnumerable<string> SystemFolders()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/System/Library/Fonts";
            yield return "/Library/Fonts";
            yield return Path.Combine(home, "Library", "Fonts");
        }
        else
        {
            yield return "/usr/share/fonts";
            yield return "/usr/local/share/fonts";
            yield return Path.Combine(home, ".fonts");
            yield return Path.Combine(home, ".local", "share", "fonts");
        }
    }
}
