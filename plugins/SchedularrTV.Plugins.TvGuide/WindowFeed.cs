using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace SchedularrTV.TvGuide;

/// <summary>
/// The stream a guide's video window plays when it isn't one channel: its commercials one after another (shuffled,
/// with their sound), or a different channel picked at random every few minutes. The guide's own ffmpeg lays it into
/// the window and, when the guide plays the top panel's sound, takes its sound too.
/// </summary>
public sealed class WindowFeed(GuideStore store, GuideSettings settings, GuideData data, TimeProvider clock, ILogger<WindowFeed> logger)
{
    /// <summary>How long before a channel's turn it's asked for, so ErsatzTV has it running by then.</summary>
    private static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(40);

    private readonly ConcurrentDictionary<string, bool> _hasAudio = new(StringComparer.OrdinalIgnoreCase);

    public async Task ServeAsync(string guideId, HttpContext context)
    {
        if (store.Find(guideId) is not { WindowIsFeed: true, Window: { } window })
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var state = new FeedState();
        await SequenceFeed.ServeAsync(context, cancellationToken => NextAsync(guideId, state, cancellationToken), settings.Ffmpeg,
            window.Width, window.Height, logger).ConfigureAwait(false);
    }

    /// <summary>What one viewer of the feed has had, so it doesn't repeat itself.</summary>
    private sealed class FeedState
    {
        public Queue<GuideAd> Ads { get; } = new();

        public string? LastAd { get; set; }

        public int? Next { get; set; }

        public int? Last { get; set; }
    }

    private async Task<FeedSegment?> NextAsync(string guideId, FeedState state, CancellationToken cancellationToken)
    {
        if (store.Find(guideId) is not { } guide)
        {
            return null;
        }

        return guide.Top switch
        {
            TopPanel.Ads => await NextAdAsync(guide, state, cancellationToken).ConfigureAwait(false),
            TopPanel.Channel when guide.CycleChannels => NextChannel(guide, state, cancellationToken),
            _ => null
        };
    }

    private async Task<FeedSegment?> NextAdAsync(GuideStation guide, FeedState state, CancellationToken cancellationToken)
    {
        if (state.Ads.Count == 0)
        {
            IReadOnlyList<GuideAd> ads = await data.AdsAsync(guide, cancellationToken).ConfigureAwait(false);
            if (ads.Count == 0)
            {
                return null;
            }

            foreach (GuideAd ad in Shuffle(ads, state.LastAd))
            {
                state.Ads.Enqueue(ad);
            }
        }

        GuideAd next = state.Ads.Dequeue();
        state.LastAd = next.File;
        bool hasAudio = await HasAudioAsync(next.File, cancellationToken).ConfigureAwait(false);
        return new FeedSegment(next.File, next.Duration, Realtime: true, HasAudio: hasAudio, Label: next.Title);
    }

    /// <summary>The ads in a random order that doesn't start with the one just played.</summary>
    public static IReadOnlyList<GuideAd> Shuffle(IReadOnlyList<GuideAd> ads, string? last)
    {
        GuideAd[] shuffled = [.. ads];
        Random.Shared.Shuffle(shuffled);
        if (shuffled.Length > 1 && shuffled[0].File == last)
        {
            (shuffled[0], shuffled[^1]) = (shuffled[^1], shuffled[0]);
        }

        return shuffled;
    }

    private FeedSegment? NextChannel(GuideStation guide, FeedState state, CancellationToken cancellationToken)
    {
        IReadOnlyList<int> candidates = Candidates(guide, data.Listings(clock.GetUtcNow(), TimeSpan.FromMinutes(30)).Select(row => row.Number));
        if (candidates.Count == 0)
        {
            return null;
        }

        // The one warmed up last time, if it's still a candidate; then the one after it, warmed up ahead of its turn.
        int current = state.Next is { } planned && candidates.Contains(planned) ? planned : Pick(candidates, state.Last);
        int following = Pick(candidates, current);
        state.Last = current;
        state.Next = following;

        TimeSpan turn = TimeSpan.FromMinutes(guide.CycleMinutes);
        if (following != current)
        {
            _ = WarmUpAsync(settings.ChannelUrl(following), turn - WarmUp, cancellationToken);
        }

        return new FeedSegment(settings.ChannelUrl(current), turn, Realtime: false, Label: $"channel {current}");
    }

    /// <summary>The channels to cycle through: the ones chosen, else every listed channel (the guide's hidden ones left out).</summary>
    public static IReadOnlyList<int> Candidates(GuideStation guide, IEnumerable<int> published)
    {
        HashSet<int> available = [.. published];
        IEnumerable<int> chosen = guide.CycleChannelNumbers.Count > 0
            ? guide.CycleChannelNumbers.Where(available.Contains)
            : available.Where(number => !guide.HiddenChannels.Contains(number));
        return [.. chosen.Distinct().Order()];
    }

    /// <summary>A random channel other than <paramref name="avoid"/>, when there's another to pick.</summary>
    public static int Pick(IReadOnlyList<int> candidates, int? avoid)
    {
        List<int> choices = [.. candidates.Where(number => number != avoid)];
        return choices.Count == 0 ? candidates[0] : choices[Random.Shared.Next(choices.Count)];
    }

    /// <summary>Asks ErsatzTV for a channel shortly before its turn, so it's started by the time the window switches.</summary>
    private async Task WarmUpAsync(string url, TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(35) };
            using HttpResponseMessage response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            // Only a head start; the switch still works without it, a little slower.
        }
    }

    /// <summary>Whether a file has sound (a commercial without any plays with silence); asked of ffprobe once per file.</summary>
    private async Task<bool> HasAudioAsync(string file, CancellationToken cancellationToken)
    {
        if (_hasAudio.TryGetValue(file, out bool known))
        {
            return known;
        }

        bool hasAudio = true;
        try
        {
            var start = new ProcessStartInfo(settings.Ffprobe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (string argument in new[] { "-v", "error", "-select_streams", "a", "-show_entries", "stream=index", "-of", "csv=p=0", file })
            {
                start.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(start) ?? throw new InvalidOperationException("ffprobe didn't start.");
            Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);
            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            if (process.ExitCode == 0)
            {
                hasAudio = output.Trim().Length > 0;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Without ffprobe, assume sound; a file without any fails to play and the next one follows.
            logger.LogDebug("Couldn't ask ffprobe whether {File} has sound: {Message}", file, ex.Message);
            return true;
        }

        _hasAudio[file] = hasAudio;
        return hasAudio;
    }
}
