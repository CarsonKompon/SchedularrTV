using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SkiaSharp;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Service.Editor;

namespace SchedularrTV.GeneratedPromos;

/// <summary>Editor previews drawn by the real renderer, so the canvas shows exactly what airs.</summary>
public sealed class GeneratedPromoPreview(
    GeneratedPromoService promos,
    IOptions<GeneratedPromoOptions> options,
    TimeProvider clock,
    IServiceProvider services)
{
    private static readonly string[] SampleTitles = ["SpongeBob SquarePants", "The Fairly OddParents", "Hey Arnold!", "Rugrats"];

    private readonly ConcurrentDictionary<string, string?> _frames = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MediaItem?> _standIns = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Finds an episode file the channel could play, so a sample preview still has a clip to play behind
    /// the promo or hand to a generator.
    /// </summary>
    public async Task PrepareStandInAsync(ChannelConfig channel, CancellationToken cancellationToken = default)
    {
        if (_standIns.ContainsKey(channel.Id) || services.GetService<LibraryBrowser>() is not { } library)
        {
            return;
        }

        IReadOnlyList<MediaItem> programmes = await library.GetProgrammesAsync(cancellationToken).ConfigureAwait(false);
        string? collection = channel.BaseRotation?.Collection;
        _standIns[channel.Id] = await Task.Run(() => programmes
            .Where(item => item.Duration > TimeSpan.FromMinutes(1) && !string.IsNullOrWhiteSpace(item.Path) && !PromoRenderer.IsImage(item.Path))
            .OrderByDescending(item => collection is not null && item.Collections.Contains(collection, StringComparer.OrdinalIgnoreCase))
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .FirstOrDefault(item => File.Exists(item.Path)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The programmes a template would show right now: upcoming ones for Up Next and Now Playing, the
    /// one just finished for This Has Been, and a later airing for Coming Up and Eventually.
    /// Stand-ins are used until the channel has a schedule.
    /// </summary>
    public IReadOnlyList<UpNextListing> Listings(ChannelConfig channel, GeneratedPromoTemplate template, out bool sample)
    {
        DateTimeOffset now = clock.GetUtcNow();
        ChannelSchedule? schedule = services.GetService<ScheduleCoordinator>()?.Latest
            .FirstOrDefault(candidate => string.Equals(candidate.ChannelId, channel.Id, StringComparison.OrdinalIgnoreCase));
        int needed = GeneratedPromoResolver.ListingsNeeded(template);
        IReadOnlyList<UpNextListing> real = schedule is null ? [] : template.Type switch
        {
            GeneratedPromoType.ThisHasBeen or GeneratedPromoType.YoureWatching or GeneratedPromoType.BackToTheShow =>
                UpNextPromo.Previous(schedule, now) is { } previous ? [previous] : [],
            GeneratedPromoType.ComingUp => UpNextPromo.Listings(schedule, now, 3, grouping: template.UpcomingGrouping) is { Count: > 1 } upcoming ? [upcoming[^1]] : [],
            GeneratedPromoType.Eventually => [],
            _ => UpNextPromo.Listings(schedule, now, needed, grouping: template.UpcomingGrouping)
        };
        sample = real.Count == 0;
        if (!sample)
        {
            return real;
        }

        var next = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute < 30 ? 30 : 0, 0, now.Offset);
        if (now.Minute >= 30)
        {
            next = next.AddHours(1);
        }

        IReadOnlyList<UpNextListing> samples = template.Type switch
        {
            GeneratedPromoType.ThisHasBeen or GeneratedPromoType.YoureWatching or GeneratedPromoType.BackToTheShow =>
                [new UpNextListing(SampleTitles[0], next.AddMinutes(-30), "Help Wanted")],
            GeneratedPromoType.ComingUp => [new UpNextListing(SampleTitles[1], next.AddHours(2), "The Big Problem")],
            GeneratedPromoType.Eventually => [new UpNextListing(SampleTitles[2], new DateTimeOffset(next.Date, next.Offset).AddDays(3).AddHours(16), "Downtown Cityscape")],
            _ => [.. Enumerable.Range(0, Math.Max(1, needed)).Select(index =>
                new UpNextListing(SampleTitles[index % SampleTitles.Length], next.AddMinutes(30 * index), index == 0 ? "Help Wanted" : null))]
        };

        // The promoted show is a real file when one was found, so episode clips work before the first build.
        return _standIns.GetValueOrDefault(channel.Id) is { } standIn
            ? [UpNextPromo.Listing(standIn, samples[0].Start), .. samples.Skip(1)]
            : samples;
    }

    public DateTimeOffset Now => clock.GetUtcNow();

    public GeneratedImageSubjects ImageSubjects(ChannelConfig channel, GeneratedPromoTemplate? template = null)
    {
        DateTimeOffset now = clock.GetUtcNow();
        ChannelSchedule? schedule = services.GetService<ScheduleCoordinator>()?.Latest
            .FirstOrDefault(candidate => string.Equals(candidate.ChannelId, channel.Id, StringComparison.OrdinalIgnoreCase));
        if (schedule is not null) return GeneratedImageSubjects.FromSchedule(schedule, now, template: template);
        // Use the same first sample and airtime as the text preview, so its background rules agree with its caption.
        var next = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute < 30 ? 30 : 0, 0, now.Offset);
        if (now.Minute >= 30) next = next.AddHours(1);
        int count = Math.Max(10, (int)Math.Ceiling((template?.BackgroundRules ?? [])
            .Where(rule => rule.MatchShows != false)
            .Select(rule => rule.Within?.TotalMinutes ?? 0).DefaultIfEmpty().Max() / 30) + 1);
        List<UpNextListing> samples = [.. Enumerable.Range(0, count)
            .Select(index => new UpNextListing(SampleTitles[index % SampleTitles.Length], next.AddMinutes(30 * index)))];
        if (_standIns.GetValueOrDefault(channel.Id) is { } standIn) samples[0] = UpNextPromo.Listing(standIn, next);
        return new(new UpNextListing(SampleTitles[0], now.AddMinutes(-15)), samples);
    }

    /// <summary>The AI writer, when the scheduler provides one (it always does; tools and tests may not).</summary>
    public SchedularrTV.Service.Writing.AiTextWriter? Writer => services.GetService<SchedularrTV.Service.Writing.AiTextWriter>();

    /// <summary>The template's AI jobs for the editor's "Try it", with what it would air with right now.</summary>
    public ResolvedGeneratedPromo? Resolve(GeneratedPromoTemplate template, ChannelConfig channel, TimeZoneInfo zone) =>
        GeneratedPromoResolver.Resolve(template, channel.Name, Listings(channel, template, out _), zone, clock.GetUtcNow(), ImageSubjects(channel, template));

    public static TimeZoneInfo Zone(SchedulerConfig config, ChannelConfig channel) =>
        SchedulingEngine.ResolveTimeZone(string.IsNullOrWhiteSpace(channel.TimeZone) ? config.Settings.TimeZone : channel.TimeZone);

    /// <summary>JPEG of one frame, scaled to at most <paramref name="width"/> pixels wide.</summary>
    public async Task<byte[]> RenderStillAsync(
        GeneratedPromoTemplate template,
        string channelName,
        IReadOnlyList<UpNextListing> listings,
        TimeZoneInfo zone,
        TimeSpan time,
        int width,
        CancellationToken cancellationToken = default,
        DateTimeOffset? airsAt = null,
        GeneratedImageSubjects? imageSubjects = null)
    {
        ResolvedGeneratedPromo promo = GeneratedPromoResolver.Resolve(template, channelName, listings, zone, airsAt ?? clock.GetUtcNow(), imageSubjects)
            ?? throw new InvalidOperationException("The preview needs at least one upcoming programme.");

        // AI-written captions show once they're written (the usual text until then; the editor redraws when they are).
        promo = GeneratedPromoWriting.Peek(promo, Writer);
        PromoComposition composition = GeneratedPromoResolver.Compose(promo);
        if (composition.BackgroundPath is { } background && !PromoRenderer.IsImage(background))
        {
            composition = composition with { BackgroundPath = await FrameAsync(background, composition, time, cancellationToken).ConfigureAwait(false) };
        }

        // Each video layer on screen now shows its frame for this moment, decoded the way the render decodes it.
        Dictionary<PromoVideoLayer, string> videoFrames = [];
        foreach (PromoVideoLayer video in composition.Layers.OfType<PromoVideoLayer>())
        {
            if (PromoAnimation.EvaluateInGroups(video, time, composition.Duration, composition.Height) is not null
                && await VideoFrameAsync(video, video.PlaybackAt(time), composition.Fps, cancellationToken).ConfigureAwait(false) is { } file)
            {
                videoFrames[video] = file;
            }
        }

        return await Task.Run(() =>
        {
            float scale = Math.Clamp(width / (float)composition.Width, 0.05f, 1f);
            var info = new SKImageInfo(Math.Max(1, (int)Math.Round(composition.Width * scale)), Math.Max(1, (int)Math.Round(composition.Height * scale)));
            using SKSurface surface = SKSurface.Create(info);
            surface.Canvas.Scale(scale);
            using var images = new PromoImageCache();
            Dictionary<PromoVideoLayer, SKImage> frames = [];
            try
            {
                foreach ((PromoVideoLayer video, string file) in videoFrames)
                {
                    if (SKImage.FromEncodedData(file) is { } frame) frames[video] = frame;
                }

                PromoRenderer.DrawFrame(surface.Canvas, composition, time, images, videos: new FixedVideoFrames(frames));
            }
            finally
            {
                foreach (SKImage frame in frames.Values) frame.Dispose();
            }

            using SKImage image = surface.Snapshot();
            using SKData data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
            return data.ToArray();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A video layer's frame <paramref name="playback"/> into its playback, sized and fitted as the render does it, as a
    /// picture file (kept, so scrubbing back over it is quick); null when it can't be had.
    /// </summary>
    private async Task<string?> VideoFrameAsync(PromoVideoLayer video, TimeSpan playback, int fps, CancellationToken cancellationToken)
    {
        if (!File.Exists(video.Path))
        {
            return null;
        }

        int frame = (int)Math.Round(playback.TotalSeconds * Math.Max(1, fps));
        double second = (video.SourceStart + TimeSpan.FromSeconds(frame / (double)Math.Max(1, fps))).TotalSeconds;
        (int width, int height) = video.FrameSize;
        string key = $"{Path.GetFullPath(video.Path)}|{File.GetLastWriteTimeUtc(video.Path).Ticks}|{second.ToString("0.###", CultureInfo.InvariantCulture)}|{width}x{height}|{video.Fill}|layer";
        if (_frames.TryGetValue(key, out string? cached) && (cached is null || File.Exists(cached)))
        {
            if (cached is not null) Used(cached);
            return cached;
        }

        string directory = Path.Combine(promos.Root, PreviewFrameCache.FolderName);
        Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, Hash(key) + ".png");
        TrimSoon();
        string filter = video.SizeFilter(Math.Max(1, fps));
        // Past the end of the video (it would hold its last frame), its opening frame stands in.
        string? result = await ExtractFilteredAsync(video.Path, second, filter, output, cancellationToken).ConfigureAwait(false)
            ?? await ExtractFilteredAsync(video.Path, video.SourceStart.TotalSeconds, filter, output, cancellationToken).ConfigureAwait(false);
        _frames[key] = result;
        return result;
    }

    private async Task<string?> ExtractFilteredAsync(string video, double second, string filter, string output, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(options.Value.FfmpegPath)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in (string[])["-hide_banner", "-loglevel", "error", "-y",
                     "-ss", Math.Max(0, second).ToString("0.###", CultureInfo.InvariantCulture), "-i", video, "-frames:v", "1", "-an", "-vf", filter, output])
        {
            info.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg could not be started.");
        await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode == 0 && File.Exists(output) ? output : null;
    }

    /// <summary>Full render with voice and music, cached like a scheduled promo.</summary>
    /// <param name="song">Renders it with this background song (one of the template's) instead of the one it would pick.</param>
    public Task<GeneratedPromoRender> RenderVideoAsync(
        GeneratedPromoTemplate template,
        string channelName,
        IReadOnlyList<UpNextListing> listings,
        TimeZoneInfo zone,
        CancellationToken cancellationToken = default,
        string? song = null,
        GeneratedImageSubjects? imageSubjects = null) =>
        promos.RenderAsync(
            (GeneratedPromoResolver.Resolve(WithSong(template, song), channelName, listings, zone, clock.GetUtcNow(), imageSubjects)
                ?? throw new InvalidOperationException("The preview needs at least one upcoming programme."))
            with { Folder = GeneratedPromoService.PreviewFolder },
            cancellationToken);

    /// <summary>A copy of the template whose only song is <paramref name="song"/>, keeping its start and volume.</summary>
    public static GeneratedPromoTemplate WithSong(GeneratedPromoTemplate template, string? song)
    {
        if (song is null || template.Music is null)
        {
            return template;
        }

        GeneratedPromoTemplate copy = GeneratedPromoEditing.Copy(template);
        copy.Music!.Path = null;
        copy.Music.Paths = [song];
        copy.Music.Library = null;
        return copy;
    }

    /// <summary>
    /// The stretch of <paramref name="song"/> a promo of <paramref name="duration"/> would play, looped and faded the way
    /// the render does it, so a start point can be auditioned without rendering a whole promo.
    /// </summary>
    public async Task<string> SongClipAsync(GeneratedMusicConfig music, string song, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(song))
        {
            throw new FileNotFoundException("The scheduler can't find this song.", song);
        }

        TimeSpan start = music.StartFor(song);
        TimeSpan fadeOut = music.FadeOut > duration ? duration : music.FadeOut;
        // Played as loud as it would be in a promo (master times the song's own), so songs can be levelled by ear.
        double volume = music.EffectiveVolume(song);
        string key = string.Join('|', Path.GetFullPath(song), File.GetLastWriteTimeUtc(song).Ticks, start.Ticks, duration.Ticks, music.FadeIn.Ticks, fadeOut.Ticks,
            volume.ToString("0.###", CultureInfo.InvariantCulture), "song");
        string directory = Path.Combine(promos.Root, GeneratedPromoService.PreviewFolder, "songs");
        string clip = Path.Combine(directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32] + ".mp4");
        if (File.Exists(clip))
        {
            File.SetLastWriteTimeUtc(clip, DateTime.UtcNow);
            return clip;
        }

        Directory.CreateDirectory(directory);
        string partial = clip + ".partial.mp4";
        var info = new ProcessStartInfo(options.Value.FfmpegPath)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        string Seconds(TimeSpan value) => value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        foreach (string argument in (string[])["-hide_banner", "-loglevel", "error", "-y", "-stream_loop", "-1",
                     "-ss", Seconds(start), "-i", song, "-vn", "-t", Seconds(duration),
                     "-af", $"volume={volume.ToString("0.###", CultureInfo.InvariantCulture)},afade=t=in:st=0:d={Seconds(music.FadeIn)},afade=t=out:st={Seconds(duration - fadeOut)}:d={Seconds(fadeOut)}",
                     "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart", "-f", "mp4", partial])
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg could not be started.");
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
                throw new InvalidOperationException($"ffmpeg could not cut the song (exit {process.ExitCode}): {(await error.ConfigureAwait(false)).Trim()}");
            }

            File.Move(partial, clip, overwrite: true);
            return clip;
        }
        finally
        {
            File.Delete(partial);
        }
    }

    public string FileUrl(string path) =>
        "api/generated-promos/files/" + string.Join('/', promos.RelativePath(path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Select(Uri.EscapeDataString));

    // Frames are kept this wide at most: plenty for the editor's canvas, and quick to decode while scrubbing.
    private const int FrameWidth = 1280;

    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _strips = new(StringComparer.Ordinal);

    /// <summary>
    /// A background video's frame at the preview time, the exact frame the render uses there. The first time a video
    /// is previewed, every frame the promo shows of it is extracted in one pass (the same way the render reads it: from
    /// its start point, looped, at the promo's frame rate), so scrubbing is frame by frame and instant afterwards; until
    /// that's done, the one frame asked for is extracted on its own.
    /// </summary>
    private async Task<string?> FrameAsync(string video, PromoComposition composition, TimeSpan time, CancellationToken cancellationToken)
    {
        if (!File.Exists(video))
        {
            return null;
        }

        int fps = Math.Max(1, composition.Fps);
        int count = Math.Max(1, composition.FrameCount);
        int index = Math.Clamp((int)Math.Round(time.TotalSeconds * fps), 0, count - 1);
        string source = $"{Path.GetFullPath(video)}|{File.GetLastWriteTimeUtc(video).Ticks}|{composition.BackgroundStart.Ticks}";
        string stripKey = $"{source}|{fps}|{count}|{FrameWidth}|strip";
        string strip = Path.Combine(promos.Root, PreviewFrameCache.FolderName, Hash(stripKey));

        Task<string?> extracting = _strips.GetOrAdd(stripKey, _ => new Lazy<Task<string?>>(() =>
            Task.Run(() => ExtractStripAsync(video, composition.BackgroundStart, fps, count, strip)))).Value;
        if (extracting.IsCompletedSuccessfully && extracting.Result is { } frames)
        {
            if (Path.Combine(frames, FrameName(index)) is var stripFrame && File.Exists(stripFrame))
            {
                Used(frames);
                return stripFrame;
            }

            // Its strip was trimmed from the cache: it's extracted again next time.
            if (!Directory.Exists(frames))
            {
                _strips.TryRemove(stripKey, out _);
            }
        }

        // Not extracted yet (or it failed): this one frame on its own, seeking straight to it.
        double second = (composition.BackgroundStart + TimeSpan.FromSeconds(index / (double)fps)).TotalSeconds;
        string key = $"{source}|{index}|{fps}|{FrameWidth}";
        if (_frames.TryGetValue(key, out string? cached) && (cached is null || File.Exists(cached)))
        {
            if (cached is not null) Used(cached);
            return cached;
        }

        string directory = Path.Combine(promos.Root, PreviewFrameCache.FolderName);
        Directory.CreateDirectory(directory);
        string frame = Path.Combine(directory, Hash(key) + ".jpg");
        string? result = await ExtractAsync(video, second, frame, cancellationToken).ConfigureAwait(false)
            ?? await ExtractAsync(video, 0, frame, cancellationToken).ConfigureAwait(false);
        _frames[key] = result;
        TrimSoon();
        return result;
    }

    private readonly ConcurrentDictionary<string, DateTime> _touched = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _trimmedAt = DateTime.MinValue;
    private int _trimming;

    /// <summary>Marks a cached frame (or strip) as in use, at most once a minute each, so trimming keeps what's being edited.</summary>
    private void Used(string path)
    {
        DateTime now = DateTime.UtcNow;
        if (_touched.TryGetValue(path, out DateTime last) && now - last < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _touched[path] = now;
        PreviewFrameCache.Touch(path);
    }

    /// <summary>
    /// Keeps the frame cache within its budget while editing (the service's own clean-up is only hourly): at most every
    /// couple of minutes, in the background.
    /// </summary>
    private void TrimSoon()
    {
        DateTime now = DateTime.UtcNow;
        if (now - _trimmedAt < TimeSpan.FromMinutes(2) || Interlocked.Exchange(ref _trimming, 1) == 1)
        {
            return;
        }

        _trimmedAt = now;
        string directory = Path.Combine(promos.Root, PreviewFrameCache.FolderName);
        _ = Task.Run(() =>
        {
            try
            {
                PreviewFrameCache.Trim(directory, DateTime.UtcNow);
                _touched.Clear();
            }
            catch (Exception)
            {
                // Only housekeeping; the service's clean-up tries again.
            }
            finally
            {
                Interlocked.Exchange(ref _trimming, 0);
            }
        });
    }

    private static string FrameName(int index) => $"{index + 1:00000}.jpg";

    private static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];

    /// <summary>
    /// Every frame the promo shows of <paramref name="video"/> into <paramref name="directory"/> (1-based, like ffmpeg
    /// numbers them), or null if ffmpeg can't. Written aside and moved into place whole, so a frame is never read half-written.
    /// </summary>
    private async Task<string?> ExtractStripAsync(string video, TimeSpan start, int fps, int count, string directory)
    {
        if (Directory.Exists(directory))
        {
            return directory;
        }

        string partial = $"{directory}.{Guid.NewGuid():N}.partial";
        try
        {
            Directory.CreateDirectory(partial);
            var info = new ProcessStartInfo(options.Value.FfmpegPath)
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            string Seconds(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
            foreach (string argument in (string[])["-hide_banner", "-loglevel", "error", "-y", "-stream_loop", "-1",
                         "-ss", Seconds(start.TotalSeconds), "-i", video, "-t", Seconds(count / (double)fps), "-an",
                         "-vf", $"fps={fps},scale='min({FrameWidth},iw)':-2", "-q:v", "3", "-frames:v", count.ToString(CultureInfo.InvariantCulture),
                         Path.Combine(partial, "%05d.jpg")])
            {
                info.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg could not be started.");
            await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(Path.Combine(partial, FrameName(0))))
            {
                return null;
            }

            try
            {
                Directory.Move(partial, directory);
            }
            catch (IOException) when (Directory.Exists(directory))
            {
                // Another preview extracted the same frames at the same time; theirs stands.
            }

            TrimSoon();
            return directory;
        }
        catch (Exception)
        {
            // Frames are then extracted one at a time.
            return null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left behind; harmless.
            }
        }
    }

    private async Task<string?> ExtractAsync(string video, double second, string frame, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(options.Value.FfmpegPath)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in (string[])["-hide_banner", "-loglevel", "error", "-y", "-stream_loop", "-1",
                     "-ss", second.ToString("0.###", CultureInfo.InvariantCulture), "-i", video, "-frames:v", "1", "-q:v", "3", frame])
        {
            info.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg could not be started.");
        await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode == 0 && File.Exists(frame) ? frame : null;
    }
}
