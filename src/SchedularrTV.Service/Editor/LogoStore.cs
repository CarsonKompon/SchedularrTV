using Microsoft.Extensions.Options;

namespace SchedularrTV.Service.Editor;

/// <summary>
/// Stores uploaded channel logos on disk and hands back the URL the guide should advertise.
/// </summary>
/// <remarks>
/// ErsatzTV Next copies the logo string straight into <c>tvg-logo</c> and the XMLTV
/// <c>&lt;icon src&gt;</c>, so an uploaded file has to be reachable over HTTP rather than by path.
/// </remarks>
public sealed class LogoStore(IOptions<SchedulerServiceOptions> options, SchedulerConfigProvider config)
{
    public const string RequestPath = "/logos";

    /// <summary>Generous enough for a high-resolution logo, small enough to bound a hostile upload.</summary>
    public const long MaxBytes = 4 * 1024 * 1024;

    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".gif", ".webp"];

    public static string Accept => string.Join(',', Extensions);

    /// <summary>
    /// Absolute path of the folder served at <see cref="RequestPath"/>, resolved against the config
    /// file so logos always travel with the schedule that references them.
    /// </summary>
    public string Root
    {
        get
        {
            string configured = options.Value.LogoPath;

            return Path.GetFullPath(Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(Path.GetDirectoryName(config.ConfigPath) ?? ".", configured));
        }
    }

    /// <summary>
    /// Saves a logo for a channel and returns the URL to record on it.
    /// </summary>
    /// <param name="baseUri">Origin the browser reached this app on, used so players can fetch the file.</param>
    public async Task<string> SaveAsync(
        string channelId,
        string fileName,
        Stream content,
        string baseUri,
        CancellationToken cancellationToken = default)
    {
        string extension = Path.GetExtension(fileName).ToLowerInvariant();

        if (!Extensions.Contains(extension))
        {
            throw new InvalidOperationException($"{extension} is not an image type this supports.");
        }

        using MemoryStream buffer = new();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        if (buffer.Length == 0)
        {
            throw new InvalidOperationException("That file is empty.");
        }

        byte[] bytes = buffer.ToArray();

        // The extension is attacker-controlled, so the bytes decide whether this is really an image.
        if (!IsImage(bytes))
        {
            throw new InvalidOperationException("That file is not a PNG, JPEG, GIF or WebP image.");
        }

        string root = Root;
        Directory.CreateDirectory(root);

        // Named by the app rather than the upload, so a crafted file name cannot escape the folder. Every upload gets a
        // file of its own: a name made only from the channel id (ch8.png) was shared by whichever channel held that id
        // later, so an upload to one channel replaced another channel's logo. The old file is let go by the caller
        // with Release, once nothing uses it.
        string safeId = SafeId(channelId);
        string name = $"{safeId}-{Guid.NewGuid().ToString("N")[..8]}{extension}";

        string path = Path.Combine(root, name);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);

        return $"{baseUri.TrimEnd('/')}{RequestPath}/{name}";
    }

    /// <summary>
    /// Deletes the stored file <paramref name="logo"/> points at, unless one of <paramref name="stillUsed"/> (the other
    /// channels' logos, and the saved schedule's, which a discard goes back to) points at the same file. Does nothing
    /// for an external URL or a file that's already gone.
    /// </summary>
    public void Release(string? logo, IEnumerable<string?> stillUsed)
    {
        if (StoredFileName(logo) is not { } name
            || stillUsed.Any(other => string.Equals(StoredFileName(other), name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        string root = Root;
        string path = Path.GetFullPath(Path.Combine(root, name));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover image is harmless; failing the edit over it would not be.
        }
    }

    /// <summary>True when the value points at a logo this app stores, rather than an external URL.</summary>
    public static bool IsStored(string? logo) => StoredFileName(logo) is not null;

    /// <summary>The file name in this app's logo folder that <paramref name="logo"/> points at, or null for anything else.</summary>
    public static string? StoredFileName(string? logo)
    {
        if (string.IsNullOrWhiteSpace(logo))
        {
            return null;
        }

        int at = logo.IndexOf(RequestPath + "/", StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return null;
        }

        string rest = logo[(at + RequestPath.Length + 1)..];
        int end = rest.IndexOfAny(['?', '#']);
        string name = Uri.UnescapeDataString(end < 0 ? rest : rest[..end]);

        return name.Length > 0 && name == Path.GetFileName(name) && Extensions.Contains(Path.GetExtension(name).ToLowerInvariant())
            ? name
            : null;
    }

    private static string SafeId(string channelId)
    {
        string safe = new([.. channelId.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')]);

        return safe.Length > 0
            ? safe
            : throw new InvalidOperationException("The channel id has no characters usable in a file name.");
    }

    private static bool IsImage(byte[] b) =>
        (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)     // PNG
        || (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)                  // JPEG
        || (b.Length >= 6 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46)                  // GIF
        || (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46  // RIFF
            && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50);             // WEBP
}
