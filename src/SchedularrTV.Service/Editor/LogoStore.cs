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

        // Named from the channel id rather than the upload, so a crafted file name cannot escape
        // the folder and re-uploading replaces the old logo instead of accumulating files.
        string safeId = SafeId(channelId);
        Remove(channelId);

        string path = Path.Combine(root, safeId + extension);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);

        // Cache-busted so a replaced logo does not keep showing the previous image.
        string stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        return $"{baseUri.TrimEnd('/')}{RequestPath}/{safeId}{extension}?v={stamp}";
    }

    /// <summary>Deletes any stored logo for a channel. Safe to call when none exists.</summary>
    public void Remove(string channelId)
    {
        string safeId = SafeId(channelId);
        string root = Root;

        foreach (string extension in Extensions)
        {
            string path = Path.Combine(root, safeId + extension);

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>True when the value points at a logo this app stores, rather than an external URL.</summary>
    public static bool IsStored(string? logo) =>
        logo is not null && logo.Contains(RequestPath + "/", StringComparison.OrdinalIgnoreCase);

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
