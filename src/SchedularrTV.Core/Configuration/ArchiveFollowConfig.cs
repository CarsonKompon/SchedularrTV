namespace SchedularrTV.Core.Configuration;

public sealed class ArchiveFollowConfig
{
    public bool Enabled { get; set; }

    public bool FollowWeekday { get; set; }

    public bool FollowMonth { get; set; } = true;

    public string Directory { get; set; } = string.Empty;

    public int StartYear { get; set; } = 1992;

    public int EndYear { get; set; } = 2026;

    public Dictionary<string, string?> Blocks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string?> Shows { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<ArchiveEpisodeAssignment> Episodes { get; set; } = [];
}

public sealed class ArchiveEpisodeAssignment
{
    public string Show { get; set; } = string.Empty;
    public string Episode { get; set; } = string.Empty;
    public string MediaId { get; set; } = string.Empty;
}