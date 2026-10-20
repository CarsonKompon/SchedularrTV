using System.Text.Json;
using System.Text.Json.Serialization;

namespace SchedularrTV.GeneratedPromos;

/// <summary>Coverage is explicit: a missing frame has no pose, rather than a guessed reference box.</summary>
public sealed class CleanupTrackingReport
{
    [JsonPropertyName("fps")] public double Fps { get; set; }
    [JsonPropertyName("frame_count")] public int FrameCount { get; set; }
    [JsonPropertyName("regions")] public List<CleanupRegionReport> Regions { get; set; } = [];

    public static CleanupTrackingReport Parse(string json)
    {
        var report = JsonSerializer.Deserialize<CleanupTrackingReport>(json) ?? throw new InvalidDataException("No tracking report was produced.");
        foreach (var region in report.Regions) region.Index();
        return report;
    }
}

public sealed class CleanupRegionReport
{
    [JsonPropertyName("start_frame")] public int StartFrame { get; set; }
    [JsonPropertyName("end_frame")] public int EndFrame { get; set; }
    [JsonPropertyName("frames")] public List<CleanupTrackedFrame> Frames { get; set; } = [];
    [JsonPropertyName("references")] public List<CleanupReferenceReport> References { get; set; } = [];
    [JsonPropertyName("untracked_frames")] public List<int> UntrackedFrames { get; set; } = [];
    private Dictionary<int, CleanupTrackedFrame> _frames = [];
    [JsonIgnore] public List<CleanupCoverage> Coverage { get; } = [];

    public CleanupTrackedFrame? At(int frame) => _frames.GetValueOrDefault(frame);

    internal void Index()
    {
        _frames = Frames.ToDictionary(f => f.Frame);
        Coverage.Clear();
        if (EndFrame < StartFrame) return;
        int start = StartFrame;
        bool tracked = _frames.ContainsKey(start);
        for (int frame = start + 1; frame <= EndFrame + 1; frame++)
        {
            bool next = _frames.ContainsKey(frame);
            if (frame == EndFrame + 1 || next != tracked)
            {
                Coverage.Add(new(start, frame - 1, tracked));
                start = frame; tracked = next;
            }
        }
    }
}

public sealed record CleanupCoverage(int Start, int End, bool Tracked);

public sealed class CleanupTrackedFrame
{
    [JsonPropertyName("frame")] public int Frame { get; set; }
    [JsonPropertyName("reference")] public int Reference { get; set; }
    [JsonPropertyName("matrix")] public double[][] Matrix { get; set; } = [];
}

public sealed class CleanupReferenceReport
{
    [JsonPropertyName("frame")] public int Frame { get; set; }
    [JsonPropertyName("backward_frames")] public int BackwardFrames { get; set; }
    [JsonPropertyName("forward_frames")] public int ForwardFrames { get; set; }
    [JsonPropertyName("mask_pixels")] public int MaskPixels { get; set; }
}
