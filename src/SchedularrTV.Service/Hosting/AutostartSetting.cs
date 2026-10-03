using SchedularrTV.Hosting;

namespace SchedularrTV.Service.Hosting;

/// <summary>Turns start-at-sign-in on or off for whichever program started this server.</summary>
public static class AutostartSetting
{
    public static void Apply(AppRuntime runtime, bool on)
    {
        if (!on)
        {
            Autostart.Disable();
            return;
        }

        string target = Autostart.DefaultTarget() ?? throw new InvalidOperationException("Can't tell where this program is.");

        // Only a non-default data folder needs passing on; the default is found again at sign-in.
        string[] arguments = string.Equals(runtime.DataDirectory, AppPaths.PerUserDirectory(), StringComparison.Ordinal)
            ? []
            : [AppPaths.DataDirectoryArgument, runtime.DataDirectory];

        // Run as "dotnet SchedularrTV.Service.dll": start the same assembly, not bare dotnet.
        if (string.Equals(Path.GetFileNameWithoutExtension(target), "dotnet", StringComparison.OrdinalIgnoreCase)
            && System.Reflection.Assembly.GetEntryAssembly()?.Location is { Length: > 0 } entry)
        {
            arguments = [entry, .. arguments];
        }

        Autostart.Enable(target, arguments);
    }
}
