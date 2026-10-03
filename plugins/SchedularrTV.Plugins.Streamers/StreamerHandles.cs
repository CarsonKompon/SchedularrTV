using System.Text.RegularExpressions;

namespace SchedularrTV.Streamers;

/// <summary>Tidies what was typed or pasted for a streamer into the form each platform is asked with.</summary>
public static partial class StreamerHandles
{
    /// <summary>
    /// A Twitch login (lower case, no URL), or a YouTube <c>@handle</c> or <c>channel/UC…</c> path; empty when what was
    /// given can't be one.
    /// </summary>
    public static string Normalize(StreamPlatform platform, string? input)
    {
        string text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        if (platform == StreamPlatform.Twitch)
        {
            Match link = TwitchLink().Match(text);
            string login = link.Success ? link.Groups[1].Value : text.TrimStart('@');
            return TwitchLogin().IsMatch(login) ? login.ToLowerInvariant() : string.Empty;
        }

        Match youtube = YouTubeLink().Match(text);
        if (youtube.Success)
        {
            text = youtube.Groups[1].Value;
        }

        text = text.Trim('/');
        if (text.StartsWith("channel/", StringComparison.OrdinalIgnoreCase))
        {
            string id = text["channel/".Length..];
            return ChannelId().IsMatch(id) ? "channel/" + id : string.Empty;
        }

        if (ChannelId().IsMatch(text))
        {
            return "channel/" + text;
        }

        if (text.StartsWith("c/", StringComparison.OrdinalIgnoreCase) || text.StartsWith("user/", StringComparison.OrdinalIgnoreCase))
        {
            return text.Split('/', 3) is [var kind, var name, ..] && name.Length > 0 ? $"{kind.ToLowerInvariant()}/{name}" : string.Empty;
        }

        string handle = text.TrimStart('@').Split('/')[0];
        return YouTubeHandle().IsMatch(handle) ? "@" + handle : string.Empty;
    }

    public static string Key(StreamPlatform platform, string handle) =>
        $"{(platform == StreamPlatform.Twitch ? "twitch" : "youtube")}:{Normalize(platform, handle).ToLowerInvariant()}";

    /// <summary>The streamer's page, for links and for yt-dlp.</summary>
    public static string Url(StreamPlatform platform, string handle) => platform == StreamPlatform.Twitch
        ? $"https://www.twitch.tv/{Normalize(platform, handle)}"
        : $"https://www.youtube.com/{Normalize(platform, handle)}";

    public static string PlatformName(StreamPlatform platform) => platform == StreamPlatform.Twitch ? "Twitch" : "YouTube";

    [GeneratedRegex(@"^(?:https?://)?(?:www\.|m\.)?twitch\.tv/([A-Za-z0-9_]+)", RegexOptions.IgnoreCase)]
    private static partial Regex TwitchLink();

    [GeneratedRegex("^[A-Za-z0-9_]{2,25}$")]
    private static partial Regex TwitchLogin();

    [GeneratedRegex(@"^(?:https?://)?(?:www\.|m\.)?youtube\.com/((?:channel|c|user)/[^/?#]+|[^/?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex YouTubeLink();

    [GeneratedRegex("^UC[A-Za-z0-9_-]{22}$")]
    private static partial Regex ChannelId();

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{3,30}$")]
    private static partial Regex YouTubeHandle();
}
