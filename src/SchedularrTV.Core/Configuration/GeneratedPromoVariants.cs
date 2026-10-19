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
    /// background's rule weighs it further, as before), and a default look left without backgrounds is only used when no
    /// variant can air. Returns whether anything changed.
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

        // Each background was as likely as any other, so the default look stands for as many as it kept.
        template.DefaultWeight = backgrounds.Count == 1 ? null : backgrounds.Count;
        return true;
    }

    /// <summary>Turns every template's older per-background layouts into variants (see <see cref="Convert"/>).</summary>
    public static int ConvertAll(IEnumerable<GeneratedPromoTemplate> templates) => templates.Count(Convert);
}
