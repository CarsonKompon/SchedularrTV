namespace SchedularrTV.Service.Components.Shared;

/// <summary>File-picker filters for the settings sections.</summary>
public static class SettingsFiles
{
    /// <summary>Programs: <c>.exe</c> on Windows; elsewhere programs have no extension, so everything is listed.</summary>
    public static readonly string[] Programs = OperatingSystem.IsWindows() ? new[] { ".exe" } : Array.Empty<string>();

    /// <summary>For folder pickers, which list no files.</summary>
    public static readonly string[] None = Array.Empty<string>();
}
