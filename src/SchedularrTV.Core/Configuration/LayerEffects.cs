namespace SchedularrTV.Core.Configuration;

/// <summary>What a layer effect does.</summary>
public enum LayerEffectKind
{
    /// <summary>A solid edge around everything drawn so far.</summary>
    Outline,

    /// <summary>A copy of everything drawn so far, offset and softened, underneath it.</summary>
    Shadow,

    /// <summary>A soft halo all the way around everything drawn so far.</summary>
    Glow
}

/// <summary>
/// One of a canvas layer's effects (generated promos, overlays, credits squeeze). A layer's effects are applied in order,
/// each to the layer as the ones before it left it: an outline after an outline goes round both, and a shadow after them
/// is cast by the whole lot. Sizes are canvas pixels.
/// </summary>
public sealed class LayerEffect
{
    public LayerEffectKind Kind { get; set; }

    /// <summary>Switched off in the list without being removed. Null (not false) when on.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? Hidden { get; set; }

    /// <summary>As #rrggbb.</summary>
    public string Color { get; set; } = "#000000";

    /// <summary>0 to 100.</summary>
    public double Opacity { get; set; } = 100;

    /// <summary>An outline's thickness, in pixels.</summary>
    public double Width { get; set; } = 4;

    /// <summary>How far a shadow falls, in pixels.</summary>
    public double Distance { get; set; } = 6;

    /// <summary>Which way a shadow falls, in degrees: 90 straight down, 0 to the right, 135 down and to the left.</summary>
    public double Angle { get; set; } = 90;

    /// <summary>How soft a shadow or glow is, in pixels (0 is sharp).</summary>
    public double Blur { get; set; } = 6;

    [YamlDotNet.Serialization.YamlIgnore, System.Text.Json.Serialization.JsonIgnore]
    public bool IsOn => Hidden != true;

    public LayerEffect Copy() => (LayerEffect)MemberwiseClone();

    /// <summary>A new effect of this kind with settings that read well straight away.</summary>
    public static LayerEffect New(LayerEffectKind kind) => kind switch
    {
        LayerEffectKind.Outline => new LayerEffect { Kind = kind, Width = 4, Opacity = 100 },
        LayerEffectKind.Shadow => new LayerEffect { Kind = kind, Opacity = 63, Distance = 6, Angle = 90, Blur = 6 },
        _ => new LayerEffect { Kind = kind, Color = "#ffffff", Opacity = 80, Blur = 12 }
    };

    public static string Name(LayerEffectKind kind) => kind switch
    {
        LayerEffectKind.Outline => "Outline",
        LayerEffectKind.Shadow => "Drop shadow",
        _ => "Glow"
    };
}

/// <summary>Layer effect lists: copying them, and reading text's older outline and shadow settings as effects.</summary>
public static class LayerEffects
{
    /// <summary>Whether any effect in the list is switched on.</summary>
    public static bool Any(IReadOnlyList<LayerEffect>? effects) => effects?.Any(effect => effect.IsOn) == true;

    public static List<LayerEffect>? Copy(IReadOnlyList<LayerEffect>? effects) => effects?.Select(effect => effect.Copy()).ToList();

    /// <summary>The switched-on effects with their sizes multiplied by <paramref name="scale"/> (a design drawn at another size); null when none is on.</summary>
    public static List<LayerEffect>? Scaled(IReadOnlyList<LayerEffect>? effects, double scale) =>
        Any(effects)
            ? effects!.Where(effect => effect.IsOn).Select(effect =>
            {
                LayerEffect copy = effect.Copy();
                (copy.Width, copy.Distance, copy.Blur) = (effect.Width * scale, effect.Distance * scale, effect.Blur * scale);
                return copy;
            }).ToList()
            : null;

    /// <summary>
    /// A text layer's older outline and drop shadow (sized as percentages of its font size) as the effects list that draws the
    /// same: the outline first, then the shadow, which falls from the outline too.
    /// </summary>
    /// <param name="usual">The shadow this kind of layer has always drawn when its shadow wasn't set up its own way.</param>
    public static List<LayerEffect> FromText(bool shadow, TextShadowSettings? shadowStyle, bool? outline, TextOutlineSettings? outlineStyle,
        TextShadowSettings usual, double fontSize)
    {
        double pixels = Math.Max(1, fontSize) / 100;
        List<LayerEffect> effects = [];
        if (outline == true)
        {
            TextOutlineSettings style = outlineStyle ?? new TextOutlineSettings();
            effects.Add(new LayerEffect
            {
                Kind = LayerEffectKind.Outline, Color = style.Color, Opacity = style.Opacity, Width = Round(style.Width * pixels)
            });
        }

        if (shadow)
        {
            TextShadowSettings style = shadowStyle ?? usual;
            effects.Add(new LayerEffect
            {
                Kind = LayerEffectKind.Shadow, Color = style.Color, Opacity = style.Opacity, Angle = style.Angle,
                Distance = Round(style.Distance * pixels), Blur = Round(style.Blur * pixels)
            });
        }

        return effects;
    }

    private static double Round(double value) => Math.Round(value, 1);
}
