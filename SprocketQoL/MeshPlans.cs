using System.Numerics;

namespace SprocketQoL;

/// Blender-style mesh tools for hand-made structures, planned as plain maths on point indices (tested offline):
/// Flatten, Loop cut, Inset, Bevel, and Proportional editing's falloff. MeshTools applies the plans to the game's mesh.
public static class MeshPlans
{
    /// A point a tool adds: where, and the existing points (with weights) its corner thickness blends from.
    public sealed record NewPoint(Vector3 P, (int V, float W)[] Blend);

    /// A face a tool adds: its corners (existing points, or pos.Count + i for Points[i]), turning the way `Source` did,
    /// and the old face it's made from (thickness and settings carry over).
    public sealed record NewFace(int[] Corners, int Source);

    /// Faces to take out, faces to put in, and the points they need. Points no face uses afterwards go too.
    public sealed record Rebuild(List<int> Remove, List<NewFace> Add, List<NewPoint> Points, string? Why)
    {
        public static Rebuild Fail(string why) => new(new(), new(), new(), why);
    }

    // ---------- checks, before anything changes ----------

    /// Why a rebuild would leave the shape broken, or null: a new face repeating a corner, squashed to next to no area, or
    /// turned over against the face it's made from; edges shared by more than two faces, longer in all than before
    /// (faces laid over each other); or open edges longer in all than before (a crack: faces no longer joined). `gaps`
    /// false skips the last, for tools asked to leave points unjoined. Faces that were squashed already are left be.
    public static string? Check(IReadOnlyList<Vector3> pos, IReadOnlyList<int[]> faces, Rebuild plan, bool gaps = true)
    {
        var at = pos.Concat(plan.Points.Select(p => p.P)).ToList();
        foreach (var nf in plan.Add)
        {
            var c = nf.Corners;
            if (c.Length < 3 || c.Distinct().Count() != c.Length || c.Any(i => i < 0 || i >= at.Count)) return "a new face would repeat a corner";
            var n = HoleRing.Normal(c.Select(i => at[i]).ToList());
            var s = HoleRing.Normal(faces[nf.Source].Select(i => pos[i]).ToList());
            if (s.Length() < 1e-9f) continue; // made from a squashed face: nothing to compare with
            if (n.Length() < 1e-5f * s.Length()) return "a new face would be squashed to next to no area";
            if (Vector3.Dot(n, s) < -0.5f * n.Length() * s.Length()) return "a new face would be turned over";
        }
        var removed = plan.Remove.ToHashSet();
        var after = faces.Where((_, i) => !removed.Contains(i)).Concat(plan.Add.Select(a => a.Corners)).ToList();
        float tolerance = 1e-5f + 1e-4f * plan.Remove.SelectMany(f => faces[f]).Select(i => pos[i]).DefaultIfEmpty().Max(p => p.Length());
        if (Length(after, at, u => u > 2) > Length(faces, at, u => u > 2) + tolerance) return "faces would be laid over each other";
        if (gaps && Length(after, at, u => u == 1) > Length(faces, at, u => u == 1) + tolerance) return "it would leave a crack (faces no longer joined)";
        return null;
    }

    /// Why moving points to `moved` would fold a face over or squash it flat, or null.
    public static string? Folds(IReadOnlyList<Vector3> pos, IReadOnlyList<int[]> faces, IReadOnlyDictionary<int, Vector3> moved)
    {
        foreach (var f in faces)
        {
            if (!f.Any(moved.ContainsKey)) continue;
            var was = HoleRing.Normal(f.Select(i => pos[i]).ToList());
            var now = HoleRing.Normal(f.Select(i => moved.TryGetValue(i, out var p) ? p : pos[i]).ToList());
            if (was.Length() < 1e-8f) continue; // squashed already: not this tool's doing
            if (now.Length() < 1e-8f) return "a face would be squashed to no area";
            if (Vector3.Dot(now, was) <= 0) return "a face would fold over";
        }
        return null;
    }

    /// The point of a face (its corners, fanned into triangles from the first) nearest to `p`, and how far away it is.
    public static (Vector3 Point, float Distance) Closest(IReadOnlyList<Vector3> corners, Vector3 p)
    {
        var best = (Point: corners[0], Distance: float.MaxValue);
        for (int k = 1; k + 1 < corners.Count; k++)
        {
            var q = OnTriangle(p, corners[0], corners[k], corners[k + 1]);
            float d = Vector3.Distance(p, q);
            if (d < best.Distance) best = (q, d);
        }
        return best;
    }

