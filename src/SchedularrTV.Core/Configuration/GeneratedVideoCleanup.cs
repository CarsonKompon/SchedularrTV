namespace SchedularrTV.Core.Configuration;

/// <summary>A non-destructive cleanup recipe for one file, shared by its uses in this template.</summary>
public sealed class GeneratedVideoCleanup
{
    public string Source { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public List<GeneratedRemovalRegion> Regions { get; set; } = [];

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Source)) throw new ArgumentException("Choose a source video.");
        if (Regions is null || Regions.Count is < 1 or > 32) throw new ArgumentException("Choose between 1 and 32 text regions.");
        foreach (GeneratedRemovalRegion region in Regions)
        {
            if (region is null || !double.IsFinite(region.Start) || !double.IsFinite(region.End) || region.Start < 0 || region.End <= region.Start)
                throw new ArgumentException("Each region needs a valid start and end time.");
            if (region.Margin is < 0 or > 30 || region.Keys is null || region.Keys.Count is < 1 or > 200)
                throw new ArgumentException("Each region needs a selection and a margin between 0 and 30 pixels.");
            if (region.Sensitivity is < 0 or > 100 || !Enum.IsDefined(region.Ink))
                throw new ArgumentException("Text detection sensitivity must be 0–100 and the ink mode must be Auto, Dark or Light.");
            foreach (GeneratedRemovalKey key in region.Keys)
                if (key is null || !new[] { key.At, key.X, key.Y, key.Width, key.Height }.All(double.IsFinite)
                    || key.At < region.Start || key.At > region.End || key.X < 0 || key.Y < 0
                    || key.Width <= 0 || key.Height <= 0 || key.X + key.Width > 1.00001 || key.Y + key.Height > 1.00001)
                    throw new ArgumentException("Selections must be inside the source image and their active time range.");
            if (region.Keys.Select(k => k.At).Distinct().Count() != region.Keys.Count)
                throw new ArgumentException("Correction times must be unique within a region.");
        }
    }
}

/// <summary>Coordinates are fractions of the oriented source image, and times are source seconds.</summary>
public sealed class GeneratedRemovalRegion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Text";
    public double Start { get; set; }
    public double End { get; set; } = 10;
    public bool Track { get; set; } = true;
    /// <summary>Remove all selected pixels instead of detecting lettering inside the selection.</summary>
    public bool WholeRegion { get; set; }
    /// <summary>Reuse the repaired reference texture on a tracked flat surface, reducing shimmer.</summary>
    public bool StableSurface { get; set; } = true;
    public int Margin { get; set; } = 3;
    public int Sensitivity { get; set; } = 50;
    public GeneratedRemovalInk Ink { get; set; }
    public List<GeneratedRemovalKey> Keys { get; set; } = [];
}

public enum GeneratedRemovalInk { Auto, Dark, Light }

/// <summary>A reference selection or manual tracking correction.</summary>
public sealed class GeneratedRemovalKey
{
    public double At { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}
