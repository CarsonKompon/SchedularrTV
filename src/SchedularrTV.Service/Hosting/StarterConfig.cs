namespace SchedularrTV.Service.Hosting;

/// <summary>The schedule written on first run: global settings only, so a new install starts clean.</summary>
public static class StarterConfig
{
    public static void WriteIfMissing(string path)
    {
        if (File.Exists(path))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"""
            ---
            # SchedularrTV channel definition. Edit it in the web UI, or here; changes are picked up automatically.
            settings:
              time_zone: "{LocalTimeZone()}"
              horizon: 48h
              rebuild_interval: 1h
              seed: "{Guid.NewGuid():N}"
              # A new schedule starts on ad breaks: there are no older break settings to move into them.
              ad_breaks_migrated: true

            channels: []
            """);
    }

    private static string LocalTimeZone()
    {
        string id = TimeZoneInfo.Local.Id;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out string? iana))
        {
            return iana;
        }

        // Some Linux setups report "Local" or a path rather than a zone name.
        return id.Contains('/') && !id.StartsWith('/') ? id : "UTC";
    }
}