    // Nearest point of triangle abc to p (Ericson, Real-Time Collision Detection 5.1.5).
    static Vector3 OnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6)));
        float denom = 1 / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }

    static IEnumerable<(int, int)> Sides(int[] f) => f.Select((v, k) => v < f[(k + 1) % f.Length] ? (v, f[(k + 1) % f.Length]) : (f[(k + 1) % f.Length], v));

    static Dictionary<(int, int), int> Uses(IEnumerable<int[]> faces)
    {
        var uses = new Dictionary<(int, int), int>();
        foreach (var f in faces) foreach (var e in Sides(f)) uses[e] = uses.GetValueOrDefault(e) + 1;
        return uses;
    }

    /// Total length of the edges used by a number of faces `which` picks (1: open edges; more than 2: crowded ones).
    static double Length(IEnumerable<int[]> faces, IReadOnlyList<Vector3> at, Func<int, bool> which) =>
        Uses(faces).Where(u => which(u.Value)).Sum(u => (double)Vector3.Distance(at[u.Key.Item1], at[u.Key.Item2]));

    // ---------- Flatten ----------

    public enum FlattenMode { BestFit, Level, Sideways, Lengthways }

    /// New positions for `points`: onto their best-fit plane (facing `normal` when faces were selected), or all at the
    /// same height (y), side (x) or length (z) position: the average of theirs. Empty if there's nothing to flatten.
    public static Dictionary<int, Vector3> Flatten(IReadOnlyList<Vector3> pos, ICollection<int> points, FlattenMode mode, Vector3? normal = null)
    {
        var result = new Dictionary<int, Vector3>();
        if (points.Count < (mode == FlattenMode.BestFit ? 3 : 2)) return result;
        var c = points.Aggregate(Vector3.Zero, (s, v) => s + pos[v]) / points.Count;
        Vector3 n = mode switch
        {
            FlattenMode.Level => Vector3.UnitY,
            FlattenMode.Sideways => Vector3.UnitX,
            FlattenMode.Lengthways => Vector3.UnitZ,
            _ => normal is { } given && given.LengthSquared() > 1e-12f ? Vector3.Normalize(given) : LeastSpread(pos, points, c),
        };
        foreach (int v in points)
            result[v] = mode switch
            {
                // Set straight to the shared value, so the points agree exactly.
                FlattenMode.Level => new Vector3(pos[v].X, c.Y, pos[v].Z),
                FlattenMode.Sideways => new Vector3(c.X, pos[v].Y, pos[v].Z),
                FlattenMode.Lengthways => new Vector3(pos[v].X, pos[v].Y, c.Z),
                _ => pos[v] - n * Vector3.Dot(pos[v] - c, n),
            };
        return result;
    }

    /// The direction the points spread least along: the covariance's smallest eigenvector, by power iteration on trace·I − C.
    static Vector3 LeastSpread(IReadOnlyList<Vector3> pos, ICollection<int> points, Vector3 c)
    {
        double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        foreach (int v in points)
        {
            var d = pos[v] - c;
            xx += d.X * d.X; xy += d.X * d.Y; xz += d.X * d.Z; yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z;
        }
        double t = xx + yy + zz;
        double[,] m = { { t - xx, -xy, -xz }, { -xy, t - yy, -yz }, { -xz, -yz, t - zz } };
        double[] x = { 0.3, 0.5, 0.8 }; // any start not along an axis
        for (int i = 0; i < 100; i++)
        {
            double[] y = { m[0, 0] * x[0] + m[0, 1] * x[1] + m[0, 2] * x[2], m[1, 0] * x[0] + m[1, 1] * x[1] + m[1, 2] * x[2], m[2, 0] * x[0] + m[2, 1] * x[1] + m[2, 2] * x[2] };
            double len = Math.Sqrt(y[0] * y[0] + y[1] * y[1] + y[2] * y[2]);
            if (len < 1e-18) break;
            x = new[] { y[0] / len, y[1] / len, y[2] / len };
        }
        return Vector3.Normalize(new Vector3((float)x[0], (float)x[1], (float)x[2]));
    }

    // ---------- Loop cut ----------

    /// A loop through the ring of quads each start edge crosses: every edge of the ring gets a middle point and each quad
    /// is cut in two between them. The ring stops at the plate's edge, at a triangle (cut in two as well), or where it
    /// closes. Start edges already on a ring are skipped (a mirrored twin on the same ring).
    public static Rebuild LoopCut(IReadOnlyList<Vector3> pos, IReadOnlyList<int[]> faces, IEnumerable<(int A, int B)> starts)
    {
        var edgeFaces = EdgeFaces(faces);
        var mids = new Dictionary<(int, int), int>();
        var points = new List<NewPoint>();
        var cut = new Dictionary<int, int>(); // face -> its corner starting the side the ring enters by
        void Mid(int a, int b)
        {
            if (mids.ContainsKey(Key(a, b))) return;
            mids[Key(a, b)] = pos.Count + points.Count;
            points.Add(new NewPoint((pos[a] + pos[b]) / 2, new[] { (a, 0.5f), (b, 0.5f) }));
        }
        foreach (var (a0, b0) in starts)
        {
            if (!edgeFaces.TryGetValue(Key(a0, b0), out var first) || mids.ContainsKey(Key(a0, b0))) continue;
            foreach (int start in first)
            {
                int f = start;
                var e = Key(a0, b0);
                while (!cut.ContainsKey(f))
                {
                    int k = SideIndex(faces[f], e);
                    if (k < 0) break;
                    cut[f] = k;
                    Mid(e.Item1, e.Item2);
                    if (faces[f].Length != 4) break; // a triangle ends the ring
                    var q = faces[f];
                    e = Key(q[(k + 2) % 4], q[(k + 3) % 4]);
                    Mid(e.Item1, e.Item2);
                    int next = edgeFaces[e].FirstOrDefault(g => g != f, -1);
                    if (next < 0) break; // the plate's edge
                    f = next;
                }
            }
        }
        if (cut.Count == 0) return Rebuild.Fail("select an edge of a face");
        // Every face on a cut edge must be cut across it too, or it would be left with a gap.
        foreach (var edge in mids.Keys)
            foreach (int g in edgeFaces[edge])
                if (!cut.TryGetValue(g, out int k) || !CutSides(faces[g], k).Contains(edge))
                    return Rebuild.Fail("the loop would cross itself or run into a face outside the ring");
        var add = new List<NewFace>();
        foreach (var (f, k) in cut)
        {
            var c = faces[f];
            if (c.Length == 4)
            {
                int m1 = mids[Key(c[k], c[(k + 1) % 4])], m2 = mids[Key(c[(k + 2) % 4], c[(k + 3) % 4])];
                add.Add(new NewFace(new[] { c[k], m1, m2, c[(k + 3) % 4] }, f));
                add.Add(new NewFace(new[] { m1, c[(k + 1) % 4], c[(k + 2) % 4], m2 }, f));
            }
            else
            {
                int m = mids[Key(c[k], c[(k + 1) % 3])];
                add.Add(new NewFace(new[] { c[k], m, c[(k + 2) % 3] }, f));
                add.Add(new NewFace(new[] { m, c[(k + 1) % 3], c[(k + 2) % 3] }, f));
            }
        }
        return new Rebuild(cut.Keys.ToList(), add, points, null);
    }

    static IEnumerable<(int, int)> CutSides(int[] face, int k)
    {
        yield return Key(face[k], face[(k + 1) % face.Length]);
        if (face.Length == 4) yield return Key(face[(k + 2) % 4], face[(k + 3) % 4]);
    }

    // ---------- Inset ----------

    /// The selected faces shrunk inward by `width` along their surface (each outline side moves in parallel), with a ring
    /// of quads joining the old outline to the new one. Faces sharing edges inset together, around holes too.
    public static Rebuild Inset(IReadOnlyList<Vector3> pos, IReadOnlyList<int[]> faces, ICollection<int> selected, float width)
    {
        if (selected.Count == 0) return Rebuild.Fail("select faces first");
        var uses = new Dictionary<(int, int), int>();
        foreach (int f in selected)
            for (int k = 0; k < faces[f].Length; k++) uses[Key(faces[f][k], faces[f][(k + 1) % faces[f].Length])] = uses.GetValueOrDefault(Key(faces[f][k], faces[f][(k + 1) % faces[f].Length])) + 1;
        // The outline: selected faces' sides that no other selected face has, in the faces' own turning.
        var next = new Dictionary<int, int>();
        var prev = new Dictionary<int, int>();
        var owner = new List<(int A, int B, int Face)>();
        foreach (int f in selected)
            for (int k = 0; k < faces[f].Length; k++)
            {
                int a = faces[f][k], b = faces[f][(k + 1) % faces[f].Length];
                if (uses[Key(a, b)] != 1) continue;
                if (!next.TryAdd(a, b) || !prev.TryAdd(b, a)) return Rebuild.Fail("the selection touches itself at a corner");
                owner.Add((a, b, f));
            }
        if (next.Count == 0) return Rebuild.Fail("the selection is a closed shape with no outline");
        var normal = new Dictionary<int, Vector3>();
        foreach (int f in selected)
        {
            var n = Newell(pos, faces[f]);
            foreach (int v in faces[f]) if (next.ContainsKey(v)) normal[v] = normal.GetValueOrDefault(v) + n;
        }
        var inner = new Dictionary<int, int>();
        var points = new List<NewPoint>();
        foreach (var (v, b) in next)
        {
            if (!prev.TryGetValue(v, out int a)) return Rebuild.Fail("the selection's outline doesn't close");
            var n = Vector3.Normalize(normal[v]);
            Vector3 in1 = Vector3.Normalize(Vector3.Cross(n, pos[v] - pos[a])), in2 = Vector3.Normalize(Vector3.Cross(n, pos[b] - pos[v]));
            var dir = in1 + in2;
            dir = dir.LengthSquared() < 1e-8f ? in1 : Vector3.Normalize(dir);
            float along = Math.Max(0.25f, Vector3.Dot(dir, in1)); // each side moves in by `width`; sharp corners go at most 4× that
            inner[v] = pos.Count + points.Count;
            points.Add(new NewPoint(pos[v] + dir * (width / along), new[] { (v, 1f) }));
        }
        var all = pos.Concat(points.Select(p => p.P)).ToList();
        var add = new List<NewFace>();
        foreach (int f in selected)
        {
            var shrunk = faces[f].Select(v => inner.TryGetValue(v, out int w) ? w : v).ToArray();
            if (Vector3.Dot(Newell(all, shrunk), Newell(pos, faces[f])) <= 0) return Rebuild.Fail("the inset is too wide for these faces");
            add.Add(new NewFace(shrunk, f));
        }
        foreach (var (a, b, f) in owner)
        {
            // Too wide, the inner outline passes through itself: a side then points backwards.
            if (Vector3.Dot(all[inner[b]] - all[inner[a]], pos[b] - pos[a]) <= 0) return Rebuild.Fail("the inset is too wide for these faces");
            add.Add(new NewFace(new[] { a, b, inner[b], inner[a] }, f));
        }
        return new Rebuild(selected.ToList(), add, points, null);
    }

    // ---------- Bevel ----------

    /// The selected edges become chamfer strips `width` wide on each side. Where bevelled edges meet, each fan of faces
    /// between them gets its own copy of the point, slid along its own edge; where three or more meet, a cap face closes
    /// the corner. A bevelled edge ending at a point with no other bevelled edge cuts the faces there in (like Blender).
    public static Rebuild Bevel(IReadOnlyList<Vector3> pos, IReadOnlyList<int[]> faces, IEnumerable<(int A, int B)> edges, float width)
    {
        var edgeFaces = EdgeFaces(faces);
        var sel = edges.Select(e => Key(e.A, e.B)).Where(e => edgeFaces.TryGetValue(e, out var fs) && fs.Count == 2).ToHashSet();
        if (sel.Count == 0) return Rebuild.Fail("select edges with a face on each side");
        var pointFaces = new Dictionary<int, List<int>>();
        for (int f = 0; f < faces.Count; f++)
            foreach (int v in faces[f])
            {
                if (!pointFaces.TryGetValue(v, out var l)) pointFaces[v] = l = new();
                l.Add(f);
            }
        var points = new List<NewPoint>();
        // How each face's corner at a bevelled point changes: one point in its place (or two, cutting the corner off).
        var corner = new Dictionary<(int Face, int Point), int[]>();
        var endsAt = new HashSet<int>(); // points where a bevel ends inside the plate and the point stays
        int Add(Vector3 p, int from)
        {
            points.Add(new NewPoint(p, new[] { (from, 1f) }));
            return pos.Count + points.Count - 1;
        }
        Vector3 Slide(int u, int w) { var d = pos[w] - pos[u]; return pos[u] + Vector3.Normalize(d) * Math.Min(width, 0.45f * d.Length()); }

        foreach (int u in sel.SelectMany(e => new[] { e.Item1, e.Item2 }).Distinct())
        {
            var around = pointFaces[u];
            var parent = around.ToDictionary(f => f, f => f);
            int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
            foreach (int f in around)
                foreach (int w in Beside(faces[f], u))
                    if (!sel.Contains(Key(u, w)))
                        foreach (int g in edgeFaces[Key(u, w)]) if (g != f && parent.ContainsKey(g)) parent[Find(g)] = Find(f);
            var sectors = around.GroupBy(Find).Select(g => g.ToList()).ToList();
            if (sectors.Count >= 2)
            {
                foreach (var sector in sectors)
                {
                    // Slide along the sector's own edge at u; with none (one face between two bevelled edges) go into
                    // the face along the bisector; with several, along their average.
                    var own = sector.SelectMany(f => Beside(faces[f], u)).Distinct().Where(w => !sel.Contains(Key(u, w))).ToList();
                    Vector3 p;
                    if (own.Count == 1) p = Slide(u, own[0]);
                    else if (own.Count == 0)
                    {
                        var ends = Beside(faces[sector[0]], u).ToArray();
                        Vector3 e1 = Vector3.Normalize(pos[ends[0]] - pos[u]), e2 = Vector3.Normalize(pos[ends[1]] - pos[u]);
                        float half = MathF.Acos(Math.Clamp(Vector3.Dot(e1, e2), -1f, 1f)) / 2;
                        p = pos[u] + Vector3.Normalize(e1 + e2) * (width / Math.Max(0.2f, MathF.Sin(half)));
                    }
                    else p = pos[u] + Vector3.Normalize(own.Aggregate(Vector3.Zero, (s, w) => s + Vector3.Normalize(pos[w] - pos[u]))) * width;
                    int id = Add(p, u);
                    foreach (int f in sector) corner[(f, u)] = new[] { id };
                }
                continue;
            }
            // One bevelled edge ends here, inside the plate: new points on the two faces' other edges at u. The faces
            // either side take them in place of u; the faces beyond those edges get them added; u goes if nothing
            // else keeps it (a box corner).
            var e0 = sel.First(e => e.Item1 == u || e.Item2 == u);
            var sides = edgeFaces[e0];
            var edgeOf = new Dictionary<int, int>(); // face beside the bevel -> its other edge's far point at u
            var at = new Dictionary<int, int>();     // that far point -> the new point on the edge
            foreach (int f in sides)
            {
                int x = Beside(faces[f], u).First(w => Key(u, w) != e0);
                edgeOf[f] = x;
                if (!at.ContainsKey(x)) at[x] = Add(Slide(u, x), u);
                corner[(f, u)] = new[] { at[x] };
            }
            bool cutOff = false;
            foreach (int g in around.Where(g => !sides.Contains(g)))
            {
                // g's corner at u, between its two neighbours there: add the new point on each side it shares.
                int k = Array.IndexOf(faces[g], u);
                int before = faces[g][(k - 1 + faces[g].Length) % faces[g].Length], after = faces[g][(k + 1) % faces[g].Length];
                bool hasBefore = at.ContainsKey(before), hasAfter = at.ContainsKey(after);
                if (hasBefore && hasAfter) { corner[(g, u)] = new[] { at[before], at[after] }; cutOff = true; } // box corner: u is cut off
                else if (hasBefore) corner[(g, u)] = new[] { at[before], u };
                else if (hasAfter) corner[(g, u)] = new[] { u, at[after] };
            }
            // More faces round u than a box corner: u stays, and the strip's end runs through it (else a hole is left
            // between u and the two new points).
            if (!cutOff) endsAt.Add(u);
        }

        int[] Rebuilt(int f) => faces[f].SelectMany(v => corner.TryGetValue((f, v), out var r) ? r : new[] { v }).ToArray();
        var remove = corner.Keys.Select(k => k.Face).Distinct().ToList();
        var shapes = remove.Select(f => (Corners: Rebuilt(f), Source: f)).ToList();
        // A strip along each bevelled edge between its two faces' new corners (a point where the bevel ends in a cut).
        var capSides = new Dictionary<int, List<(int From, int To)>>();
        int Origin(int id) => id < pos.Count ? id : points[id - pos.Count].Blend[0].V;
        foreach (var e in sel)
        {
            var fs = edgeFaces[e];
            int f1 = SideIndex(faces[fs[0]], e) is int k0 && faces[fs[0]][k0] == e.Item1 ? fs[0] : fs[1];
            int f2 = fs[0] == f1 ? fs[1] : fs[0];
            int a = e.Item1, b = e.Item2; // f1 runs a -> b, f2 runs b -> a
            int[] At(int f, int v) => corner.TryGetValue((f, v), out var r) ? r : new[] { v };
            // f1 runs a -> b: the strip runs b -> a along f1's side and a -> b along f2's side.
            var strip = new List<int>();
            strip.AddRange(At(f1, b).Reverse());
            strip.AddRange(At(f1, a).Reverse());
            if (endsAt.Contains(a)) strip.Add(a);
            strip.AddRange(At(f2, a).Reverse());
            strip.AddRange(At(f2, b).Reverse());
            if (endsAt.Contains(b)) strip.Add(b);
            strip = strip.Where((v, i) => v != strip[(i + 1) % strip.Count]).Distinct().ToList();
            if (strip.Count < 3) continue;
            shapes.Add((strip.ToArray(), f1));
            for (int i = 0; i < strip.Count; i++)
            {
                int p = strip[i], q = strip[(i + 1) % strip.Count];
                if (p != q && Origin(p) == Origin(q) && (p >= pos.Count || q >= pos.Count))
                {
                    if (!capSides.TryGetValue(Origin(p), out var l)) capSides[Origin(p)] = l = new();
                    l.Add((q, p)); // the cap runs the other way along the strip's side
                }
            }
        }
        if (shapes.Count == remove.Count) return Rebuild.Fail("a single bevelled edge needs a corner or the plate's edge at one end");
        // Where three or more bevelled edges meet, a cap closes the corner.
        foreach (var (u, sidesAtU) in capSides.Where(c => c.Value.Count >= 3))
        {
            var chain = sidesAtU.GroupBy(s => s.From).ToDictionary(g => g.Key, g => g.First().To);
            var loop = new List<int> { sidesAtU[0].From };
            while (loop.Count <= sidesAtU.Count && chain.TryGetValue(loop[^1], out int to) && to != loop[0]) loop.Add(to);
            if (loop.Count == sidesAtU.Count) shapes.Add((loop.ToArray(), pointFaces[u][0]));
        }
        // Faces that grew past four corners are filled with triangles and quads from their own points.
        var all = pos.Concat(points.Select(p => p.P)).ToList();
        var add = new List<NewFace>();
        foreach (var (c, source) in shapes)
        {
            if (c.Length <= 4) { add.Add(new NewFace(c, source)); continue; }
            var filled = Fill.Region(all, c.ToList(), new List<List<int>>(), Newell(all, c), null);
            if (filled.Count == 0) return Rebuild.Fail("a face around the bevel couldn't be rebuilt");
            add.AddRange(filled.Select(x => new NewFace(x, source)));
        }
        return new Rebuild(remove, add, points, null);
    }

    /// The two corners next to `u` in a face.
    static IEnumerable<int> Beside(int[] face, int u)
    {
        int k = Array.IndexOf(face, u);
        yield return face[(k + 1) % face.Length];
        yield return face[(k - 1 + face.Length) % face.Length];
    }

    // ---------- Proportional editing ----------

    /// For each point within `radius` of the moved ones: the moved points it follows and how much. A smooth falloff with
    /// the distance to the nearest moved point, shared between nearby moved points by inverse distance squared.
    public static Dictionary<int, (int[] Moved, float[] Weights)> Falloff(IReadOnlyList<Vector3> pos, ICollection<int> moved, float radius)
    {
        var result = new Dictionary<int, (int[], float[])>();
        if (radius <= 0) return result;
        var movers = moved.ToList();
        for (int v = 0; v < pos.Count; v++)
        {
            if (moved.Contains(v)) continue;
            var near = movers.Select(m => (M: m, D: Vector3.Distance(pos[v], pos[m]))).Where(x => x.D < radius).ToList();
            if (near.Count == 0) continue;
            float x = near.Min(n => n.D) / radius;
            float fall = 1 - x * x * (3 - 2 * x); // smooth: 1 at a moved point, 0 at the radius
            var inverse = near.Select(n => 1 / (n.D * n.D + 1e-8f)).ToArray();
            float sum = inverse.Sum();
            result[v] = (near.Select(n => n.M).ToArray(), inverse.Select(w => fall * w / sum).ToArray());
        }
        return result;
    }

    // ---------- Select linked flat faces ----------

    /// Faces joined to the seeds through shared edges, each within `maxAngle` degrees of the face it's reached from
    /// (Blender's Select Linked Flat Faces).
    public static HashSet<int> LinkedFlat(IReadOnlyList<Vector3> pos, IReadOnlyList<int[]> faces, IEnumerable<int> seeds, float maxAngle)
    {
        var edgeFaces = EdgeFaces(faces);
        float cos = MathF.Cos(maxAngle * MathF.PI / 180);
        var normals = faces.Select(f => Newell(pos, f) is var n && n.LengthSquared() > 0 ? Vector3.Normalize(n) : n).ToArray();
        var found = new HashSet<int>(seeds);
        var queue = new Queue<int>(found);
        while (queue.Count > 0)
        {
            int f = queue.Dequeue();
            for (int k = 0; k < faces[f].Length; k++)
                foreach (int g in edgeFaces[Key(faces[f][k], faces[f][(k + 1) % faces[f].Length])])
                    if (!found.Contains(g) && Vector3.Dot(normals[f], normals[g]) >= cos) { found.Add(g); queue.Enqueue(g); }
        }
        return found;
    }

    // ---------- Mirror ----------

    /// For each of `points`, the point at its mirrored position across x = 0 (the game's Mirror plane) within
    /// `tolerance` metres, if there is one. A point on the plane is its own twin.
    public static Dictionary<int, int> Twins(IReadOnlyList<Vector3> pos, IEnumerable<int> points, float tolerance)
    {
        float cell = Math.Max(tolerance, 1e-5f);
        (int, int, int) Cell(Vector3 p) => ((int)MathF.Round(p.X / cell), (int)MathF.Round(p.Y / cell), (int)MathF.Round(p.Z / cell));
        var grid = new Dictionary<(int, int, int), List<int>>();
        for (int v = 0; v < pos.Count; v++)
        {
            if (!grid.TryGetValue(Cell(pos[v]), out var here)) grid[Cell(pos[v])] = here = new();
            here.Add(v);
        }
        var twins = new Dictionary<int, int>();
        foreach (int v in points)
        {
            var m = new Vector3(-pos[v].X, pos[v].Y, pos[v].Z);
            var (x, y, z) = Cell(m);
            int found = -1;
            for (int dx = -1; dx <= 1 && found < 0; dx++) for (int dy = -1; dy <= 1 && found < 0; dy++) for (int dz = -1; dz <= 1 && found < 0; dz++)
                if (grid.TryGetValue((x + dx, y + dy, z + dz), out var near))
                    foreach (int w in near) if (Vector3.Distance(pos[w], m) <= tolerance) { found = w; break; }
            if (found >= 0) twins[v] = found;
        }
        return twins;
    }

    // ---------- helpers ----------

    static Dictionary<(int, int), List<int>> EdgeFaces(IReadOnlyList<int[]> faces)
    {
        var map = new Dictionary<(int, int), List<int>>();
        for (int f = 0; f < faces.Count; f++)
            for (int k = 0; k < faces[f].Length; k++)
            {
                var key = Key(faces[f][k], faces[f][(k + 1) % faces[f].Length]);
                if (!map.TryGetValue(key, out var l)) map[key] = l = new();
                l.Add(f);
            }
        return map;
    }

    /// The corner starting the face's side along `edge`, or -1.
    static int SideIndex(int[] face, (int, int) edge)
    {
        for (int k = 0; k < face.Length; k++) if (Key(face[k], face[(k + 1) % face.Length]) == edge) return k;
        return -1;
    }

    static Vector3 Newell(IReadOnlyList<Vector3> pos, IReadOnlyList<int> loop)
    {
        var n = Vector3.Zero;
        for (int k = 0; k < loop.Count; k++) n += Vector3.Cross(pos[loop[k]], pos[loop[(k + 1) % loop.Count]]);
        return n;
    }

    static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);
}
