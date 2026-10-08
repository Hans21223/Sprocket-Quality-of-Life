using System.Numerics;
using SprocketQoL;

/// Bridge, Circle, Fix mirror and mirrored Merge points on small shapes with known answers (no game or blueprints needed).
static class ToolTests
{
    static int checks;
    static void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }

    static Vector3 Nw(IReadOnlyList<Vector3> p, IReadOnlyList<int> f) { var n = Vector3.Zero; for (int k = 0; k < f.Count; k++) n += Vector3.Cross(p[f[k]], p[f[(k + 1) % f.Count]]); return n; }

    static (List<Vector3> Pos, List<int[]> Faces) Applied(List<Vector3> pos, List<int[]> faces, MeshPlans.Rebuild r)
    {
        var p = pos.Concat(r.Points.Select(x => x.P)).ToList();
        var gone = r.Remove.ToHashSet();
        return (p, faces.Where((_, i) => !gone.Contains(i)).Concat(r.Add.Select(a => a.Corners)).ToList());
    }

    /// Every side used at most twice, and when twice, run opposite ways (faces turned consistently); with `closed`, every
    /// side used exactly twice (no open edge).
    static bool Consistent(List<int[]> faces, bool closed = false)
    {
        var sides = new Dictionary<(int, int), int>();
        foreach (var f in faces) for (int k = 0; k < f.Length; k++) sides[(f[k], f[(k + 1) % f.Length])] = sides.GetValueOrDefault((f[k], f[(k + 1) % f.Length])) + 1;
        return sides.All(s => s.Value == 1 && (closed ? sides.GetValueOrDefault((s.Key.Item2, s.Key.Item1)) == 1 : sides.GetValueOrDefault((s.Key.Item2, s.Key.Item1)) <= 1));
    }

    static double Volume(List<Vector3> p, List<int[]> faces) => faces.Sum(f => Enumerable.Range(1, f.Length - 2).Sum(k => Vector3.Dot(p[f[0]], Vector3.Cross(p[f[k]], p[f[k + 1]])) / 6.0));
    static double Area(List<Vector3> p, IEnumerable<int[]> fs) => fs.Sum(f => (double)Nw(p, f).Length() / 2);

    public static void Run()
    {
        Bridge();
        Circle();
        FixMirror();
        MergePoints();
        Validation();
        Console.WriteLine($"TOOL_TESTS_OK: {checks} checks (bridge, circle, fix mirror, mirrored merge)");
    }

    static void Validation()
    {
        var points = new List<Vector3> { Vector3.Zero, Vector3.UnitX, Vector3.UnitY };
        var faces = new List<int[]> { new[] { 0, 1, 2 } };
        var nan = new MeshPlans.Rebuild(new() { 0 }, new() { new(new[] { 3, 1, 2 }, 0) },
            new() { new(new Vector3(float.NaN, 0, 0), new[] { (0, 1f) }) }, null);
        Check(MeshPlans.Check(points, faces, nan) != null, "mesh safety rejects non-finite added points");
        var duplicate = new MeshPlans.Rebuild(new(), new() { new(new[] { 0, 1, 2 }, 0) }, new(), null);
        Check(MeshPlans.Check(points, faces, duplicate) != null, "mesh safety rejects duplicate faces facing the same way");
        var cube = Enumerable.Range(0, 8).Select(i => new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1)).ToList();
        var shell = new List<int[]> { new[] { 0,1,5,4 }, new[] { 2,3,7,6 }, new[] { 0,2,6,4 }, new[] { 1,3,7,5 }, new[] { 0,1,3,2 }, new[] { 4,5,7,6 } };
        var crack = new MeshPlans.Rebuild(new() { 0 }, new(), new(), null);
        Check(MeshPlans.Check(cube, shell, crack) != null, "mesh safety rejects a missing face at the origin");
        Check(MeshPlans.Check(cube.Select(p => p + new Vector3(100000, 0, 0)).ToList(), shell, crack) != null,
            "mesh safety rejects the same crack far from the origin");
        var missingSource = duplicate with { Add = new() { new(new[] { 0,1,2 }, 8) } };
        Check(MeshPlans.Check(points,faces,missingSource)!=null,"missing source face is rejected without an index error");
        var pentagonPoints = Enumerable.Range(0, 5).Select(i => new Vector3(MathF.Cos(i * MathF.Tau / 5), MathF.Sin(i * MathF.Tau / 5), 0)).ToList();
        var ngon = new MeshPlans.Rebuild(new() { 0 }, new() { new(new[] { 0, 1, 2, 3, 4 }, 0) }, new(), null);
        Check(MeshPlans.Check(pentagonPoints, faces, ngon, gaps: false)?.Contains("more than four corners") == true,
            "reject otherwise valid pentagon before native clone/save would fail");
        Check(MeshPlans.Check(pentagonPoints, new List<int[]> { new[] { 0, 1, 2, 3, 4 } }, new(new(), new(), new(), null), gaps: false) != null,
            "reject existing n-gon before accessing unsupported native corner arrays");
        Check(MeshPlans.Folds(points,faces,new Dictionary<int,Vector3> { [0] = new(float.PositiveInfinity,0,0) })!=null,"movement rejects non-finite points");
        Check(MeshPlans.Inset(points,faces,new[]{0},float.NaN).Why!=null,"inset rejects a non-finite width");
        Check(MeshPlans.Bevel(cube,shell,new[]{(2,3),(0,7)},.1f).Why!=null,"bevel rejects an invalid mixed selection atomically");
        Check(MeshPlans.MergePoints(points,faces,new[]{-1},0).Why!=null,"merge rejects stale point selections");
        foreach(var outer in new[]{new List<int>(),new(){0},new(){0,1},new(){0,1,9}})
            Check(Fill.Region(points.ToList(),outer,new(),Vector3.UnitZ,null).Count==0,"fill rejects empty or invalid boundaries safely");
        Check(Fill.Region(points.ToList(),new(){0,1,2},new(),Vector3.Zero,null).Count==0,"fill rejects a missing plane normal");
        var outside=points.Concat(new[]{new Vector3(2,2,0),new(3,2,0),new(2,3,0)}).ToList();
        Check(Fill.Region(outside,new(){0,1,2},new(){new(){3,4,5}},Vector3.UnitZ,null).Count==0,"fill never erases or bridges a hole outside its plate");
        var groups=FaceMerge.Plan(points,faces,new HashSet<int>{9},new HashSet<(int,int)>(),FaceMerge.SidePoints.Keep);
        Check(groups.Count==1&&groups[0].Why!=null,"merge faces rejects a stale selection safely");

        // Many separate selected patches: each merge stays local while outline lookup scales by shared vertices.
        var meshPoints=new List<Vector3>(); var patches=new List<int[]>();
        const int count=600;
        for(int i=0;i<count;i++)
        {
            int k=meshPoints.Count; float x=i*3;
            meshPoints.AddRange(new[]{new Vector3(x,0,0),new(x+.5f,0,0),new(x+1,0,0),new(x,1,0),new(x+.5f,1,0),new(x+1,1,0)});
            patches.Add(new[]{k,k+1,k+4,k+3}); patches.Add(new[]{k+1,k+2,k+5,k+4});
        }
        var clock=System.Diagnostics.Stopwatch.StartNew();
        var merged=FaceMerge.Plan(meshPoints,patches,Enumerable.Range(0,patches.Count).ToHashSet(),new HashSet<(int,int)>(),FaceMerge.SidePoints.Keep);
        Check(merged.Count==count&&merged.All(g=>g.Why==null&&g.NewFaces.Count==1&&g.NewFaces[0].Length==4&&g.Removed.Count==2),"large face selection merges every patch independently and removes only its straight boundary points");
        Console.WriteLine($"  FACE_MERGE_BENCHMARK: {count} patches / {patches.Count} faces in {clock.ElapsedMilliseconds} ms");
    }

    static void Bridge()
    {
        // Two unit squares side by side with a gap, both facing up (+z): bridge the edges facing each other.
        var pos = new List<Vector3> { new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0), new(2, 0, 0), new(3, 0, 0), new(3, 1, 0), new(2, 1, 0) };
        var faces = new List<int[]> { new[] { 0, 1, 2, 3 }, new[] { 4, 5, 6, 7 } };
        var one = MeshPlans.Bridge(pos, faces, new[] { (1, 2), (4, 7) }, 0, 0);
        Check(one.Why == null && one.Add.Count == 1 && one.Points.Count == 0, $"bridge a gap: one face ({one.Why})");
        var (p1, f1) = Applied(pos, faces, one);
        Check(Consistent(f1) && Nw(p1, f1[2]).Z > 0 && Math.Abs(Area(p1, f1) - 3) < 1e-5, "bridge a gap: turned like the plates (up), area 3");
        Check(MeshPlans.Check(pos, faces, one, gaps: false) == null, $"bridge a gap: passes the check ({MeshPlans.Check(pos, faces, one, gaps: false)})");
        // Three cuts, smooth: flat plates run on straight, so the rows are evenly across the gap.
        var cut = MeshPlans.Bridge(pos, faces, new[] { (1, 2), (4, 7) }, 3, 1);
        var (pc, fc) = Applied(pos, faces, cut);
        var xs = cut.Points.Select(x => x.P.X).Distinct().OrderBy(x => x).ToList();
        Check(cut.Why == null && cut.Add.Count == 4 && cut.Points.Count == 6 && xs.Count == 3 && Math.Abs(xs[0] - 1.25f) < 1e-5 && Math.Abs(xs[2] - 1.75f) < 1e-5,
              $"bridge with 3 cuts between flat plates: 4 quads, rows at 1.25, 1.5, 1.75 ({cut.Why}, {string.Join(" ", xs)})");
        Check(Consistent(fc) && fc.Skip(2).All(f => Nw(pc, f).Z > 0), "bridge with cuts: every face turned up");

        // The Blender picture: a plate standing up and one lying down, bridged smooth into a rounded corner.
        // Standing: z = 0, y 1..2, facing +z; lying: y = 0, z 1..2, facing +y (both facing into the L).
        var l = new List<Vector3> { new(0, 1, 0), new(1, 1, 0), new(1, 2, 0), new(0, 2, 0), new(0, 0, 1), new(1, 0, 1), new(1, 0, 2), new(0, 0, 2) };
        var lf = new List<int[]> { new[] { 0, 1, 2, 3 }, new[] { 4, 7, 6, 5 } };
        Check(Nw(l, lf[0]).Z > 0 && Nw(l, lf[1]).Y > 0, "test L: plates face into the L");
        var round = MeshPlans.Bridge(l, lf, new[] { (0, 1), (4, 5) }, 7, 1);
        var (pr, fr) = Applied(l, lf, round);
        Check(round.Why == null && round.Add.Count == 8 && Consistent(fr), $"bridge an L smooth: 8 rows, faces turned consistently ({round.Why})");
        Check(MeshPlans.Check(l, lf, round, gaps: false) == null, $"bridge an L smooth: passes the check ({MeshPlans.Check(l, lf, round, gaps: false)})");
        // The new points sweep round the corner (centre y = 1, z = 1, radius 1), not straight across it.
        var mid = round.Points.Where(x => x.P.X == 0).OrderBy(x => x.P.Z).ElementAt(3).P; // the middle of 7 rows
        float fromCentre = Vector2.Distance(new Vector2(mid.Y, mid.Z), new Vector2(1, 1));
        Check(Math.Abs(fromCentre - 1) < 0.06f && mid.Y < 0.45f && mid.Z < 0.45f, $"bridge an L smooth: rounded like a quarter circle ({mid}, {fromCentre:0.000} from the centre)");
        // Leaving the standing plate straight down first (the plate runs on), arriving flat into the lying one.
        var firstRow = round.Points.Where(x => x.P.X == 0).OrderBy(x => x.P.Z).First().P;
        var lastRow = round.Points.Where(x => x.P.X == 0).OrderBy(x => x.P.Z).Last().P;
        Check(firstRow.Z < 0.05f && firstRow.Y < 1 && lastRow.Y < 0.05f && lastRow.Z < 1, $"bridge an L smooth: carries on from each plate ({firstRow}, {lastRow})");
        var straight = MeshPlans.Bridge(l, lf, new[] { (0, 1), (4, 5) }, 1, 0);
        Check(straight.Points.All(x => Math.Abs(x.P.Y - 0.5f) < 1e-5 && Math.Abs(x.P.Z - 0.5f) < 1e-5), "bridge an L straight: the row halfway along the straight line");
        // Plates facing opposite ways can't be joined by one strip turned consistently.
        var flippedL = new List<int[]> { lf[0], lf[1].Reverse().ToArray() };
        Check(MeshPlans.Bridge(l, flippedL, new[] { (0, 1), (4, 5) }, 3, 1).Why?.Contains("opposite") == true, "bridge plates facing opposite ways: says to flip one");

        // Two square loops, one above the other, capped: bridged into a closed box of volume 1, whichever way and from
        // whichever point the second loop is listed.
        var box = new List<Vector3> { new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0), new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1) };
        var caps = new List<int[]> { new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 } }; // bottom faces down, top faces up
        foreach (var top in new[] { new[] { (4, 5), (5, 6), (6, 7), (7, 4) }, new[] { (6, 5), (7, 6), (4, 7), (5, 4) } })
        {
            var tube = MeshPlans.Bridge(box, caps, new[] { (0, 1), (1, 2), (2, 3), (3, 0) }.Concat(top), 0, 0);
            var (pt, ft) = Applied(box, caps, tube);
            Check(tube.Why == null && tube.Add.Count == 4 && Consistent(ft, closed: true) && Math.Abs(Volume(pt, ft) - 1) < 1e-6,
                  $"bridge two loops: a closed box, volume {Volume(pt, ft):0.000000} ({tube.Why})");
        }
        var tube4 = MeshPlans.Bridge(box, caps, new[] { (0, 1), (1, 2), (2, 3), (3, 0), (4, 5), (5, 6), (6, 7), (7, 4) }, 2, 0);
        var (p4, f4) = Applied(box, caps, tube4);
        Check(tube4.Add.Count == 12 && Consistent(f4, closed: true) && Math.Abs(Volume(p4, f4) - 1) < 1e-6, "bridge two loops with 2 cuts: closed, volume 1");

        // Refusals, each saying why.
        Check(MeshPlans.Bridge(pos, faces, new[] { (1, 2) }, 0, 0).Why?.Contains("two chains") == true, "bridge one chain: asks for two");
        Check(MeshPlans.Bridge(pos, faces, new[] { (1, 2), (4, 7), (0, 3) }, 0, 0).Why?.Contains("two chains") == true, "bridge three chains: asks for two");
        Check(MeshPlans.Bridge(pos, faces, new[] { (1, 2), (4, 7), (7, 6) }, 0, 0).Why?.Contains("as many points") == true, "bridge 2 points to 3: says they differ");
        Check(MeshPlans.Bridge(pos, faces, new[] { (1, 2), (2, 3), (2, 0), (4, 7) }, 0, 0).Why?.Contains("branch") == true, "bridge edges that branch: says so");
        var shared = new List<int[]> { new[] { 0, 1, 2, 3 }, new[] { 1, 4, 7, 2 }, new[] { 4, 5, 6, 7 } };
        Check(MeshPlans.Bridge(pos, shared, new[] { (1, 2), (4, 7) }, 0, 0).Why?.Contains("both sides") == true, "bridge from an edge with faces on both sides: refused");

        // Mirror on: the same bridge on the other side, point for point, and the result is its own mirror image.
        var right = pos.Select(x => x + new Vector3(0.5f, 0, 0)).ToList();
        var both = right.Concat(right.Select(x => new Vector3(-x.X, x.Y, x.Z))).ToList();
        var bothFaces = faces.Concat(faces.Select(f => f.Select(i => i + 8).Reverse().ToArray())).ToList();
        var twins = MeshPlans.Twins(both, Enumerable.Range(0, both.Count), 1e-4f);
        var mirrored = MeshPlans.Bridge(both, bothFaces, new[] { (1, 2), (4, 7) }, 2, 1, twins);
        var (pm, fm) = Applied(both, bothFaces, mirrored);
        var allTwins = MeshPlans.Twins(pm, Enumerable.Range(0, pm.Count), 1e-4f);
        Check(mirrored.Why == null && mirrored.Add.Count == 6 && mirrored.Points.Count == 8 && allTwins.Count == pm.Count && Consistent(fm),
              $"bridge with Mirror: both sides bridged, every point has its mirror image ({mirrored.Why}, {mirrored.Add.Count} faces)");
        Check(MeshPlans.Check(both, bothFaces, mirrored, gaps: false) == null, $"bridge with Mirror: passes the check ({MeshPlans.Check(both, bothFaces, mirrored, gaps: false)})");
        var bothSelected = MeshPlans.Bridge(both, bothFaces, new[] { (1, 2), (4, 7), (9, 10), (12, 15) }, 2, 1, twins);
        Check(bothSelected.Why == null && bothSelected.Add.Count == 6, $"bridge with Mirror, both sides selected: the same 6 faces ({bothSelected.Why})");
        // Across the middle: bridging an edge to its own mirror image is its own mirror image, made once.
        var across = MeshPlans.Bridge(both, bothFaces, new[] { (0, 3), (8, 11) }, 1, 0, twins);
        Check(across.Why == null && across.Add.Count == 2, $"bridge across the middle with Mirror: made once ({across.Why}, {across.Add.Count} faces)");
    }

    static void Circle()
    {
        // Eight points round a squashed, wobbly ring in a tilted plane.
        var turn = Matrix4x4.CreateFromYawPitchRoll(0.4f, 1.1f, -0.3f) * Matrix4x4.CreateTranslation(1, 2, 3);
        var pos = Enumerable.Range(0, 8).Select(k => { float a = k * MathF.Tau / 8 + (k % 2 == 0 ? 0.08f : -0.05f); return Vector3.Transform(new Vector3(1.4f * MathF.Cos(a), 0.8f * MathF.Sin(a), k % 3 == 0 ? 0.02f : -0.01f), turn); }).ToList();
        var moved = MeshPlans.Circle(pos, Enumerable.Range(0, 8).ToList());
        Check(moved.Count == 8, "circle: every point moved");
        var c = moved.Values.Aggregate(Vector3.Zero, (s, p) => s + p) / 8;
        var r = moved.Values.Select(p => Vector3.Distance(p, c)).ToList();
        Check(r.Max() - r.Min() < 1e-4f, $"circle: all as far from the middle ({r.Min():0.0000}..{r.Max():0.0000})");
        var n = Vector3.Normalize(Nw(moved.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList(), Enumerable.Range(0, 8).ToList()));
        Check(moved.Values.All(p => Math.Abs(Vector3.Dot(p - c, n)) < 1e-4f), "circle: all on one plane");
        var ordered = Enumerable.Range(0, 8).Select(k => moved[k]).ToList();
        var gaps = Enumerable.Range(0, 8).Select(k => Vector3.Distance(ordered[k], ordered[(k + 1) % 8])).ToList();
        Check(gaps.Max() - gaps.Min() < 1e-4f, $"circle: evenly apart, in the same order ({gaps.Min():0.0000}..{gaps.Max():0.0000})");
        Check(Enumerable.Range(0, 8).All(k => Vector3.Distance(pos[k], moved[k]) < 0.45f), "circle: each point stays near where it was");
        var sized = MeshPlans.Circle(pos, Enumerable.Range(0, 8).ToList(), 2);
        Check(sized.Values.All(p => Math.Abs(Vector3.Distance(p, c) - 2) < 1e-3f), "circle with a set radius: that radius");
        Check(MeshPlans.Circle(new List<Vector3> { new(0, 0, 0), new(1, 0, 0), new(2, 0, 0), new(3, 0, 0) }, new[] { 0, 1, 2, 3 }).Count == 0, "circle: points along a line are left alone");
        Check(MeshPlans.Circle(pos, new[] { 0, 1 }).Count == 0, "circle: two points are left alone");

        // Elongated (10:1 aspect ratio) loop on a tilted plate (where power iteration previously tilted 3° into the plate)
        var tiltedTurn = Matrix4x4.CreateFromYawPitchRoll(0.7f, -0.5f, 0.3f) * Matrix4x4.CreateTranslation(2, -1, 4);
        var expectedNormal = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, tiltedTurn));
        var platePoints = Enumerable.Range(0, 12).Select(k =>
        {
            float a = k * MathF.Tau / 12;
            return Vector3.Transform(new Vector3(2.5f * MathF.Cos(a), 0.25f * MathF.Sin(a), 0), tiltedTurn);
        }).ToList();
        var plateCircle = MeshPlans.Circle(platePoints, Enumerable.Range(0, 12).ToList());
        Check(plateCircle.Count == 12, "circle elongated: all 12 points placed");
        var plateC = plateCircle.Values.Aggregate(Vector3.Zero, (s, p) => s + p) / 12;
        var plateN = Vector3.Normalize(Nw(plateCircle.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList(), Enumerable.Range(0, 12).ToList()));
        float normalDot = Math.Abs(Vector3.Dot(plateN, expectedNormal));
        Check(normalDot > 0.9999f, $"circle elongated: normal matches plate normal exactly (dot={normalDot:0.00000})");
        Check(plateCircle.Values.All(p => Math.Abs(Vector3.Dot(p - plateC, expectedNormal)) < 1e-4f), "circle elongated: all points flush with the plate plane");

        // Explicit normal supplied
        var explicitCircle = MeshPlans.Circle(platePoints, Enumerable.Range(0, 12).ToList(), normal: expectedNormal);
        Check(explicitCircle.Values.All(p => Math.Abs(Vector3.Dot(p - plateC, expectedNormal)) < 1e-5f), "circle explicit normal: 100% flush with provided plate normal");
    }

    static void FixMirror()
    {
        var pos = new List<Vector3>
        {
            new(0.5f, 1, 2), new(-0.502f, 1.001f, 2),   // a pair 2 mm out
            new(0.002f, 3, 1),                          // nearly on the middle
            new(0.8f, 0, 0),                            // no mirror image anywhere
            new(0.3f, 0.5f, 0.5f), new(-0.3f, 0.5f, 0.5f), // an exact pair
            new(1, 1, 1), new(-1.003f, 1, 1), new(-1.001f, 1, 1), // two candidates: the nearer one is the pair
        };
        var all = Enumerable.Range(0, pos.Count).ToList();
        var (half, unmatched) = MeshPlans.FixMirror(pos, all, 0.005f, MeshPlans.MirrorKeep.Halfway);
        Check(Vector3.Distance(half[0], new Vector3(0.501f, 1.0005f, 2)) < 1e-6f && Vector3.Distance(half[1], new Vector3(-0.501f, 1.0005f, 2)) < 1e-6f, $"fix mirror: a pair meets halfway ({half[0]}, {half[1]})");
        Check(half[2] == new Vector3(0, 3, 1), "fix mirror: a point by the middle goes onto it");
        Check(unmatched.OrderBy(v => v).SequenceEqual(new[] { 3, 7 }) && !half.ContainsKey(3) && !half.ContainsKey(7), "fix mirror: points with no mirror image are reported, not moved");
        Check(!half.ContainsKey(4) && !half.ContainsKey(5), "fix mirror: an exact pair isn't touched");
        Check(half.ContainsKey(8) && half[8] == new Vector3(-1.0005f, 1, 1), $"fix mirror: the nearer candidate is the pair ({(half.TryGetValue(8, out var h8) ? h8 : default)})");
        var (keepRight, _) = MeshPlans.FixMirror(pos, all, 0.005f, MeshPlans.MirrorKeep.Right);
        Check(!keepRight.ContainsKey(0) && keepRight[1] == new Vector3(-0.5f, 1, 2), "fix mirror, right side kept: the left point moves to match");
        var (keepLeft, _) = MeshPlans.FixMirror(pos, all, 0.005f, MeshPlans.MirrorKeep.Left);
        Check(!keepLeft.ContainsKey(1) && keepLeft[0] == new Vector3(0.502f, 1.001f, 2), "fix mirror, left side kept: the right point moves to match");
        var (some, _) = MeshPlans.FixMirror(pos, new[] { 1 }, 0.005f, MeshPlans.MirrorKeep.Halfway);
        Check(some.Count == 2 && some.ContainsKey(0), "fix mirror from one selected point: its partner moves too");
        Check(MeshPlans.Twins(pos.Select((p, i) => half.TryGetValue(i, out var m) ? m : p).ToList(), new[] { 0 }, 0.0003f).ContainsKey(0), "fix mirror: the game's Mirror (0.3 mm) pairs them afterwards");
    }

    static void MergePoints()
    {
        // Two quads side by side sharing the edge 1-4: merging 1 and 4 turns both into triangles, still joined.
        var pos = new List<Vector3> { new(0, 0, 0), new(1, 0, 0), new(2, 0, 0), new(0, 1, 0), new(1, 1, 0), new(2, 1, 0) };
        var faces = new List<int[]> { new[] { 0, 1, 4, 3 }, new[] { 1, 2, 5, 4 } };
        var plan = MeshPlans.MergePoints(pos, faces, new[] { 4 }, 1);
        var (p, f) = Applied(pos, faces, plan);
        Check(plan.Why == null && f.Count == 2 && f.All(x => x.Length == 3 && x.Contains(1) && !x.Contains(4)) && Consistent(f), $"merge two points: two triangles, turned the same way ({plan.Why})");
        var moved = pos.ToList();
        moved[1] = new Vector3(1, 0.5f, 0);
        // A merge can lengthen the plate's open outline (it pinches here), so the check leaves gaps out, as for the tool.
        Check(MeshPlans.Check(moved, faces, plan, gaps: false) == null, $"merge two points: passes the check ({MeshPlans.Check(moved, faces, plan, gaps: false)})");
        // Opposite corners of one quad would pinch it.
        Check(MeshPlans.MergePoints(pos, faces, new[] { 4 }, 0).Why?.Contains("pinch") == true, "merge opposite corners of a face: refused");
        // Merging all of a triangle's corners takes it away.
        var tri = MeshPlans.MergePoints(pos, new List<int[]> { new[] { 0, 1, 3 } }, new[] { 1, 3 }, 0);
        Check(tri.Why == null && tri.Remove.Count == 1 && tri.Add.Count == 0, "merge a whole triangle: it goes");
    }
}
