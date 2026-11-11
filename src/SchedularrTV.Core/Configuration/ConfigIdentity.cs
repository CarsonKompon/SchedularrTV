using System.Collections;

namespace SchedularrTV.Core.Configuration;

/// <summary>
/// Channels and blocks as they were known at some moment: each channel's stable <see cref="ChannelConfig.Uid"/> with the id
/// it had, and each of its blocks' <see cref="BlockConfig.Uid"/> with the name it had. Comparing a schedule against it tells
/// what was renamed since, so references to the old id or name can follow (<see cref="ConfigIdentity.FollowRenames"/>).
/// </summary>
public sealed class ConfigIdentity
{
    private readonly Dictionary<string, (string Id, Dictionary<string, string> Blocks)> _channels = new(StringComparer.Ordinal);

    private ConfigIdentity()
    {
    }

    public static ConfigIdentity Empty { get; } = new();

    /// <summary>The ids and block names <paramref name="config"/> has now, by uid (channels and blocks without one are left out).</summary>
    /// <param name="previous">
    /// What was known before: an id or name cleared for the moment (mid-typing) is remembered as it was, so when it's
    /// filled in again the rename is from the old one.
    /// </param>
    public static ConfigIdentity Of(SchedulerConfig config, ConfigIdentity? previous = null)
    {
        var identity = new ConfigIdentity();
        foreach (ChannelConfig channel in config.Channels)
        {
            if (channel.Uid is not { Length: > 0 } uid || identity._channels.ContainsKey(uid))
            {
                continue;
            }

            (string Id, Dictionary<string, string> Blocks)? was = previous is not null && previous._channels.TryGetValue(uid, out var known) ? known : null;
            Dictionary<string, string> blocks = new(StringComparer.Ordinal);
            foreach (BlockConfig block in channel.Blocks)
            {
                if (block.Uid is { Length: > 0 } blockUid)
                {
                    string? oldName = null;
                    blocks.TryAdd(blockUid, string.IsNullOrWhiteSpace(block.Name) && was?.Blocks.TryGetValue(blockUid, out oldName) == true ? oldName! : block.Name);
                }
            }

            identity._channels[uid] = (string.IsNullOrWhiteSpace(channel.Id) && was is { } before ? before.Id : channel.Id, blocks);
        }

        return identity;
    }

    /// <summary>
    /// Gives every channel, and every block within its channel, a uid of its own: new ones get one, and of two sharing one
    /// (a duplicated channel or block), the one still matching <paramref name="before"/> keeps it. Returns whether any was given.
    /// </summary>
    public static bool EnsureUids(SchedulerConfig config, ConfigIdentity? before = null)
    {
        bool changed = false;
        HashSet<string> taken = new(StringComparer.Ordinal);
        bool ChannelOwns(ChannelConfig candidate, string candidateUid) =>
            before is not null && before._channels.TryGetValue(candidateUid, out var knownChannel)
            && string.Equals(knownChannel.Id, candidate.Id, StringComparison.Ordinal);

        foreach (ChannelConfig channel in Owners(config.Channels, candidate => candidate.Uid, ChannelOwns))
        {
            if (channel.Uid is not { Length: > 0 } channelUid || !taken.Add(channelUid))
            {
                channel.Uid = NewUid(taken);
                changed = true;
            }

            Dictionary<string, string>? knownBlocks = before is not null && before._channels.TryGetValue(channel.Uid!, out var knownThis) ? knownThis.Blocks : null;
            bool BlockOwns(BlockConfig candidate, string candidateUid) =>
                knownBlocks is not null && knownBlocks.TryGetValue(candidateUid, out string? knownName)
                && string.Equals(knownName, candidate.Name, StringComparison.Ordinal);

            HashSet<string> blockTaken = new(StringComparer.Ordinal);
            foreach (BlockConfig block in Owners(channel.Blocks, candidate => candidate.Uid, BlockOwns))
            {
                if (block.Uid is not { Length: > 0 } blockUid || !blockTaken.Add(blockUid))
                {
                    block.Uid = NewUid(blockTaken);
                    changed = true;
                }
            }
        }

        return changed;
    }

