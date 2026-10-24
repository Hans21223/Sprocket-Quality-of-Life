using System.Numerics;
using System.Text.Json.Nodes;

namespace SprocketQoL;

/// Plans duplicate assemblies without touching the active design. All coordinates are vehicle-world metres.
public static class PartPatternMath
{
    public enum Pattern { Linear, Radial }
    public sealed record Options(Pattern Kind, int Copies, Vector3 Spacing, Vector3 Axis, float SweepDegrees, Vector3 Centre, float Radius, bool RotateCopies = true);
    public sealed record Result(string Json, int FocusVuid, int AddedParts, Vector3[] Pivots);

    public static Matrix4x4[] Transforms(Options options, Vector3 pivot)
    {
        if (options.Copies < 1 || options.Copies > 100) throw new ArgumentException("Add between 1 and 100 copies.");
        if (!Finite(pivot) || !Finite(options.Spacing) || !Finite(options.Centre) || !Finite(options.Axis) || !float.IsFinite(options.Radius) || !float.IsFinite(options.SweepDegrees))
            throw new ArgumentException("Pattern coordinates must be finite.");
        if (options.Spacing.Length() > 10000 || options.Centre.Length() > 100000 || options.Radius < 0 || options.Radius > 10000 || Math.Abs(options.SweepDegrees) > 3600)
            throw new ArgumentException("The pattern extends beyond the supported range.");
        if (options.Kind == Pattern.Linear)
        {
            if (options.Spacing.LengthSquared() < 1e-12f) throw new ArgumentException("Choose non-zero spacing before adding a line of copies.");
            return Enumerable.Range(1, options.Copies).Select(i => Matrix4x4.CreateTranslation(options.Spacing * i)).ToArray();
        }
        if (options.Kind != Pattern.Radial || options.Axis.LengthSquared() < 1e-12f) throw new ArgumentException("Choose a valid rotation axis.");
        if (Math.Abs(options.SweepDegrees) < 0.001f) throw new ArgumentException("Choose a non-zero sweep angle.");
        var axis = Vector3.Normalize(options.Axis);
        var axial = axis * Vector3.Dot(pivot - options.Centre, axis);
        var radius = pivot - options.Centre - axial;
        float originalRadius = radius.Length();
        if (options.Radius > 0)
        {
            if (radius.LengthSquared() < 1e-12f)
            {
                var basis = Math.Abs(axis.X) < 0.9f ? Vector3.UnitX : Vector3.UnitZ;
                radius = Vector3.Normalize(basis - axis * Vector3.Dot(basis, axis));
            }
            else radius = Vector3.Normalize(radius);
            radius *= options.Radius;
        }
        if (radius.LengthSquared() < 1e-12f && !options.RotateCopies) throw new ArgumentException("Set a radius or move the centre away from the copied group.");
        bool closed = Math.Abs(Math.Abs(options.SweepDegrees) - 360) < 0.001f;
        bool includesOriginal = Math.Abs(radius.Length() - originalRadius) < 0.00001f;
        if (originalRadius < 0.00001f && options.Radius == 0) throw new ArgumentException("Move the centre away from the group or set a radius.");
        var result = new Matrix4x4[options.Copies];
        for (int i = 0; i < result.Length; i++)
        {
            // When the source is on the ring it occupies the first position; added copies start after it.
            float fraction = includesOriginal ? (float)(i + 1) / (closed ? options.Copies + 1 : options.Copies)
                : closed ? (float)i / options.Copies : options.Copies == 1 ? 1 : (float)i / (options.Copies - 1);
            float angle = options.SweepDegrees * MathF.PI / 180 * fraction;
            var rotation = Matrix4x4.CreateFromAxisAngle(axis, angle);
            var at = options.Centre + axial + Vector3.TransformNormal(radius, rotation);
            result[i] = Matrix4x4.CreateTranslation(-pivot) * (options.RotateCopies ? rotation : Matrix4x4.Identity) * Matrix4x4.CreateTranslation(at);
        }
        // A sweep past a full turn, or ending where it starts, puts copies on each other or on the original (kept as it is).
        static bool Same(Matrix4x4 a, Matrix4x4 b)
        {
            var d = a - b;
            return new[] { d.M11, d.M12, d.M13, d.M14, d.M21, d.M22, d.M23, d.M24, d.M31, d.M32, d.M33, d.M34, d.M41, d.M42, d.M43, d.M44 }.All(x => MathF.Abs(x) < 0.001f);
        }
        var placed = result.Prepend(Matrix4x4.Identity).ToArray();
        for (int i = 0; i < placed.Length; i++)
            for (int j = i + 1; j < placed.Length; j++)
                if (Same(placed[i], placed[j]))
                    throw new ArgumentException("Some copies would land on each other or on the original: keep the sweep within one turn, or change the number of copies.");
        return result;
    }

    public static Vector3 SelectionPivot(string json, IReadOnlyCollection<int> selected)
    {
        var objects = Conversion.Objects(Conversion.Parse(json));
        var matrices = Conversion.WorldMatrices(objects);
        var chosen = selected.Where(objects.ContainsKey).ToHashSet();
        var roots = chosen.Where(id => !chosen.Contains(Conversion.Id(objects[id], "pvuid"))).ToArray();
        if (roots.Length == 0) throw new ArgumentException("Select at least one part to repeat.");
        return roots.Aggregate(Vector3.Zero, (sum, id) => sum + matrices[id].Translation) / roots.Length;
    }

    public static Result Apply(string json, IReadOnlyCollection<int> selected, Options options)
    {
        var original = Conversion.Parse(json); var objects = Conversion.Objects(original);
        if (selected.Count == 0 || selected.Any(id => !objects.ContainsKey(id))) throw new ArgumentException("Select parts that still belong to this vehicle.");
        if (selected.Any(id => Conversion.Id(objects[id], "pvuid") < 0)) throw new ArgumentException("Select attached parts rather than the main hull.");
        var (clipboard, count) = BlueprintClipboard.Copy(json, selected.ToArray());
        if (count == 0 || (long)count * options.Copies > 5000) throw new ArgumentException("A pattern may add at most 5,000 parts, including attached components.");
        var pivot = SelectionPivot(json, selected); var transforms = Transforms(options, pivot);
        int parent = Conversion.Id(objects[selected.Last()], "pvuid"), focus = -1;
        var result = Conversion.Parse(json); // every copy goes into this one tree, written out once
        foreach (var transform in transforms)
        {
            var clip = JsonNode.Parse(clipboard)!.AsObject();
            var roots = clip["rootTransforms"]!.AsObject();
            foreach (var pair in roots.ToArray())
            {
                var a = pair.Value!.AsArray().Select(v => v!.GetValue<float>()).ToArray();
                if (a.Length != 16) throw new FormatException("Invalid copied root transform.");
                var world = new Matrix4x4(a[0],a[1],a[2],a[3],a[4],a[5],a[6],a[7],a[8],a[9],a[10],a[11],a[12],a[13],a[14],a[15]) * transform;
                roots[pair.Key] = new JsonArray(new[] { world.M11,world.M12,world.M13,world.M14,world.M21,world.M22,world.M23,world.M24,world.M31,world.M32,world.M33,world.M34,world.M41,world.M42,world.M43,world.M44 }.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
            }
            var pasted = BlueprintClipboard.PasteInto(result, clip, parent);
            if (focus < 0) focus = pasted.FocusVuid;
        }
        return new(BlueprintClipboard.Write(result), focus, checked(count * options.Copies), transforms.Select(t => Vector3.Transform(pivot, t)).ToArray());
    }
    static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
