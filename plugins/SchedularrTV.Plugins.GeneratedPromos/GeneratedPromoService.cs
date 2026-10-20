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

public sealed record GeneratedPromoRender(string Path, Exception? VoiceError)
{
    /// <summary>For a promo with a layer curated by AI: how its plan came about (or why there wasn't one).</summary>
    public CurationReport? Curation { get; init; }
}

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
    SchedularrTV.Service.Images.AutomaticImages? automaticImages = null,
    IEnumerable<ISpeechTranscriber>? transcribers = null,
    PromoVideoCleanupService? cleanup = null) : BackgroundService, IScheduleFinisher
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
    private PromoTranscripts? _transcripts;

    /// <summary>Where episodes' transcripts are kept, for layers curated by AI.</summary>
    public string TranscriptDirectory => Path.Combine(Root, "transcripts");

    /// <summary>Episodes' transcripts, for layers curated by AI: made once each by a speech recognition plugin.</summary>
    public PromoTranscripts Transcripts
    {
        get
        {
            lock (_gate)
            {
                return _transcripts ??= new PromoTranscripts(transcribers ?? [], () => _options.FfmpegPath, () => TranscriptDirectory, logger);
            }
        }
    }

    // For the curation part of this class (GeneratedPromoService.Curation.cs), which can't see the constructor's parameters.
    private SchedularrTV.Service.Writing.AiTextWriter? Writer => writer;
    private ILogger Log => logger;

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
            ChannelSchedule namedSchedule = UpNextPromo.WithPreferredNames(schedule, config);
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
                    IReadOnlyList<UpNextListing> listings = Subject(template.Type, namedSchedule, namedSchedule.Entries[index], needed, template.UpcomingGrouping);

                    // A template for some programmes only: the one the promo turned out to be about has to match too
                    // (when it couldn't be known at planning, or the prediction was wrong); otherwise the stand-in airs.
                    bool suits = !GeneratedPromoEligibility.HasProgrammeQuery(template) || (listings.Count > 0
                        && GeneratedPromoEligibility.SuitsProgramme(template, UpNextPromo.ItemFor(namedSchedule, listings[0])));
                    if (suits && (listings.Count >= needed || (listings.Count > 0 && entry.Start - now < PartialListingWindow)))
                    {
                        // Variable images' automatic pictures are looked for (in the background) for promos about to be made.
                        bool making = watched && WithinLead(config.Settings, entry.Start, now);
                        ResolvedGeneratedPromo promo = GeneratedPromoResolver.Resolve(template, source.Owner.Name, listings, zone, entry.Start,
                            GeneratedImageSubjects.FromSchedule(namedSchedule, entry.Start, entry.End, template), automaticImages?.Lookup(queue: making),
                            new GeneratedVoiceContext(channel!.Id, entry.BlockName, channel.Name),
                            // Variants limited to some programmes check the one this promo is about.
                            listings.Count > 0 && template.HasVariants ? UpNextPromo.ItemFor(namedSchedule, listings[0]) : null)!
                            with { Folder = Folder(source, template) };
                        path = Existing(promo);
                        if (path is not null)
                        {
                            referenced.Add(path);
                        }

                        // One whose picture is still being searched for is made once it's found (or the search gives up),
                        // on a later pass, rather than twice.
                        if ((path is null || !File.Exists(FinalPath(promo))) && making && !promo.ImagesPending)
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
            GeneratedPromoType.ThisHasBeen or GeneratedPromoType.YoureWatching or GeneratedPromoType.BackToTheShow
                or GeneratedPromoType.RightBack =>
                UpNextPromo.Previous(schedule, entry.Start) is { } previous ? [previous] : [],
            GeneratedPromoType.NewEpisode =>
                UpNextPromo.NextNew(schedule, entry.End, Around(schedule, entry)) is { } fresh ? [fresh] : [],
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
        using WorkScope? work = Work.BeginUnlessWithin("Generated Promos", "Generating promo", promo.Template.Name);
        Directory.CreateDirectory(Root);

        // An AI-written voice-over or caption is written now (or taken from what was written before); the usual words
        // stand in wherever it can't be.
        promo = await GeneratedPromoWriting.WriteAsync(promo, writer, cancellationToken).ConfigureAwait(false);
        GeneratedPromoTemplate template = promo.Template;
        string? audio = null;
        Exception? voiceError = null;
        double tempo = 1;

        // A layer curated by AI: its clips picked and its lines written and spoken now. Whenever that can't be done the
        // promo is made the usual way: its read-out over the layer's one clip.
        CuratedRender? curated = null;
        CurationReport? report = null;
        if (promo.Curation is not null && template.Voice is { Enabled: true } curatedVoice)
        {
            try
            {
                (curated, report) = await CurateAsync(promo, curatedVoice, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Generated promo '{Template}': couldn't curate its clips, so it's made the usual way: {Message}",
                    template.Name, ex.GetBaseException().Message);
                report = (report ?? new CurationReport()) with { Problem = $"it failed: {ex.GetBaseException().Message}" };
            }
        }

        if (curated is not null)
        {
            audio = curated.VoicePath;
            promo = promo with { CuratedClips = curated.Timeline.Clips };
        }
        else if (promo.Prerecorded is { } recorded && template.Voice is { Enabled: true } recordedVoice)
        {
            try
            {
                (audio, tempo) = await PrerecordedAsync(recorded, template.Duration - recordedVoice.Start - TimeSpan.FromMilliseconds(300), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                audio = null;
                voiceError = ex;
            }
        }
        else if (promo.Script is { } script && template.Voice is { Enabled: true } voice && (!voice.IsPrerecorded || promo.TtsFallback))
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

        string target = voiceError is null ? FinalPath(promo) : SilentPath(promo);
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
            if (cleanup is not null)
                composition = await cleanup.PrepareAsync(composition, template, wait: true, cancellationToken).ConfigureAwait(false);
            if (curated is not null)
            {
                // The curated voice-over is already placed (and paced) in the promo's time.
                composition = composition with { AudioStart = TimeSpan.Zero, AudioTempo = 1 };
            }

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

        return new GeneratedPromoRender(target, voiceError) { Curation = report };
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
        Work.Track(process, "Generated Promos", "Probing media");
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

    // Takes' lengths by path and when the file last changed, so they're measured once each.
    private readonly ConcurrentDictionary<string, TimeSpan> _takeLengths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long a recorded take is, measured once (and again whenever its file changes).</summary>
    public async Task<TimeSpan> TakeLengthAsync(string path, CancellationToken cancellationToken = default)
    {
        string key = $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}";
        if (_takeLengths.TryGetValue(key, out TimeSpan known))
        {
            return known;
        }

        TimeSpan length = await ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        _takeLengths[key] = length;
        return length;
    }

    /// <summary>
    /// A pre-recorded voice-over put together: the template and takes that fit <paramref name="room"/> (see
    /// <see cref="PrerecordedVoice.Choose"/>), said one after another, and how much it's sped up when even the
    /// shortest runs long (only if the template allows it).
    /// </summary>
    public async Task<(string Path, double Tempo)> PrerecordedAsync(PrerecordedVoiceRequest request, TimeSpan room, CancellationToken cancellationToken = default)
    {
        (string path, _, double tempo) = await SayPrerecordedAsync(request, room, cancellationToken).ConfigureAwait(false);
        return (path, tempo);
    }

    /// <summary>
    /// Chooses the template and takes for <paramref name="room"/> and puts them together: the file, what was chosen, and
    /// how much it's sped up. With <see cref="PrerecordedVoiceRequest.TrimSilence"/>, each take's silent ends are trimmed
    /// first (once per version of its file), and the trimmed takes are what's measured and joined.
    /// </summary>
    public async Task<(string Path, PrerecordedChoice Choice, double Tempo)> SayPrerecordedAsync(PrerecordedVoiceRequest request, TimeSpan room,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> used = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, TimeSpan> lengths = new(StringComparer.OrdinalIgnoreCase);
        foreach (string take in request.Candidates.SelectMany(candidate => candidate.Parts).SelectMany(part => part.Takes).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Cleaned first, so trimming and levelling go by the voice rather than the noise.
            string file = request.CleanFilter is { } clean ? await CleanedAsync(take, clean, cancellationToken).ConfigureAwait(false) : take;
            if (request.TrimSilence)
            {
                file = await TrimmedAsync(file, cancellationToken).ConfigureAwait(false);
            }

            if (request.NormalizeLevels)
            {
                file = await LevelledAsync(file, cancellationToken).ConfigureAwait(false);
            }

            used[take] = file;
            lengths[take] = await TakeLengthAsync(file, cancellationToken).ConfigureAwait(false);
        }

        PrerecordedChoice choice = PrerecordedVoice.Choose(request, take => lengths[take], room);
        string path = await AssembleAsync([.. choice.Takes.Select(take => used[take])], choice.Joints, cancellationToken).ConfigureAwait(false);
        double tempo = 1;
        if (request.SpeedUp && room > TimeSpan.Zero && choice.Length > room)
        {
            tempo = Math.Min(MaxTempo, choice.Length / room);
        }

        return (path, choice, tempo);
    }

    // Quieter than this counts as silence at a take's ends; a little is left on so words aren't clipped.
    private const string TrimFilter = "silenceremove=start_periods=1:start_threshold=-45dB:start_silence=0.02,areverse,"
        + "silenceremove=start_periods=1:start_threshold=-45dB:start_silence=0.03,areverse";

    /// <summary>A take with the silence at either end trimmed, kept in the voice-over folder (and made again if the take changes).</summary>
    public async Task<string> TrimmedAsync(string take, CancellationToken cancellationToken = default)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"trimmed\u001f{TrimFilter}\u001f{Path.GetFullPath(take)}\u001f{File.GetLastWriteTimeUtc(take).Ticks}")))[..32];
        if (CachedVoice(hash) is { } cached)
        {
            Touch(cached);
            return cached;
        }

        Directory.CreateDirectory(VoiceDirectory);
        string path = Path.Combine(VoiceDirectory, $"{hash}.wav");
        await FfmpegToVoiceAsync(["-i", take, "-vn", "-af", $"aformat=sample_fmts=s16:sample_rates=48000:channel_layouts=stereo,{TrimFilter}"],
            path, "could not trim a take", cancellationToken).ConfigureAwait(false);

        // A take that's all silence (or too quiet) would trim to nothing; it's used as it is instead.
        if (new FileInfo(path).Length < 4096)
        {
            File.Delete(path);
            return take;
        }

        return path;
    }

    /// <summary>
    /// A take with a voice pack's cleaning applied (<see cref="VoiceCleanup.Filter"/>), kept in the voice-over folder and
    /// made again if the take or the cleaning changes.
    /// </summary>
    public async Task<string> CleanedAsync(string take, string filter, CancellationToken cancellationToken = default)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"cleaned\u001f{filter}\u001f{Path.GetFullPath(take)}\u001f{File.GetLastWriteTimeUtc(take).Ticks}")))[..32];
        if (CachedVoice(hash) is { } cached)
        {
            Touch(cached);
            return cached;
        }

        Directory.CreateDirectory(VoiceDirectory);
        string path = Path.Combine(VoiceDirectory, $"{hash}.wav");
        await FfmpegToVoiceAsync(["-i", take, "-vn", "-af", $"aformat=sample_fmts=s16:sample_rates=48000:channel_layouts=stereo,{filter}"],
            path, "could not clean up a take", cancellationToken).ConfigureAwait(false);
        return path;
    }

    /// <summary>
    /// The pitch of a steady whine or squeal in some takes (see <see cref="VoiceCleanup.FindWhine"/>), read from up to
    /// 30 seconds of each; null when there's no clear one.
    /// </summary>
    public async Task<double?> FindWhineAsync(IEnumerable<string> takes, CancellationToken cancellationToken = default)
    {
        List<float[]> decoded = [];
        foreach (string take in takes.Where(File.Exists))
        {
            decoded.Add(await MonoSamplesAsync(take, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false));
        }

        return VoiceCleanup.FindWhine(decoded);
    }

    /// <summary>A recording as mono samples at <see cref="VoiceCleanup.Rate"/>, up to <paramref name="most"/> of it.</summary>
    private async Task<float[]> MonoSamplesAsync(string path, TimeSpan most, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(_options.FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in (string[])["-hide_banner", "-loglevel", "error", "-i", path, "-vn", "-t", most.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                     "-ac", "1", "-ar", VoiceCleanup.Rate.ToString(CultureInfo.InvariantCulture), "-f", "f32le", "-"])
        {
            info.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg could not be started.");
        Work.Track(process, "Generated Promos", "Listening for a mic whine");
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        using MemoryStream raw = new();
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(raw, cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg could not read {Path.GetFileName(path)} (exit {process.ExitCode}): {(await error.ConfigureAwait(false)).Trim()}");
        }

        byte[] bytes = raw.ToArray();
        float[] samples = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * sizeof(float));
        return samples;
    }

    // Each take's measured change in dB, by file and version, so a take already at the target isn't measured every time.
    private readonly ConcurrentDictionary<string, double> _takeGains = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A take brought to the same loudness as every other (<see cref="PrerecordedVoice.TargetLoudness"/>), kept in the
    /// voice-over folder and made again if the take changes; the take itself when it's already close enough, or when it
    /// can't be measured (it's then joined as it is, rather than failing the voice-over).
    /// </summary>
    public async Task<string> LevelledAsync(string take, CancellationToken cancellationToken = default)
    {
        string version = $"{Path.GetFullPath(take)}\u001f{File.GetLastWriteTimeUtc(take).Ticks}";
        if (!_takeGains.TryGetValue(version, out double gain))
        {
            try
            {
                (double? integrated, double? mean, double? peak) = await LoudnessAsync(take, cancellationToken).ConfigureAwait(false);
                gain = PrerecordedVoice.LevelGain(integrated, mean, peak);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                logger.LogWarning("Couldn't measure how loud {Take} is, so it's used as recorded: {Message}", take, ex.GetBaseException().Message);
                return take;
            }

            _takeGains[version] = gain;
        }

        if (gain == 0)
        {
            return take;
        }

        string amount = gain.ToString("0.##", CultureInfo.InvariantCulture);
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"levelled-v1\u001f{amount}\u001f{version}")))[..32];
        if (CachedVoice(hash) is { } cached)
        {
            Touch(cached);
            return cached;
        }

        Directory.CreateDirectory(VoiceDirectory);
        string path = Path.Combine(VoiceDirectory, $"{hash}.wav");
        await FfmpegToVoiceAsync(["-i", take, "-vn", "-af", $"aformat=sample_fmts=s16:sample_rates=48000:channel_layouts=stereo,volume={amount}dB"],
            path, "could not even out a take's level", cancellationToken).ConfigureAwait(false);
        return path;
    }

    /// <summary>A take's integrated loudness, mean volume and peak, as ffmpeg measures them.</summary>
    private async Task<(double? Integrated, double? Mean, double? Peak)> LoudnessAsync(string take, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(_options.FfmpegPath)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in (string[])["-hide_banner", "-nostats", "-i", take, "-vn", "-af", "ebur128=peak=true,volumedetect", "-f", "null", "-"])
        {
            info.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg could not be started.");
        Work.Track(process, "Generated Promos", "Measuring a take's level");
        Task<string> output = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }

        string text = await output.ConfigureAwait(false);
        return process.ExitCode == 0
            ? PrerecordedVoice.ParseLoudness(text)
            : throw new InvalidOperationException($"ffmpeg could not measure it (exit {process.ExitCode}): {text.Trim()}");
    }

    /// <summary>
    /// Takes said one after another, with the pause in <paramref name="joints"/> before each one after the first (a
    /// negative pause overlaps it with the one before, crossfaded), as one file in the voice-over folder (kept and reused
    /// like a spoken one).
    /// </summary>
    public async Task<string> AssembleAsync(IReadOnlyList<string> takes, IReadOnlyList<TimeSpan> joints, CancellationToken cancellationToken = default)
    {
        if (takes.Count == 0)
        {
            throw new InvalidOperationException("There are no takes to put together.");
        }

        TimeSpan JointAt(int index) => index - 1 < joints.Count ? joints[index - 1] : TimeSpan.Zero;
        string identity = string.Join('\u001f', takes.Select((take, index) =>
            $"{Path.GetFullPath(take)}|{File.GetLastWriteTimeUtc(take).Ticks}|{(index == 0 ? 0 : JointAt(index).Ticks)}"));
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"prerecorded-v2\u001f{identity}")))[..32];
        if (CachedVoice(hash) is { } cached)
        {
            Touch(cached);
            return cached;
        }

        static string Seconds(TimeSpan value) => value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        StringBuilder filter = new();
        for (int index = 0; index < takes.Count; index++)
        {
            // Every take in the same format, as they may have been recorded differently.
            filter.Append(CultureInfo.InvariantCulture, $"[{index}:a]aformat=sample_fmts=s16:sample_rates=48000:channel_layouts=stereo[a{index}];");
        }

        // Joined one at a time: silence then the next take, or the next take crossfaded over the end of what's there.
        string current = "a0";
        for (int index = 1; index < takes.Count; index++)
        {
            TimeSpan joint = JointAt(index);
            string next = $"j{index}";
            if (joint < TimeSpan.Zero)
            {
                filter.Append(CultureInfo.InvariantCulture, $"[{current}][a{index}]acrossfade=d={Seconds(-joint)}:c1=tri:c2=tri[{next}];");
            }
            else if (joint > TimeSpan.Zero)
            {
                filter.Append(CultureInfo.InvariantCulture, $"[{current}]apad=pad_dur={Seconds(joint)}[p{index}];[p{index}][a{index}]concat=n=2:v=0:a=1[{next}];");
            }
            else
            {
                filter.Append(CultureInfo.InvariantCulture, $"[{current}][a{index}]concat=n=2:v=0:a=1[{next}];");
            }

            current = next;
        }

        filter.Append(CultureInfo.InvariantCulture, $"[{current}]anull[out]");

        List<string> arguments = [];
        foreach (string take in takes)
        {
            arguments.Add("-i");
            arguments.Add(take);
        }

        arguments.AddRange(["-filter_complex", filter.ToString(), "-map", "[out]", "-vn"]);
        Directory.CreateDirectory(VoiceDirectory);
        string path = Path.Combine(VoiceDirectory, $"{hash}.wav");
        await FfmpegToVoiceAsync(arguments, path, "could not put the voice-over's takes together", cancellationToken).ConfigureAwait(false);
        return path;
    }

    /// <summary>Runs ffmpeg with <paramref name="arguments"/> (inputs and filters) writing a WAV to <paramref name="path"/>, moved into place once it's whole.</summary>
    private async Task FfmpegToVoiceAsync(IEnumerable<string> arguments, string path, string problem, CancellationToken cancellationToken)
    {
        string partial = Path.Combine(VoiceDirectory, $"{PartialPrefix}{Guid.NewGuid():N}");
        var info = new ProcessStartInfo(_options.FfmpegPath)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in (string[])["-hide_banner", "-loglevel", "error", "-y"])
        {
            info.ArgumentList.Add(argument);
        }

        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (string argument in (string[])["-c:a", "pcm_s16le", "-f", "wav", partial])
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg could not be started.");
            Work.Track(process, "Generated Promos", "Putting the voice-over together");
            Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }

            if (process.ExitCode != 0 || !File.Exists(partial))
            {
                throw new InvalidOperationException($"ffmpeg {problem} (exit {process.ExitCode}): {(await error.ConfigureAwait(false)).Trim()}");
            }

            File.Move(partial, path, overwrite: true);
        }
        finally
        {
            File.Delete(partial);
        }
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
        Work.Track(process, "Generated Promos", "Probing media");
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
            // Transcripts take minutes to make and keep for months; they tidy themselves.
            if (file.StartsWith(previewFrames + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || (cleanup is not null && file.StartsWith(cleanup.CacheDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                || file.StartsWith(TranscriptDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
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
