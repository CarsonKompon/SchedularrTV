using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Platform;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Plugins.Promos;
using SchedularrTV.Providers.Jellyfin;
using SchedularrTV.Service;
using SkiaSharp;

namespace SchedularrTV.CreditSqueeze;

/// <summary>What the squeeze renderer is up to, for the editor.</summary>
public sealed record SqueezeQueueStatus(int Waiting, string? Rendering, int Made, string? Problem);

/// <summary>
/// Makes the squeezes: a picture of the frame (the next programme's art, title and time, the ones after it) drawn once,
/// the credits played into its box with ffmpeg, the credits' own sound kept. Made shortly before they air (soonest
/// first), kept by content, so the same squeeze is made once; ones no schedule has used for a few days are deleted.
/// </summary>
public sealed class SqueezeRenderer(CreditSqueezeSettings settings, IServiceProvider services, TimeProvider clock, ILogger<SqueezeRenderer> logger)
    : BackgroundService
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(0.9);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(30);

    private readonly PriorityQueue<SqueezeJob, DateTimeOffset> _queue = new();
    private readonly HashSet<string> _queued = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _used = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly Lock _gate = new();
    private string? _rendering;
    private int _made;
    private string? _problem;
    private DateTimeOffset _cleanedAt = DateTimeOffset.MinValue;

    /// <summary>Raised when a squeeze has been made, so schedules are published again with it.</summary>
    public event Action? Made;

    public SqueezeQueueStatus Status
    {
        get
        {
            lock (_gate)
            {
                return new SqueezeQueueStatus(_queue.Count, _rendering, _made, _problem);
            }
        }
    }

    private static readonly JsonSerializerOptions HashJson = new(JsonSerializerDefaults.Web);

    /// <summary>A squeeze's name, from everything that shows in it.</summary>
    public static string Key(SqueezeJob job)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(job, HashJson)));
        return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }

    private string PathOf(SqueezeJob job) => Path.Combine(settings.SaveFolder, Key(job) + ".mp4");

    /// <summary>The squeeze's file when it's been made; null while it hasn't.</summary>
    public string? Ready(SqueezeJob job)
    {
        string path = PathOf(job);
        if (!File.Exists(path))
        {
            return null;
        }

        _used[path] = clock.GetUtcNow();
        return path;
    }

    // A squeeze waiting for its generated promo is made without it once it's this close to airing.
    private static readonly TimeSpan StopWaitingForPromo = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Asks for a squeeze airing at <paramref name="airsAt"/>; it's made once that's within the lead time (and, when
    /// <paramref name="waitForPromo"/>, only once it's nearly on, in case its promo is made meanwhile).
    /// </summary>
    public void Want(SqueezeJob job, DateTimeOffset airsAt, bool waitForPromo = false)
    {
        DateTimeOffset now = clock.GetUtcNow();
        string key = Key(job);
        if (airsAt < now || airsAt - now > (waitForPromo ? StopWaitingForPromo : settings.LeadTime)
            || (_failed.TryGetValue(key, out DateTimeOffset failed) && now - failed < RetryAfter))
        {
            return;
        }

        lock (_gate)
        {
            if (!_queued.Add(key))
            {
                return;
            }

            _queue.Enqueue(job, airsAt);
        }

        _wake.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(TimeSpan.FromMinutes(30), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Cleanup();
            while (Next() is { } job)
            {
                string key = Key(job);
                try
                {
                    await RenderAsync(job, PathOf(job), stoppingToken).ConfigureAwait(false);
                    lock (_gate)
                    {
                        _made++;
                        _problem = null;
                    }

                    Made?.Invoke();
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    _failed[key] = clock.GetUtcNow();
                    lock (_gate)
                    {
                        _problem = ex.Message;
                    }

                    logger.LogWarning("Credit squeeze: couldn't make the squeeze of {Source}: {Message}", Path.GetFileName(job.SourcePath), ex.Message);
                }
                finally
                {
                    lock (_gate)
                    {
                        _queued.Remove(key);
                        _rendering = null;
                    }
                }
            }
        }
    }

    private SqueezeJob? Next()
    {
        lock (_gate)
        {
            if (!_queue.TryDequeue(out SqueezeJob? job, out _))
            {
                return null;
            }

            _rendering = $"{job.Next.Title} (after {Path.GetFileNameWithoutExtension(job.SourcePath)})";
            return job;
        }
    }

    private string PreviewFolder => Path.Combine(settings.SaveFolder, "_previews");

    /// <summary>Makes a preview of a squeeze for the editor, replacing the channel's last one; returns its file name.</summary>
    public async Task<string> PreviewAsync(SqueezeJob job, string channelId, CancellationToken cancellationToken)
    {
        string name = $"{new string([.. channelId.Where(char.IsAsciiLetterOrDigit)])}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.mp4";
        Directory.CreateDirectory(PreviewFolder);
        foreach (string old in Directory.EnumerateFiles(PreviewFolder, $"{new string([.. channelId.Where(char.IsAsciiLetterOrDigit)])}-*.mp4"))
        {
            TryDelete(old);
        }

        await RenderAsync(job, Path.Combine(PreviewFolder, name), cancellationToken).ConfigureAwait(false);
        return name;
    }

    /// <summary>A preview by its file name; null for anything else.</summary>
    public string? PreviewFile(string name) =>
        name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.') && !name.Contains("..", StringComparison.Ordinal)
        && Path.Combine(PreviewFolder, name) is var path && File.Exists(path)
            ? path
            : null;

    /// <summary>The frame alone (no video), at preview size, for the editor to show as settings change.</summary>
    public static byte[] SampleFrame(SqueezeJob job) => DrawFrame(job, 640, 360, art: null);

    /// <summary>Makes a squeeze into <paramref name="output"/>: animated in, or (if this ffmpeg can't) starting in its box.</summary>
    public async Task RenderAsync(SqueezeJob job, string output, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        job = await WithMadePromoAsync(job, cancellationToken).ConfigureAwait(false);
        (job, string? playlist) = await WithCommercialsAsync(job, output, cancellationToken).ConfigureAwait(false);
        try
        {
            await RenderMadeAsync(FittedToPromo(job), output, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (playlist is not null)
            {
                TryDelete(playlist);
            }
        }
    }

    /// <summary>
    /// With commercials in the panel: the ones that fit the credits (after the promo, when it opens with one), joined
    /// into one clip that then plays as the panel's promo does. Returns that clip's file, to be deleted afterwards.
    /// </summary>
    private async Task<(SqueezeJob Job, string? Playlist)> WithCommercialsAsync(SqueezeJob job, string output, CancellationToken cancellationToken)
    {
        if (job.Panel == SqueezePanel.Promo || job.Commercials is not { Count: > 0 } pool)
        {
            return (job, null);
        }

        List<SqueezeClip> clips = [];
        TimeSpan room = job.Length - PromoStart;
        if (job.Panel == SqueezePanel.PromoThenCommercials && job.PromoPath is { } promo && job.PromoLength > TimeSpan.Zero)
        {
            clips.Add(new SqueezeClip(promo, job.PromoLength));
            room -= job.PromoLength;
        }

        List<SqueezeClip> ads = SqueezePlanner.FillWith(pool, room).Where(ad => File.Exists(ad.Path)).ToList();
        if (ads.Count == 0)
        {
            return (job, null);
        }

        clips.AddRange(ads);
        string playlist = output + ".panel.mp4";
        List<string> arguments = ["-y", "-hide_banner", "-loglevel", "error", "-nostdin"];
        List<string> parts = [];
        string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        for (int index = 0; index < clips.Count; index++)
        {
            SqueezeClip clip = clips[index];
            string length = N(clip.Length.TotalSeconds);
            arguments.AddRange(["-i", clip.Path]);
            parts.Add($"[{index}:v]trim=duration={length},setpts=PTS-STARTPTS,scale=1280:720:force_original_aspect_ratio=decrease:force_divisible_by=2,"
                      + $"pad=1280:720:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,format=yuv420p[v{index}]");
            parts.Add(await HasAudioAsync(clip.Path, cancellationToken).ConfigureAwait(false)
                ? $"[{index}:a]atrim=duration={length},asetpts=PTS-STARTPTS,aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,apad=whole_dur={length}[a{index}]"
                : $"anullsrc=r=48000:cl=stereo,atrim=duration={length},aformat=sample_fmts=fltp:channel_layouts=stereo[a{index}]");
        }

        string joined = string.Concat(Enumerable.Range(0, clips.Count).Select(index => $"[v{index}][a{index}]"));
        parts.Add($"{joined}concat=n={clips.Count}:v=1:a=1[v][a]");
        arguments.AddRange(["-filter_complex", string.Join(';', parts), "-map", "[v]", "-map", "[a]"]);
        RenderEncodingSettings encoding = CurrentEncoding;
        List<string> tail = ["-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k", "-ac", "2", playlist];
        (int code, string errors, _) = await RunAsync(settings.Ffmpeg, [.. arguments, .. VideoArguments(encoding, 1280, 720), .. tail], cancellationToken)
            .ConfigureAwait(false);
        if (code != 0 && encoding.Video.Encoder.Hardware)
        {
            // A hardware encoder this computer doesn't have mustn't lose the panel: the same join in software.
            (code, errors, _) = await RunAsync(settings.Ffmpeg, [.. arguments, .. VideoArguments(InSoftware(encoding), 1280, 720), .. tail], cancellationToken)
                .ConfigureAwait(false);
        }

        if (code != 0 || !File.Exists(playlist))
        {
            logger.LogWarning("Credit squeeze: couldn't join the commercials for the panel: {Error}", errors.Trim());
            TryDelete(playlist);
            return (job, null);
        }

        TimeSpan total = TimeSpan.FromTicks(clips.Sum(clip => clip.Length.Ticks));
        return (job with { PromoPath = playlist, PromoLength = total }, playlist);
    }

    private async Task RenderMadeAsync(SqueezeJob job, string output, CancellationToken cancellationToken)
    {
        (int width, int height) = settings.Size;
        byte[]? art = PlaysPromo(job) ? null : await ArtAsync(job.Next.ImageId, cancellationToken).ConfigureAwait(false);
        string panel = output + ".frame.png";
        string temp = output + ".making.mp4";
        if (IsVideo(job.BackgroundPath) && !File.Exists(job.BackgroundPath))
        {
            throw new InvalidOperationException($"The background video isn't there: {job.BackgroundPath}");
        }

        await File.WriteAllBytesAsync(panel, DrawFrame(job, width, height, art), cancellationToken).ConfigureAwait(false);
        bool creditsAudio = await HasAudioAsync(job.SourcePath, cancellationToken).ConfigureAwait(false);
        bool promoAudio = job.PromoPath is { } promo && await HasAudioAsync(promo, cancellationToken).ConfigureAwait(false);
        try
        {
            string? failure = null;
            RenderEncodingSettings encoding = CurrentEncoding;
            foreach (bool animate in job.Animate ? new[] { true, false } : new[] { false })
            {
                (int code, string errors, _) = await RunAsync(settings.Ffmpeg, Arguments(job, panel, temp, width, height, animate, creditsAudio, promoAudio,
                    encoding), cancellationToken).ConfigureAwait(false);
                if ((code != 0 || !File.Exists(temp)) && encoding.Video.Encoder.Hardware)
                {
                    // A hardware encoder this computer doesn't have mustn't stop the squeeze: from here on it's made in software.
                    encoding = InSoftware(encoding);
                    (code, errors, _) = await RunAsync(settings.Ffmpeg, Arguments(job, panel, temp, width, height, animate, creditsAudio, promoAudio,
                        encoding), cancellationToken).ConfigureAwait(false);
                }

                if (code == 0 && File.Exists(temp))
                {
                    File.Move(temp, output, overwrite: true);

                    // How far it runs past its credits, for the schedule to make room for.
                    string extra = ExtraPath(output);
                    if (job.Extra > TimeSpan.Zero)
                    {
                        await File.WriteAllTextAsync(extra, job.Extra.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        TryDelete(extra);
                    }

                    return;
                }

                failure = errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(line => line.Length > 0) ?? $"exit code {code}";
            }

            throw new InvalidOperationException($"ffmpeg couldn't make it: {failure}");
        }
        finally
        {
            TryDelete(panel);
            TryDelete(temp);
        }
    }

    private static string ExtraPath(string output) => Path.ChangeExtension(output, ".extra");

    /// <summary>How far a made squeeze runs past its credits (its promo playing on, full screen); zero when it doesn't.</summary>
    public TimeSpan ExtraOf(SqueezeJob job)
    {
        try
        {
            string path = ExtraPath(PathOf(job));
            return File.Exists(path) && double.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                ? TimeSpan.FromSeconds(Math.Max(0, seconds))
                : TimeSpan.Zero;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TimeSpan.Zero;
        }
    }

    /// <summary>
    /// The squeeze's length set by its promo: a preview (<see cref="SqueezeJob.FitToPromo"/>) runs just as long as the
    /// promo needs, rather than a set 20 seconds; and a promo longer than the credits plays on to its end, growing to
    /// fill the picture as they finish (on air, as far as the break after it has room for).
    /// </summary>
    public static SqueezeJob FittedToPromo(SqueezeJob job)
    {
        if (!PlaysPromo(job))
        {
            // A preview without a promo after all: the usual 20 seconds.
            return job.FitToPromo && job.Length > PreviewLength ? job with { Length = PreviewLength } : job;
        }

        TimeSpan wanted = PromoStart + job.PromoLength;
        if (job.FitToPromo && wanted < job.Length)
        {
            job = job with { Length = wanted };
        }

        TimeSpan over = wanted - job.Length;
        TimeSpan allowed = job.FitToPromo ? over : (over < job.MaxExtra ? over : job.MaxExtra);
        return over > TimeSpan.Zero && allowed > TimeSpan.FromSeconds(0.5) ? job with { Extra = allowed } : job;
    }

    /// <summary>
    /// A squeeze with no promo from the schedule gets one made by the channel's generated promos (when that plugin is on
    /// and the channel has a template of a wanted kind); otherwise it's left with the next programme's picture.
    /// </summary>
    private async Task<SqueezeJob> WithMadePromoAsync(SqueezeJob job, CancellationToken cancellationToken)
    {
        if (!job.WantsMadePromo || services.GetService<IPromoMaker>() is not { } maker
            || services.GetService<SchedulerConfigProvider>()?.Current is not { } config
            || config.Channels.FirstOrDefault(channel => string.Equals(channel.Id, job.ChannelId, StringComparison.OrdinalIgnoreCase)) is not { } channel
            || services.GetService<ScheduleCoordinator>()?.Latest
                .FirstOrDefault(schedule => string.Equals(schedule.ChannelId, job.ChannelId, StringComparison.OrdinalIgnoreCase)) is not { } schedule)
        {
            return job;
        }

        for (int index = 0; index < job.MakeKinds!.Count; index++)
        {
            SqueezePromo kind = job.MakeKinds[index];
            GeneratedPromoType type = kind switch
            {
                SqueezePromo.ComingUp => GeneratedPromoType.ComingUp,
                SqueezePromo.Eventually => GeneratedPromoType.Eventually,
                SqueezePromo.NowPlaying => GeneratedPromoType.NowPlaying,
                _ => GeneratedPromoType.UpNext
            };
            if (!maker.CanMake(config, channel, job.BlockName, type))
            {
                continue;
            }

            try
            {
                if (await maker.MakeAsync(config, channel, schedule, job.BlockName, type, job.NextStart, cancellationToken).ConfigureAwait(false) is { } made
                    && File.Exists(made.Path))
                {
                {
                    string heading = job.MakeHeadings is { } headings && index < headings.Count ? headings[index] : job.Heading;
                    SqueezeListing next = job.Next;

                    // A Coming Up or Eventually promo is for a later airing: the panel names that one.
                    if (kind is SqueezePromo.ComingUp or SqueezePromo.Eventually && made.Subject is { Length: > 0 } subject && made.SubjectStart is { } at)
                    {
                        TimeZoneInfo zone = SchedulingEngine.ResolveTimeZone(string.IsNullOrWhiteSpace(channel.TimeZone) ? config.Settings.TimeZone : channel.TimeZone);
                        string? image = schedule.Entries.FirstOrDefault(entry => entry.Start == at && entry.Item is not null)?.Item is { } item
                            ? item.SeriesId ?? (Guid.TryParse(item.Id, out _) ? item.Id : null)
                            : null;
                        next = new SqueezeListing(subject, made.SubjectEpisode, SqueezePlanner.When(at, zone, job.NextStart), image);
                    }

                    return job with { PromoPath = made.Path, PromoLength = made.Length, Heading = heading, Next = next };
                }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Credit squeeze: the channel's {Type} promo couldn't be made for the panel: {Message}", type, ex.Message);
            }
        }

        return job;
    }

    /// <summary>When the promo starts: once the credits have shrunk into place.</summary>
    public static readonly TimeSpan PromoStart = TimeSpan.FromSeconds(1.2);

    /// <summary>How long a preview without a promo runs.</summary>
    public static readonly TimeSpan PreviewLength = TimeSpan.FromSeconds(20);

    // How long the promo takes to fade up from black, and back down.
    private static readonly TimeSpan PromoFade = TimeSpan.FromSeconds(0.4);

    /// <summary>Whether a promo plays in the panel (one is set, and there's room for it after the credits settle).</summary>
    public static bool PlaysPromo(SqueezeJob job) =>
        job.PromoPath is not null && job.PromoLength > TimeSpan.Zero && job.Length > PromoStart + TimeSpan.FromSeconds(2);

    // The credits' sound while a promo with its own plays.
    private const double UnderPromo = 0.15;

    /// <summary>
    /// The ffmpeg command: the frame looped as the background, the credits scaled into their box (shrinking from the
    /// whole picture over the first second when <paramref name="animate"/>), the promo (if any) in the panel's picture
    /// once they're in place, and the sound: the credits' at the chosen volume, dipped under the promo's while it plays.
    /// </summary>
    /// <param name="creditsAudio">The credits have sound.</param>
    /// <param name="promoAudio">The promo has sound.</param>
    /// <param name="encoding">The <c>Rendering</c> app settings; those in use now if not given.</param>
    public static IReadOnlyList<string> Arguments(SqueezeJob job, string framePath, string output, int width, int height, bool animate,
        bool creditsAudio = true, bool promoAudio = true, RenderEncodingSettings? encoding = null)
    {
        encoding ??= CurrentEncoding;
        string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        SqueezeLayout layout = job.Layout(width, height);
        int boxX = layout.Credits.X, boxY = layout.Credits.Y, boxW = layout.Credits.Width, boxH = layout.Credits.Height;
        string length = N(job.Length.TotalSeconds);
        string total = N(job.Total.TotalSeconds);
        bool promo = PlaysPromo(job);
        promoAudio &= promo;

        // A promo longer than the credits plays on after them, growing from its slot to fill the picture as they end.
        bool runsOn = promo && job.Extra > TimeSpan.Zero;
        bool backgroundVideo = IsVideo(job.BackgroundPath);
        int promoInput = 2;
        int backgroundInput = promo ? 3 : 2;

        // The background: the frame itself, or (with a background video) the frame laid over the video, which loops.
        string video = backgroundVideo
            ? $"[{backgroundInput}:v]setpts=PTS-STARTPTS,scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height},setsar=1,fps=30,format=yuv420p[bgv];"
              + "[bgv][0:v]overlay=0:0:format=auto,format=yuv420p[bg]"
            : "[0:v]format=yuv420p[bg]";

        // The promo, fitted into the picture slot from the moment the credits are in place; the frame shows through after it ends.
        Rect slot = layout.Picture;
        string start = N(PromoStart.TotalSeconds);
        // It fades up from black (the frame leaves its slot black when there's a promo) and back down as it ends.
        string fades = $",fade=t=in:st={start}:d={N(PromoFade.TotalSeconds)}";
        TimeSpan promoEnds = PromoStart + job.PromoLength;
        if (promoEnds <= job.Total)
        {
            fades += $",fade=t=out:st={N((promoEnds - PromoFade).TotalSeconds)}:d={N(PromoFade.TotalSeconds)}";
        }

        video += promo && !runsOn
            ? $";[{promoInput}:v]setpts=PTS-STARTPTS+{start}/TB,{Fitted(job.PanelFit, slot.Width, slot.Height)}{fades}[promo];"
              + $"[bg][promo]overlay=x='{slot.X}+({slot.Width}-w)/2':y='{slot.Y}+({slot.Height}-h)/2':eof_action=pass:shortest=0[panelled]"
            : ";[bg]null[panelled]";

        // Running on, the promo goes over everything instead (added below): in its slot until the credits are nearly done,
        // then growing to fill the picture as they end.
        string credited = runsOn ? "[v0]" : "[v]";

        // The credits on top: they shrink in from the whole picture and (side by side) grow back out over the last moment.
        if (animate)
        {
            string d = N(Settle.TotalSeconds);
            string l = N(job.Length.TotalSeconds);

            // Quoted, so the commas in it don't split the filter graph.
            string into = $"if(gte(t,{d}),1,(t/{d})*(t/{d})*(3-2*(t/{d})))";
            string outOf = $"if(gte({l}-t,{d}),1,(({l}-t)/{d})*(({l}-t)/{d})*(3-2*(({l}-t)/{d})))";
            string e = layout.Style == SqueezeStyle.SideBySide && !runsOn ? $"min({into},{outOf})" : into;
            string w = $"w='trunc(({width}+({boxW}-{width})*{e})/2)*2'";
            string h = $"h='trunc(({height}+({boxH}-{height})*{e})/2)*2'";
            // Squeezed or cropped, the credits still start (and, side by side, end) as they play full screen: kept whole,
            // with bars, so the cut from the episode is seamless. They ease from that into their stretched or cropped
            // fit as they shrink, on a black picture the size of their box at that moment, which holds the bars and
            // trims what's outside it.
            string rw = $"({width}+({boxW}-{width})*{e})";
            string rh = $"({height}+({boxH}-{height})*{e})";
            string wholeW = $"min({rw},{rh}*dar)";
            string wholeH = $"min({rh},{rw}/dar)";
            string masked = $";color=c=black:s={width}x{height}:r=30:d={length},scale={w}:{h}:eval=frame[cmask];"
                            + "[1:v]setpts=PTS-STARTPTS,scale=w='{0}':h='{1}':eval=frame,setsar=1[fitted];"
                            + "[cmask][fitted]overlay=x='(main_w-overlay_w)/2':y='(main_h-overlay_h)/2':eval=frame:shortest=1,setsar=1[box]";
            video += job.CreditsFit switch
            {
                SqueezeFit.Squeeze => masked.Replace("{0}", $"trunc(({wholeW}+({rw}-{wholeW})*{e})/2)*2", StringComparison.Ordinal)
                    .Replace("{1}", $"trunc(({wholeH}+({rh}-{wholeH})*{e})/2)*2", StringComparison.Ordinal),
                SqueezeFit.Crop => masked.Replace("{0}", $"trunc(({wholeW}+(max({rw},{rh}*dar)-{wholeW})*{e})/2)*2", StringComparison.Ordinal)
                    .Replace("{1}", "trunc(ow/dar/2)*2", StringComparison.Ordinal),
                _ => $";[1:v]setpts=PTS-STARTPTS,scale={w}:{h}:force_original_aspect_ratio=decrease:force_divisible_by=2:eval=frame,setsar=1[box]"
            };
            // The panel comes in with them (and, side by side, goes with them), so nothing of it shows around credits that
            // don't fill the picture: side by side it slides in from its edge, framed it fades up from black.
            if (layout.Style == SqueezeStyle.SideBySide)
            {
                int panelW = layout.Panel.Width, panelH = layout.Panel.Height;
                (string x, string y) = job.Side switch
                {
                    CreditsSide.Right => ($"-(1-{e})*{panelW}", "0"),
                    CreditsSide.Top => ("0", $"(1-{e})*{panelH}"),
                    CreditsSide.Bottom => ("0", $"-(1-{e})*{panelH}"),
                    _ => ($"(1-{e})*{panelW}", "0")
                };
                video += $";color=c=black:s={width}x{height}:r=30:d={total}[stagebase];"
                         + $"[stagebase][panelled]overlay=x='{x}':y='{y}':eval=frame:shortest=1[stage]";
            }
            else
            {
                video += $";[panelled]fade=t=in:st=0:d={d}[stage]";
            }

            video += $";[stage][box]overlay=x='({width}/2+({boxX}+{boxW}/2-{width}/2)*{e})-w/2':y='({height}/2+({boxY}+{boxH}/2-{height}/2)*{e})-h/2'"
                     + $":eval=frame:eof_action=pass:shortest=0,format=yuv420p{credited}";
        }
        else
        {
            video += $";[1:v]setpts=PTS-STARTPTS,{Fitted(job.CreditsFit, boxW, boxH)}[box];"
                     + $"[panelled][box]overlay=x='{boxX}+({boxW}-w)/2':y='{boxY}+({boxH}-h)/2':eof_action=pass:shortest=0,format=yuv420p{credited}";
        }

        if (runsOn)
        {
            // g: 0 while the promo's in its slot, easing to 1 (the whole picture) over the moment before the credits end.
            string l = N(job.Length.TotalSeconds);
            string d = N(Settle.TotalSeconds);
            string a = N((job.Length - Settle).TotalSeconds);
            string g = animate
                ? $"if(lt(t,{a}),0,if(gte(t,{l}),1,((t-{a})/{d})*((t-{a})/{d})*(3-2*((t-{a})/{d}))))"
                : $"gte(t,{l})";
            string fit = job.PanelFit == SqueezeFit.Squeeze ? string.Empty : ":force_original_aspect_ratio=decrease:force_divisible_by=2";
            double centreX = slot.X + slot.Width / 2.0, centreY = slot.Y + slot.Height / 2.0;
            video += $";[{promoInput}:v]setpts=PTS-STARTPTS+{start}/TB,"
                     + $"scale=w='trunc(({slot.Width}+({width}-{slot.Width})*{g})/2)*2':h='trunc(({slot.Height}+({height}-{slot.Height})*{g})/2)*2'{fit}:eval=frame,setsar=1{fades}[promo];"
                     + $"[v0][promo]overlay=x='({N(centreX)}+({width}/2-{N(centreX)})*{g})-w/2':y='({N(centreY)}+({height}/2-{N(centreY)})*{g})-h/2'"
                     + ":eval=frame:eof_action=pass:shortest=0,format=yuv420p[v]";
        }

        List<string> arguments =
        [
            "-y", "-hide_banner", "-loglevel", "error", "-nostdin",
            "-loop", "1", "-framerate", "30", "-t", total, "-i", framePath,
            "-ss", N(job.SourceStart.TotalSeconds), "-t", length, "-i", job.SourcePath
        ];
        if (promo)
        {
            arguments.AddRange(["-i", job.PromoPath!]);
        }

        if (backgroundVideo)
        {
            arguments.AddRange(["-stream_loop", "-1", "-t", total, "-i", job.BackgroundPath!]);
        }

        // The sound: the credits' (dipped while a promo talks over them), the promo's from when it starts, or both mixed.
        string volume = N(job.CreditsVolume);
        string promoEnd = N((PromoStart + job.PromoLength).TotalSeconds);
        string? audio = (creditsAudio, promoAudio) switch
        {
            (true, true) => $"[1:a]asetpts=PTS-STARTPTS,volume='if(between(t,{start},{promoEnd}),{N(job.CreditsVolume * UnderPromo)},{volume})':eval=frame[ca];"
                            + $"[2:a]asetpts=PTS-STARTPTS,adelay={(int)PromoStart.TotalMilliseconds}:all=1[pa];"
                            + $"[ca][pa]amix=inputs=2:duration={(runsOn ? "longest" : "first")}:normalize=0,aresample=48000[a]",
            (false, true) => $"[2:a]asetpts=PTS-STARTPTS,adelay={(int)PromoStart.TotalMilliseconds}:all=1,apad,aresample=48000[a]",
            (true, false) => $"[1:a]asetpts=PTS-STARTPTS,volume={volume},{(runsOn ? "apad," : "")}aresample=48000[a]",
            _ => null
        };

        arguments.AddRange(["-filter_complex", audio is null ? video : $"{video};{audio}", "-map", "[v]"]);
        if (audio is not null)
        {
            arguments.AddRange(["-map", "[a]", "-c:a", "aac", "-b:a", "160k", "-ac", "2"]);
        }

        arguments.AddRange(["-t", total, "-r", "30"]);
        arguments.AddRange(VideoArguments(encoding, width, height));
        arguments.AddRange(["-pix_fmt", "yuv420p", "-movflags", "+faststart", output]);
        return arguments;
    }

    /// <summary>The preset squeezes are made with when nothing else is set (and off a Raspberry Pi).</summary>
    public const string StandardPreset = "veryfast";

    /// <summary>The <c>Rendering</c> app settings in use now (<c>veryfast</c>, or <c>superfast</c> on two threads on a Raspberry Pi).</summary>
    public static RenderEncodingSettings CurrentEncoding => RenderEncodingSettings.Current(StandardPreset);

    /// <summary>
    /// The <c>-c:v …</c> arguments: x264 at crf 20, or for a hardware encoder the 1080p bitrate scaled to this size
    /// (they have no quality setting).
    /// </summary>
    private static IReadOnlyList<string> VideoArguments(RenderEncodingSettings encoding, int width, int height) =>
        encoding.Video.VideoArguments(Math.Max(1500, (int)(encoding.HardwareBitrateKbps * (width * (double)height) / (1920 * 1080))), crf: 20,
            profileHigh: false);

    /// <summary>The same settings made in software, for when a hardware encoder can't be used.</summary>
    private static RenderEncodingSettings InSoftware(RenderEncodingSettings encoding) =>
        encoding with { Video = encoding.Video with { Encoder = H264Encoder.Software } };

    /// <summary>A video fitted into <paramref name="width"/>×<paramref name="height"/>: kept whole with room around it, stretched, or trimmed.</summary>
    private static string Fitted(SqueezeFit fit, int width, int height) => fit switch
    {
        SqueezeFit.Squeeze => $"scale={width}:{height},setsar=1",
        SqueezeFit.Crop => $"scale={width}:{height}:force_original_aspect_ratio=increase:force_divisible_by=2,crop={width}:{height},setsar=1",
        _ => $"scale={width}:{height}:force_original_aspect_ratio=decrease:force_divisible_by=2,setsar=1"
    };

    private static readonly string[] PictureTypes =[".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"];

    /// <summary>A background that's a video (anything set that isn't a picture), played under the frame.</summary>
    public static bool IsVideo(string? path) =>
        !string.IsNullOrWhiteSpace(path) && !PictureTypes.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool IsPicture(string? path) =>
        !string.IsNullOrWhiteSpace(path) && PictureTypes.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, bool> HasSound = new(StringComparer.Ordinal);

    /// <summary>Whether a file has a sound track (asked of ffprobe, beside ffmpeg; assumed so when it can't say).</summary>
    private async Task<bool> HasAudioAsync(string path, CancellationToken cancellationToken)
    {
        if (HasSound.TryGetValue(path, out bool known))
        {
            return known;
        }

        string ffmpeg = settings.Ffmpeg;
        string ffprobe = Path.IsPathRooted(ffmpeg)
            ? Path.Combine(Path.GetDirectoryName(ffmpeg)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe")
            : "ffprobe";
        try
        {
            (int code, _, string output) = await RunAsync(ffprobe,
                ["-v", "error", "-select_streams", "a", "-show_entries", "stream=index", "-of", "csv=p=0", path], cancellationToken).ConfigureAwait(false);
            bool has = code != 0 || output.Trim().Length > 0;
            if (code == 0)
            {
                HasSound[path] = has;
            }

            return has;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    /// <summary>How long a video runs (asked of ffprobe); null when it can't say.</summary>
    public async Task<TimeSpan?> DurationAsync(string path, CancellationToken cancellationToken)
    {
        string ffmpeg = settings.Ffmpeg;
        string ffprobe = Path.IsPathRooted(ffmpeg)
            ? Path.Combine(Path.GetDirectoryName(ffmpeg)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe")
            : "ffprobe";
        try
        {
            (int code, _, string output) = await RunAsync(ffprobe, ["-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", path],
                cancellationToken).ConfigureAwait(false);
            return code == 0 && double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>The next programme's picture from Jellyfin: a landscape still, else its backdrop, else its poster.</summary>
    private async Task<byte[]?> ArtAsync(string? itemId, CancellationToken cancellationToken)
    {
        if (itemId is null || services.GetService<JellyfinClient>() is not { } jellyfin)
        {
            return null;
        }

        foreach (string type in new[] { "Thumb", "Backdrop", "Primary" })
        {
            try
            {
                if (await jellyfin.GetImageAsync(itemId, type, 800, cancellationToken).ConfigureAwait(false) is { Content.Length: > 0 } image)
                {
                    return image.Content;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Credit squeeze: no {Type} picture for {Item}.", type, itemId);
            }
        }

        return null;
    }

    /// <summary>The frame around the credits: background, the box's border, and the side panel's up-next card.</summary>
    public static byte[] DrawFrame(SqueezeJob job, int width, int height, byte[]? art)
    {
        float scale = width / 1280f;
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using SKSurface surface = SKSurface.Create(info);
        SKCanvas canvas = surface.Canvas;
        canvas.Scale(scale);

        SKColor background = Colour(job.BackgroundColor, new SKColor(0x0b, 0x0d, 0x14));
        SKColor accent = Colour(job.AccentColor, new SKColor(0xff, 0x5c, 0x8a));
        SKColor soft = SKColors.White.WithAlpha(180);

        // The background: a picture, else the colour's gradient; with a background video it's left clear for the video.
        if (IsVideo(job.BackgroundPath))
        {
            canvas.Clear(SKColors.Transparent);
        }
        else
        {
            using (var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(1280, 720),
                       [background, Mix(background, accent, 0.18f), background], null, SKShaderTileMode.Clamp))
            using (var paint = new SKPaint { Shader = shader })
            {
                canvas.DrawRect(0, 0, 1280, 720, paint);
            }

            if (IsPicture(job.BackgroundPath) && File.Exists(job.BackgroundPath))
            {
                try
                {
                    using SKImage? backdrop = SKImage.FromEncodedData(job.BackgroundPath);
                    if (backdrop is not null)
                    {
                        Cover(canvas, backdrop, SKRect.Create(0, 0, 1280, 720));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        // Drawn at 1280×720 and scaled, so the layout is worked out at that size.
        SqueezeLayout layout = job.Layout();
        SKRect Sk(Rect rect) => SKRect.Create(rect.X, rect.Y, rect.Width, rect.Height);
        SKRect box = Sk(layout.Credits);
        SKRect words = Sk(layout.Text);
        bool framed = layout.Style == SqueezeStyle.Framed;
        string headingFont = job.HeadingFont;
        string textFont = job.TextFont;

        if (framed)
        {
            // The credits' box: black (the credits are fitted inside it) with an accent edge and a glow.
            using (var glow = new SKPaint { Color = accent.WithAlpha(90), IsAntialias = true, ImageFilter = SKImageFilter.CreateBlur(18, 18) })
            {
                canvas.DrawRoundRect(Inflate(box, 6), 16, 16, glow);
            }

            using (var black = new SKPaint { Color = SKColors.Black, IsAntialias = true })
            {
                canvas.DrawRoundRect(Inflate(box, 4), 12, 12, black);
            }

            using (var edge = new SKPaint { Color = accent, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 })
            {
                canvas.DrawRoundRect(Inflate(box, 4), 12, 12, edge);
            }

            Text(canvas, job.ChannelName.ToUpperInvariant(), box.Left, box.Top - 24, 18, soft, bold: true, width: box.Width, family: headingFont);
        }
        else
        {
            // Side by side: the credits' whole strip is black (bars around them when letterboxed), with a thin accent line
            // where it meets the panel.
            SKRect strip = Sk(layout.Strip);
            using (var black = new SKPaint { Color = SKColors.Black })
            {
                canvas.DrawRect(strip, black);
            }

            using var line = new SKPaint { Color = accent, StrokeWidth = 3, IsAntialias = true };
            SKRect panelRect = Sk(layout.Panel);
            if (layout.TextBeside)
            {
                float edgeY = strip.Top <= 0 ? strip.Bottom : strip.Top;
                canvas.DrawLine(0, edgeY, 1280, edgeY, line);
            }
            else
            {
                float edgeX = panelRect.Left >= strip.Right ? strip.Right : strip.Left;
                canvas.DrawLine(edgeX, 0, edgeX, 720, line);
            }
        }

        // The credits' own words: what's ending, and the channel's line.
        float captionBottom = box.Bottom;
        if (!layout.Caption.IsEmpty)
        {
            SKRect caption = Sk(layout.Caption);
            float cy = caption.Top;
            float captionSize = Math.Clamp(caption.Width / 14f, 18, 30);
            if (!string.IsNullOrWhiteSpace(job.EndingShow))
            {
                if (!string.IsNullOrWhiteSpace(job.CreditsCaption))
                {
                    Text(canvas, job.CreditsCaption.ToUpperInvariant(), caption.Left, cy + 16, 15, accent, bold: true, width: caption.Width, family: headingFont);
                    cy += 20;
                }

                cy = Wrapped(canvas, job.EndingShow, caption.Left, cy, captionSize, SKColors.White, caption.Width, maxLines: 2, family: textFont);
            }

            if (job.CreditsLine is { Length: > 0 } own && cy + 22 <= caption.Bottom + 4)
            {
                cy = Wrapped(canvas, own, caption.Left, cy + 4, Math.Min(18, captionSize), soft, caption.Width, maxLines: 2, bold: false, family: textFont);
            }

            captionBottom = Math.Max(captionBottom, cy);
        }

        // The picture slot, where a promo plays (over the next programme's picture, which shows before and after it). A
        // panel that's just its video, side by side, fills it edge to edge.
        SKRect picture = Sk(layout.Picture);
        bool bare = layout.Text.IsEmpty;
        float corner = bare && !framed ? 0 : 12;
        bool playsPromo = PlaysPromo(job);
        using (SKImage? image = art is null || playsPromo ? null : SKImage.FromEncodedData(art))
        {
            canvas.Save();
            canvas.ClipRoundRect(new SKRoundRect(picture, corner, corner), antialias: true);
            if (playsPromo)
            {
                // The promo fades up from black here; the programme's picture is only for a panel without one.
                using var black = new SKPaint { Color = SKColors.Black };
                canvas.DrawRect(job.PanelFit == SqueezeFit.Letterbox ? Contain(16 / 9f, picture) : picture, black);
            }
            else if (image is not null)
            {
                Place(canvas, image, picture, job.PanelFit);
            }
            else
            {
                using var stand = new SKPaint { Color = Mix(background, accent, 0.35f) };
                canvas.DrawRect(job.PanelFit == SqueezeFit.Letterbox ? Contain(16 / 9f, picture) : picture, stand);
            }

            canvas.Restore();
        }

        if (framed && bare)
        {
            // Two boxes alike: the panel's picture gets the same edge as the credits'.
            using var edge = new SKPaint { Color = accent.WithAlpha(150), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
            canvas.DrawRoundRect(Inflate(picture, 4), 12, 12, edge);
        }

        if (!bare)
        {
            // The heading: above the picture, or (a short panel) at the top of the words beside it.
            float x = layout.TextBeside ? words.Left : picture.Left;
            float y;
            if (layout.TextBeside)
            {
                Text(canvas, job.Heading.ToUpperInvariant(), x, words.Top + 20, 20, accent, bold: true, width: words.Width, family: headingFont);
                y = words.Top + 28;
            }
            else
            {
                Text(canvas, job.Heading.ToUpperInvariant(), x, picture.Top - 16, 22, accent, bold: true, width: picture.Width, family: headingFont);
                y = picture.Bottom + 12;
            }

            // Smaller text for a narrow panel.
            float size = Math.Clamp(words.Width / 12f, 18, 32);
            y = Wrapped(canvas, job.Next.Title, x, y, size, SKColors.White, words.Width, maxLines: 2, family: textFont);
            if (job.Next.Episode is { Length: > 0 } episode)
            {
                y = Wrapped(canvas, episode, x, y + 4, Math.Min(20, size), soft, words.Width, maxLines: 1, bold: false, family: textFont);
            }

            float whenSize = Math.Min(24, size);
            Text(canvas, $"at {job.Next.When}", x, y + whenSize + 6, whenSize, accent, bold: true, width: words.Width, family: headingFont);
            y += whenSize + 6;

            if (job.Later.Count > 0)
            {
                if (framed)
                {
                    // Along the bottom, under both boxes.
                    float laterY = Math.Max(captionBottom, y) + 50;
                    if (laterY < 700)
                    {
                        Text(canvas, "LATER", 48, laterY, 16, accent, bold: true, width: 120, family: headingFont);
                        float laterX = 130;
                        foreach (SqueezeListing later in job.Later)
                        {
                            Text(canvas, $"{later.When}  {later.Title}", laterX, laterY, 18, soft, bold: false, width: 520, family: textFont);
                            laterX += 560;
                        }
                    }
                }
                else
                {
                    // In the words' column, as long as there's room left in it.
                    float laterY = y + 40;
                    foreach (SqueezeListing later in job.Later)
                    {
                        if (laterY > words.Bottom)
                        {
                            break;
                        }

                        Text(canvas, $"Later  {later.When}  {later.Title}", x, laterY, 16, soft, bold: false, width: words.Width, family: textFont);
                        laterY += 24;
                    }
                }
            }
        }

        using SKImage snapshot = surface.Snapshot();
        using SKData data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static SKRect Inflate(SKRect rect, float by)
    {
        rect.Inflate(by, by);
        return rect;
    }

    private static void Cover(SKCanvas canvas, SKImage image, SKRect bounds)
    {
        float scale = Math.Max(bounds.Width / image.Width, bounds.Height / image.Height);
        var destination = SKRect.Create(bounds.MidX - image.Width * scale / 2, bounds.MidY - image.Height * scale / 2, image.Width * scale, image.Height * scale);
        canvas.DrawImage(image, destination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    }

    /// <summary>An image in <paramref name="bounds"/>: kept whole (centred), stretched, or covering it.</summary>
    private static void Place(SKCanvas canvas, SKImage image, SKRect bounds, SqueezeFit fit)
    {
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        switch (fit)
        {
            case SqueezeFit.Squeeze:
                canvas.DrawImage(image, bounds, sampling);
                break;
            case SqueezeFit.Crop:
                Cover(canvas, image, bounds);
                break;
            default:
                canvas.DrawImage(image, Contain(image.Width / (float)image.Height, bounds), sampling);
                break;
        }
    }

    /// <summary>The largest box of this shape that fits in <paramref name="bounds"/>, centred.</summary>
    private static SKRect Contain(float aspect, SKRect bounds)
    {
        float width = Math.Min(bounds.Width, bounds.Height * aspect);
        float height = width / aspect;
        return SKRect.Create(bounds.MidX - width / 2, bounds.MidY - height / 2, width, height);
    }

    /// <summary>The installed fonts the squeeze can be drawn in.</summary>
    public static IReadOnlyList<string> InstalledFonts() =>
    [
        .. SKFontManager.Default.GetFontFamilies()
            .Where(family => !string.IsNullOrWhiteSpace(family) && family[0] != '@')
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
    ];

    private static readonly ConcurrentDictionary<(string Family, bool Bold), SKTypeface> Typefaces = new();

    private static SKFont Font(string family, float size, bool bold)
    {
        SKTypeface typeface = Typefaces.GetOrAdd((family, bold), key =>
            SKTypeface.FromFamilyName(key.Family, key.Bold ? SKFontStyle.Bold : SKFontStyle.Normal)
            ?? SKTypeface.FromFamilyName("Arial", key.Bold ? SKFontStyle.Bold : SKFontStyle.Normal)
            ?? SKTypeface.Default);
        return new SKFont(typeface, size) { Edging = SKFontEdging.Antialias, Subpixel = true };
    }

    /// <summary>One line, shortened with "…" to fit <paramref name="width"/>; <paramref name="y"/> is its baseline.</summary>
    private static void Text(SKCanvas canvas, string text, float x, float y, float size, SKColor colour, bool bold, float width, string family = "Arial")
    {
        using SKFont font = Font(family, size, bold);
        using var paint = new SKPaint { Color = colour, IsAntialias = true };
        canvas.DrawText(Fit(text, font, paint, width), x, y, SKTextAlign.Left, font, paint);
    }

    /// <summary>Text over up to <paramref name="maxLines"/> lines from <paramref name="top"/>; returns where it ends.</summary>
    private static float Wrapped(SKCanvas canvas, string text, float x, float top, float size, SKColor colour, float width, int maxLines, bool bold = true,
        string family = "Arial")
    {
        using SKFont font = Font(family, size, bold);
        using var paint = new SKPaint { Color = colour, IsAntialias = true };
        List<string> lines = [];
        string current = string.Empty;
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string tried = current.Length == 0 ? word : $"{current} {word}";
            if (font.MeasureText(tried, paint) <= width || current.Length == 0)
            {
                current = tried;
                continue;
            }

            lines.Add(current);
            current = word;
        }

        if (current.Length > 0)
        {
            lines.Add(current);
        }

        if (lines.Count > maxLines)
        {
            lines = [.. lines.Take(maxLines - 1), string.Join(' ', lines.Skip(maxLines - 1))];
        }

        float y = top;
        foreach (string line in lines)
        {
            y += size * 1.12f;
            canvas.DrawText(Fit(line, font, paint, width), x, y, SKTextAlign.Left, font, paint);
        }

        return y;
    }

    private static string Fit(string text, SKFont font, SKPaint paint, float width)
    {
        if (font.MeasureText(text, paint) <= width)
        {
            return text;
        }

        string shorter = text;
        while (shorter.Length > 1 && font.MeasureText(shorter + "…", paint) > width)
        {
            shorter = shorter[..^1];
        }

        return shorter.TrimEnd() + "…";
    }

    private static SKColor Colour(string? text, SKColor fallback) => SKColor.TryParse(text, out SKColor colour) ? colour : fallback;

    private static SKColor Mix(SKColor a, SKColor b, float amount) => new(
        (byte)(a.Red + (b.Red - a.Red) * amount),
        (byte)(a.Green + (b.Green - a.Green) * amount),
        (byte)(a.Blue + (b.Blue - a.Blue) * amount));

    private static async Task<(int Code, string Errors, string Output)> RunAsync(string program, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start ffmpeg ({program}).");
        Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromMinutes(20));
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            throw;
        }

        return (process.ExitCode, await errors.ConfigureAwait(false), await output.ConfigureAwait(false));
    }

    /// <summary>Deletes squeezes no schedule has used for a while (and leftovers of interrupted renders).</summary>
    private void Cleanup()
    {
        DateTimeOffset now = clock.GetUtcNow();
        if (now - _cleanedAt < TimeSpan.FromHours(1) || !Directory.Exists(settings.SaveFolder))
        {
            return;
        }

        _cleanedAt = now;
        foreach (string file in Directory.EnumerateFiles(settings.SaveFolder))
        {
            DateTimeOffset last = _used.TryGetValue(file, out DateTimeOffset used) ? used : File.GetLastWriteTimeUtc(file);
            if (now - last > settings.KeepFor)
            {
                TryDelete(file);
                _used.TryRemove(file, out _);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }
}
