using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SchedularrTV.Hosting;

namespace SchedularrTV.Service.Hosting;

/// <summary>Where updates come from: numbered releases, or the nightly build of every push.</summary>
public enum UpdateChannel
{
    Release,
    Nightly
}

/// <summary>Where to look for new releases. <c>Updates</c> in appsettings.json or settings.json.</summary>
public sealed class UpdateOptions
{
    public const string SectionName = "Updates";

    /// <summary>The setting the update channel is saved under in settings.json.</summary>
    public const string ChannelKey = "Updates:Channel";

    /// <summary>
    /// The GitHub repository releases are published to, as <c>owner/name</c>. Change it here (or in
    /// appsettings.json) when the project moves.
    /// </summary>
    public const string DefaultRepository = "CarsonKompon/SchedularrTV";

    public string Repository { get; set; } = DefaultRepository;

    public bool Enabled { get; set; } = true;

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Numbered releases (the default), or the nightly pre-release rebuilt from every push.</summary>
    public UpdateChannel Channel { get; set; } = UpdateChannel.Release;

    /// <summary>The tag the nightly pre-release is published under.</summary>
    public string NightlyTag { get; set; } = "nightly";
}

/// <summary>A file attached to a release.</summary>
/// <param name="Sha256">Its SHA-256 (hex) as GitHub publishes it, or null when the release doesn't say.</param>
public sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256 = null);

/// <summary>A release newer than the one running.</summary>
public sealed record AvailableUpdate(string Tag, string Name, string Url, DateTimeOffset? PublishedAt)
{
    /// <summary>Its version number, e.g. <c>0.2.1</c> or <c>0.2.1-nightly.58</c>.</summary>
    public string Version { get; init; } = Tag.TrimStart('v', 'V');

    public UpdateChannel Channel { get; init; } = UpdateChannel.Release;

    /// <summary>The release's files, to install it from.</summary>
    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = [];

    /// <summary>How it's named to people: <c>v0.2.1</c>, or <c>nightly 58 (0.2.1)</c>.</summary>
    public string Label => UpdateChecker.Label(Version);
}

