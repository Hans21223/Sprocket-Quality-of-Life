using System.Numerics;

namespace SprocketQoL;

/// Carries edge settings through bevel's shortened sides, independently of face creation order.
public static class BevelEdges
{
    /// The native mesh has one angle threshold for the whole structure. Pin existing
    /// hard borders before raising it, then smooth only the rounded surface and its
    /// tangent joins. HardEdges also includes the recreated descendants of pinned edges.
    public sealed record SmoothingResult(float AngleDegrees,
        HashSet<(int, int)> OriginalHardEdges, HashSet<(int, int)> HardEdges, HashSet<(int, int)> SmoothEdges);

    public static SmoothingResult Smoothing(IReadOnlyList<Vector3> positions, IReadOnlyList<int[]> faces,
        MeshPlans.Rebuild plan, ISet<(int, int)> roundedEdges, float oldAngleDegrees)
    {
        float oldAngle = float.IsFinite(oldAngleDegrees) ? Math.Max(0, oldAngleDegrees) : 0;
        // Below 0.5 degrees the native builder skips normal averaging entirely,
        // even for edges whose computed smooth flag would pass its angle test.
        float oldLimit = oldAngle < 0.5f ? 1e-4f : oldAngle;
        var selected = roundedEdges.Select(e => FaceMerge.Key(e.Item1, e.Item2)).ToHashSet();
        var originalNormals = faces.Select(f => Normal(positions, f)).ToArray();
        var oldHard = new HashSet<(int, int)>();
        var hard = new HashSet<(int, int)>();
        var smooth = new HashSet<(int, int)>();
        foreach (var group in faces.SelectMany((f, i) => Sides(f).Select(e => (Edge: e, Face: i))).GroupBy(e => e.Edge))
        {
            var adjacent = group.Select(e => e.Face).Distinct().ToArray();
            if (adjacent.Length == 2 && !selected.Contains(group.Key) &&
                Angle(originalNormals[adjacent[0]], originalNormals[adjacent[1]]) >= oldLimit)
                oldHard.Add(group.Key);
        }

        var all = positions.Concat(plan.Points.Select(p => p.P)).ToArray();
        var removed = plan.Remove.ToHashSet();
        var after = faces.Select((f, i) => (Corners: f, Source: i, Curved: false))
            .Where((_, i) => !removed.Contains(i))
            .Concat(plan.Add.Select(f => (f.Corners, f.Source,
                Curved: Angle(Normal(all, f.Corners), originalNormals[f.Source]) > 0.05f))).ToArray();
        var normals = after.Select(f => Normal(all, f.Corners)).ToArray();
        var sources = Sources(positions, faces, plan, selected);
        float threshold = Math.Max(oldAngle, 1);
        foreach (var group in after.SelectMany((f, i) => Sides(f.Corners).Select(e => (Edge: e, Face: i))).GroupBy(e => e.Edge))
        {
            var adjacent = group.Select(e => e.Face).Distinct().ToArray();
            var edge = group.Key;
            // Kept original edges are not necessarily in the rebuilt face source map.
            if (oldHard.Contains(edge) || sources.TryGetValue(edge, out var source) && oldHard.Contains(source))
            {
                hard.Add(edge);
                continue;
            }
            if (adjacent.Length != 2) continue;
            int a = adjacent[0], b = adjacent[1];
            bool ac = after[a].Curved, bc = after[b].Curved;
            if (!ac && !bc) continue;
            // A cylindrical strip can end against a perpendicular plate: that seam
            // must not join the smooth fan. Its tangent seam meets its own source plane.
            bool tangent = ac && bc || ac && Angle(normals[b], originalNormals[after[a].Source]) <= 0.05f
                || bc && Angle(normals[a], originalNormals[after[b].Source]) <= 0.05f;
            if (tangent)
            {
                smooth.Add(edge);
                threshold = Math.Max(threshold, Angle(normals[a], normals[b]) + 0.1f);
            }
            else hard.Add(edge);
        }
        return new(threshold, oldHard, hard, smooth);
    }

    static Vector3 Normal(IReadOnlyList<Vector3> positions, int[] face)
    {
        Vector3 result = Vector3.Zero;
        for (int i = 1; i + 1 < face.Length; i++)
            result += Vector3.Cross(positions[face[i]] - positions[face[0]], positions[face[i + 1]] - positions[face[0]]);
        return result.LengthSquared() > 0 ? Vector3.Normalize(result) : Vector3.Zero;
    }

    static float Angle(Vector3 a, Vector3 b) =>
        MathF.Atan2(Vector3.Cross(a, b).Length(), Math.Clamp(Vector3.Dot(a, b), -1, 1)) * (180 / MathF.PI);

    public static Dictionary<(int, int), (int, int)> Sources(IReadOnlyList<Vector3> positions,
        IReadOnlyList<int[]> faces, MeshPlans.Rebuild plan, ISet<(int,int)>? roundedEdges = null)
    {
        var original = faces.SelectMany(Sides).ToHashSet();
        var result = new Dictionary<(int, int), (int, int)>();
        IEnumerable<int> Origins(int v) => v < positions.Count ? new[] { v }
            : plan.Points[v - positions.Count].Blend.Where(b => b.W > 0).Select(b => b.V);
        Vector3 At(int v) => v < positions.Count ? positions[v] : plan.Points[v - positions.Count].P;
        bool OnSide(int v, (int A, int B) edge)
        {
            var start = positions[edge.A]; var delta = positions[edge.B] - start;
            float length2 = delta.LengthSquared();
            if (length2 < 1e-16f) return false;
            float t = Vector3.Dot(At(v) - start, delta) / length2;
            return t >= -1e-5f && t <= 1.00001f &&
                Vector3.DistanceSquared(At(v), start + t * delta) <= Math.Max(1e-12f, length2 * 1e-10f);
        }
        foreach (var edge in plan.Add.SelectMany(f => Sides(f.Corners)).Distinct())
        {
            if (original.Contains(edge)) { result[edge] = edge; continue; }
            var from = Origins(edge.Item1).Concat(Origins(edge.Item2)).Distinct().ToArray();
            if (from.Length == 2)
            {
                var source = FaceMerge.Key(from[0], from[1]);
                if (original.Contains(source) && roundedEdges?.Contains(source) != true) result[edge] = source;
            }
            else if (from.Length == 1)
            {
                // A bevel ending inside a face fan keeps the old corner and inserts a point on
                // its side. Both have the same origin, so identify the shortened side geometrically.
                var candidates = original.Where(e => (e.Item1 == from[0] || e.Item2 == from[0]) &&
                    OnSide(edge.Item1, e) && OnSide(edge.Item2, e)).Take(2).ToArray();
                if (candidates.Length == 1) result[edge] = candidates[0];
            }
        }
        return result;
    }

    static IEnumerable<(int, int)> Sides(int[] face) =>
        face.Select((v, i) => FaceMerge.Key(v, face[(i + 1) % face.Length]));
}
