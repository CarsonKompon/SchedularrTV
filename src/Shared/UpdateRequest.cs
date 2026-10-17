using System.Text.Json;
using System.Text.Json.Serialization;

namespace SchedularrTV.Hosting;

/// <summary>
/// An update the user asked to install: what to download and where it goes. The server writes it into the data folder
/// (<see cref="PathFor"/>); the tray notices it, starts a copy of itself to install it (<see cref="ApplyArgument"/>) and
/// quits, so nothing of the app is running while its files are replaced. Shared (as a linked source file) by both.
/// </summary>
public sealed class UpdateRequest
{
    /// <summary>Starts the tray program as the installer for the request file named after it.</summary>
    public const string ApplyArgument = "--apply-update";

    /// <summary>The tray that's quitting for the update, waited for before anything is replaced.</summary>
    public const string WaitForArgument = "--wait-for";

    /// <summary>The app's own folder (or the .app on macOS) the update replaces.</summary>
    public const string AppRootArgument = "--app-root";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The version being installed, e.g. <c>0.2.1</c> or <c>0.2.1-nightly.58</c>.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary><c>release</c> or <c>nightly</c>.</summary>
    public string Channel { get; set; } = "release";

    /// <summary>The release's page, shown when something goes wrong so it can be installed by hand.</summary>
    public string? ReleaseUrl { get; set; }

    public List<UpdateDownload> Downloads { get; set; } = [];

    /// <summary>When it was asked for; an old request left behind (the tray wasn't running) isn't acted on.</summary>
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The arguments SchedularrTV was started with, to start it again the same way afterwards.</summary>
    public List<string> RelaunchArguments { get; set; } = [];

    /// <summary>Where the request is written: <c>update/request.json</c> in the data folder.</summary>
    public static string PathFor(string dataDirectory) => Path.Combine(dataDirectory, "update", "request.json");

    /// <summary>
    /// The folder an update replaces for a tray at <paramref name="trayPath"/>: the folder it's in, or on a Mac the .app
    /// around it (<c>SchedularrTV.app/Contents/MacOS/SchedularrTV</c>).
    /// </summary>
    public static string AppRootFor(string trayPath)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(trayPath))!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(Path.GetFileName(folder), "MacOS", StringComparison.Ordinal)
            && Path.GetDirectoryName(folder) is { } contents && string.Equals(Path.GetFileName(contents), "Contents", StringComparison.Ordinal)
            && Path.GetDirectoryName(contents) is { } bundle && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            return bundle;
        }

        return folder;
    }

    /// <summary>Where the installer keeps its log: <c>logs/update.log</c> in the data folder.</summary>
    public static string LogPathFor(string dataDirectory) => Path.Combine(dataDirectory, "logs", "update.log");

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The request in <paramref name="path"/>, or null when there's none (or it can't be read).</summary>
    public static UpdateRequest? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<UpdateRequest>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>One file an update downloads: the app for this computer, or one plugin.</summary>
public sealed class UpdateDownload
{
    public const string AppKind = "app";
    public const string PluginKind = "plugin";

    /// <summary><see cref="AppKind"/> or <see cref="PluginKind"/>.</summary>
    public string Kind { get; set; } = AppKind;

    /// <summary>The file's name in the release, e.g. <c>SchedularrTV-v0.2.1-build-win-x64.zip</c>.</summary>
    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    /// <summary>Its size in bytes, as the release says (0 when unknown).</summary>
    public long Size { get; set; }

    /// <summary>Its SHA-256 as the release publishes it (hex), checked after downloading; null when there's none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sha256 { get; set; }

    /// <summary>For a plugin: its id, and the folder it's installed in (replaced by the new one).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PluginId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PluginName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetDirectory { get; set; }
}