/// <summary>
/// Checks GitHub now and then for a release newer than this copy, for the badge in the sidebar and the banner on the
/// settings pages, from the chosen channel: numbered releases, or the nightly build. Says nothing when the repository
/// can't be reached (offline, private, renamed).
/// </summary>
public sealed partial class UpdateChecker(
    IHttpClientFactory httpClients,
    IOptions<UpdateOptions> options,
    ILogger<UpdateChecker> logger,
    AppRuntime? runtime = null) : BackgroundService
{
    public AvailableUpdate? Available { get; private set; }

    /// <summary>When it last asked GitHub (successfully or not), for the settings page.</summary>
    public DateTimeOffset? LastChecked { get; private set; }

    /// <summary>Why the last check got no answer, for the settings page; null when it did.</summary>
    public string? LastProblem { get; private set; }

    public event Action? Changed;

    /// <summary>
    /// The channel updates come from: as chosen in App settings (settings.json, read each time so a change applies at
    /// once), else as configured.
    /// </summary>
    public UpdateChannel Channel =>
        runtime is not null && AppSettingsFile.GetString(runtime.Settings.Load(), UpdateOptions.ChannelKey) is { } saved
        && Enum.TryParse(saved, ignoreCase: true, out UpdateChannel chosen)
            ? chosen
            : options.Value.Channel;

    /// <summary>Saves the channel in settings.json and checks it straight away.</summary>
    public async Task SetChannelAsync(UpdateChannel channel, CancellationToken cancellationToken)
    {
        if (runtime is not null)
        {
            System.Text.Json.Nodes.JsonObject file = runtime.Settings.Load();
            AppSettingsFile.Set(file, UpdateOptions.ChannelKey, System.Text.Json.Nodes.JsonValue.Create(channel.ToString()));
            runtime.Settings.Save(file);
        }

        await CheckAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the app a moment to start before going online.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (options.Value.Enabled)
            {
                await CheckAsync(stoppingToken).ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(options.Value.Interval > TimeSpan.FromMinutes(5) ? options.Value.Interval : TimeSpan.FromHours(6), stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        string repository = options.Value.Repository.Trim().Trim('/');
        if (repository.Split('/') is not [{ Length: > 0 }, { Length: > 0 }])
        {
            return;
        }

        UpdateChannel channel = Channel;
        string path = channel == UpdateChannel.Nightly
            ? $"releases/tags/{Uri.EscapeDataString(options.Value.NightlyTag.Trim())}"
            : "releases/latest";
        try
        {
            using HttpClient http = httpClients.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/{path}");
            request.Headers.UserAgent.ParseAdd($"SchedularrTV/{AppRuntime.Version}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            LastChecked = DateTimeOffset.UtcNow;
            if (!response.IsSuccessStatusCode)
            {
                LastProblem = $"GitHub answered {(int)response.StatusCode} for {repository}'s {(channel == UpdateChannel.Nightly ? "nightly build" : "latest release")}.";
                logger.LogDebug("No release information from {Repository} ({Status}).", repository, (int)response.StatusCode);
                Changed?.Invoke();
                return;
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
            AvailableUpdate? update = channel == UpdateChannel.Nightly
                ? NewerNightly(json.RootElement, AppRuntime.Version)
                : Newer(json.RootElement, AppRuntime.Version);
            LastProblem = null;
            if (update != Available)
            {
                Available = update;
                if (update is not null)
                {
                    logger.LogInformation("SchedularrTV {Version} is available: {Url}", update.Label, update.Url);
                }
            }

            Changed?.Invoke();
        }
        // Anything else (an odd JSON shape, a bad repository name) must not escape: it would stop the whole host.
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LastChecked = DateTimeOffset.UtcNow;
            LastProblem = $"Couldn't reach GitHub: {ex.GetBaseException().Message}";
            logger.LogDebug("Couldn't check for updates: {Message}", ex.Message);
            Changed?.Invoke();
        }
    }

    /// <summary>The release in GitHub's "latest release" answer, if it's newer than <paramref name="current"/>.</summary>
    public static AvailableUpdate? Newer(JsonElement release, string current)
    {
        if (release.ValueKind != JsonValueKind.Object
            || !release.TryGetProperty("tag_name", out JsonElement tagElement) || tagElement.GetString() is not { Length: > 0 } tag
            || (release.TryGetProperty("draft", out JsonElement draft) && draft.ValueKind == JsonValueKind.True)
            || (release.TryGetProperty("prerelease", out JsonElement prerelease) && prerelease.ValueKind == JsonValueKind.True))
        {
            return null;
        }

        return IsNewer(tag, current) ? Read(release, tag, tag.TrimStart('v', 'V'), UpdateChannel.Release) : null;
    }

    /// <summary>
    /// The nightly build in GitHub's answer for its tag, if it's newer than <paramref name="current"/>. Its version
    /// (<c>0.2.1-nightly.58</c>) is in its title or notes; a nightly that doesn't say is never offered.
    /// </summary>
    public static AvailableUpdate? NewerNightly(JsonElement release, string current)
    {
        if (release.ValueKind != JsonValueKind.Object
            || (release.TryGetProperty("draft", out JsonElement draft) && draft.ValueKind == JsonValueKind.True))
        {
            return null;
        }

        string text = $"{Text(release, "name")} {Text(release, "body")}";
        if (NightlyVersionPattern().Match(text) is not { Success: true } match)
        {
            return null;
        }

        string version = match.Value;
        string tag = Text(release, "tag_name") is { Length: > 0 } named ? named : "nightly";
        return Compare(version, current) > 0 ? Read(release, tag, version, UpdateChannel.Nightly) : null;
    }

    private static AvailableUpdate Read(JsonElement release, string tag, string version, UpdateChannel channel)
    {
        string url = Text(release, "html_url");
        string name = Text(release, "name") is { Length: > 0 } title ? title : tag;
        DateTimeOffset? published = release.TryGetProperty("published_at", out JsonElement at) && at.TryGetDateTimeOffset(out DateTimeOffset when) ? when : null;
        List<ReleaseAsset> assets = [];
        if (release.TryGetProperty("assets", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in list.EnumerateArray())
            {
                string assetName = Text(asset, "name");
                string download = Text(asset, "browser_download_url");
                if (assetName.Length == 0 || download.Length == 0)
                {
                    continue;
                }

                long size = asset.TryGetProperty("size", out JsonElement bytes) && bytes.TryGetInt64(out long value) ? value : 0;
                // GitHub publishes each asset's digest as "sha256:<hex>".
                string? sha = Text(asset, "digest") is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    ? digest["sha256:".Length..].Trim().ToLowerInvariant()
                    : null;
                assets.Add(new ReleaseAsset(assetName, download, size, string.IsNullOrEmpty(sha) ? null : sha));
            }
        }

        return new AvailableUpdate(tag, name, url, published) { Version = version, Channel = channel, Assets = assets };
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    /// <summary>
    /// Whether release <paramref name="tag"/> (<c>v1.2.0</c>) is newer than the running <paramref name="current"/>
    /// version. A pre-release such as a nightly (<c>1.2.0-nightly.42</c>) counts as older than the release of the same
    /// number. Unknown versions never ask to update.
    /// </summary>
    public static bool IsNewer(string tag, string current) => Compare(tag, current) > 0;

    /// <summary>
    /// Orders two versions (<c>v1.2.0</c>, <c>1.2.1-nightly.58</c>): by number first, then a release above its
    /// pre-releases, then pre-releases part by part (numbers as numbers, so nightly 58 is after nightly 9). 0 when
    /// either can't be read, so an unknown version is never taken as newer.
    /// </summary>
    public static int Compare(string left, string right)
    {
        if (!TryParse(left, out Version? a, out string? aPre) || !TryParse(right, out Version? b, out string? bPre))
        {
            return 0;
        }

        int byNumber = a!.CompareTo(b);
        if (byNumber != 0)
        {
            return byNumber;
        }

        if (aPre is null || bPre is null)
        {
            return aPre is null ? (bPre is null ? 0 : 1) : -1;
        }

        string[] aParts = aPre.Split('.');
        string[] bParts = bPre.Split('.');
        for (int i = 0; i < Math.Min(aParts.Length, bParts.Length); i++)
        {
            bool aNumber = long.TryParse(aParts[i], out long aValue);
            bool bNumber = long.TryParse(bParts[i], out long bValue);
            int part = aNumber && bNumber ? aValue.CompareTo(bValue)
                : aNumber ? -1
                : bNumber ? 1
                : string.CompareOrdinal(aParts[i], bParts[i]);
            if (part != 0)
            {
                return part;
            }
        }

        return aParts.Length.CompareTo(bParts.Length);
    }

    /// <summary>A version as people read it: <c>v0.2.1</c>, or <c>nightly 58 (0.2.1)</c> for a nightly.</summary>
    public static string Label(string version)
    {
        string trimmed = version.Trim().TrimStart('v', 'V');
        int nightly = trimmed.IndexOf("-nightly.", StringComparison.Ordinal);
        return nightly >= 0 ? $"nightly {trimmed[(nightly + 9)..]} ({trimmed[..nightly]})" : $"v{trimmed}";
    }

    private static bool TryParse(string text, out Version? version, out string? preRelease)
    {
        string trimmed = text.Trim().TrimStart('v', 'V');
        int build = trimmed.IndexOf('+');
        if (build >= 0)
        {
            trimmed = trimmed[..build];
        }

        int suffix = trimmed.IndexOf('-');
        preRelease = suffix >= 0 ? trimmed[(suffix + 1)..] : null;
        string core = suffix >= 0 ? trimmed[..suffix] : trimmed;

        // "1.2" and "1.2.0" are the same release.
        if (System.Version.TryParse(core, out Version? parsed))
        {
            version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
            return true;
        }

        version = null;
        return false;
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+-nightly\.\d+")]
    private static partial Regex NightlyVersionPattern();
}