    // The items in an order where, of several sharing a uid, the one that rightly owns it comes first (so it keeps it).
    private static IEnumerable<T> Owners<T>(IEnumerable<T> items, Func<T, string?> uidOf, Func<T, string, bool> owns)
    {
        List<T> list = [.. items];
        return list
            .Select((item, index) => (Item: item, Index: index, Owns: uidOf(item) is { Length: > 0 } uid && owns(item, uid)))
            .OrderByDescending(entry => entry.Owns)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Item)
            .ToList();
    }

    private static string NewUid(HashSet<string> taken)
    {
        string uid;
        do
        {
            uid = Guid.NewGuid().ToString("N")[..12];
        }
        while (!taken.Add(uid));

        return uid;
    }

    /// <summary>
    /// Whatever in <paramref name="config"/> still names a channel by the id it had in <paramref name="before"/>, or a block
    /// by its old name, is changed to the new one: generated promos and overlays taken <c>from</c> another channel, channel
    /// and block voice-over takes, and Archive Follow's block mapping. Renames are matched by uid, so swapping two ids works
    /// too. Returns whether anything changed.
    /// </summary>
    public static bool FollowRenames(SchedulerConfig config, ConfigIdentity before)
    {
        Dictionary<string, string> channelIds = new(StringComparer.OrdinalIgnoreCase);
        // Block renames by the channel's id now.
        Dictionary<string, Dictionary<string, string>> blockNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (ChannelConfig channel in config.Channels)
        {
            if (channel.Uid is not { Length: > 0 } uid || !before._channels.TryGetValue(uid, out var was))
            {
                continue;
            }

            if (!string.Equals(was.Id, channel.Id, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(was.Id) && !string.IsNullOrWhiteSpace(channel.Id))
            {
                channelIds.TryAdd(was.Id, channel.Id);
            }

            Dictionary<string, string> renamed = new(StringComparer.OrdinalIgnoreCase);
            foreach (BlockConfig block in channel.Blocks)
            {
                if (block.Uid is { Length: > 0 } blockUid && was.Blocks.TryGetValue(blockUid, out string? oldName)
                    && !string.Equals(oldName, block.Name, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(oldName) && !string.IsNullOrWhiteSpace(block.Name))
                {
                    renamed.TryAdd(oldName, block.Name);
                }
            }

            if (renamed.Count > 0)
            {
                blockNames[channel.Id] = renamed;
            }
        }

        if (channelIds.Count == 0 && blockNames.Count == 0)
        {
            return false;
        }

        bool changed = false;
        string? Channel(string? id) => id is not null && channelIds.TryGetValue(id, out string? now) ? now : null;

        foreach (ChannelConfig channel in config.Channels)
        {
            changed |= FollowFrom(channel.GeneratedPromos, Channel);
            changed |= FollowPluginFrom(channel.Plugins, Channel);
            foreach (BlockConfig block in channel.Blocks)
            {
                changed |= FollowFrom(block.GeneratedPromos, Channel);
                changed |= FollowPluginFrom(block.Plugins, Channel);
            }

            // Archive Follow maps archived blocks onto this channel's blocks by name.
            if (channel.Follow?.Blocks is { Count: > 0 } mapping && blockNames.TryGetValue(channel.Id, out var renamed))
            {
                foreach (string archived in mapping.Keys.ToList())
                {
                    if (mapping[archived] is { } target && renamed.TryGetValue(target, out string? now))
                    {
                        mapping[archived] = now;
                        changed = true;
                    }
                }
            }
        }

        // Channel name voice-over takes are kept per channel ("channel:<id>") and per block ("block:<id>|<name>").
        foreach (GeneratedVoicePack pack in config.Settings.GeneratedPromosVoicePacks)
        {
            foreach (GeneratedVoicePhrase phrase in pack.Phrases)
            {
                foreach (GeneratedVoiceEntry entry in phrase.Entries)
                {
                    if (FollowKey(entry.Key, channelIds, blockNames) is { } key)
                    {
                        entry.Key = key;
                        changed = true;
                    }
                }
            }
        }

        return changed;
    }

    /// <summary>The channels whose id changed since <paramref name="before"/>: old id to new.</summary>
    public static IReadOnlyDictionary<string, string> ChannelRenames(SchedulerConfig config, ConfigIdentity before)
    {
        Dictionary<string, string> renamed = new(StringComparer.OrdinalIgnoreCase);
        foreach (ChannelConfig channel in config.Channels)
        {
            if (channel.Uid is { Length: > 0 } uid && before._channels.TryGetValue(uid, out var was)
                && !string.IsNullOrWhiteSpace(was.Id) && !string.IsNullOrWhiteSpace(channel.Id)
                && !string.Equals(was.Id, channel.Id, StringComparison.OrdinalIgnoreCase))
            {
                renamed.TryAdd(was.Id, channel.Id);
            }
        }

        return renamed;
    }

    private static bool FollowFrom(GeneratedPromosConfig? generated, Func<string?, string?> channel)
    {
        if (generated is not null && channel(generated.From) is { } now)
        {
            generated.From = now;
            return true;
        }

        return false;
    }

    // Any plugin's settings taken "from" another channel (overlays, say) follow it too.
    private static bool FollowPluginFrom(Dictionary<string, object?>? plugins, Func<string?, string?> channel)
    {
        if (plugins is null)
        {
            return false;
        }

        bool changed = false;
        foreach (object? section in plugins.Values)
        {
            if (section is not IDictionary settings)
            {
                continue;
            }

            foreach (object key in settings.Keys.Cast<object>().ToList())
            {
                if (string.Equals(key?.ToString(), "from", StringComparison.OrdinalIgnoreCase)
                    && settings[key!] is string from && channel(from) is { } now)
                {
                    settings[key!] = now;
                    changed = true;
                }
            }
        }

        return changed;
    }

    private static string? FollowKey(string key, Dictionary<string, string> channelIds, Dictionary<string, Dictionary<string, string>> blockNames)
    {
        const string channelPrefix = "channel:";
        const string blockPrefix = "block:";
        if (key.StartsWith(channelPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return channelIds.TryGetValue(key[channelPrefix.Length..], out string? now) ? GeneratedVoiceKeys.Channel(now) : null;
        }

        if (key.StartsWith(blockPrefix, StringComparison.OrdinalIgnoreCase) && key.IndexOf('|', blockPrefix.Length) is var bar and > 0)
        {
            string oldChannel = key[blockPrefix.Length..bar];
            string oldBlock = key[(bar + 1)..];
            string channel = channelIds.TryGetValue(oldChannel, out string? movedTo) ? movedTo : oldChannel;
            string block = blockNames.TryGetValue(channel, out var renamed) && renamed.TryGetValue(oldBlock, out string? newName) ? newName : oldBlock;
            string followed = GeneratedVoiceKeys.Block(channel, block);
            return string.Equals(followed, key, StringComparison.Ordinal) ? null : followed;
        }

        return null;
    }
}
