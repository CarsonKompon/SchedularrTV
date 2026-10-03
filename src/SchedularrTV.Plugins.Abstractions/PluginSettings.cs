using SchedularrTV.Core.Configuration;

namespace SchedularrTV.Plugins;

/// <summary>
/// Reads a plugin's section from the schedule YAML (<c>plugins:</c> under <c>settings</c>, a channel or a block)
/// into a settings class, with the same snake_case naming as the rest of the file.
/// </summary>
public static class PluginSettings
{
    public static T? Read<T>(IReadOnlyDictionary<string, object?>? sections, string pluginId) where T : class
    {
        if (sections is null)
        {
            return null;
        }

        foreach ((string key, object? value) in sections)
        {
            if (string.Equals(key, pluginId, StringComparison.OrdinalIgnoreCase))
            {
                return value is null ? null : SchedulerConfigLoader.ConvertSection<T>(value);
            }
        }

        return null;
    }
}

/// <summary>
/// Given to a plugin's own settings component (see <see cref="PluginManifest.SettingsComponent"/>): read and
/// change the values stored under <c>Plugins:&lt;id&gt;</c>. The Plugins page saves them.
/// </summary>
public sealed class PluginSettingsEditor(string pluginId, IDictionary<string, string?> values)
{
    public string PluginId { get; } = pluginId;

    public bool IsDirty { get; private set; }

    public event Action? Changed;

    public string? Get(string key) => values.TryGetValue(key, out string? value) ? value : null;

    public void Set(string key, string? value)
    {
        values[key] = string.IsNullOrEmpty(value) ? null : value;
        IsDirty = true;
        Changed?.Invoke();
    }

    public IReadOnlyDictionary<string, string?> Values => new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);

    public void MarkSaved() => IsDirty = false;
}
