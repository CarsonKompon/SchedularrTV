using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SchedularrTV.Service.Hosting;

/// <summary>Where to look for new releases. <c>Updates</c> in appsettings.json or settings.json.</summary>
public sealed class UpdateOptions
{
    public const string SectionName = "Updates";

    /// <summary>
    /// The GitHub repository releases are published to, as <c>owner/name</c>. Change it here (or in
    /// appsettings.json) when the project moves. Only published, non-pre-release releases count, so the nightly
    /// build never shows up as an update.
    /// </summary>
    public const string DefaultRepository = "CarsonKompon/tv_scheduler";

    public string Repository { get; set; } = DefaultRepository;

    public bool Enabled { get; set; } = true;

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);
}

/// <summary>A release newer than the one running.</summary>
public sealed record AvailableUpdate(string Tag, string Name, string Url, DateTimeOffset? PublishedAt);

/// <summary>
/// Checks GitHub now and then for a release newer than this copy, for the badge in the sidebar and the banner on the
/// settings pages. Says nothing when the repository can't be reached (offline, private, renamed).
/// </summary>
public sealed class UpdateChecker(IHttpClientFactory httpClients, IOptions<UpdateOptions> options, ILogger<UpdateChecker> logger) : BackgroundService
{
    public AvailableUpdate? Available { get; private set; }

    public event Action? Changed;

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

        try
        {
            using HttpClient http = httpClients.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
            request.Headers.UserAgent.ParseAdd($"SchedularrTV/{AppRuntime.Version}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("No release information from {Repository} ({Status}).", repository, (int)response.StatusCode);
                return;
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
            AvailableUpdate? update = Newer(json.RootElement, AppRuntime.Version);
            if (update != Available)
            {
                Available = update;
                if (update is not null)
                {
                    logger.LogInformation("SchedularrTV {Tag} is available: {Url}", update.Tag, update.Url);
                }

                Changed?.Invoke();
            }
        }
        // Anything else (an odd JSON shape, a bad repository name) must not escape: it would stop the whole host.
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Couldn't check for updates: {Message}", ex.Message);
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

        if (!IsNewer(tag, current))
        {
            return null;
        }

        string url = release.TryGetProperty("html_url", out JsonElement link) && link.GetString() is { Length: > 0 } page ? page : string.Empty;
        string name = release.TryGetProperty("name", out JsonElement title) && title.GetString() is { Length: > 0 } text ? text : tag;
        DateTimeOffset? published = release.TryGetProperty("published_at", out JsonElement at) && at.TryGetDateTimeOffset(out DateTimeOffset when) ? when : null;
        return new AvailableUpdate(tag, name, url, published);
    }

    /// <summary>
    /// Whether release <paramref name="tag"/> (<c>v1.2.0</c>) is newer than the running <paramref name="current"/>
    /// version. A pre-release such as a nightly (<c>1.2.0-nightly.42</c>) counts as older than the release of the same
    /// number. Unknown versions (a development build) never ask to update.
    /// </summary>
    public static bool IsNewer(string tag, string current)
    {
        if (!TryParse(tag, out Version? available, out _) || !TryParse(current, out Version? running, out bool runningIsPreRelease))
        {
            return false;
        }

        return available > running || (available == running && runningIsPreRelease);
    }

    private static bool TryParse(string text, out Version? version, out bool preRelease)
    {
        string trimmed = text.Trim().TrimStart('v', 'V');
        int suffix = trimmed.IndexOfAny(['-', '+']);
        preRelease = suffix >= 0 && trimmed[suffix] == '-';
        string core = suffix >= 0 ? trimmed[..suffix] : trimmed;

        // "1.2" and "1.2.0" are the same release.
        if (Version.TryParse(core, out Version? parsed))
        {
            version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
            return true;
        }

        version = null;
        return false;
    }
}
