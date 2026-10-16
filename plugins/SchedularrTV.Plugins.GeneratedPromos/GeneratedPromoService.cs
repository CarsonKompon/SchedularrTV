using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Publishing;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.GeneratedPromos;

/// <param name="ImageProblem">Why some automatic images weren't found (the render is then provisional, like a silent one, and tried again).</param>
public sealed record GeneratedPromoRender(string Path, Exception? VoiceError, string? ImageProblem = null);

/// <summary>A promo being rendered: where it's filed (channel › block › template) and when it airs.</summary>
public sealed record GeneratedPromoJob(string Label, DateTimeOffset AirsAt, DateTimeOffset StartedAt);

/// <summary>Progress through the promos queued since the queue was last empty.</summary>
public sealed record GeneratedPromoQueueStatus(int Done, int Failed, int Total, IReadOnlyList<GeneratedPromoJob> Rendering, DateTimeOffset? NextAirsAt)
{
    public int Waiting => Math.Max(0, Total - Done - Failed - Rendering.Count);

    public double Percent => Total == 0 ? 0 : (Done + Failed) * 100.0 / Total;
}

/// <summary>
/// Resolves generated promo slots against built schedules, renders them in air-time order, and
/// attaches finished files. Unrendered slots publish their stand-in break, or black without one.
/// </summary>
public sealed partial class GeneratedPromoService(
    IOptions<GeneratedPromoOptions> options,
    TextToSpeechCatalog voices,
    TimeProvider clock,
    ILogger<GeneratedPromoService> logger,
    SchedulerConfigProvider? config = null,
    IChannelViewers? viewers = null,
    SchedularrTV.Service.Writing.AiTextWriter? writer = null,
    IEnumerable<IMusicLibrary>? musicLibraries = null,
    GeneratorSetup? generatorSetup = null,
    SchedularrTV.Service.Images.AutomaticImages? automaticImages = null) : BackgroundService, IScheduleFinisher
{
    private const double MaxTempo = 1.35;

    // Promos this far out wait for the schedule to reach every programme they list.
    private static readonly TimeSpan PartialListingWindow = TimeSpan.FromHours(2);

    // Read on each use: settings saved in the app apply straight away.
    private GeneratedPromoOptions _options => options.Value;
    private readonly Lock _gate = new();
    private readonly PriorityQueue<ResolvedGeneratedPromo, DateTimeOffset> _queue = new();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _failedAt = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _work = new(0);
    private readonly Dictionary<string, GeneratedPromoJob> _rendering = new(StringComparer.Ordinal);
    private int _batchTotal;
    private int _batchDone;
    private int _batchFailed;
    private DateTimeOffset _cleanedAt = DateTimeOffset.MinValue;
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _voiceCalls = new(StringComparer.Ordinal);
    private const string PartialPrefix = "partial-";

    /// <summary>
    /// Where renders go: the folder chosen in the plugin's settings, else the older schedule-file setting
    /// (<c>generated-promos: path:</c>), else the default.
    /// </summary>
    public string Root => Path.GetFullPath(
        _options.SaveFolder is { Length: > 0 } chosen ? chosen
        : config?.Current.Settings.GeneratedPromosPath is { Length: > 0 } configured ? configured
        : DefaultRoot);

    /// <summary>Whether the folder comes from the plugin's settings rather than the schedule file or the default.</summary>
    public bool FolderFromPluginSettings => _options.SaveFolder is { Length: > 0 };

    /// <summary>Where files go when the schedule settings don't name a folder.</summary>
    public string DefaultRoot => Path.GetFullPath(_options.OutputPath);

    public string VoiceDirectory => Path.Combine(Root, "voice");

    /// <summary>Editor preview renders, kept apart from what airs.</summary>
    public const string PreviewFolder = "_previews";

    /// <summary>Where a template's renders live under <see cref="Root"/>: channel, then block when a block defines it, then template.</summary>
    public static string Folder(GeneratedPromoSource source, GeneratedPromoTemplate template) =>
        Path.Combine([
            FolderName(string.IsNullOrWhiteSpace(source.Owner.Name) ? source.Owner.Id : source.Owner.Name),
            .. source.DefinedIn is { } block ? [FolderName(block.Name)] : Array.Empty<string>(),
            FolderName(template.Name)
        ]);

    /// <summary>A cached render by its path under <see cref="Root"/>, or null for anything outside it.</summary>
    public string? RenderedFile(string relativePath)
    {
        string root = Root;
        string full = Path.GetFullPath(Path.Combine(root, relativePath));
        return full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && RenderName().IsMatch(Path.GetFileName(full)) && File.Exists(full)
            ? full
            : null;
    }

    public string RelativePath(string path) => Path.GetRelativePath(Root, path);

    /// <summary>Raised after a render lands on disk, from a worker thread.</summary>
    public event Action? Rendered;

    /// <summary>Raised from a worker thread when promos are queued, start or finish.</summary>
    public event Action? QueueChanged;

    /// <summary>Null when nothing is queued or rendering.</summary>
    public GeneratedPromoQueueStatus? Queue
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count == 0 ? null : new GeneratedPromoQueueStatus(
                    _batchDone, _batchFailed, Math.Max(_batchTotal, _batchDone + _batchFailed + _pending.Count),
                    [.. _rendering.Values.OrderBy(job => job.AirsAt)],
                    _queue.TryPeek(out _, out DateTimeOffset next) ? next : null);
            }
        }
    }

    public int Pending
    {
        get
        {
            lock (_gate) return _pending.Count;
        }
    }

    // Each of these is the plugin's own setting (Plugins page), else what an earlier version kept in the schedule file
    // (or its default).

    /// <summary>How long before a promo airs it's rendered; zero renders the whole playout ahead.</summary>
    public TimeSpan LeadTime(SchedulerSettings settings) => _options.LeadTime ?? settings.GeneratedPromosLeadTime;

    public bool WatchedOnly(SchedulerSettings settings) => _options.WatchedOnly ?? settings.GeneratedPromosWatchedOnly;

    public TimeSpan KeepVideos(SchedulerSettings? settings) => _options.KeepVideos ?? settings?.GeneratedPromosKeepVideos ?? _options.KeepFor;

    public TimeSpan KeepAudio(SchedulerSettings? settings) => _options.KeepAudio ?? settings?.GeneratedPromosKeepAudio ?? TimeSpan.Zero;

    // Rendering far ahead wastes work on slots a later rebuild replaces, leaving orphaned files behind.
    private bool WithinLead(SchedulerSettings settings, DateTimeOffset airsAt, DateTimeOffset now) =>
        LeadTime(settings) is var lead && (lead <= TimeSpan.Zero || airsAt - now <= lead);

    // Unknown counts as watched, so a playout target that can't report viewers still gets its promos.
    private bool Watched(SchedulerSettings settings, int channelNumber) =>
        !WatchedOnly(settings) || viewers?.IsWatched(channelNumber) != false;

    /// <summary>Returns the same schedule instances when nothing needs to change.</summary>
    public IReadOnlyList<ChannelSchedule> Prepare(SchedulerConfig config, IReadOnlyList<ChannelSchedule> schedules, DateTimeOffset now)
    {
        HashSet<string> referenced = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> wanted = new(StringComparer.Ordinal);
        bool queued = false;
        List<ChannelSchedule> prepared = new(schedules.Count);

        foreach (ChannelSchedule schedule in schedules)
        {
            ChannelConfig? channel = config.Channels.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, schedule.ChannelId, StringComparison.OrdinalIgnoreCase));
            if (!schedule.Entries.Any(entry => entry.GeneratedTemplate is not null))
            {
                prepared.Add(schedule);
                continue;
            }

            bool watched = Watched(config.Settings, schedule.ChannelNumber);
            TimeZoneInfo zone = SchedulingEngine.ResolveTimeZone(
                string.IsNullOrWhiteSpace(channel?.TimeZone) ? config.Settings.TimeZone : channel.TimeZone);
            List<ScheduleEntry>? changed = null;

            for (int index = 0; index < schedule.Entries.Count; index++)
            {
                ScheduleEntry entry = schedule.Entries[index];
                if (entry.GeneratedTemplate is not { } name || entry.Item is not { } item || entry.End <= now)
                {
                    continue;
                }

                GeneratedPromoSource? source = channel is null ? null : GeneratedPromoPlanner.Source(config, channel, entry.BlockName);
                GeneratedPromoTemplate? template = source?.Promos.Templates
                    .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                string? path = null;

                if (template is { Enabled: true } && source is not null && GeneratedPromoEligibility.IsAvailable(template, entry.Start, zone))
                {
                    int needed = GeneratedPromoResolver.ListingsNeeded(template);
                    IReadOnlyList<UpNextListing> listings = Subject(template.Type, schedule, entry, needed, template.UpcomingGrouping);
                    if (listings.Count >= needed || (listings.Count > 0 && entry.Start - now < PartialListingWindow))
                    {
                        ResolvedGeneratedPromo promo = GeneratedPromoResolver.Resolve(template, source.Owner.Name, listings, zone, entry.Start,
                            GeneratedImageSubjects.FromSchedule(schedule, entry.Start, entry.End, template))!
                            with { Folder = Folder(source, template) };
                        path = Existing(promo);
                        if (path is not null)
                        {
                            referenced.Add(path);
                        }

                        if ((path is null || !File.Exists(FinalPath(promo))) && watched && WithinLead(config.Settings, entry.Start, now))
                        {
                            wanted.Add(FinalPath(promo));
                            queued |= Enqueue(promo, entry.Start, now);
                        }
                    }
                }

                if (!string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    changed ??= [.. schedule.Entries];
                    changed[index] = entry with { Item = WithPath(item, path) };
                }
            }

            prepared.Add(changed is null ? schedule : schedule with { Entries = changed });
        }

        if (now - _cleanedAt >= TimeSpan.FromHours(1))
        {
            _cleanedAt = now;
            Cleanup(referenced, now);
        }

        if (queued | Prune(wanted))
        {
            QueueChanged?.Invoke();
        }

        return prepared;
    }

    /// <summary>
    /// Which programme a slot promotes: what follows it, what preceded it (a sign-off, or the show a mid-show break
    /// interrupts), or its target airing.
    /// </summary>
    public static IReadOnlyList<UpNextListing> Subject(GeneratedPromoType type, ChannelSchedule schedule, ScheduleEntry entry, int needed,
        GeneratedUpcomingGrouping grouping = GeneratedUpcomingGrouping.Timeslot) =>
        type switch
        {
            GeneratedPromoType.ThisHasBeen or GeneratedPromoType.YoureWatching or GeneratedPromoType.BackToTheShow =>
                UpNextPromo.Previous(schedule, entry.Start) is { } previous ? [previous] : [],
            GeneratedPromoType.ComingUp or GeneratedPromoType.Eventually when entry.GeneratedTargetAt is { } at =>
                (string.IsNullOrWhiteSpace(entry.GeneratedSubject)
                    ? UpNextPromo.FirstFrom(schedule, at, OpenTargetReach, type == GeneratedPromoType.ComingUp ? Around(schedule, entry) : Array.Empty<string>())
                    : UpNextPromo.At(schedule, at, entry.GeneratedSubject)) is { } target ? [target] : [],
            GeneratedPromoType.ComingUp or GeneratedPromoType.Eventually => [],
            _ => UpNextPromo.Listings(schedule, entry.End, needed, grouping: grouping)
        };

    // An airing with no predicted show (a block of several shows, or rotation time) names the first programme from then.
    private static readonly TimeSpan OpenTargetReach = TimeSpan.FromHours(2);

    // The shows either side of a promo, which a Coming Up shouldn't name (the one after is Up Next's).
    private static string[] Around(ChannelSchedule schedule, ScheduleEntry entry) =>
        [.. new[] { UpNextPromo.Previous(schedule, entry.Start)?.Title, UpNextPromo.Listings(schedule, entry.End, 1).FirstOrDefault()?.Title }
            .OfType<string>()];

    /// <summary>The promo's file if it's been made already, else renders it now.</summary>
    public async Task<string> MadeOrRenderAsync(ResolvedGeneratedPromo promo, CancellationToken cancellationToken = default)
    {
        if (Existing(promo) is { } made && File.Exists(made))
        {
            Touch(made);
            return made;
        }

        return (await RenderAsync(promo, cancellationToken).ConfigureAwait(false)).Path;
    }

    /// <summary>Renders one promo, falling back to a silent file when the voice cannot be produced.</summary>
    public async Task<GeneratedPromoRender> RenderAsync(ResolvedGeneratedPromo promo, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Root);

        // An AI-written voice-over or caption is written now (or taken from what was written before); the usual words
        // stand in wherever it can't be.
        promo = await GeneratedPromoWriting.WriteAsync(promo, writer, cancellationToken).ConfigureAwait(false);

        // Automatic images are searched for now (or taken from what was found before); one that can't be found is left
        // out, and the promo is made again later in case it can be then.
        (promo, IReadOnlyList<string> missingImages) = await GeneratedPromoImages.FindAutomaticAsync(promo, automaticImages, wait: true, cancellationToken)
            .ConfigureAwait(false);
        string? imageProblem = missingImages.Count == 0 ? null
            : $"no picture found for {string.Join(", ", missingImages.Select(query => $"“{query}”"))}"
              + (automaticImages?.SetupProblem(null) is { } setup ? $" ({setup})" : string.Empty);
        GeneratedPromoTemplate template = promo.Template;
        string? audio = null;
        Exception? voiceError = null;
        double tempo = 1;

        if (promo.Script is { } script && template.Voice is { Enabled: true } voice)
        {
            try
            {
                audio = await VoiceAsync(voice, script, cancellationToken).ConfigureAwait(false);
                TimeSpan room = template.Duration - voice.Start - TimeSpan.FromMilliseconds(300);
                TimeSpan length = await ProbeAsync(audio, cancellationToken).ConfigureAwait(false);
                if (room > TimeSpan.Zero && length > room)
                {
                    tempo = Math.Min(MaxTempo, length / room);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                audio = null;
                voiceError = ex;
            }
        }

        string target = voiceError is null && imageProblem is null ? FinalPath(promo) : SilentPath(promo);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string partial = Path.Combine(Path.GetDirectoryName(target)!, $"{promo.Key}.{Guid.NewGuid():N}.partial.mp4");
        string? source = null;
        try
        {
            string? generated = null;
            if (template.Generator is { } generator)
            {
                if (promo.ClipPath is null && GeneratedPromoResolver.RequiresClip(generator))
                {
                    throw new InvalidOperationException("The generator needs {clip}, but the promoted show has no episode file that can be read from this PC.");
                }

                // The first time a generator's folder is used, its setup command (npm install and the like) runs there.
                if (generatorSetup is not null)
                {
                    await generatorSetup.EnsureAsync(generator, cancellationToken).ConfigureAwait(false);
                }

                source = Path.Combine(Path.GetDirectoryName(target)!, $"{promo.Key}.{Guid.NewGuid():N}.source{generator.OutputExtension}");
                generated = await ExternalPromoGenerator.RunAsync(generator, promo.GeneratorArguments, source, cancellationToken).ConfigureAwait(false);
            }

            PromoComposition composition = GeneratedPromoResolver.Compose(promo, audio, tempo, generated);
            composition = composition with
            {
                BackgroundAudio = await AudibleAsync(composition.BackgroundAudio, cancellationToken).ConfigureAwait(false),
                OverlayAudio = await AudibleAsync(composition.OverlayAudio, cancellationToken).ConfigureAwait(false)
            };
            // Video layers whose files have no sound track play silently rather than stopping the render.
            List<PromoMusic> layerAudio = [];
            foreach (PromoMusic sound in composition.LayerAudio)
            {
                if (await AudibleAsync(sound, cancellationToken).ConfigureAwait(false) is { } audible) layerAudio.Add(audible);
            }

            composition = composition with { LayerAudio = layerAudio };
            if (composition.Music is null && template.Music is { FromLibrary: true } fromLibrary)
            {
                composition = composition with { Music = await LibraryMusicAsync(fromLibrary, promo.Key, cancellationToken).ConfigureAwait(false) };
            }

            await new PromoRenderer(_options.FfmpegPath) { FfprobePath = _options.FfprobePath }
                .RenderAsync(composition, partial, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                File.Move(partial, target, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && File.Exists(target))
            {
                // Another render of the same promo finished at the same moment (Windows refuses to replace a file
                // that's still being moved into place or read). Its file is the same promo, so it stands; this
                // partial is deleted below.
            }
        }
        finally
        {
            File.Delete(partial);
            if (source is not null) File.Delete(source);
        }

        return new GeneratedPromoRender(target, voiceError, imageProblem);
    }

    /// <summary>Where songs from a music library are copied for rendering (those that aren't files already).</summary>
    public string MusicDirectory => Path.Combine(Root, "music");

    /// <summary>
    /// The promo's song from its music library (Jellyfin's, local music folders, a plugin's), fixed by the promo's key so
    /// a re-render sounds the same; null, and the promo rendered without music, when there's none or it can't be had.
    /// </summary>
    private async Task<PromoMusic?> LibraryMusicAsync(GeneratedMusicConfig music, string key, CancellationToken cancellationToken)
    {
        try
        {
            string? song = await SchedularrTV.Plugins.MusicFiles.PickFileAsync(music.Library!, musicLibraries ?? [], key, _options.FfmpegPath, MusicDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (song is null)
            {
                logger.LogWarning("Generated promo: no songs from {Music}; rendering without music.", music.Library!.Describe(musicLibraries));
                return null;
            }

            return await AudibleAsync(new PromoMusic(song, music.EffectiveVolume(song), music.StartFor(song), music.FadeIn, music.FadeOut, music.DuckUnderVoice)
            {
                DuckUnderBackground = music.DuckUnderClip == true
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Generated promo: couldn't get a song from {Music}: {Message}. Rendering without music.", music.Library!.Describe(musicLibraries), ex.Message);
            return null;
        }
    }

    /// <summary>The bed itself, or null when its file has no audio track, which ffmpeg would reject.</summary>
    private async Task<PromoMusic?> AudibleAsync(PromoMusic? bed, CancellationToken cancellationToken)
    {
        if (bed is null)
        {
            return null;
        }

        var info = new ProcessStartInfo(_options.FfprobePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in (string[])["-v", "error", "-select_streams", "a", "-show_entries", "stream=index", "-of", "csv=p=0", bed.Path])
        {
            info.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(info) ?? throw new InvalidOperationException("ffprobe could not be started.");
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await error.ConfigureAwait(false);
        return process.ExitCode == 0 && output.Trim().Length > 0 ? bed : null;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, Math.Max(1, _options.Concurrency)).Select(_ => WorkAsync(stoppingToken)));

    public override void Dispose()
    {
        _work.Dispose();
        base.Dispose();
    }

    private async Task WorkAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _work.WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            ResolvedGeneratedPromo? promo;
            lock (_gate)
            {
                if (!_queue.TryDequeue(out promo, out DateTimeOffset airsAt))
                {
                    continue;
                }

                _rendering[FinalPath(promo)] = new GeneratedPromoJob(
                    promo.Folder.Replace(Path.DirectorySeparatorChar.ToString(), " › ", StringComparison.Ordinal), airsAt, clock.GetUtcNow());
            }

            QueueChanged?.Invoke();
            bool failed = false;
            try
            {
                GeneratedPromoRender render = await RenderAsync(promo, stoppingToken).ConfigureAwait(false);
                if (render.VoiceError is { } error)
                {
                    logger.LogWarning("Voice-over for generated promo '{Template}' failed; using a silent render until retry: {Message}",
                        promo.Template.Name, error.GetBaseException().Message);
                    lock (_gate) _failedAt[FinalPath(promo)] = clock.GetUtcNow();
                }
                else if (render.ImageProblem is { } imageProblem)
                {
                    logger.LogWarning("Generated promo '{Template}' was made without some automatic images; it's made again later: {Problem}",
                        promo.Template.Name, imageProblem);
                    lock (_gate) _failedAt[FinalPath(promo)] = clock.GetUtcNow();
                }

                Rendered?.Invoke();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failed = true;
                logger.LogError(ex, "Generated promo '{Template}' failed to render.", promo.Template.Name);
                lock (_gate) _failedAt[FinalPath(promo)] = clock.GetUtcNow();
            }
            finally
            {
                lock (_gate)
                {
                    _pending.Remove(FinalPath(promo));
                    _rendering.Remove(FinalPath(promo));
                    if (failed) _batchFailed++; else _batchDone++;
                    if (_pending.Count == 0)
                    {
                        _batchTotal = _batchDone = _batchFailed = 0;
                    }
                }

                QueueChanged?.Invoke();
            }
        }
    }

    private bool Enqueue(ResolvedGeneratedPromo promo, DateTimeOffset airsAt, DateTimeOffset now)
    {
        string target = FinalPath(promo);
        lock (_gate)
        {
            if (_pending.Contains(target)
                || (_failedAt.TryGetValue(target, out DateTimeOffset failed) && now - failed < _options.RetryAfter))
            {
                return false;
            }

            _pending.Add(target);
            _batchTotal++;
            _queue.Enqueue(promo, airsAt);
        }

        _work.Release();
        return true;
    }

    /// <summary>
    /// Drops queued renders no slot asks for any more: the viewer left, the slot aired while waiting,
    /// or a rebuild replaced it. Renders already under way are left to finish.
    /// </summary>
    private bool Prune(HashSet<string> wanted)
    {
        lock (_gate)
        {
            List<(ResolvedGeneratedPromo Element, DateTimeOffset Priority)> keep = [];
            foreach ((ResolvedGeneratedPromo element, DateTimeOffset priority) in _queue.UnorderedItems)
            {
                if (wanted.Contains(FinalPath(element)))
                {
                    keep.Add((element, priority));
                }
                else
                {
                    _pending.Remove(FinalPath(element));
                }
            }

            int dropped = _queue.Count - keep.Count;
            if (dropped == 0)
            {
                return false;
            }

            _queue.Clear();
            _queue.EnqueueRange(keep);
            _batchTotal = Math.Max(0, _batchTotal - dropped);
            if (_pending.Count == 0)
            {
                _batchTotal = _batchDone = _batchFailed = 0;
            }

            return true;
        }
    }

    private async Task<string> VoiceAsync(GeneratedVoiceConfig voice, string script, CancellationToken cancellationToken)
    {
        ITextToSpeechProvider provider = voices.Get(voice.Provider);
        string identity = provider.CacheIdentity is { Length: > 0 } settings ? $"{settings}\u001f" : string.Empty;
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{voice.Provider}\u001f{identity}{voice.Voice}\u001f{script}")))[..32];
        if (CachedVoice(hash) is { } cached)
        {
            Touch(cached);
            return cached;
        }

        // Paid providers bill per call, so simultaneous renders of the same line share one request.
        Lazy<Task<string>> shared = _voiceCalls.GetOrAdd(hash, key => new Lazy<Task<string>>(() => SynthesizeVoiceAsync(provider, voice.Voice, script, key)));
        return await shared.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SynthesizeVoiceAsync(ITextToSpeechProvider provider, string voice, string script, string hash)
    {
        try
        {
            return CachedVoice(hash) ?? await WriteVoiceAsync(provider, voice, script, hash).ConfigureAwait(false);
        }
        finally
        {
            _voiceCalls.TryRemove(hash, out _);
        }
    }

    private async Task<string> WriteVoiceAsync(ITextToSpeechProvider provider, string voice, string script, string hash)
    {
        TtsAudio audio = await provider.SynthesizeAsync(script, voice, CancellationToken.None).ConfigureAwait(false);
        Directory.CreateDirectory(VoiceDirectory);
        string path = Path.Combine(VoiceDirectory, $"{hash}.{audio.Extension}");
        string partial = Path.Combine(VoiceDirectory, $"{PartialPrefix}{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllBytesAsync(partial, audio.Data).ConfigureAwait(false);
            File.Move(partial, path, overwrite: true);
        }
        finally
        {
            File.Delete(partial);
        }

        return path;
    }

    private string? CachedVoice(string hash) =>
        Directory.Exists(VoiceDirectory) ? Directory.EnumerateFiles(VoiceDirectory, $"{hash}.*").FirstOrDefault() : null;

    /// <summary>Marks a reused voice-over as recently used, so cache expiry counts from its last use.</summary>
    private void Touch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, clock.GetUtcNow().UtcDateTime);
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Could not refresh voice-over {Path}.", path);
        }
    }

    private async Task<TimeSpan> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(_options.FfprobePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in (string[])["-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", path])
        {
            info.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(info) ?? throw new InvalidOperationException("ffprobe could not be started.");
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode == 0 && double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            ? TimeSpan.FromSeconds(seconds)
            : throw new InvalidOperationException($"ffprobe could not read the voice-over: {await error.ConfigureAwait(false)}");
    }

    private string? Existing(ResolvedGeneratedPromo promo) =>
        File.Exists(FinalPath(promo)) ? FinalPath(promo) : File.Exists(SilentPath(promo)) ? SilentPath(promo) : null;

    private string FinalPath(ResolvedGeneratedPromo promo) => Path.Combine(Root, promo.Folder, $"{promo.Key}.mp4");

    private string SilentPath(ResolvedGeneratedPromo promo) => Path.Combine(Root, promo.Folder, $"{promo.Key}.silent.mp4");

    private static string FolderName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new string([.. name.Select(character => invalid.Contains(character) ? '_' : character)]).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "_" : cleaned;
    }

    [GeneratedRegex("^[0-9a-f]{32}(\\.silent)?\\.mp4$")]
    private static partial Regex RenderName();

    private void Cleanup(HashSet<string> referenced, DateTimeOffset now)
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        // Without schedule settings (tools, tests) paid voice-overs are never thrown away.
        TimeSpan keepVideos = KeepVideos(config?.Current.Settings);
        TimeSpan keepAudio = KeepAudio(config?.Current.Settings);

        // The editor's extracted frames have a much shorter life and a size budget of their own.
        string previewFrames = Path.Combine(Root, PreviewFrameCache.FolderName);
        PreviewFrameCache.Trim(previewFrames, now.UtcDateTime);

        foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            if (file.StartsWith(previewFrames + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                bool voice = string.Equals(Path.GetDirectoryName(file), VoiceDirectory, StringComparison.OrdinalIgnoreCase);
                bool partial = Path.GetFileName(file).StartsWith(PartialPrefix, StringComparison.Ordinal);
                TimeSpan keep = partial ? _options.KeepFor : voice ? keepAudio : keepVideos;
                if (keep > TimeSpan.Zero && !referenced.Contains(file) && now - File.GetLastWriteTimeUtc(file) > keep)
                {
                    File.Delete(file);
                }
            }
            catch (IOException ex)
            {
                logger.LogDebug(ex, "Could not delete old generated promo file {Path}.", file);
            }
        }

        // Deepest first, so a template folder emptied above lets its channel folder go too.
        foreach (string directory in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(directory => directory.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch (IOException ex)
            {
                logger.LogDebug(ex, "Could not remove empty generated promo folder {Path}.", directory);
            }
        }
    }

    private static MediaItem WithPath(MediaItem item, string? path) => new()
    {
        Id = item.Id,
        Title = item.Title,
        Kind = item.Kind,
        Duration = item.Duration,
        Path = path
    };
}

/// <summary>Republishes shortly after renders land so finished files replace black placeholders.</summary>
public sealed class GeneratedPromoRefresher(
    GeneratedPromoService promos,
    ScheduleCoordinator coordinator,
    ILogger<GeneratedPromoRefresher> logger) : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(10);

    // How often slots are re-checked for entering the lead time or gaining a viewer.
    private static readonly TimeSpan LeadCheck = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var signal = new SemaphoreSlim(0, 1);
        void OnRendered()
        {
            try
            {
                signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // A refresh is already due.
            }
        }

        promos.Rendered += OnRendered;
        try
        {
            while (true)
            {
                bool rendered = await signal.WaitAsync(LeadCheck, stoppingToken).ConfigureAwait(false);
                if (rendered)
                {
                    await Task.Delay(Debounce, stoppingToken).ConfigureAwait(false);
                }

                try
                {
                    await coordinator.RefreshFinishedSchedulesAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Could not publish newly rendered promos.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            promos.Rendered -= OnRendered;
        }
    }
}
