namespace SchedularrTV.Overlays;

/// <summary>
/// A turn (degrees clockwise, y pointing down) followed by a move: how a group's space sits in the space around it.
/// Groups only move and turn their contents, so these compose without any stretching.
/// </summary>
public readonly record struct OverlaySpace(double Angle, double X, double Y)
{
    public static OverlaySpace Identity => new(0, 0, 0);

    public (double X, double Y) Apply(double x, double y)
    {
        (double turnedX, double turnedY) = Rotate(x, y, Angle);
        return (turnedX + X, turnedY + Y);
    }

    /// <summary>This, and then <paramref name="outer"/>.</summary>
    public OverlaySpace Then(OverlaySpace outer)
    {
        (double x, double y) = outer.Apply(X, Y);
        return new OverlaySpace(Angle + outer.Angle, x, y);
    }

    public OverlaySpace Inverse()
    {
        (double x, double y) = Rotate(-X, -Y, -Angle);
        return new OverlaySpace(-Angle, x, y);
    }

    public static (double X, double Y) Rotate(double x, double y, double degrees)
    {
        double radians = degrees * Math.PI / 180;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        return (cos * x - sin * y, sin * x + cos * y);
    }
}

/// <summary>A layer as it ends up on the canvas: its middle, size and total turn.</summary>
public readonly record struct OverlayBox(double CenterX, double CenterY, double Width, double Height, double Angle);

/// <summary>Where a dragged layer goes, relative to the row it's dropped on (the list shows the top first).</summary>
public enum OverlayDrop
{
    /// <summary>In front of it, next to it.</summary>
    Above,

    /// <summary>Behind it, next to it.</summary>
    Below,

    /// <summary>Inside it (a group), in front of what's already there.</summary>
    Into
}

/// <summary>
/// Groups and the layers inside them, kept in the design's one list (bottom first): each layer names the group it's
/// inside, and a group's layers are kept straight after it, in drawing order.
/// </summary>
public static class OverlayHierarchy
{
    public static bool IsGroup(OverlayLayer layer) => layer.Kind == OverlayLayerKinds.Group;

    /// <summary>The group the layer is inside, or null at the top (also when its group isn't in the design).</summary>
    public static OverlayLayer? ParentOf(Overlay overlay, OverlayLayer layer) =>
        layer.Parent is { Length: > 0 } id
            ? overlay.Layers.FirstOrDefault(candidate => IsGroup(candidate) && string.Equals(candidate.Id, id, StringComparison.Ordinal)
                && !ReferenceEquals(candidate, layer))
            : null;

    /// <summary>The groups around the layer, innermost first (stopping at a loop written by hand).</summary>
    public static IEnumerable<OverlayLayer> Ancestors(Overlay overlay, OverlayLayer layer)
    {
        HashSet<OverlayLayer> seen = [layer];
        for (OverlayLayer? group = ParentOf(overlay, layer); group is not null && seen.Add(group); group = ParentOf(overlay, group))
        {
            yield return group;
        }
    }

    public static bool IsWithin(Overlay overlay, OverlayLayer layer, OverlayLayer group) =>
        ReferenceEquals(layer, group) || Ancestors(overlay, layer).Contains(group);

    /// <summary>Whether the layer is locked, by itself or by a group it's in.</summary>
    public static bool IsLocked(Overlay overlay, OverlayLayer layer) => layer.Locked || Ancestors(overlay, layer).Any(group => group.Locked);

    /// <summary>The layers directly inside <paramref name="group"/> (null: at the top), in drawing order.</summary>
    public static List<OverlayLayer> Children(Overlay overlay, OverlayLayer? group) =>
        [.. overlay.Layers.Where(layer => ReferenceEquals(ParentOf(overlay, layer), group))];

    /// <summary>The layer and everything inside it, in drawing order.</summary>
    public static List<OverlayLayer> Subtree(Overlay overlay, OverlayLayer layer) =>
        [.. overlay.Layers.Where(candidate => IsWithin(overlay, candidate, layer))];

