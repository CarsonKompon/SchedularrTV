using System.Runtime.InteropServices;
using SchedularrTV.Hosting;
using SchedularrTV.Service.Plugins;

namespace SchedularrTV.Service.Hosting;

/// <summary>Whether an update can be installed from the app, and if not, why (so the page offers the download instead).</summary>
public sealed record UpdateReadiness(bool CanInstall, string? Reason, IReadOnlyList<UpdateDownload> Downloads, IReadOnlyList<string> PluginsLeftAlone);

/// <summary>
/// Plans installing an update from the app: the download for this computer and a new copy of every installed plugin the
/// release has, and hands the plan to the tray (<see cref="UpdateRequest"/>), which quits, installs it and starts
/// SchedularrTV again. Only a copy started by the tray, from a downloaded build, can install itself: a development
/// build, or a server run on its own (Docker, a service), offers the download instead.
/// </summary>
public sealed class UpdateInstaller(AppRuntime runtime, PluginRegistry? plugins = null, ILogger<UpdateInstaller>? logger = null)
{
    /// <summary>Raised when an install has been handed to the tray.</summary>
    public event Action? Requested;

    private UpdateRequest? _pending;

    /// <summary>
    /// Set once an install has been handed to the tray, while the app closes for it. One the tray hasn't taken up after a
    /// couple of minutes (it isn't running, say) is withdrawn, with <see cref="Problem"/> saying so.
    /// </summary>
    public UpdateRequest? Pending
    {
        get
        {
            if (_pending is { } pending && DateTimeOffset.UtcNow - pending.RequestedAt > TimeSpan.FromMinutes(2)
                && File.Exists(UpdateRequest.PathFor(runtime.DataDirectory)))
            {
                try
                {
                    File.Delete(UpdateRequest.PathFor(runtime.DataDirectory));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }

                _pending = null;
                Problem = "The tray app didn't take up the update. Check it's running (its icon is in the system tray or menu bar), or download the new version instead.";
            }

            return _pending;
        }
    }

    /// <summary>Why the last install didn't go ahead, for the banner; null when nothing went wrong.</summary>
    public string? Problem { get; private set; }

    /// <summary>What installing <paramref name="update"/> would download, and whether it can be done from here.</summary>
    public UpdateReadiness Check(AvailableUpdate update)
    {
        List<UpdateDownload> downloads = [];
        List<string> leftAlone = [];

        string? rid = RuntimeId();
        ReleaseAsset? app = rid is null ? null : AppAsset(update.Assets, rid);
        if (app is not null)
        {
            downloads.Add(new UpdateDownload { Kind = UpdateDownload.AppKind, Name = app.Name, Url = app.Url, Size = app.Size, Sha256 = app.Sha256 });
        }

        foreach (PluginDescriptor plugin in InstalledPlugins())
        {
            if (PluginAsset(update.Assets, plugin.Id) is { } asset)
            {
                downloads.Add(new UpdateDownload
                {
                    Kind = UpdateDownload.PluginKind, Name = asset.Name, Url = asset.Url, Size = asset.Size, Sha256 = asset.Sha256,
                    PluginId = plugin.Id, PluginName = plugin.Manifest.Name, TargetDirectory = plugin.Directory
                });
            }
            else
            {
                leftAlone.Add(plugin.Manifest.Name);
            }
        }

        string? reason = WhyNot(rid, app);
        return new UpdateReadiness(reason is null, reason, downloads, leftAlone);
    }

