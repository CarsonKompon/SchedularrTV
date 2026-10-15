using System.Text.RegularExpressions;

namespace SchedularrTV.Core.Configuration;

/// <summary>The supported placeholders shared by voice-template validation and rendering.</summary>
public static class GeneratedVoiceTokens
{
    public static readonly Regex Placeholder = new(@"\{([^{}]+)\}", RegexOptions.CultureInvariant);
    private static readonly Regex Upcoming = new(@"^up_next(?:_([1-9]|10))?(?::(raw|start_time|end_time|episode))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool TryUpcoming(string token, out int index, out string field)
    {
        Match match = Upcoming.Match(token);
        index = match.Success && match.Groups[1].Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) - 1 : 0;
        field = match.Success ? match.Groups[2].Value.ToLowerInvariant() : string.Empty;
        return match.Success;
    }

    public static bool Known(string token) => token.ToLowerInvariant() is "channel" or "show" or "episode" or "start_time" or "end_time"
        || TryUpcoming(token, out _, out _);

    public static IEnumerable<string> Unknown(string text) => Placeholder.Matches(text).Select(match => match.Groups[1].Value).Where(token => !Known(token));

    public static int RequiredUpcoming(string text) => Placeholder.Matches(text)
        .Select(match => TryUpcoming(match.Groups[1].Value, out int index, out _) ? index + 1 : 0).DefaultIfEmpty().Max();

}