    /// <summary>The rows of the layer list, top (drawn last) first, each with how deep in groups it is.</summary>
    public static List<(OverlayLayer Layer, int Depth)> Rows(Overlay overlay, Func<OverlayLayer, bool> open)
    {
        List<(OverlayLayer, int)> rows = [];
        void Add(OverlayLayer? group, int depth)
        {
            foreach (OverlayLayer layer in Enumerable.Reverse(Children(overlay, group)))
            {
                rows.Add((layer, depth));
                if (IsGroup(layer) && open(layer) && depth < 32)
                {
                    Add(layer, depth + 1);
                }
            }
        }

        Add(null, 0);
        return rows;
    }

    /// <summary>
    /// When a part of a design comes and goes within an appearance: its own coming in late and going early, and its
    /// groups' (the latest in and the earliest out of them all).
    /// </summary>
    public static LayerTiming EffectiveTiming(Overlay overlay, OverlayLayer layer)
    {
        LayerTiming own = LayerTiming.Of(layer);
        double late = own.InSeconds, early = own.OutSeconds;
        foreach (OverlayLayer group in Ancestors(overlay, layer))
        {
            late = Math.Max(late, Math.Max(0, group.InSeconds));
            early = Math.Max(early, Math.Max(0, group.OutSeconds));
        }

        return own with { InSeconds = late, OutSeconds = early };
    }

    // ---- Spaces ----

    /// <summary>How a group (posed like this) places its contents: from its corner, turned about its middle.</summary>
    public static OverlaySpace Placing(OverlayPose group)
    {
        double pivotX = group.Width / 2.0;
        double pivotY = group.Height / 2.0;
        double angle = group.Rotation ?? 0;
        (double turnedX, double turnedY) = OverlaySpace.Rotate(pivotX, pivotY, angle);
        return new OverlaySpace(angle, group.X + pivotX - turnedX, group.Y + pivotY - turnedY);
    }

    /// <summary>From inside <paramref name="group"/> (null: the canvas) to the canvas, with each group posed at <paramref name="seconds"/>.</summary>
    public static OverlaySpace SpaceOf(Overlay overlay, OverlayLayer? group, double seconds)
    {
        OverlaySpace space = OverlaySpace.Identity;
        if (group is null)
        {
            return space;
        }

        foreach (OverlayLayer around in Ancestors(overlay, group).Prepend(group))
        {
            space = space.Then(Placing(OverlayAnimation.PoseAt(around, seconds)));
        }

        return space;
    }

    /// <summary>Where the layer is on the canvas at <paramref name="seconds"/>.</summary>
    public static OverlayBox World(Overlay overlay, OverlayLayer layer, double seconds)
    {
        OverlayPose local = OverlayAnimation.PoseAt(layer, seconds);
        OverlaySpace space = SpaceOf(overlay, ParentOf(overlay, layer), seconds);
        (double x, double y) = space.Apply(local.X + local.Width / 2.0, local.Y + local.Height / 2.0);
        return new OverlayBox(x, y, local.Width, local.Height, space.Angle + (local.Rotation ?? 0));
    }

    /// <summary>The total turn of the space the layer is placed in (its groups', not its own).</summary>
    public static double SpaceAngle(Overlay overlay, OverlayLayer layer, double seconds) =>
        SpaceOf(overlay, ParentOf(overlay, layer), seconds).Angle;

    // ---- Moving between groups ----

    /// <summary>Moves and turns a layer (and each of its keyframes) by <paramref name="change"/>, about each box's middle.</summary>
    public static void Transform(OverlayLayer layer, OverlaySpace change)
    {
        if (change == OverlaySpace.Identity)
        {
            return;
        }

        (layer.X, layer.Y, float turned) = Moved(layer.X, layer.Y, layer.Width, layer.Height, layer.Rotation ?? 0, change);
        layer.Rotation = Math.Abs(turned) < 0.0005f ? null : turned;
        foreach (OverlayKeyframe key in layer.Keyframes ?? [])
        {
            (key.X, key.Y, key.Rotation) = Moved(key.X, key.Y, key.Width, key.Height, key.Rotation, change);
        }
    }