    /// <summary>
    /// Hands installing <paramref name="update"/> to the tray, which closes the app within a few seconds. Null when it's
    /// on its way; otherwise why it can't be.
    /// </summary>
    public string? Install(AvailableUpdate update)
    {
        UpdateReadiness readiness = Check(update);
        if (!readiness.CanInstall)
        {
            return readiness.Reason;
        }

        var request = new UpdateRequest
        {
            Version = update.Version,
            Channel = update.Channel == UpdateChannel.Nightly ? "nightly" : "release",
            ReleaseUrl = update.Url,
            Downloads = [.. readiness.Downloads],
            RequestedAt = DateTimeOffset.UtcNow
        };

        try
        {
            request.Save(UpdateRequest.PathFor(runtime.DataDirectory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Couldn't hand the update to the tray: {ex.Message}";
        }

        logger?.LogInformation("Installing SchedularrTV {Version}: the app closes, updates and starts again.", update.Version);
        Problem = null;
        _pending = request;
        Requested?.Invoke();
        return null;
    }

    /// <summary>Why installing from here can't be done, or null when it can.</summary>
    private string? WhyNot(string? rid, ReleaseAsset? app)
    {
        if (rid is null)
        {
            return "There's no download for this kind of computer.";
        }

        if (app is null)
        {
            return $"This release has no download for {rid}.";
        }

        if (!runtime.UnderTray || Environment.GetEnvironmentVariable(AppPaths.TrayPathVariable) is not { Length: > 0 } tray || !File.Exists(tray))
        {
            return "SchedularrTV wasn't started by its tray app (it's running on its own, as a service or in Docker, say), so it can't update itself. Download the new version instead.";
        }

        // A systemd service's processes are all stopped with it, which would stop the installer too.
        if (OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INVOCATION_ID")))
        {
            return "SchedularrTV is running as a systemd service, which would stop the installer along with it. Download the new version and update by hand instead.";
        }

        if (AppRuntime.Version.Contains("-dev", StringComparison.OrdinalIgnoreCase))
        {
            return "This is a development build (run from the source code), which updates through git rather than downloads.";
        }

        // The tray's folder (or the .app on a Mac) is what's replaced, so it, and the folder it's in, must be writable.
        string root = AppRoot(tray);
        string? parent = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (parent is null || !Writable(parent) || !Writable(root))
        {
            return $"SchedularrTV can't write to where it's installed ({root}), so it can't replace itself. Download the new version and install it by hand, or move SchedularrTV to a folder you own.";
        }

        return null;
    }

    /// <summary>Plugins installed as folders (in the data folder or the app's own), which a release may have new copies of.</summary>
    private IEnumerable<PluginDescriptor> InstalledPlugins() =>
        (plugins?.Plugins ?? []).Where(plugin => plugin.Source is PluginSource.Installed or PluginSource.Bundled
            && !string.IsNullOrWhiteSpace(plugin.Directory) && Directory.Exists(plugin.Directory));

    /// <summary>The folder an update replaces for a tray at <paramref name="trayPath"/> (see <see cref="UpdateRequest.AppRootFor"/>).</summary>
    public static string AppRoot(string trayPath) => UpdateRequest.AppRootFor(trayPath);

    /// <summary>The download names' system part for this computer (<c>win-x64</c>, <c>osx-arm64</c>…); null when there's none.</summary>
    public static string? RuntimeId()
    {
        Architecture architecture = RuntimeInformation.OSArchitecture;
        if (OperatingSystem.IsWindows())
        {
            return architecture == Architecture.X64 ? "win-x64" : null;
        }

        if (OperatingSystem.IsMacOS())
        {
            return architecture == Architecture.Arm64 ? "osx-arm64" : architecture == Architecture.X64 ? "osx-x64" : null;
        }

        if (OperatingSystem.IsLinux())
        {
            return architecture == Architecture.Arm64 ? "linux-arm64" : architecture == Architecture.X64 ? "linux-x64" : null;
        }

        return null;
    }

    /// <summary>
    /// The release's download of the app for <paramref name="rid"/>: <c>…-build-win-x64.zip</c>, or as older releases
    /// named it, <c>…-win-x64.zip</c> (a <c>.tar.gz</c> on Linux).
    /// </summary>
    public static ReleaseAsset? AppAsset(IEnumerable<ReleaseAsset> assets, string rid)
    {
        bool Matches(ReleaseAsset asset, string suffix) => asset.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            && !asset.Name.Contains("-plugin-", StringComparison.OrdinalIgnoreCase);

        List<ReleaseAsset> list = [.. assets];
        string[] extensions = rid.StartsWith("linux", StringComparison.Ordinal) ? [".tar.gz"] : [".zip"];
        foreach (string extension in extensions)
        {
            if (list.FirstOrDefault(asset => Matches(asset, $"-build-{rid}{extension}")) is { } current)
            {
                return current;
            }

            if (list.FirstOrDefault(asset => Matches(asset, $"-{rid}{extension}")) is { } older)
            {
                return older;
            }
        }

        return null;
    }

    /// <summary>The release's zip of one plugin: <c>…-plugin-&lt;id&gt;.zip</c>.</summary>
    public static ReleaseAsset? PluginAsset(IEnumerable<ReleaseAsset> assets, string pluginId) =>
        assets.FirstOrDefault(asset => asset.Name.EndsWith($"-plugin-{pluginId}.zip", StringComparison.OrdinalIgnoreCase));

    private static bool Writable(string folder)
    {
        try
        {
            string probe = Path.Combine(folder, $".schedularrtv-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
