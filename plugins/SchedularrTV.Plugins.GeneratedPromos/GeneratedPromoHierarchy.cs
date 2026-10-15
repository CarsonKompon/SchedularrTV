using SchedularrTV.Core.Configuration;

namespace SchedularrTV.GeneratedPromos;

/// <summary>
/// A turn (degrees clockwise, as the canvas draws with y pointing down) followed by a move: how a group's space sits
/// in the space around it. Groups only move and turn their contents, so these compose without any stretching.
/// </summary>
public readonly record struct Rigid(double Angle, double X, double Y)
{
    public static Rigid Identity => new(0, 0, 0);

    public (double X, double Y) Apply(double x, double y)
    {
        (double turnedX, double turnedY) = Turn(x, y);
        return (turnedX + X, turnedY + Y);
    }

    /// <summary>Only the turn: for movements, which don't depend on where they start.</summary>
    public (double X, double Y) Turn(double x, double y) => Rotate(x, y, Angle);

    /// <summary>This, and then <paramref name="outer"/>.</summary>
    public Rigid Then(Rigid outer)
    {
        (double x, double y) = outer.Apply(X, Y);
        return new Rigid(Angle + outer.Angle, x, y);
    }

    public Rigid Inverse()
    {
        (double x, double y) = Rotate(-X, -Y, -Angle);
        return new Rigid(-Angle, x, y);
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
public readonly record struct WorldBox(double CenterX, double CenterY, double Width, double Height, double Angle);

/// <summary>Where a dragged layer goes, relative to the layer it's dropped on, in the layer list (top first).</summary>
public enum LayerDrop
{
    /// <summary>In front of it (listed above it, drawn over it), next to it.</summary>
    Above,

    /// <summary>Behind it, next to it.</summary>
    Below,

    /// <summary>Inside it (a group), in front of what's already there.</summary>
    Into
}

/// <summary>
/// Groups and the layers inside them. Layers are kept in one list (so everything that numbers them stays as it was);
/// each names the group it's inside, and a group's layers are kept straight after it, in drawing order.
/// </summary>
public static class GeneratedPromoHierarchy
{
    public static bool IsGroup(GeneratedLayer layer) => layer.Kind == GeneratedLayerKind.Group;

    /// <summary>The group the layer is inside, or null at the top (also when its group isn't in the template).</summary>
    public static GeneratedLayer? ParentOf(GeneratedPromoTemplate template, GeneratedLayer layer) =>
        layer.Parent is { Length: > 0 } id
            ? template.Layers.FirstOrDefault(candidate => IsGroup(candidate) && string.Equals(candidate.Id, id, StringComparison.Ordinal)
                && !ReferenceEquals(candidate, layer))
            : null;

    /// <summary>The groups around the layer, innermost first (stopping at a loop, should one have been written by hand).</summary>
    public static IEnumerable<GeneratedLayer> Ancestors(GeneratedPromoTemplate template, GeneratedLayer layer)
    {
        HashSet<GeneratedLayer> seen = [layer];
        for (GeneratedLayer? group = ParentOf(template, layer); group is not null && seen.Add(group); group = ParentOf(template, group))
        {
            yield return group;
        }
    }

    /// <summary>Whether <paramref name="layer"/> is <paramref name="group"/> or somewhere inside it.</summary>
    public static bool IsWithin(GeneratedPromoTemplate template, GeneratedLayer layer, GeneratedLayer group) =>
        ReferenceEquals(layer, group) || Ancestors(template, layer).Contains(group);

    /// <summary>The layers directly inside <paramref name="group"/> (null: at the top), in drawing order.</summary>
    public static List<GeneratedLayer> Children(GeneratedPromoTemplate template, GeneratedLayer? group) =>
        [.. template.Layers.Where(layer => ReferenceEquals(ParentOf(template, layer), group))];

    /// <summary>The layer and everything inside it, in drawing order.</summary>
    public static List<GeneratedLayer> Subtree(GeneratedPromoTemplate template, GeneratedLayer layer) =>
        [.. template.Layers.Where(candidate => IsWithin(template, candidate, layer))];

    /// <summary>The rows of the layer list, top (drawn last) first, each with how deep in groups it is.</summary>
    public static List<(GeneratedLayer Layer, int Depth)> Rows(GeneratedPromoTemplate template, Func<GeneratedLayer, bool> open)
    {
        List<(GeneratedLayer, int)> rows = [];
        void Add(GeneratedLayer? group, int depth)
        {
            foreach (GeneratedLayer layer in Enumerable.Reverse(Children(template, group)))
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

    // ---- Spaces ----

    /// <summary>How a group (posed like this) places its contents: from its corner, turned about its middle.</summary>
    public static Rigid Placing(LayerPose group)
    {
        double pivotX = group.Width / 2.0;
        double pivotY = group.Height / 2.0;
        double angle = group.Rotation ?? 0;
        (double turnedX, double turnedY) = Rigid.Rotate(pivotX, pivotY, angle);
        return new Rigid(angle, group.X + pivotX - turnedX, group.Y + pivotY - turnedY);
    }

    /// <summary>
    /// From inside <paramref name="group"/> (null: the canvas itself) to the canvas, with each group posed as
    /// <paramref name="pose"/> has it at <paramref name="time"/>.
    /// </summary>
    public static Rigid SpaceOf(GeneratedPromoTemplate template, GeneratedLayer? group, Func<GeneratedLayer, IGeneratedPose> pose, TimeSpan time)
    {
        Rigid space = Rigid.Identity;
        if (group is null)
        {
            return space;
        }

        foreach (GeneratedLayer around in Ancestors(template, group).Prepend(group))
        {
            space = space.Then(Placing(GeneratedPromoEditing.PoseAt(pose(around), time)));
        }

        return space;
    }

    /// <summary>Where the layer is on the canvas at <paramref name="time"/>.</summary>
    public static WorldBox World(GeneratedPromoTemplate template, GeneratedLayer layer, Func<GeneratedLayer, IGeneratedPose> pose, TimeSpan time)
    {
        LayerPose local = GeneratedPromoEditing.PoseAt(pose(layer), time);
        Rigid space = SpaceOf(template, ParentOf(template, layer), pose, time);
        (double x, double y) = space.Apply(local.X + local.Width / 2.0, local.Y + local.Height / 2.0);
        return new WorldBox(x, y, local.Width, local.Height, space.Angle + (local.Rotation ?? 0));
    }

    /// <summary>The total turn of the space the layer is placed in (its groups', not its own).</summary>
    public static double SpaceAngle(GeneratedPromoTemplate template, GeneratedLayer layer, Func<GeneratedLayer, IGeneratedPose> pose, TimeSpan time) =>
        SpaceOf(template, ParentOf(template, layer), pose, time).Angle;

    // ---- Moving between groups ----

    /// <summary>Moves and turns a pose (and each of its keyframes) by <paramref name="change"/>, about each box's middle.</summary>
    public static void Transform(IGeneratedPose pose, Rigid change)
    {
        if (change == Rigid.Identity)
        {
            return;
        }

        (pose.X, pose.Y, float turned) = Moved(pose.X, pose.Y, pose.Width, pose.Height, pose.Rotation ?? 0, change);
        pose.Rotation = Math.Abs(turned) < 0.0005f ? null : turned;
        foreach (GeneratedKeyframe key in pose.Keyframes ?? [])
        {
            (key.X, key.Y, key.Rotation) = Moved(key.X, key.Y, key.Width, key.Height, key.Rotation, change);
        }
    }

    private static (float X, float Y, float Rotation) Moved(float x, float y, float width, float height, float rotation, Rigid change)
    {
        (double centreX, double centreY) = change.Apply(x + width / 2.0, y + height / 2.0);
        return ((float)Math.Round(centreX - width / 2.0, 2), (float)Math.Round(centreY - height / 2.0, 2), (float)Math.Round(rotation + change.Angle, 3));
    }

    /// <summary>
    /// Puts the layer inside <paramref name="group"/> (null: at the top) without it moving on screen: its position and
    /// turn (keyframes and each background's placement too) are worked out again for the new group, as the groups stand
    /// at <paramref name="time"/>.
    /// </summary>
    public static void Rehome(GeneratedPromoTemplate template, GeneratedLayer layer, GeneratedLayer? group, TimeSpan time)
    {
        if (group is not null && (!IsGroup(group) || IsWithin(template, group, layer)))
        {
            return;
        }

        GeneratedLayer? from = ParentOf(template, layer);
        if (ReferenceEquals(from, group))
        {
            layer.Parent = group?.Id;
            return;
        }

        foreach (string? background in Backgrounds(template))
        {
            IGeneratedPose Pose(GeneratedLayer candidate) => GeneratedPromoResolver.Pose(template, background, candidate);
            IGeneratedPose? target = background is null ? layer : GeneratedPromoResolver.Placement(template, background, layer);
            if (target is null)
            {
                continue;
            }

            Rigid change = SpaceOf(template, from, Pose, time).Then(SpaceOf(template, group, Pose, time).Inverse());
            Transform(target, change);
        }

        if (group is not null)
        {
            group.Id ??= NewId(template);
        }

        layer.Parent = group?.Id;
    }

    // The template's own layout, then every background that places layers its own way.
    private static IEnumerable<string?> Backgrounds(GeneratedPromoTemplate template) =>
        new string?[] { null }.Concat((template.BackgroundOverrides ?? []).Select(tracked => (string?)tracked.Background));

    /// <summary>
    /// Puts every group's layers straight after it, keeping the drawing order otherwise: what the layer list shows, and
    /// what keeps a group's contents together when it's drawn.
    /// </summary>
    public static void Tidy(GeneratedPromoTemplate template)
    {
        List<GeneratedLayer> ordered = [];
        HashSet<GeneratedLayer> placed = [];
        void Add(GeneratedLayer? group)
        {
            foreach (GeneratedLayer layer in Children(template, group))
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
        // Anything left out (only a hand-written loop of groups) keeps its place at the end, at the top.
        foreach (GeneratedLayer layer in template.Layers.Where(layer => !placed.Contains(layer)))
        {
            layer.Parent = null;
            ordered.Add(layer);
        }

        template.Layers.Clear();
        template.Layers.AddRange(ordered);
    }

    /// <summary>
    /// The layers with none of their groups among them (dragging a group brings what's inside it along), in drawing order.
    /// </summary>
    public static List<GeneratedLayer> Outermost(GeneratedPromoTemplate template, IEnumerable<GeneratedLayer> layers)
    {
        HashSet<GeneratedLayer> chosen = [.. layers];
        return [.. template.Layers.Where(layer => chosen.Contains(layer) && !Ancestors(template, layer).Any(chosen.Contains))];
    }

    /// <summary>
    /// Moves <paramref name="layers"/> (with what's inside them) next to or into <paramref name="target"/> in the list,
    /// keeping where they are on screen. False when that can't be done (into themselves, or onto a non-group "into").
    /// </summary>
    public static bool Move(GeneratedPromoTemplate template, IEnumerable<GeneratedLayer> layers, GeneratedLayer target, LayerDrop drop, TimeSpan time)
    {
        List<GeneratedLayer> moving = Outermost(template, layers);
        if (moving.Count == 0 || moving.Any(layer => IsWithin(template, target, layer)) || (drop == LayerDrop.Into && !IsGroup(target)))
        {
            return false;
        }

        GeneratedLayer? group = drop == LayerDrop.Into ? target : ParentOf(template, target);
        foreach (GeneratedLayer layer in moving)
        {
            Rehome(template, layer, group, time);
        }

        // Take the blocks out, then put them back together beside (or inside) the target.
        List<GeneratedLayer> block = [.. template.Layers.Where(layer => moving.Any(root => IsWithin(template, layer, root)))];
        template.Layers.RemoveAll(block.Contains);
        int at = drop == LayerDrop.Below
            ? template.Layers.IndexOf(target)
            : template.Layers.IndexOf(target) + Subtree(template, target).Count;
        template.Layers.InsertRange(Math.Clamp(at, 0, template.Layers.Count), block);
        Tidy(template);
        return true;
    }

    /// <summary>Moves a layer one step up (+1) or down (-1) among the layers beside it in its group.</summary>
    public static bool Step(GeneratedPromoTemplate template, GeneratedLayer layer, int direction, TimeSpan time)
    {
        List<GeneratedLayer> siblings = Children(template, ParentOf(template, layer));
        int index = siblings.IndexOf(layer) + Math.Sign(direction);
        return index >= 0 && index < siblings.Count
            && Move(template, [layer], siblings[index], direction > 0 ? LayerDrop.Above : LayerDrop.Below, time);
    }

    /// <summary>
    /// A new group around <paramref name="layers"/>, sized to fit them as they are at <paramref name="time"/>, without
    /// anything moving on screen. It has no transitions of its own until you give it some.
    /// </summary>
    public static GeneratedLayer Group(GeneratedPromoTemplate template, IEnumerable<GeneratedLayer> layers, TimeSpan time)
    {
        List<GeneratedLayer> members = Outermost(template, layers);
        if (members.Count == 0)
        {
            throw new InvalidOperationException("Choose the layers to group.");
        }

        // Inside the group they share; if they're in different groups, at the top.
        GeneratedLayer? around = ParentOf(template, members[0]);
        if (members.Any(member => !ReferenceEquals(ParentOf(template, member), around)))
        {
            around = null;
        }

        foreach (GeneratedLayer member in members)
        {
            Rehome(template, member, around, time);
        }

        // Fit the members as drawn now, turned boxes included, in the space they're in.
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (GeneratedLayer member in members)
        {
            LayerPose pose = GeneratedPromoEditing.PoseAt(member, time);
            foreach ((double cornerX, double cornerY) in Corners(pose))
            {
                (left, top, right, bottom) = (Math.Min(left, cornerX), Math.Min(top, cornerY), Math.Max(right, cornerX), Math.Max(bottom, cornerY));
            }
        }

        var group = new GeneratedLayer
        {
            Id = NewId(template),
            Name = GeneratedPromoEditing.UniqueName(template.Layers.Select(layer => layer.Name), "Group"),
            Kind = GeneratedLayerKind.Group,
            Parent = around?.Id,
            X = (float)Math.Floor(left),
            Y = (float)Math.Floor(top),
            Width = (float)Math.Max(1, Math.Ceiling(right - Math.Floor(left))),
            Height = (float)Math.Max(1, Math.Ceiling(bottom - Math.Floor(top))),
            TransitionIn = GeneratedTransition.None,
            TransitionOut = GeneratedTransition.None,
            TransitionDuration = TimeSpan.Zero
        };
        template.Layers.Insert(template.Layers.IndexOf(members[0]), group);
        foreach (GeneratedLayer member in members)
        {
            Rehome(template, member, group, time);
        }

        Tidy(template);
        return group;
    }

    private static IEnumerable<(double X, double Y)> Corners(LayerPose pose)
    {
        double centreX = pose.X + pose.Width / 2.0;
        double centreY = pose.Y + pose.Height / 2.0;
        foreach ((double dx, double dy) in new[] { (-1.0, -1.0), (1.0, -1.0), (1.0, 1.0), (-1.0, 1.0) })
        {
            (double x, double y) = Rigid.Rotate(dx * pose.Width / 2.0, dy * pose.Height / 2.0, pose.Rotation ?? 0);
            yield return (centreX + x, centreY + y);
        }
    }

    /// <summary>
    /// Takes the group's layers out into the group around it, where they are on screen at <paramref name="time"/>, and
    /// removes the group. The group's own animation and transitions go with it.
    /// </summary>
    public static List<GeneratedLayer> Ungroup(GeneratedPromoTemplate template, GeneratedLayer group, TimeSpan time)
    {
        GeneratedLayer? around = ParentOf(template, group);
        List<GeneratedLayer> members = Children(template, group);
        foreach (GeneratedLayer member in members)
        {
            Rehome(template, member, around, time);
        }

        template.Layers.Remove(group);
        GeneratedPromoEditing.ForgetLayer(template, group);
        Tidy(template);
        return members;
    }

    /// <summary>Removes the layer and, for a group, everything inside it.</summary>
    public static void Remove(GeneratedPromoTemplate template, GeneratedLayer layer)
    {
        foreach (GeneratedLayer gone in Subtree(template, layer))
        {
            template.Layers.Remove(gone);
            GeneratedPromoEditing.ForgetLayer(template, gone);
        }
    }

    /// <summary>
    /// A copy of the layer (with everything inside it, for a group) just after it, nudged so it can be seen. Copies have
    /// ids of their own, so they're placed with the template's layout over every background.
    /// </summary>
    public static GeneratedLayer Duplicate(GeneratedPromoTemplate template, GeneratedLayer layer)
    {
        List<GeneratedLayer> originals = Subtree(template, layer);
        List<GeneratedLayer> copies = [.. originals.Select(original => GeneratedPromoEditing.Copy(original))];
        Dictionary<string, string> ids = new(StringComparer.Ordinal);
        List<string> names = [.. template.Layers.Select(existing => existing.Name)];
        foreach (GeneratedLayer copy in copies)
        {
            string? old = copy.Id;
            copy.Id = IsGroup(copy) ? NewId(template, ids.Values) : null;
            if (old is not null && copy.Id is not null) ids[old] = copy.Id;
            copy.Name = GeneratedPromoEditing.UniqueName(names, copy.Name);
            names.Add(copy.Name);
        }

        foreach (GeneratedLayer copy in copies.Skip(1))
        {
            copy.Parent = copy.Parent is { } parent && ids.TryGetValue(parent, out string? moved) ? moved : copy.Parent;
        }

        GeneratedPromoEditing.Offset(copies[0], 20, 20);
        template.Layers.InsertRange(template.Layers.IndexOf(originals[^1]) + 1, copies);
        Tidy(template);
        return copies[0];
    }

    /// <summary>An id no layer in the template has (nor any of <paramref name="taken"/>).</summary>
    public static string NewId(GeneratedPromoTemplate template, IEnumerable<string>? taken = null)
    {
        HashSet<string> used = [.. template.Layers.Select(layer => layer.Id).OfType<string>(), .. taken ?? []];
        while (true)
        {
            string id = Guid.NewGuid().ToString("N")[..8];
            if (!used.Contains(id))
            {
                return id;
            }
        }
    }
}
