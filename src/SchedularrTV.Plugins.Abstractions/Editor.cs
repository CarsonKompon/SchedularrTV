using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using SchedularrTV.Core.Configuration;

namespace SchedularrTV.Plugins.Editor;

/// <summary>Where a plugin's editor section appears.</summary>
public enum EditorSurface
{
    /// <summary>A tab on a channel's page.</summary>
    Channel,

    /// <summary>A collapsible section in each block's editor.</summary>
    Block,

    /// <summary>A card on the Schedule settings page (the <c>settings:</c> part of the schedule).</summary>
    ScheduleSettings
}

/// <summary>
/// A plugin's part of the schedule editor: a component shown on a channel, in a block or on the Schedule settings
/// page. Register one with <see cref="EditorServiceCollectionExtensions"/>; a plugin that declares
/// <see cref="PluginManifest.ChannelSettings"/> (or block or schedule settings) gets a generated one instead.
/// </summary>
public sealed class PluginEditorSection
{
    public required string PluginId { get; init; }

    public required string Title { get; init; }

    public required EditorSurface Surface { get; init; }

    /// <summary>A component with a <c>[Parameter] PluginEditorContext Context</c>.</summary>
    public required Type Component { get; init; }

    /// <summary>Identifies the section among the plugin's own, e.g. as the channel tab in the address bar.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Lower comes first. The scheduler's own channel tabs are General 0, Blocks 10 and Breaks 30.</summary>
    public int Order { get; init; } = 100;

    /// <summary>One of the app's icon names, e.g. <c>image</c>.</summary>
    public string? Icon { get; init; }

    /// <summary>The section's key, made unique across plugins.</summary>
    public string Id => string.IsNullOrEmpty(Key) ? PluginId : $"{PluginId}.{Key}";
}

/// <summary>
/// What a plugin's editor component edits. Change the channel, block or settings (or this plugin's section) in
/// place, then call <see cref="NotifyChanged"/> so the editor offers to save.
/// </summary>
public sealed class PluginEditorContext
{
    public required string PluginId { get; init; }

    /// <summary>The whole schedule being edited.</summary>
    public required SchedulerConfig Config { get; init; }

    /// <summary>The channel being edited; null on the Schedule settings page.</summary>
    public ChannelConfig? Channel { get; init; }

    /// <summary>The block being edited; null on a channel tab or the Schedule settings page.</summary>
    public BlockConfig? Block { get; init; }

    public required Action Changed { get; init; }

    public EditorSurface Surface => Block is not null ? EditorSurface.Block : Channel is not null ? EditorSurface.Channel : EditorSurface.ScheduleSettings;

    /// <summary>The <c>plugins:</c> map at this level: the block's, the channel's or the global one.</summary>
    public Dictionary<string, object?>? Sections
    {
        get => Block is not null ? Block.Plugins : Channel is not null ? Channel.Plugins : Config.Settings.Plugins;
        set
        {
            if (Block is not null) Block.Plugins = value;
            else if (Channel is not null) Channel.Plugins = value;
            else Config.Settings.Plugins = value;
        }
    }

    /// <summary>This plugin's section at this level, bound to <typeparamref name="T"/>; null when there isn't one.</summary>
    public T? Read<T>() where T : class => PluginSettings.Read<T>(Sections, PluginId);

    /// <summary>Replaces this plugin's section at this level; null removes it. Calls <see cref="NotifyChanged"/>.</summary>
    public void Write(object? section)
    {
        Dictionary<string, object?> sections = Sections ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        string key = sections.Keys.FirstOrDefault(existing => string.Equals(existing, PluginId, StringComparison.OrdinalIgnoreCase)) ?? PluginId;
        if (section is null)
        {
            sections.Remove(key);
        }
        else
        {
            sections[key] = section;
        }

        Sections = sections.Count > 0 ? sections : null;
        NotifyChanged();
    }

    /// <summary>This plugin's section at this level as raw values (as the YAML has them), or null.</summary>
    public IReadOnlyDictionary<string, object?>? Raw =>
        Sections?.FirstOrDefault(pair => string.Equals(pair.Key, PluginId, StringComparison.OrdinalIgnoreCase)).Value as IReadOnlyDictionary<string, object?>;

    public void NotifyChanged() => Changed();
}

/// <summary>Adds a plugin's components to the schedule editor.</summary>
public static class EditorServiceCollectionExtensions
{
    /// <summary>A tab on every channel's page.</summary>
    public static IServiceCollection AddChannelEditorTab<TComponent>(this IServiceCollection services,
        IPluginContext context, string title, int order = 100, string? icon = null, string key = "")
        where TComponent : IComponent =>
        services.AddEditorSection<TComponent>(context, title, EditorSurface.Channel, order, icon, key);

    /// <summary>A section in every block's editor.</summary>
    public static IServiceCollection AddBlockEditorSection<TComponent>(this IServiceCollection services,
        IPluginContext context, string title, int order = 100, string key = "")
        where TComponent : IComponent =>
        services.AddEditorSection<TComponent>(context, title, EditorSurface.Block, order, null, key);

    /// <summary>A card on the Schedule settings page.</summary>
    public static IServiceCollection AddScheduleSettingsSection<TComponent>(this IServiceCollection services,
        IPluginContext context, string title, int order = 100, string key = "")
        where TComponent : IComponent =>
        services.AddEditorSection<TComponent>(context, title, EditorSurface.ScheduleSettings, order, null, key);

    private static IServiceCollection AddEditorSection<TComponent>(this IServiceCollection services,
        IPluginContext context, string title, EditorSurface surface, int order, string? icon, string key) =>
        services.AddSingleton(new PluginEditorSection
        {
            PluginId = context.Manifest.Id,
            Title = title,
            Surface = surface,
            Component = typeof(TComponent),
            Order = order,
            Icon = icon,
            Key = key
        });
}
