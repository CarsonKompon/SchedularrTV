using SchedularrTV.Providers.ErsatzTv;
using SchedularrTV.Providers.FileSystem;
using SchedularrTV.Providers.Jellyfin;

namespace SchedularrTV.Service.Plugins;

/// <summary>
/// The plugins compiled into the scheduler, in the order they register (outputs run in this order). Generated
/// promos and the voices are separate downloads (the all-plugins zip of each release), installed like any other.
/// </summary>
public static class BuiltInPlugins
{
    public static IReadOnlyList<IBuiltInPlugin> All =>
    [
        new JellyfinPlugin(),
        new FileSystemPlugin(),
        new ErsatzTvPlugin(),
        new PromosPlugin(),
        new ArchiveFollowPlugin()
    ];

    internal static string Version => typeof(BuiltInPlugins).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}
