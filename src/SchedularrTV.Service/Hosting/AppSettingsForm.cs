using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SchedularrTV.Hosting;
using SchedularrTV.Providers.ErsatzTv;
using SchedularrTV.Providers.FileSystem;
using SchedularrTV.Providers.Jellyfin;

namespace SchedularrTV.Service.Hosting;

public sealed record ConnectionTest(bool Ok, string Message);

/// <summary>
/// The editable copy of the app settings shared by the App settings page and the setup wizard: loaded from
/// what is in effect now, saved as only the fields that were changed into <c>settings.json</c>.
/// </summary>
/// <remarks>Transient: each page or wizard gets its own copy.</remarks>
public sealed class AppSettingsForm(
    AppRuntime runtime,
    IConfiguration configuration,
    IOptions<JellyfinOptions> jellyfin,
    IOptions<FileSystemLibraryOptions> folders,
    IOptions<ErsatzTvOptions> ersatz,
    IHttpClientFactory httpClients,
    ScheduleRebuildService? rebuilds = null,
    SchedularrTV.Service.Editor.LibraryBrowser? library = null,
    IEnumerable<SchedularrTV.Core.Media.IMediaSourceInfo>? sources = null,
    SchedularrTV.Plugins.Playout.IScheduleRepublisher? republisher = null)
{
    public const string OnboardingKey = "Onboarding:Completed";

    // What was shown when loaded, so only fields the user changed are written.
    private Dictionary<string, string?> _original = new();

    public int Port { get; set; }

    public int LoadedPort { get; private set; }

    public bool AllowRemote { get; set; }

    public string JellyfinUrl { get; set; } = string.Empty;

    /// <summary>Typed by the user; blank keeps the saved key.</summary>
    public string JellyfinKey { get; set; } = string.Empty;

    public bool JellyfinKeySet { get; private set; }

    public string JellyfinUser { get; set; } = string.Empty;

    public bool ErsatzManage { get; set; }

    public string ErsatzExe { get; set; } = string.Empty;

    public string ErsatzRoot { get; set; } = string.Empty;

    public int ErsatzPort { get; set; }

    /// <summary>Blank means "find automatically".</summary>
    public string Ffmpeg { get; set; } = string.Empty;

    public string Ffprobe { get; set; } = string.Empty;

    public List<MediaFolderOptions> Folders { get; private set; } = [];

    /// <summary>
    /// The hardware profile and the live and rendering encoding settings, by their settings.json key
    /// (<c>Hardware:Profile</c>, <c>LiveEncoding:Preset</c>…). Blank means "the default for this computer".
    /// </summary>
    public Dictionary<string, string> Performance { get; } = new(StringComparer.Ordinal);

    /// <summary>Every key <see cref="Performance"/> holds.</summary>
    public static readonly IReadOnlyList<string> PerformanceKeys =
    [
        SchedularrTV.Core.Platform.HardwareProfile.SettingKey,
        "LiveEncoding:Encoder", "LiveEncoding:Preset", "LiveEncoding:Height", "LiveEncoding:FrameRate", "LiveEncoding:BitrateKbps", "LiveEncoding:Threads",
        "Rendering:Encoder", "Rendering:Preset", "Rendering:Threads", "Rendering:HardwareBitrateKbps",
        ErsatzEncodingPrefix + "Hardware", ErsatzEncodingPrefix + "Height", ErsatzEncodingPrefix + "BitrateKbps",
        ErsatzEncodingPrefix + "VaapiDevice", ErsatzEncodingPrefix + "VaapiDriver"
    ];

    /// <summary>The app-wide ErsatzTV encoding (<see cref="ErsatzTvEncodingOptions"/>), which every channel inherits.</summary>
    public const string ErsatzEncodingPrefix = "ErsatzTv:Encoding:";

    public string PerformanceValue(string key) => Performance.TryGetValue(key, out string? value) ? value : string.Empty;

    public void SetPerformance(string key, string? value)
    {
        Performance[key] = value?.Trim() ?? string.Empty;
        IsDirty = true;
    }

    public bool FoldersChanged { get; set; }

    /// <summary>True once something was edited since loading or saving.</summary>
    public bool IsDirty { get; set; }

    /// <summary>
    /// True when settings were written that the running server hasn't picked up yet: the port or remote access, since
    /// the server only starts listening once. Everything else applies as soon as it's saved.
    /// </summary>
    public bool NeedsRestart { get; private set; }

    private bool _loadedAllowRemote;

    /// <summary>Whether the port or remote access differ from what the server started with.</summary>
    public bool ListeningChanged => Port != LoadedPort || AllowRemote != _loadedAllowRemote;

    public string SettingsPath => runtime.Settings.Path;

    public string DataDirectory => runtime.DataDirectory;

    /// <summary>The programs in use now, after automatic lookup (the same lookup Generated promos uses).</summary>
    public string FfmpegInUse => ToolLocator.Resolve(configuration["GeneratedPromos:FfmpegPath"] ?? "ffmpeg", "ffmpeg", runtime.DataDirectory);

    public string FfprobeInUse => folders.Value.FfprobePath;

    public ErsatzTvOptions ErsatzInUse => ersatz.Value;

    /// <summary>Whether the running server can reach a media library: an address and a key are configured.</summary>
    public bool JellyfinConfiguredInUse =>
        !string.IsNullOrWhiteSpace(jellyfin.Value.BaseUrl) && !string.IsNullOrWhiteSpace(jellyfin.Value.ApiKey);

    /// <summary>
    /// Whether the running server has somewhere to get shows and movies from: Jellyfin, a local TV or movie folder, or
    /// any other library plugin that's set up.
    /// </summary>
    public bool LibraryConfiguredInUse =>
        JellyfinConfiguredInUse || folders.Value.Folders.Any(folder => folder.IsVideoLibrary && !string.IsNullOrWhiteSpace(folder.Path))
        || (sources ?? []).Any(source => source is not JellyfinMediaLibrary and not FileSystemMediaLibrary && source.IsConfigured);

    /// <summary>The same for the settings being edited (before they're saved).</summary>
    public bool LibraryFolderFilled => Folders.Any(folder => folder.IsVideoLibrary && !string.IsNullOrWhiteSpace(folder.Path));

    public bool OnboardingCompleted => AppSettingsFile.GetBool(runtime.Settings.Load(), OnboardingKey) == true;

    public void Load()
    {
        JsonObject file = runtime.Settings.Load();
        Port = LoadedPort = AppSettingsFile.Port(file);
        AllowRemote = _loadedAllowRemote = AppSettingsFile.AllowRemote(file);

        JellyfinUrl = jellyfin.Value.BaseUrl;
        JellyfinKey = string.Empty;
        JellyfinKeySet = !string.IsNullOrWhiteSpace(jellyfin.Value.ApiKey);
        JellyfinUser = jellyfin.Value.UserId ?? string.Empty;

        ErsatzManage = ersatz.Value.ManageProcess;
        ErsatzExe = ersatz.Value.ExecutablePath ?? string.Empty;
        ErsatzRoot = ersatz.Value.ConfigRoot;
        ErsatzPort = ersatz.Value.Lineup.Port;

        Ffmpeg = Raw("GeneratedPromos:FfmpegPath");
        Ffprobe = Raw("GeneratedPromos:FfprobePath");

        Folders = [.. folders.Value.Folders.Select(folder =>
            JsonSerializer.Deserialize<MediaFolderOptions>(JsonSerializer.Serialize(folder, AppSettingsJson.Options), AppSettingsJson.Options)!)];
        FoldersChanged = false;

        _original = new Dictionary<string, string?>
        {
            ["Jellyfin:BaseUrl"] = JellyfinUrl,
            ["Jellyfin:UserId"] = JellyfinUser,
            ["ErsatzTv:ExecutablePath"] = ErsatzExe,
            ["ErsatzTv:ConfigRoot"] = ErsatzRoot,
            ["GeneratedPromos:FfmpegPath"] = Ffmpeg,
            ["GeneratedPromos:FfprobePath"] = Ffprobe
        };

        foreach (string key in PerformanceKeys)
        {
            Performance[key] = configuration[key]?.Trim() ?? string.Empty;
            _original[key] = Performance[key];
        }

        IsDirty = false;
    }

    /// <summary>Writes the changed fields to settings.json. Throws on I/O failure.</summary>
    public void Save(bool completeOnboarding = false)
    {
        JsonObject file = runtime.Settings.Load();
        AppSettingsFile.Set(file, "Server:Port", Port == AppPaths.DefaultPort ? null : JsonValue.Create(Port));
        AppSettingsFile.Set(file, "Server:AllowRemote", AllowRemote ? JsonValue.Create(true) : null);

        SetIfChanged(file, "Jellyfin:BaseUrl", JellyfinUrl);
        SetIfChanged(file, "Jellyfin:UserId", JellyfinUser);
        if (!string.IsNullOrWhiteSpace(JellyfinKey))
        {
            AppSettingsFile.Set(file, "Jellyfin:ApiKey", JsonValue.Create(JellyfinKey.Trim()));
        }

        if (ErsatzManage != ersatz.Value.ManageProcess)
        {
            AppSettingsFile.Set(file, "ErsatzTv:ManageProcess", JsonValue.Create(ErsatzManage));
        }

        SetIfChanged(file, "ErsatzTv:ExecutablePath", ErsatzExe);
        SetIfChanged(file, "ErsatzTv:ConfigRoot", ErsatzRoot);
        if (ErsatzPort != ersatz.Value.Lineup.Port)
        {
            AppSettingsFile.Set(file, "ErsatzTv:Lineup:Port", JsonValue.Create(ErsatzPort));
        }

        // Both readers of ffprobe use the same program.
        SetIfChanged(file, "GeneratedPromos:FfmpegPath", Ffmpeg);
        if (Ffprobe != _original["GeneratedPromos:FfprobePath"])
        {
            SetIfChanged(file, "GeneratedPromos:FfprobePath", Ffprobe);
            AppSettingsFile.Set(file, "FileSystemLibrary:FfprobePath", string.IsNullOrWhiteSpace(Ffprobe) ? null : JsonValue.Create(Ffprobe.Trim()));
        }

        if (FoldersChanged)
        {
            List<MediaFolderOptions> keep = [.. Folders.Where(folder => !string.IsNullOrWhiteSpace(folder.Path))];
            AppSettingsFile.Set(file, "FileSystemLibrary:Folders", JsonSerializer.SerializeToNode(keep, AppSettingsJson.Options));
        }

        bool liveEncodingChanged = false;
        bool ersatzEncodingChanged = false;
        foreach (string key in PerformanceKeys)
        {
            string value = PerformanceValue(key);
            bool changed = !string.Equals(_original.GetValueOrDefault(key) ?? string.Empty, value, StringComparison.Ordinal);
            liveEncodingChanged |= (key.StartsWith("LiveEncoding:", StringComparison.Ordinal) || key == SchedularrTV.Core.Platform.HardwareProfile.SettingKey)
                                   && changed;
            ersatzEncodingChanged |= key.StartsWith(ErsatzEncodingPrefix, StringComparison.Ordinal) && changed;
            SetIfChanged(file, key, value);
        }

        if (completeOnboarding)
        {
            AppSettingsFile.Set(file, OnboardingKey, JsonValue.Create(true));
        }

        bool sourcesChanged = FoldersChanged || !string.IsNullOrWhiteSpace(JellyfinKey)
            || _original.Keys.Any(key => key.StartsWith("Jellyfin:", StringComparison.Ordinal) && _original[key] != Current(key))
            || Ffprobe != _original["GeneratedPromos:FfprobePath"];
        runtime.SaveSettings(file);

        // The live page channels' streams are described to ErsatzTV in the published schedule (their size and frame
        // rate), so new live settings are republished with a rebuild; a running stream restarts with them by itself.
        if (liveEncodingChanged && !sourcesChanged)
        {
            rebuilds?.RequestFullRebuild();
        }

        // ErsatzTV's encoding is only in the lineup, so publishing again (no rebuild) applies it; ErsatzTV restarts for it.
        if (ersatzEncodingChanged && !liveEncodingChanged && !sourcesChanged)
        {
            republisher?.RequestRepublish();
        }

        // A different media server or folder changes what the schedule can use, so it's replanned now.
        if (sourcesChanged)
        {
            rebuilds?.RequestFullRebuild();

            // Read the library now, in the background, rather than when a library page is first opened: right after
            // setup a schedule with no channels never asks for it, and a large library takes minutes to read.
            library?.Invalidate();
            library?.WarmUp();
        }

        // Everything else applies as soon as it's saved; only where the server listens needs a fresh start.
        NeedsRestart |= ListeningChanged;
        IsDirty = false;
        JellyfinKeySet |= !string.IsNullOrWhiteSpace(JellyfinKey);
        JellyfinKey = string.Empty;
        foreach (string key in _original.Keys.ToList())
        {
            _original[key] = Current(key);
        }

        FoldersChanged = false;
    }

    /// <summary>Asks Jellyfin who it is, with the typed key or else the saved one.</summary>
    public async Task<ConnectionTest> TestJellyfinAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(JellyfinUrl))
        {
            return new ConnectionTest(false, "Enter the server address first");
        }

        string key = string.IsNullOrWhiteSpace(JellyfinKey) ? jellyfin.Value.ApiKey : JellyfinKey.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            return new ConnectionTest(false, "Enter an API key first");
        }

        try
        {
            using HttpClient http = httpClients.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{JellyfinUrl.TrimEnd('/')}/System/Info");
            // The same header the Jellyfin source sends. Newer Jellyfin (10.11+) no longer accepts the old X-Emby-Token
            // header by default, so testing with it refused every key.
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("MediaBrowser",
                $"Token=\"{key}\", Client=\"SchedularrTV\", Device=\"SchedularrTV\", DeviceId=\"schedularrtv\", Version=\"1.0\"");
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new ConnectionTest(false, response.StatusCode == HttpStatusCode.Unauthorized
                    ? "Reached Jellyfin, but the API key was refused"
                    : $"Jellyfin answered {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            using JsonDocument info = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            string name = info.RootElement.TryGetProperty("ServerName", out JsonElement server) ? server.GetString() ?? "Jellyfin" : "Jellyfin";
            string version = info.RootElement.TryGetProperty("Version", out JsonElement v) ? v.GetString() ?? "" : "";
            return new ConnectionTest(true, $"Connected to {name} {version}".Trim());
        }
        catch (Exception ex) when (ex is HttpRequestException or UriFormatException or InvalidOperationException or JsonException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new ConnectionTest(false, ex is TaskCanceledException ? "No answer within 10 seconds" : ex.Message);
        }
    }

    public void AddFolder()
    {
        Folders.Add(new MediaFolderOptions { Path = string.Empty, Tags = ["Commercials"] });
        FoldersChanged = IsDirty = true;
    }

    /// <summary>Adds a TV (<see cref="MediaKind.Episode"/>) or movie (<see cref="MediaKind.Movie"/>) library folder.</summary>
    public void AddLibraryFolder(SchedularrTV.Core.Media.MediaKind kind)
    {
        Folders.Add(new MediaFolderOptions { Path = string.Empty, Kind = kind, PromoFolder = null });
        FoldersChanged = IsDirty = true;
    }

    private string? Current(string key) => key switch
    {
        "Jellyfin:BaseUrl" => JellyfinUrl,
        "Jellyfin:UserId" => JellyfinUser,
        "ErsatzTv:ExecutablePath" => ErsatzExe,
        "ErsatzTv:ConfigRoot" => ErsatzRoot,
        "GeneratedPromos:FfmpegPath" => Ffmpeg,
        "GeneratedPromos:FfprobePath" => Ffprobe,
        _ => Performance.TryGetValue(key, out string? value) ? value : null
    };

    // The raw configured value, so a bare "ffmpeg" (find automatically) shows as blank rather than wherever it was found.
    private string Raw(string key)
    {
        string? value = configuration[key];
        return string.IsNullOrWhiteSpace(value) || !(value.Contains('/') || value.Contains('\\')) ? string.Empty : value;
    }

    /// <summary>Writes a field only when it was edited; blank removes it so the default applies again.</summary>
    private void SetIfChanged(JsonObject file, string key, string value)
    {
        if (_original.TryGetValue(key, out string? original) && string.Equals(original ?? string.Empty, value, StringComparison.Ordinal))
        {
            return;
        }

        AppSettingsFile.Set(file, key, string.IsNullOrWhiteSpace(value) ? null : JsonValue.Create(value.Trim()));
    }
}
