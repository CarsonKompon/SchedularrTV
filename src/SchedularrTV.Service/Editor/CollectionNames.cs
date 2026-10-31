using System.Collections;
using System.Reflection;
using SchedularrTV.Core.Configuration;

namespace SchedularrTV.Service.Editor;

/// <summary>Renaming collections, keeping everything that names them in step.</summary>
public static class CollectionNames
{
    /// <summary>
    /// Renames a collection everywhere it's named: every selector that plays it (channels' rotations, blocks' shows,
    /// anchors, interstitials, other collections…) and holidays that fill in from it. Null when done; otherwise why
    /// it couldn't be.
    /// </summary>
    public static string? Rename(SchedulerConfig config, string from, string to, out int references)
    {
        references = 0;
        string renamed = to.Trim();
        if (renamed.Length == 0)
        {
            return "Give the collection a name.";
        }

        if (!config.GlobalCollections.TryGetValue(from, out ContentSelector? collection))
        {
            return "That collection no longer exists.";
        }

        if (string.Equals(renamed, from, StringComparison.Ordinal))
        {
            return null;
        }

        if (!string.Equals(renamed, from, StringComparison.OrdinalIgnoreCase) && config.GlobalCollections.ContainsKey(renamed))
        {
            return $"A collection called \"{renamed}\" already exists.";
        }

        config.GlobalCollections.Remove(from);
        config.GlobalCollections[renamed] = collection;

        foreach (ContentSelector selector in Selectors(config))
        {
            if (string.Equals(selector.Collection, from, StringComparison.OrdinalIgnoreCase))
            {
                selector.Collection = renamed;
                references++;
            }
        }

        foreach (ChannelHoliday holiday in config.GlobalHolidays.Concat(config.Channels.SelectMany(channel => channel.Holidays)))
        {
            if (string.Equals(holiday.Fallback, from, StringComparison.OrdinalIgnoreCase))
            {
                holiday.Fallback = renamed;
                references++;
            }
        }

        return null;
    }

    /// <summary>
    /// Every selector anywhere in the schedule, found by walking its settings (so a selector a new setting adds is
    /// found too). Computed and unsaved properties are skipped, as are plugins' untyped sections.
    /// </summary>
    public static IEnumerable<ContentSelector> Selectors(SchedulerConfig config)
    {
        HashSet<object> seen = new(ReferenceEqualityComparer.Instance);
        List<ContentSelector> found = [];
        Walk(config, seen, found);
        return found;
    }

    private static void Walk(object? value, HashSet<object> seen, List<ContentSelector> found)
    {
        if (value is null or string || value.GetType().IsValueType || !seen.Add(value))
        {
            return;
        }

        if (value is ContentSelector selector)
        {
            found.Add(selector);
        }

        if (value is IDictionary dictionary)
        {
            foreach (object? entry in dictionary.Values)
            {
                Walk(entry, seen, found);
            }

            return;
        }

        if (value is IEnumerable sequence)
        {
            foreach (object? entry in sequence)
            {
                Walk(entry, seen, found);
            }

            return;
        }

        // Only the schedule's own settings; anything else (a plugin's untyped section) is left alone.
        if (value.GetType().Namespace?.StartsWith("SchedularrTV", StringComparison.Ordinal) != true)
        {
            return;
        }

        foreach (PropertyInfo property in Properties(value.GetType()))
        {
            Walk(property.GetValue(value), seen, found);
        }
    }

    private static readonly Dictionary<Type, PropertyInfo[]> Cache = [];

    private static PropertyInfo[] Properties(Type type)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(type, out PropertyInfo[]? properties))
            {
                properties = [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(property => property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0
                        && !property.IsDefined(typeof(YamlDotNet.Serialization.YamlIgnoreAttribute))
                        && !property.IsDefined(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute))
                        && !property.PropertyType.IsValueType && property.PropertyType != typeof(string))];
                Cache[type] = properties;
            }

            return properties;
        }
    }
}
