namespace SchedularrTV.Core.Configuration;

/// <summary>Which layers a variant shows, and turning older templates' per-background layouts into variants.</summary>
public static class GeneratedPromoVariants
{
    /// <summary>The name the template's own look goes by among its variants.</summary>
    public static string DefaultName(GeneratedPromoTemplate template) =>
        string.IsNullOrWhiteSpace(template.DefaultName) ? "Default" : template.DefaultName.Trim();

    /// <summary>
    /// Whether <paramref name="layer"/> is drawn in the variant <paramref name="variantId"/> (null: the default look): a
    /// template layer the variant doesn't hide, or one of the variant's own, inside groups that are drawn too.
    /// </summary>
    public static bool Visible(GeneratedPromoTemplate template, string? variantId, GeneratedLayer layer)
    {
        GeneratedPromoVariant? variant = template.Variant(variantId);
        HashSet<GeneratedLayer> seen = [];
        for (GeneratedLayer? current = layer; current is not null && seen.Add(current); current = Parent(template, current))
        {
            if (!OwnVisible(variant, current))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether the layer itself (not its groups) is drawn in <paramref name="variant"/> (null: the default look).</summary>
    public static bool OwnVisible(GeneratedPromoVariant? variant, GeneratedLayer layer)
    {
        if (layer.Variant is { Length: > 0 } owner)
        {
            return variant is not null && string.Equals(owner, variant.Id, StringComparison.Ordinal);
        }

        return variant?.Hidden is not { Count: > 0 } hidden || layer.Id is not { Length: > 0 } id || !hidden.Contains(id, StringComparer.Ordinal);
    }

    /// <summary>Whether the variant hides the template's layer <paramref name="layer"/>.</summary>
    public static bool Hides(GeneratedPromoVariant? variant, GeneratedLayer layer) =>
        variant?.Hidden is { Count: > 0 } hidden && layer.Id is { Length: > 0 } id && hidden.Contains(id, StringComparer.Ordinal);

    private static GeneratedLayer? Parent(GeneratedPromoTemplate template, GeneratedLayer layer) =>
        layer.Parent is { Length: > 0 } id
            ? template.Layers.FirstOrDefault(candidate => candidate.Kind == GeneratedLayerKind.Group
                && string.Equals(candidate.Id, id, StringComparison.Ordinal) && !ReferenceEquals(candidate, layer))
            : null;

    /// <summary>
    /// Every pick rule the template has: its backgrounds', its variants' backgrounds', and its layers' choices', so
    /// whatever looks ahead at upcoming shows looks far enough for all of them.
    /// </summary>
    public static IEnumerable<GeneratedBackgroundRule> AllRules(GeneratedPromoTemplate template) =>
        (template.BackgroundRules ?? [])
            .Concat((template.Variants ?? []).SelectMany(variant => variant.BackgroundRules ?? []))
            .Concat(template.Layers.SelectMany(layer => (layer.Alternatives ?? []).Select(choice => choice.Rule).Prepend(layer.ChoiceRule)).OfType<GeneratedBackgroundRule>());

    /// <summary>
    /// Turns an older template's per-background layouts (<see cref="GeneratedPromoTemplate.BackgroundOverrides"/>) into
    /// variants: each background that placed, coloured or timed layers its own way becomes a variant with just that
    /// background, its pick rule and those edits; backgrounds without any stay with the template's default look. The
    /// chances stay as they were: the default look weighs as many backgrounds as it keeps, each variant one (its
    /// background's rule weighs it further, as before). When every background had its own layout, the first one becomes
    /// the default look, so there's never a look without a background. Returns whether anything changed.
    /// </summary>
    public static bool Convert(GeneratedPromoTemplate template)
    {
        if (template.BackgroundOverrides is not { Count: > 0 } overrides)
        {
            return false;
        }

        List<string> backgrounds = [.. template.BackgroundChoices];
        List<GeneratedPromoVariant> made = [];
        HashSet<string> names = new((template.Variants ?? []).Select(variant => variant.Name), StringComparer.OrdinalIgnoreCase);
        foreach (GeneratedBackgroundOverride tracked in overrides)
        {
            string? path = backgrounds.FirstOrDefault(candidate => string.Equals(candidate, tracked.Background, StringComparison.OrdinalIgnoreCase));
            bool edits = tracked.Layers.Count > 0 || tracked.Colors is { Count: > 0 } || tracked.Timings is { Count: > 0 };
            if (path is null || !edits)
            {
                continue; // a layout for a background that's gone (or an empty one) has nothing to show
            }

            GeneratedBackgroundRule? rule = template.BackgroundRules?.FirstOrDefault(candidate => string.Equals(candidate.Background, path, StringComparison.OrdinalIgnoreCase));
            string name = Path.GetFileNameWithoutExtension(path);
            name = string.IsNullOrWhiteSpace(name) ? "Variant" : name;
            for (int number = 2; !names.Add(name); number++)
            {
                name = $"{Path.GetFileNameWithoutExtension(path)} {number}";
            }

            made.Add(new GeneratedPromoVariant
            {
                Name = name,
                Background = path,
                BackgroundRules = rule is null ? null : [rule],
                Layers = tracked.Layers,
                Colors = tracked.Colors is { Count: > 0 } ? tracked.Colors : null,
                Timings = tracked.Timings is { Count: > 0 } ? tracked.Timings : null
            });
            backgrounds.RemoveAll(candidate => string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase));
            template.BackgroundRules?.Remove(rule!);
        }

        template.BackgroundOverrides = null;
        if (made.Count == 0)
        {
            return true;
        }

        template.Background = backgrounds.Count > 0 ? backgrounds[0] : null;
        template.Backgrounds = backgrounds.Count > 1 ? [.. backgrounds.Skip(1)] : null;
        if (template.BackgroundRules is { Count: 0 }) template.BackgroundRules = null;
        (template.Variants ??= []).AddRange(made);

        // Every background had its own layout: the first becomes the default look (it never went without a background).
        if (backgrounds.Count == 0)
        {
            FoldIntoDefault(template, made[0]);
        }

        // Each background was as likely as any other, so the default look stands for as many as it kept.
        int kept = template.BackgroundChoices.Count;
        template.DefaultWeight = kept <= 1 ? null : kept;
        return true;
    }

    /// <summary>
    /// Repairs a template converted by an earlier version, which left the default look without any background (and only
    /// as a fallback) when every background had its own layout: its first variant becomes the default look again. Only
    /// a template that still looks exactly as that conversion left it is touched. Returns whether it was.
    /// </summary>
    public static bool RepairConverted(GeneratedPromoTemplate template)
    {
        if (template.Variants is not { Count: > 0 } variants || template.DefaultWeight is not 0.0 || template.BackgroundChoices.Count > 0
            || !variants.All(variant => variant is { Enabled: true, Availability: null, Hidden: null, Weight: 1.0 }
                && variant.BackgroundChoices.Count == 1
                && variant.Name.StartsWith(Path.GetFileNameWithoutExtension(variant.BackgroundChoices[0]), StringComparison.Ordinal))
            || template.Layers.Any(layer => layer.Variant is not null))
        {
            return false;
        }

        FoldIntoDefault(template, variants[0]);
        template.DefaultWeight = null;
        return true;
    }

    /// <summary>
    /// Makes <paramref name="variant"/> the template's default look: its background (and rule) and its placements,
    /// colours and timings become the template's own. The other variants are given the default's old placement,
    /// colour or timing of each layer it changes (where they didn't have their own), so they look just as before.
    /// </summary>
    private static void FoldIntoDefault(GeneratedPromoTemplate template, GeneratedPromoVariant variant)
    {
        List<GeneratedPromoVariant> others = [.. (template.Variants ?? []).Where(other => !ReferenceEquals(other, variant))];
        foreach (GeneratedLayer layer in template.Layers)
        {
            if (layer.Id is not { Length: > 0 } id)
            {
                continue;
            }

            if (variant.Layers.FirstOrDefault(placed => placed.Layer == id) is { } pose)
            {
                foreach (GeneratedPromoVariant other in others.Where(other => !other.Layers.Any(placed => placed.Layer == id)))
                {
                    other.Layers.Add(new GeneratedLayerOverride
                    {
                        Layer = id, X = layer.X, Y = layer.Y, Width = layer.Width, Height = layer.Height, Opacity = layer.Opacity,
                        Rotation = layer.Rotation, Corners = Copy(layer.Corners), Keyframes = Copy(layer.Keyframes)
                    });
                }

                (layer.X, layer.Y, layer.Width, layer.Height, layer.Opacity) = (pose.X, pose.Y, pose.Width, pose.Height, pose.Opacity);
                layer.Rotation = pose.Rotation;
                layer.Corners = pose.Corners;
                layer.Keyframes = pose.Keyframes;
            }

            if (variant.Colors?.FirstOrDefault(colour => colour.Layer == id) is { } recoloured)
            {
                foreach (GeneratedPromoVariant other in others.Where(other => other.Colors?.Any(colour => colour.Layer == id) != true))
                {
                    (other.Colors ??= []).Add(new GeneratedLayerColor { Layer = id, Color = layer.Color });
                }

                layer.Color = recoloured.Color;
            }

            if (variant.Timings?.FirstOrDefault(timing => timing.Layer == id) is { } timed)
            {
                foreach (GeneratedPromoVariant other in others.Where(other => other.Timings?.Any(timing => timing.Layer == id) != true))
                {
                    (other.Timings ??= []).Add(new GeneratedLayerTiming
                    {
                        Layer = id, Start = layer.Start, End = layer.End, TransitionIn = layer.TransitionIn, TransitionOut = layer.TransitionOut,
                        TransitionDuration = layer.TransitionDuration, TransitionOutDuration = layer.TransitionOutDuration
                    });
                }

                (layer.Start, layer.End, layer.TransitionIn, layer.TransitionOut) = (timed.Start, timed.End, timed.TransitionIn, timed.TransitionOut);
                (layer.TransitionDuration, layer.TransitionOutDuration) = (timed.TransitionDuration, timed.TransitionOutDuration);
            }
        }

        List<string> backgrounds = [.. variant.BackgroundChoices, .. template.BackgroundChoices];
        template.Background = backgrounds.Count > 0 ? backgrounds[0] : null;
        template.Backgrounds = backgrounds.Count > 1 ? [.. backgrounds.Skip(1)] : null;
        if (variant.BackgroundRules is { Count: > 0 } rules)
        {
            (template.BackgroundRules ??= []).AddRange(rules);
        }

        template.Variants!.Remove(variant);
        if (template.Variants.Count == 0)
        {
            template.Variants = null;
        }
    }

    private static GeneratedCorners? Copy(GeneratedCorners? corners) => corners is null ? null : new GeneratedCorners
    {
        TopLeftX = corners.TopLeftX, TopLeftY = corners.TopLeftY, TopRightX = corners.TopRightX, TopRightY = corners.TopRightY,
        BottomRightX = corners.BottomRightX, BottomRightY = corners.BottomRightY, BottomLeftX = corners.BottomLeftX, BottomLeftY = corners.BottomLeftY
    };

    private static List<GeneratedKeyframe>? Copy(List<GeneratedKeyframe>? keyframes) => keyframes?.Select(key => new GeneratedKeyframe
    {
        At = key.At, X = key.X, Y = key.Y, Width = key.Width, Height = key.Height, Opacity = key.Opacity,
        Rotation = key.Rotation, Corners = Copy(key.Corners), Easing = key.Easing
    }).ToList();

    /// <summary>Turns every template's older per-background layouts into variants (see <see cref="Convert"/>).</summary>
    public static int ConvertAll(IEnumerable<GeneratedPromoTemplate> templates) => templates.Count(Convert);
}
