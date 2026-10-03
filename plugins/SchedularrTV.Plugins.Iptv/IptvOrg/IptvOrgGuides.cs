using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;

namespace SchedularrTV.Iptv.IptvOrg;

/// <summary>How a provider's iptv-org guide stands, for the IPTV page.</summary>
/// <param name="Planned">Carried channels iptv-org has a listings site for.</param>
/// <param name="Listed">Of those, how many got listings in the last grab.</param>
/// <param name="Unknown">Carried channels iptv-org has no listings site for (or couldn't be identified).</param>
public sealed record IptvOrgStatus(DateTimeOffset? GrabbedAt, int Planned, int Listed, int Unknown, string? Activity, string? Problem);

/// <summary>
/// Grabs guides with iptv-org/epg for the providers that ask for it: works out a listings site for each carried channel,
/// runs the grabber (every so many hours, or as soon as the carried channels change), and keeps the result for the
/// refresher to read in with the provider's playlist. A channel whose site gave nothing is tried on another next time.
/// </summary>
public sealed partial class IptvOrgGuides(
    SourceStore store,
    IptvCatalog catalog,
    IptvOrgApi api,
    IptvOrgTools tools,
    IptvSettings settings,
    TimeProvider clock,
    ILogger<IptvOrgGuides> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly ConcurrentDictionary<string, string> _activity = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when a grab finished with a new guide, so the provider's guide is read again.</summary>
    public event Action? Grabbed;

    /// <summary>Raised when what's going on changes, for the IPTV page.</summary>
    public event Action? Changed;

    /// <summary>Kept per provider in <c>iptv-org/&lt;provider&gt;/state.json</c>.</summary>
    public sealed class GrabState
    {
        public DateTimeOffset? GrabbedAt { get; set; }

        /// <summary>Which channels were asked for, so a change to them grabs again straight away.</summary>
        public string? ChannelsHash { get; set; }

        public int Planned { get; set; }

        public int Listed { get; set; }

        public int Unknown { get; set; }

        public string? Problem { get; set; }

        /// <summary>Guide id → sites that gave it nothing, tried again only once the others have been.</summary>
        public Dictionary<string, HashSet<string>> Failed { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Guide id → playlist name, for channels found by name (their listings are matched to them by it).</summary>
        public Dictionary<string, string> Names { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private string Folder(string sourceId) => Path.Combine(tools.Root, "guides", IptvSource.Slug(sourceId));

    /// <summary>The last grabbed guide for a provider, or null when there isn't one yet.</summary>
    public string? GuideFile(string sourceId) => Path.Combine(Folder(sourceId), "guide.xml") is var path && File.Exists(path) ? path : null;

    public GrabState State(string sourceId)
    {
        string path = Path.Combine(Folder(sourceId), "state.json");
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<GrabState>(File.ReadAllText(path), Json) ?? new GrabState() : new GrabState();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new GrabState();
        }
    }

    public IptvOrgStatus Status(string sourceId)
    {
        GrabState state = State(sourceId);
        // Setting the grabber up (shared by every provider) says more than "grabbing" while it's going on.
        string? setup = tools.Stage is { } stage ? tools.Progress is { } done ? $"{stage} ({done:P0})" : stage : null;
        string? activity = _activity.TryGetValue(sourceId, out string? now) ? setup ?? now : null;
        return new IptvOrgStatus(state.GrabbedAt, state.Planned, state.Listed, state.Unknown, activity, state.Problem);
    }

    /// <summary>Grabs the provider's guide again now, whatever its age.</summary>
    public void GrabNow(string sourceId)
    {
        string path = Path.Combine(Folder(sourceId), "state.json");
        if (File.Exists(path))
        {
            GrabState state = State(sourceId);
            state.GrabbedAt = null;
            Save(sourceId, state);
        }

        Poke();
    }

    private void Poke()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        store.Changed += Poke;
        catalog.Changed += Poke;
        tools.Changed += OnToolsChanged;
        try
        {
            // Let the refresher read the playlists first.
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (IptvSource source in store.Sources.Where(source => source.Enabled && source.IptvOrgGuide))
                {
                    try
                    {
                        await GrabIfDueAsync(source, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning("IPTV {Source}: the iptv-org guide couldn't be made: {Message}", source.Name, ex.Message);
                        GrabState state = State(source.Id);
                        state.Problem = ex.Message;
                        Save(source.Id, state);
                    }
                    finally
                    {
                        _activity.TryRemove(source.Id, out _);
                        Changed?.Invoke();
                    }
                }

                try
                {
                    await _wake.WaitAsync(TimeSpan.FromMinutes(15), stoppingToken).ConfigureAwait(false);

                    // Saves and refreshes come in bursts.
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            store.Changed -= Poke;
            catalog.Changed -= Poke;
            tools.Changed -= OnToolsChanged;
        }
    }

    private void OnToolsChanged() => Changed?.Invoke();

    private async Task GrabIfDueAsync(IptvSource source, CancellationToken cancellationToken)
    {
        if (catalog.Get(source.Id)?.Playlist is not { Channels.Count: > 0 } playlist)
        {
            return;
        }

        List<PlaylistChannel> carried = [.. playlist.Channels.Where(channel => source.Includes(channel.Key))];
        if (carried.Count == 0)
        {
            return;
        }

        GrabState state = State(source.Id);
        string hash = Hash(carried.Select(channel => channel.Key));
        DateTimeOffset now = clock.GetUtcNow();
        bool due = GuideFile(source.Id) is null || state.ChannelsHash != hash || state.GrabbedAt is not { } last || now - last >= settings.IptvOrgGrabEvery;
        if (!due)
        {
            return;
        }

        Activity(source.Id, "Reading iptv-org's channel list");
        IReadOnlyList<IptvOrgGuideEntry> guides = await api.GuidesAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, string> names = await api.ChannelIdsByNameAsync(cancellationToken).ConfigureAwait(false);
        List<GrabChannel> plan = IptvOrgPlan.Plan(carried, guides, names, state.Failed);
        int unknown = carried.DistinctBy(channel => channel.TvgId ?? channel.Key).Count() - plan.Count;
        state.ChannelsHash = hash;
        state.Planned = plan.Count;
        state.Unknown = Math.Max(0, unknown);
        state.Names = plan.Where(channel => channel.ByName).ToDictionary(channel => channel.XmltvId, channel => channel.PlaylistName, StringComparer.OrdinalIgnoreCase);
        if (plan.Count == 0)
        {
            state.GrabbedAt = now;
            state.Listed = 0;
            state.Problem = "iptv-org has no listings site for any of the carried channels.";
            Save(source.Id, state);
            return;
        }

        NodeRuntime node = await tools.PrepareAsync(cancellationToken).ConfigureAwait(false);
        string folder = Folder(source.Id);
        Directory.CreateDirectory(folder);
        string channelsFile = Path.Combine(folder, "channels.xml");
        string output = Path.Combine(folder, "guide.xml.grabbing");
        await File.WriteAllTextAsync(channelsFile, IptvOrgPlan.ChannelsXml(plan), cancellationToken).ConfigureAwait(false);
        File.Delete(output);

        (string tsx, string script) = GrabCommand(tools.EpgFolder);
        Activity(source.Id, $"Grabbing listings for {plan.Count} channel(s)");
        logger.LogInformation("IPTV {Source}: grabbing iptv-org listings for {Count} channel(s).", source.Name, plan.Count);
        IptvOrgTools.ProcessResult result = await IptvOrgTools.RunAsync(node.Node,
            [tsx, script, $"--channels={channelsFile}", $"--output={output}", $"--days={settings.IptvOrgDays}", $"--maxConnections={settings.IptvOrgConnections}"],
            tools.EpgFolder, TimeSpan.FromHours(3), IptvOrgTools.GrabberEnvironment(node), cancellationToken,
            line =>
            {
                if (Step().Match(line) is { Success: true } step)
                {
                    Activity(source.Id, $"Grabbing listings: {step.Groups[1].Value} of {step.Groups[2].Value}");
                }
            }).ConfigureAwait(false);

        if (!File.Exists(output))
        {
            state.Problem = $"iptv-org/epg made no guide (exit code {result.Code}): {IptvOrgTools.LastLines(result.Errors.Length > 0 ? result.Errors : result.Output)}";
            state.GrabbedAt = now;
            Save(source.Id, state);
            return;
        }

        // Which channels came back with listings; the rest try another site next time.
        Activity(source.Id, "Reading the grabbed guide");
        HashSet<string> listed = await Task.Run(() => ListedChannels(output), cancellationToken).ConfigureAwait(false);
        foreach (GrabChannel channel in plan)
        {
            IptvOrgPlan.Remember(state.Failed, channel, listed.Contains(channel.XmltvId));
        }

        File.Move(output, Path.Combine(folder, "guide.xml"), overwrite: true);
        state.GrabbedAt = clock.GetUtcNow();
        state.Listed = plan.Count(channel => listed.Contains(channel.XmltvId));
        state.Problem = result.Code == 0 ? null : $"iptv-org/epg stopped early (exit code {result.Code}); kept what it grabbed.";
        Save(source.Id, state);
        logger.LogInformation("IPTV {Source}: iptv-org listings for {Listed} of {Planned} channel(s).", source.Name, state.Listed, state.Planned);
        Grabbed?.Invoke();
    }

    /// <summary>The grabber's command, from iptv-org/epg's own <c>package.json</c>: tsx's program and the grab script.</summary>
    public static (string Tsx, string Script) GrabCommand(string epgFolder)
    {
        string script = "scripts/commands/epg/grab.ts";
        try
        {
            using JsonDocument package = JsonDocument.Parse(File.ReadAllText(Path.Combine(epgFolder, "package.json")));
            if (package.RootElement.TryGetProperty("scripts", out JsonElement scripts) && scripts.TryGetProperty("grab", out JsonElement grab)
                && grab.GetString()?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [.., var last] && last.EndsWith(".ts", StringComparison.Ordinal))
            {
                script = last;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        string tsx = Path.Combine(epgFolder, "node_modules", "tsx", "dist", "cli.mjs");
        try
        {
            using JsonDocument package = JsonDocument.Parse(File.ReadAllText(Path.Combine(epgFolder, "node_modules", "tsx", "package.json")));
            JsonElement bin = package.RootElement.TryGetProperty("bin", out JsonElement found) ? found : default;
            string? relative = bin.ValueKind == JsonValueKind.String ? bin.GetString()
                : bin.ValueKind == JsonValueKind.Object && bin.TryGetProperty("tsx", out JsonElement named) ? named.GetString() : null;
            if (relative is { Length: > 0 })
            {
                tsx = Path.GetFullPath(Path.Combine(epgFolder, "node_modules", "tsx", relative));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return (tsx, script);
    }

    /// <summary>The guide ids with at least one listing in a guide file.</summary>
    public static HashSet<string> ListedChannels(string file)
    {
        using FileStream stream = File.OpenRead(file);
        // The grabber lists every channel before its programmes, so the channels read so far are all of them.
        XmltvGuide guide = XmltvReader.Read(stream, read => new HashSet<string>(read.Channels.Keys, StringComparer.OrdinalIgnoreCase),
            DateTimeOffset.MinValue.AddYears(1), DateTimeOffset.MaxValue.AddYears(-1));
        return new HashSet<string>(guide.Programmes.Where(pair => pair.Value.Count > 0).Select(pair => pair.Key), StringComparer.OrdinalIgnoreCase);
    }

    private void Activity(string sourceId, string text)
    {
        _activity[sourceId] = text;
        Changed?.Invoke();
    }

    private void Save(string sourceId, GrabState state)
    {
        string folder = Folder(sourceId);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "state.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(state, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private static string Hash(IEnumerable<string> keys) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', keys.Order(StringComparer.Ordinal)))))[..16];

    // The grabber's progress lines: "[12/240] site - channel …".
    [GeneratedRegex(@"\[(\d+)/(\d+)\]")]
    private static partial Regex Step();

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }
}
