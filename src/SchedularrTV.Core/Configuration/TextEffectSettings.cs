namespace SchedularrTV.Core.Configuration;

/// <summary>
/// A text layer's drop shadow (generated promos, overlays, credits squeeze). Sizes are percentages of the text's size, so
/// the shadow keeps its look as text shrinks to fit its box.
/// </summary>
public sealed class TextShadowSettings
{
    /// <summary>The shadow's colour, as #rrggbb.</summary>
    public string Color { get; set; } = "#000000";

    /// <summary>0 to 100.</summary>
    public double Opacity { get; set; } = 63;

    /// <summary>How far the shadow falls from the text, as a percentage of its size.</summary>
    public double Distance { get; set; } = 4;

    /// <summary>Which way it falls, in degrees: 90 straight down, 0 to the right, 135 down and to the left.</summary>
    public double Angle { get; set; } = 90;

    /// <summary>How soft its edge is, as a percentage of the text's size (0 is sharp).</summary>
    public double Blur { get; set; } = 5;

    public TextShadowSettings Copy() => (TextShadowSettings)MemberwiseClone();

    /// <summary>The shadow generated promos and credits squeeze have always drawn.</summary>
    public static TextShadowSettings Promo => new();

    /// <summary>The shadow overlays have always drawn.</summary>
    public static TextShadowSettings Overlay => new() { Opacity = 67, Distance = 5, Blur = 6 };
}

/// <summary>A text layer's outline, drawn around the letters before any shadow (so the shadow falls from the outline too).</summary>
public sealed class TextOutlineSettings
{
    /// <summary>The outline's colour, as #rrggbb.</summary>
    public string Color { get; set; } = "#000000";

    /// <summary>0 to 100.</summary>
    public double Opacity { get; set; } = 100;

    /// <summary>How thick it is outside the letters, as a percentage of the text's size.</summary>
    public double Width { get; set; } = 4;

    public TextOutlineSettings Copy() => (TextOutlineSettings)MemberwiseClone();
}
