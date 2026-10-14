using SchedularrTV.Core.Configuration;

namespace SchedularrTV.Service.Editor;

/// <summary>Renaming and deleting ad breaks, keeping the channels and blocks that name them in step.</summary>
public static class AdBreakNames
{
    /// <summary>Deletes an ad break and takes it off every channel and block.</summary>
    public static void Remove(SchedulerConfig config, string name)
    {
        config.AdBreaks.Remove(name);
        foreach (ChannelConfig channel in config.Channels)
        {
            channel.AdBreaks = Without(channel.AdBreaks, name);
            foreach (BlockConfig block in channel.Blocks)
            {
                block.AdBreaks = Without(block.AdBreaks, name);
            }
        }
    }

    /// <summary>Renames an ad break everywhere. Null when done; otherwise why it couldn't be.</summary>
    public static string? Rename(SchedulerConfig config, string from, string to)
    {
        string renamed = to.Trim();
        if (renamed.Length == 0)
        {
            return "Give the ad break a name.";
        }

        if (!config.AdBreaks.TryGetValue(from, out AdBreakConfig? adBreak))
        {
            return "That ad break no longer exists.";
        }

        if (!string.Equals(renamed, from, StringComparison.OrdinalIgnoreCase) && config.AdBreaks.ContainsKey(renamed))
        {
            return $"An ad break called \"{renamed}\" already exists.";
        }

        config.AdBreaks.Remove(from);
        config.AdBreaks[renamed] = adBreak;
        foreach (ChannelConfig channel in config.Channels)
        {
            channel.AdBreaks = Renamed(channel.AdBreaks, from, renamed);
            foreach (BlockConfig block in channel.Blocks)
            {
                block.AdBreaks = Renamed(block.AdBreaks, from, renamed);
            }
        }

        return null;
    }

    private static List<string>? Without(List<string>? names, string key) =>
        names?.Where(name => !string.Equals(name, key, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: > 0 } left ? left : null;

    private static List<string>? Renamed(List<string>? names, string from, string to) =>
        names?.Select(name => string.Equals(name, from, StringComparison.OrdinalIgnoreCase) ? to : name).ToList();
}
