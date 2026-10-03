using SchedularrTV.Core.Configuration;

namespace SchedularrTV.Service.Editor;

/// <summary>
/// A shared, editable copy of the schedule configuration.
/// </summary>
/// <remarks>
/// The UI mutates this copy and saves explicitly, so a half-finished channel never reaches the
/// scheduler. Saving round-trips through YAML and the same validator the file watcher uses, which
/// guarantees the editor cannot produce a config the loader would reject. Registered as a singleton
/// so unsaved work survives a page refresh; <see cref="Changed"/> may therefore be raised on any
/// thread and subscribers must marshal to their own renderer.
/// </remarks>
public sealed class ConfigEditor(SchedulerConfigProvider provider)
{
    private SchedulerConfig _working = Clone(provider.Current);

    public SchedulerConfig Working => _working;

    public bool IsDirty { get; private set; }

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public IReadOnlyList<string> Warnings { get; private set; } = [];

    public string? SaveError { get; private set; }

    public event Action? Changed;

    /// <summary>Call after any mutation so the UI and dirty state stay in step.</summary>
    public void MarkDirty()
    {
        IsDirty = true;
        Validate();
        Changed?.Invoke();
    }

    public void Reload()
    {
        _working = Clone(provider.Current);
        IsDirty = false;
        SaveError = null;
        Validate();
        Changed?.Invoke();
    }

    public bool Save()
    {
        SaveError = null;

        try
        {
            // Merge against the file on disk so the user's comments and hand-written durations survive
            // a save from the visual editor.
            string yaml = YamlCommentMerger.Merge(provider.ReadRaw(), SchedulerConfigLoader.Serialize(_working));
            ConfigValidationResult result = provider.Save(yaml);

            Errors = result.Errors;
            Warnings = result.Warnings;

            if (!result.IsValid)
            {
                return false;
            }

            IsDirty = false;
            Changed?.Invoke();
            return true;
        }
        catch (Exception ex) when (ex is SchedulerConfigException or YamlDotNet.Core.YamlException or IOException)
        {
            SaveError = ex.Message;
            Changed?.Invoke();
            return false;
        }
    }

    public string ToYaml() => SchedulerConfigLoader.Serialize(_working);

    public void Validate()
    {
        ConfigValidationResult result = SchedulerConfigLoader.Validate(_working);
        Errors = result.Errors;
        Warnings = result.Warnings;
    }

    public ChannelConfig? FindChannel(string id) =>
        _working.Channels.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    public ChannelConfig CopyChannelSettings(string destinationId, string sourceId)
    {
        ChannelConfig destination = FindChannel(destinationId)
            ?? throw new InvalidOperationException("The destination channel no longer exists.");
        ChannelConfig source = FindChannel(sourceId)
            ?? throw new InvalidOperationException("The source channel no longer exists.");
        if (ReferenceEquals(destination, source))
        {
            throw new InvalidOperationException("Choose a different channel to copy from.");
        }

        ChannelConfig copied = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(
            new SchedulerConfig { Channels = [source] })).Channels.Single();
        copied.Id = destination.Id;
        copied.Name = destination.Name;
        copied.Number = destination.Number;
        copied.Logo = destination.Logo;
        _working.Channels[_working.Channels.IndexOf(destination)] = copied;
        MarkDirty();
        return copied;
    }

    /// <summary>Next unused channel number, so a new channel never collides with an existing one.</summary>
    public int NextChannelNumber()
    {
        int highest = _working.Channels.Count == 0 ? 900 : _working.Channels.Max(c => c.Number);
        return highest + 1;
    }

    /// <summary>
    /// First unused channel id. Ids are not derived from the channel number: the number can be
    /// changed afterwards, which would leave the id claiming a number a later channel goes on to use.
    /// </summary>
    public string NextChannelId()
    {
        for (int i = 1; ; i++)
        {
            string candidate = $"ch{i}";
            if (!IsChannelIdTaken(candidate))
            {
                return candidate;
            }
        }
    }

    public bool IsChannelIdTaken(string id, ChannelConfig? ignoring = null) =>
        _working.Channels.Any(c => !ReferenceEquals(c, ignoring)
                                   && string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    // Round-tripping through YAML is the simplest deep clone that is guaranteed to match what the
    // loader will accept, and the document is small enough that the cost is irrelevant.
    private static SchedulerConfig Clone(SchedulerConfig config)
    {
        try
        {
            return SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config));
        }
        catch (Exception ex) when (ex is SchedulerConfigException or YamlDotNet.Core.YamlException)
        {
            return new SchedulerConfig();
        }
    }
}
