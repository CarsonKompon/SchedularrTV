using System.Collections.Frozen;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;

namespace SchedularrTV.WebChannels;

/// <summary>
/// The channels as a media source. Each channel is a show (its name), and each allowed length an episode of it, so a
/// page can go anywhere a show can: a channel's base rotation for a 24/7 channel, a block, or a channel's interstitial
/// shows for short segments between programmes.
/// </summary>
public sealed class WebChannelLibrary(ChannelStore store, WebChannelSettings settings) : IMediaLibrary
{
    public const string SourceId = "web-channels";

    /// <summary>Every web page channel carries this tag, besides its preset's own (Weather, Clock…).</summary>
    public const string Tag = "Web channel";

    public ValueTask<IReadOnlyList<MediaItem>> SearchAsync(QueryExpression expression, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<MediaItem>>([.. Items().Where(item => QueryEvaluator.Matches(item, expression))]);

    public ValueTask RefreshAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public IEnumerable<MediaItem> Items()
    {
        foreach (WebChannel channel in store.Channels)
        {
            // A custom channel without a usable address has nothing to show.
            if (channel.Preset == WebPresets.Custom && !WebPresets.IsWebAddress(channel.Url))
            {
                continue;
            }

            string url = settings.LiveUrl(channel.Id);
            foreach (int minutes in channel.ValidLengths)
            {
                yield return Item(channel, minutes, url);
            }
        }
    }

    public static MediaItem Item(WebChannel channel, int minutes, string url)
    {
        WebPreset preset = channel.PresetInfo;
        return new MediaItem
        {
            Id = $"{SourceId}:{channel.Id}:{minutes}",
            Title = $"{preset.EpisodeTitle(channel)} ({Describe(minutes)})",
            Kind = MediaKind.Episode,
            Duration = TimeSpan.FromMinutes(minutes),
            SeriesId = $"{SourceId}:{channel.Id}",
            SeriesName = channel.Name,
            SeasonNumber = 1,
            EpisodeNumber = minutes,
            Overview = preset.Overview(channel),
            Tags = [.. preset.Tags, Tag],
            Networks = preset.Network is { } network ? new[] { network } : Array.Empty<string>(),
            Path = url,
            Attributes = new Dictionary<string, string>
            {
                ["ersatztv.live"] = "true",
                ["ersatztv.probe_hint"] = LiveStream.ProbeHint,
                ["web-channels.channel"] = channel.Id,
                ["web-channels.preset"] = preset.Id
            }.ToFrozenDictionary()
        };
    }

    private static string Describe(int minutes) =>
        minutes % 60 == 0 ? minutes == 60 ? "1 hour" : $"{minutes / 60} hours" : $"{minutes} min";
}