    private static (float X, float Y, float Rotation) Moved(float x, float y, float width, float height, float rotation, OverlaySpace change)
    {
        (double centreX, double centreY) = change.Apply(x + width / 2.0, y + height / 2.0);
        return ((float)Math.Round(centreX - width / 2.0, 2), (float)Math.Round(centreY - height / 2.0, 2), (float)Math.Round(rotation + change.Angle, 3));
    }

    /// <summary>
    /// Puts the layer inside <paramref name="group"/> (null: at the top) without it moving on screen, as the groups stand
    /// at <paramref name="seconds"/>.
    /// </summary>
    public static void Rehome(Overlay overlay, OverlayLayer layer, OverlayLayer? group, double seconds)
    {
        if (group is not null && (!IsGroup(group) || IsWithin(overlay, group, layer)))
        {
            return;
        }

        OverlayLayer? from = ParentOf(overlay, layer);
        if (!ReferenceEquals(from, group))
        {
            OverlaySpace change = SpaceOf(overlay, from, seconds).Then(SpaceOf(overlay, group, seconds).Inverse());
            Transform(layer, change);
        }

        if (group is not null)
        {
            group.Id ??= NewId(overlay);
        }

        layer.Parent = group?.Id;
    }

    /// <summary>Puts every group's layers straight after it, keeping the drawing order otherwise.</summary>
    public static void Tidy(Overlay overlay)
    {
        List<OverlayLayer> ordered = [];
        HashSet<OverlayLayer> placed = [];
        void Add(OverlayLayer? group)
        {
            foreach (OverlayLayer layer in Children(overlay, group))
            {
                if (placed.Add(layer))
                {
                    ordered.Add(layer);
                    if (IsGroup(layer))
                    {
                        Add(layer);
                    }
                }
            }
        }

        Add(null);
        foreach (OverlayLayer layer in overlay.Layers.Where(layer => !placed.Contains(layer)))
        {
            layer.Parent = null;
            ordered.Add(layer);
        }

        overlay.Layers.Clear();
        overlay.Layers.AddRange(ordered);
    }

    /// <summary>The layers with none of their groups among them, in drawing order.</summary>
    public static List<OverlayLayer> Outermost(Overlay overlay, IEnumerable<OverlayLayer> layers)
    {
        HashSet<OverlayLayer> chosen = [.. layers];
        return [.. overlay.Layers.Where(layer => chosen.Contains(layer) && !Ancestors(overlay, layer).Any(chosen.Contains))];
    }

    /// <summary>
    /// Moves <paramref name="layers"/> (with what's inside them) next to or into <paramref name="target"/>, keeping where
    /// they are on screen. False when that can't be done (into themselves, or "into" something that isn't a group).
    /// </summary>
    public static bool Move(Overlay overlay, IEnumerable<OverlayLayer> layers, OverlayLayer target, OverlayDrop drop, double seconds)
    {
        List<OverlayLayer> moving = Outermost(overlay, layers);
        if (moving.Count == 0 || moving.Any(layer => IsWithin(overlay, target, layer)) || (drop == OverlayDrop.Into && !IsGroup(target)))
        {
            return false;
        }

        OverlayLayer? group = drop == OverlayDrop.Into ? target : ParentOf(overlay, target);
        foreach (OverlayLayer layer in moving)
        {
            Rehome(overlay, layer, group, seconds);
        }

        List<OverlayLayer> block = [.. overlay.Layers.Where(layer => moving.Any(root => IsWithin(overlay, layer, root)))];
        overlay.Layers.RemoveAll(block.Contains);
        int at = drop == OverlayDrop.Below
            ? overlay.Layers.IndexOf(target)
            : overlay.Layers.IndexOf(target) + Subtree(overlay, target).Count;
        overlay.Layers.InsertRange(Math.Clamp(at, 0, overlay.Layers.Count), block);
        Tidy(overlay);
        return true;
    }

