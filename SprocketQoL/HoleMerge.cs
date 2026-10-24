using System.Numerics;

namespace SprocketQoL;

/// Create Hole on several faces, merged first: selected faces that share an edge and lie within 20° of each other are
/// one area, and each area gets one hole, its ring inside the area's outline and the faces round it rebuilt as for a
/// single face. Plain maths on vertex indices, tested offline; HoleQuality applies it to the game's mesh.
public static class HoleMerge
{
    const float SameWay = 0.94f; // about 20°, as Merge faces

    /// The selected faces in areas: faces sharing a side (run opposite ways, so facing the same side) within 20°.
    public static List<List<int>> Areas(IReadOnlyList<Vector3> pos, IReadOnlyList<int[]> faces, IEnumerable<int> selected)
    {
        var chosen = selected.Distinct().ToList();
        var parent = chosen.ToDictionary(f => f, f => f);
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var facing = chosen.ToDictionary(f => f, f => Vector3.Normalize(Normal(pos, faces[f])));
        var sides = new Dictionary<(int, int), int>();
        foreach (int f in chosen)
            for (int k = 0; k < faces[f].Length; k++) sides[(faces[f][k], faces[f][(k + 1) % faces[f].Length])] = f;
        foreach (var ((a, b), f) in sides)
            if (sides.TryGetValue((b, a), out int g) && Vector3.Dot(facing[f], facing[g]) >= SameWay) parent[Find(f)] = Find(g);
        return chosen.GroupBy(Find).Select(g => g.ToList()).ToList();
    }

    /// One hole through each area: its faces go, a ring of `segments` new points goes in (`size` of the largest circle
    /// that fits, `cardinal` points square to the part, as Hole quality says), and the faces between the area's outline
    /// and the ring are filled as `mode` says. Why says what stopped it.
    public static MeshPlans.Rebuild Plan(IReadOnlyList<Vector3> pos, IReadOnlyList<int[]> faces, IEnumerable<List<int>> areas,
                                         int segments, float size, bool cardinal, Fill.Mode mode)
    {
        var remove = new List<int>();
        var add = new List<MeshPlans.NewFace>();
        var points = new List<MeshPlans.NewPoint>();
        foreach (var area in areas)
        {
            if (Ring(pos, faces, area, segments, size, cardinal, out var why) is not { } found) return MeshPlans.Rebuild.Fail(why);
            var (outline, ring, normal) = found;
            var outer = outline.Select(v => pos[v]).ToArray();
            int n = outer.Length, m = ring.Length;
            var local = outer.Concat(ring).ToList();
            var hole = Enumerable.Range(n, m).ToList();
            var added = mode == Fill.Mode.Fewest ? null : new List<Fill.Added>();
            var made = Fill.Region(local, Enumerable.Range(0, n).ToList(), new List<List<int>> { hole }, normal, added, mode == Fill.Mode.Light, mode);
            if (HoleFill.Check(local, Enumerable.Range(0, n).ToArray(), hole, made, normal) is string problem)
                return MeshPlans.Rebuild.Fail("the faces round the hole didn't fit: " + problem);

            int first = pos.Count + points.Count;
            int Global(int i) => i < n ? outline[i] : first + i - n;
            var blend = outline.Select(v => (v, 1f)).ToArray(); // new points: the area's average thickness
            points.AddRange(local.Skip(n).Select(p => new MeshPlans.NewPoint(p, blend)));
            int source = area.OrderByDescending(f => Normal(pos, faces[f]).Length()).First();
            remove.AddRange(area);
            add.AddRange(made.Select(f => new MeshPlans.NewFace(f.Select(Global).ToArray(), source)));
        }
        return new MeshPlans.Rebuild(remove, add, points, null);
    }

    /// An area's outline (in its faces' turning), the hole ring through it and which way it faces, or null (`why`).
    /// The ring is as the game sizes one face's hole: 90% of the room from the middle to the nearest side, then Hole
    /// quality's size. The middle of one face is its corners' average, as the game's; of an area, its middle by area,
    /// so extra points along one side don't pull the hole over.
    public static (List<int> Outline, Vector3[] Ring, Vector3 Normal)? Ring(IReadOnlyList<Vector3> pos, IReadOnlyList<int[]> faces,
        List<int> area, int segments, float size, bool cardinal, out string why)
    {
        why = "";
        // The outline: sides no other face of the area runs the other way, chained head to tail.
        var sides = area.SelectMany(f => faces[f].Select((v, k) => (v, faces[f][(k + 1) % faces[f].Length]))).ToHashSet();
        var next = new Dictionary<int, int>();
        foreach (var (v, w) in sides)
            if (!sides.Contains((w, v)) && !next.TryAdd(v, w)) { why = "the selected faces only touch at a corner"; return null; }
        if (next.Count < 3) { why = "the selected faces have no outline"; return null; }
        var outline = new List<int>();
        for (int v = next.Keys.Min(); outline.Count < next.Count && !outline.Contains(v) && next.ContainsKey(v); v = next[v]) outline.Add(v);
        if (outline.Count != next.Count) { why = "the selected area has a hole in it already"; return null; }

        var outer = outline.Select(v => pos[v]).ToArray();
        var normal = Normal(pos, outline);
        if (!(normal.LengthSquared() > 0) || area.Any(f => Vector3.Dot(Vector3.Normalize(Normal(pos, faces[f])), Vector3.Normalize(normal)) < SameWay))
        { why = "the selected faces curve more than 20°"; return null; }
        Vector3 sum = Vector3.Zero;
        float total = 0;
        foreach (var c in area.Select(f => faces[f]))
            for (int k = 1; k + 1 < c.Length; k++)
            {
                float a = Vector3.Cross(pos[c[k]] - pos[c[0]], pos[c[k + 1]] - pos[c[0]]).Length() / 2;
                sum += a * (pos[c[0]] + pos[c[k]] + pos[c[k + 1]]) / 3;
                total += a;
            }
        if (!(total > 0)) { why = "the selected faces have no area"; return null; }
        var centre = area.Count == 1 ? outer.Aggregate(Vector3.Zero, (s, p) => s + p) / outer.Length : sum / total;
        float room = Enumerable.Range(0, outer.Length).Min(i => Distance(centre, outer[i], outer[(i + 1) % outer.Length]));
        var seed = HoleRing.Circle(segments, centre, normal, 0.9f * room, false);
        if (!HoleRing.TryFit(outer, seed, centre, out var ring, out why, size, cardinal)) return null;
        return (outline, ring, normal);
    }

    static Vector3 Normal(IReadOnlyList<Vector3> pos, IReadOnlyList<int> corners) => HoleRing.Normal(corners.Select(v => pos[v]).ToArray());

    static float Distance(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() < 1e-12f ? 0 : Math.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
        return Vector3.Distance(p, a + t * ab);
    }
}
