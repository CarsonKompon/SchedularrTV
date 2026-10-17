namespace SchedularrTV.Core.Configuration;

/// <summary>
/// Public front door for the friendly duration syntax the YAML accepts (<c>30m</c>, <c>1h</c>,
/// <c>21:00</c>, <c>500ms</c>), so editors can parse and render the same forms.
/// </summary>
public static class DurationText
{
    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            value = FlexibleTimeSpanConverter.Parse(text);
            return true;
        }
        catch (SchedulerConfigException)
        {
            return false;
        }
    }

    /// <summary>Renders the shortest unambiguous form, matching how the samples are written.</summary>
    public static string Format(TimeSpan? value)
    {
        if (value is not { } v)
        {
            return string.Empty;
        }

        if (v == TimeSpan.Zero)
        {
            return "0s";
        }

        // Negative durations (an overlap) read back with the same leading minus.
        if (v < TimeSpan.Zero)
        {
            return "-" + Format(-v);
        }

        if (v.TotalSeconds < 1)
        {
            return $"{v.TotalMilliseconds:0}ms";
        }

        if (v.TotalSeconds < 60)
        {
            return $"{v.TotalSeconds:0.##}s";
        }

        if (v is { Seconds: 0, Milliseconds: 0 } && v.TotalMinutes < 60)
        {
            return $"{v.TotalMinutes:0}m";
        }

        if (v is { Minutes: 0, Seconds: 0, Milliseconds: 0 })
        {
            return v is { Hours: 0 } ? $"{v.TotalDays:0}d" : $"{v.TotalHours:0}h";
        }

        return v.ToString(@"hh\:mm\:ss");
    }
}