    /// <summary>Moves a layer one step up (+1) or down (-1) among the layers beside it in its group.</summary>
    public static bool Step(Overlay overlay, OverlayLayer layer, int direction, double seconds)
    {
        List<OverlayLayer> siblings = Children(overlay, ParentOf(overlay, layer));
        int index = siblings.IndexOf(layer) + Math.Sign(direction);
        return index >= 0 && index < siblings.Count
            && Move(overlay, [layer], siblings[index], direction > 0 ? OverlayDrop.Above : OverlayDrop.Below, seconds);
    }

    /// <summary>A new group around <paramref name="layers"/>, sized to fit them as they are at <paramref name="seconds"/>, without anything moving.</summary>
    public static OverlayLayer Group(Overlay overlay, IEnumerable<OverlayLayer> layers, double seconds)
    {
        List<OverlayLayer> members = Outermost(overlay, layers);
        if (members.Count == 0)
        {
            throw new InvalidOperationException("Choose the layers to group.");
        }

        OverlayLayer? around = ParentOf(overlay, members[0]);
        if (members.Any(member => !ReferenceEquals(ParentOf(overlay, member), around)))
        {
            around = null;
        }

        foreach (OverlayLayer member in members)
        {
            Rehome(overlay, member, around, seconds);
        }

        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (OverlayLayer member in members)
        {
            OverlayPose pose = OverlayAnimation.PoseAt(member, seconds);
            foreach ((double cornerX, double cornerY) in Corners(pose))
            {
                (left, top, right, bottom) = (Math.Min(left, cornerX), Math.Min(top, cornerY), Math.Max(right, cornerX), Math.Max(bottom, cornerY));
            }
        }

        var group = new OverlayLayer
        {
            Id = NewId(overlay),
            Name = UniqueName(overlay, "Group"),
            Kind = OverlayLayerKinds.Group,
            Parent = around?.Id,
            X = (float)Math.Floor(left),
            Y = (float)Math.Floor(top),
            Width = (float)Math.Max(1, Math.Ceiling(right - Math.Floor(left))),
            Height = (float)Math.Max(1, Math.Ceiling(bottom - Math.Floor(top))),
            Shadow = false
        };
        overlay.Layers.Insert(overlay.Layers.IndexOf(members[0]), group);
        foreach (OverlayLayer member in members)
        {
            Rehome(overlay, member, group, seconds);
        }

        Tidy(overlay);
        return group;
    }

    private static IEnumerable<(double X, double Y)> Corners(OverlayPose pose)
    {
        double centreX = pose.X + pose.Width / 2.0;
        double centreY = pose.Y + pose.Height / 2.0;
        foreach ((double dx, double dy) in new[] { (-1.0, -1.0), (1.0, -1.0), (1.0, 1.0), (-1.0, 1.0) })
        {
            (double x, double y) = OverlaySpace.Rotate(dx * pose.Width / 2.0, dy * pose.Height / 2.0, pose.Rotation ?? 0);
            yield return (centreX + x, centreY + y);
        }
    }

    /// <summary>Takes the group's layers out where they are on screen at <paramref name="seconds"/>, and removes the group.</summary>
    public static List<OverlayLayer> Ungroup(Overlay overlay, OverlayLayer group, double seconds)
    {
        OverlayLayer? around = ParentOf(overlay, group);
        List<OverlayLayer> members = Children(overlay, group);
        foreach (OverlayLayer member in members)
        {
            Rehome(overlay, member, around, seconds);
        }

        overlay.Layers.Remove(group);
        Forget(overlay, group);
        Tidy(overlay);
        return members;
    }

    /// <summary>Removes the layer and, for a group, everything inside it.</summary>
    public static void Remove(Overlay overlay, OverlayLayer layer)
    {
        foreach (OverlayLayer gone in Subtree(overlay, layer))
        {
            overlay.Layers.Remove(gone);
            Forget(overlay, gone);
        }

        Tidy(overlay);
    }

    // Masks of a deleted layer stay, invisible, until a new target is chosen.
    private static void Forget(Overlay overlay, OverlayLayer layer)
    {
        if (layer.Id is { Length: > 0 } id)
        {
            foreach (OverlayLayer mask in overlay.Layers.Where(candidate => candidate.Mask?.Target == id))
            {
                mask.Mask!.Target = null;
            }
        }
    }

    /// <summary>A copy of the layer (with everything inside it, for a group) just after it, nudged so it can be seen.</summary>
    public static OverlayLayer Duplicate(Overlay overlay, OverlayLayer layer)
    {
        List<OverlayLayer> originals = Subtree(overlay, layer);
        List<OverlayLayer> copies = [.. originals.Select(original => original.Copy())];
        Dictionary<string, string> ids = new(StringComparer.Ordinal);
        List<string> names = [.. overlay.Layers.Select(existing => existing.Name)];
        foreach (OverlayLayer copy in copies)
        {
            string? old = copy.Id;
            copy.Id = IsGroup(copy) || old is not null ? NewId(overlay, ids.Values) : null;
            if (old is not null && copy.Id is not null)
            {
                ids[old] = copy.Id;
            }

            copy.Name = UniqueName(names, copy.Name);
            names.Add(copy.Name);
        }

        foreach (OverlayLayer copy in copies.Skip(1))
        {
            copy.Parent = copy.Parent is { } parent && ids.TryGetValue(parent, out string? moved) ? moved : copy.Parent;
        }

        foreach (OverlayLayer copy in copies)
        {
            if (copy.Mask?.Target is { } target && ids.TryGetValue(target, out string? moved))
            {
                copy.Mask.Target = moved;
            }
        }

        OverlayAnimation.Offset(copies[0], 20, 20);
        overlay.Layers.InsertRange(overlay.Layers.IndexOf(originals[^1]) + 1, copies);
        Tidy(overlay);
        return copies[0];
    }

    /// <summary>An id no layer in the design has (nor any of <paramref name="taken"/>).</summary>
    public static string NewId(Overlay overlay, IEnumerable<string>? taken = null)
    {
        HashSet<string> used = [.. overlay.Layers.Select(layer => layer.Id).OfType<string>(), .. taken ?? []];
        while (true)
        {
            string id = Guid.NewGuid().ToString("N")[..8];
            if (!used.Contains(id))
            {
                return id;
            }
        }
    }

    public static string UniqueName(Overlay overlay, string name) => UniqueName(overlay.Layers.Select(layer => layer.Name), name);

    public static string UniqueName(IEnumerable<string> existing, string name)
    {
        HashSet<string> taken = new(existing, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name))
        {
            return name;
        }

        string stem = name.TrimEnd(' ', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        stem = stem.Length == 0 ? name : stem;
        for (int index = 2; ; index++)
        {
            string candidate = $"{stem} {index}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>The layers a mask can cut: drawn ones beneath it that aren't groups, videos or masks, nearest first.</summary>
    public static IEnumerable<OverlayLayer> MaskTargets(Overlay overlay, OverlayLayer mask) => overlay.Layers
        .Take(Math.Max(0, overlay.Layers.IndexOf(mask)))
        .Where(layer => layer.Kind is OverlayLayerKinds.Text or OverlayLayerKinds.Image or OverlayLayerKinds.Rect && layer.Mask is null)
        .Reverse();

    public static bool CanMask(OverlayLayer layer) => layer.Kind is OverlayLayerKinds.Rect or OverlayLayerKinds.Image;
}
