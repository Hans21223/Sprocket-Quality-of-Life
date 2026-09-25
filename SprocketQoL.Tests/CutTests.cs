using System.Numerics;
using System.Text.Json.Nodes;
using SprocketQoL;

/// Boolean cut tests: holes and pockets in a box with known answers, rivets, and real cuts on the saved tanks.
static class CutTests
{
    static int checks;
    static void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }

    static Vector3[] MeshVerts(JsonObject mesh)
    {
        var v = mesh["vertices"]!.AsArray().Select(x => MeshCut.F(x)).ToArray();
        return Enumerable.Range(0, v.Length / 3).Select(i => new Vector3(v[3 * i], v[3 * i + 1], v[3 * i + 2])).ToArray();
    }
    static List<int[]> MeshFaces(JsonObject mesh) => mesh["faces"]!.AsArray().Select(f => f!["v"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray()).ToList();
    static double MeshArea(JsonObject mesh) { var vs = MeshVerts(mesh); return MeshFaces(mesh).Sum(f => HoleRing.Normal(f.Select(i => vs[i]).ToList()).Length() / 2.0); }
    static double Volume(JsonObject mesh) { var vs = MeshVerts(mesh); return MeshFaces(mesh).Sum(f => Enumerable.Range(1, f.Length - 2).Sum(k => Vector3.Dot(vs[f[0]], Vector3.Cross(vs[f[k]], vs[f[k + 1]])) / 6.0)); }
    static (int, int) EdgeKey(int a, int b) => a < b ? (a, b) : (b, a);
    static Dictionary<(int, int), int> EdgeUses(JsonObject mesh)
    {
        var uses = new Dictionary<(int, int), int>();
        foreach (var v in MeshFaces(mesh)) for (int k = 0; k < v.Length; k++) { var key = EdgeKey(v[k], v[(k + 1) % v.Length]); uses[key] = uses.GetValueOrDefault(key) + 1; }
        return uses;
    }
    static double CrowdedLength(JsonObject mesh) { var vs = MeshVerts(mesh); return EdgeUses(mesh).Where(u => u.Value > 2).Sum(u => Vector3.Distance(vs[u.Key.Item1], vs[u.Key.Item2])); }

    /// Structure checks every cut result must pass. `crowdedBefore`: total length of edges with 3+ faces before (inside walls).
    static void CheckMesh(JsonObject mesh, string what, double crowdedBefore)
    {
        int nv = MeshVerts(mesh).Length;
        var e = mesh["edges"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray();
        var edges = Enumerable.Range(0, e.Length / 2).Select(i => EdgeKey(e[2 * i], e[2 * i + 1])).ToList();
        Check(edges.Count == mesh["edgeFlags"]!.AsArray().Count, what + ": one flag per edge");
        Check(edges.Distinct().Count() == edges.Count && e.All(i => i >= 0 && i < nv), what + ": edges unique and valid");
        var set = edges.ToHashSet();
        foreach (var f in mesh["faces"]!.AsArray())
        {
            var v = f!["v"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray();
            Check(v.Length is 3 or 4 && v.Distinct().Count() == v.Length && v.All(i => i >= 0 && i < nv), what + ": faces are triangles or quads with real corners");
            Check(f["t"]!.AsArray().Count == v.Length, what + ": a thickness per corner");
            ulong te = (ulong)MeshCut.L(f["te"]);
            for (int k = 0; k < v.Length; k++)
            {
                Check(set.Contains(EdgeKey(v[k], v[(k + 1) % v.Length])), what + ": every face edge is in the edge list");
                int r = (int)((te >> (16 * k)) & 0xFFFF);
                Check(r == 0 || r == 0xFFFF || (r < edges.Count && (edges[r].Item1 == v[k] || edges[r].Item2 == v[k])), what + ": each corner's thicken edge touches it");
            }
        }
        Check(CrowdedLength(mesh) <= crowdedBefore + 1e-4, $"{what}: no new edges shared by more than two faces");
    }

    static MeshCut.Solid SolidOf(JsonObject mesh, Func<Vector3, Vector3> place, float thickness = 5)
    {
        var vs = MeshVerts(mesh).Select(place).ToList();
        var faces = MeshFaces(mesh);
        return new MeshCut.Solid(vs, faces, faces.Select(f => f.Select(_ => thickness).ToArray()).ToList(), faces.Select(f => f.Select(_ => (byte)1).ToArray()).ToList());
    }
    static List<(Vector3 N, float D)> PlanesOf(MeshCut.Solid s) => s.Faces.Select(f =>
    {
        var n = Vector3.Normalize(HoleRing.Normal(f.Select(i => s.Verts[i]).ToList()));
        return (n, Vector3.Dot(n, s.Verts[f[0]]));
    }).ToList();
    static Vector3 RivetPos(JsonObject meshData, JsonObject node)
    {
        var tri = node["faceOffset"]!.GetValue<int>() switch { 2 => new[] { 2, 3, 0 }, -1 => new[] { 2, 3, 1 }, -2 => new[] { 3, 0, 1 }, _ => new[] { 0, 1, 2 } };
        var vs = MeshVerts(meshData["mesh"]!.AsObject());
        var f = MeshFaces(meshData["mesh"]!.AsObject())[node["face"]!.GetValue<int>()];
        return MeshCut.F(node["u"]) * vs[f[tri[0]]] + MeshCut.F(node["v"]) * vs[f[tri[1]]] + MeshCut.F(node["w"]) * vs[f[tri[2]]];
    }

    static readonly JsonObject Tool16 = AddonEdits.Cylinder(0.1f, 0.4f, 16, 5).MeshData["mesh"]!.AsObject();
    static readonly double Hole16 = 0.5 * 16 * 0.01 * Math.Sin(2 * Math.PI / 16), Rim16 = 16 * 2 * 0.1 * Math.Sin(Math.PI / 16);
    static readonly Matrix4x4 Top = Matrix4x4.CreateTranslation(0.05f, 0.8f, 0.02f);
    static readonly Matrix4x4 Through = Matrix4x4.CreateScale(1, 4, 1) * Matrix4x4.CreateTranslation(0.05f, -0.3f, 0.02f);
    static readonly Matrix4x4 Side = Matrix4x4.CreateRotationZ(1.2f) * Matrix4x4.CreateTranslation(0.45f, 0.5f, 0.1f);

    /// A 1 m tall box (20 mm plate) cut by `tool`; returns the box's mesh data, the result and the plate area removed.
    static (JsonObject Box, MeshCut.Result Result, double Removed) CutBox(string what, MeshCut.Solid tool, bool pocket, Action<JsonObject>? prepare = null)
    {
        var (box, _) = AddonEdits.Cylinder(0.5f, 1f, 4, 20);
        prepare?.Invoke(box);
        double before = MeshArea(box["mesh"]!.AsObject());
        var result = MeshCut.Cut(box, new[] { tool }, pocket);
        CheckMesh(box["mesh"]!.AsObject(), what, 0);
        return (box, result, before - MeshArea(box["mesh"]!.AsObject()));
    }

    static void CheckHole(string what, Matrix4x4 place, int holes, Func<Vector3[], Vector3[]>? reshape = null, double? area = null)
    {
        var solid = SolidOf(Tool16, p => Vector3.Transform(p, place));
        if (reshape != null) solid = solid with { Verts = reshape(solid.Verts.ToArray()) };
        Fill.Paths.Clear();
        var (box, result, removed) = CutBox(what, solid, false);
        Console.WriteLine($"  {what} fills: {string.Join(", ", Fill.Paths)}");
        var mesh = box["mesh"]!.AsObject();
        double expect = area ?? holes * Hole16;
        Check(removed > 1e-4 && (holes < 0 || Math.Abs(removed - expect) < 1e-4), $"{what}: removes the add-on's cross-section ({removed:0.00000} m², expected {expect:0.00000})");
        Check(Math.Abs(result.ArmourChange + removed * 20 / 1000.0) < 1e-6, $"{what}: armour volume drops by the hole");
        var vs = MeshVerts(mesh);
        var planes = PlanesOf(solid);
        foreach (var f in MeshFaces(mesh))
        {
            var c = f.Aggregate(Vector3.Zero, (s, i) => s + vs[i]) / f.Length;
            if (reshape == null) Check(planes.Any(q => Vector3.Dot(q.N, c) - q.D > 1e-5f), $"{what}: nothing left inside the add-on");
            Check(Vector3.Dot(HoleRing.Normal(f.Select(i => vs[i]).ToList()), c - new Vector3(0, 0.5f, 0)) > 0, $"{what}: every face still faces out");
        }
        if (reshape == null)
            foreach (var ((a, b), _) in EdgeUses(mesh).Where(u => u.Value == 1))
                Check(Math.Abs(planes.Max(q => Vector3.Dot(q.N, (vs[a] + vs[b]) / 2) - q.D)) < 1e-4f, $"{what}: only the hole's rim is open, no cracks");
        Console.WriteLine($"  {what}: {MeshFaces(mesh).Count(f => f.Length == 4)} quads, {MeshFaces(mesh).Count(f => f.Length == 3)} triangles");
    }

    /// Pockets: the structure stays a closed shell whose faces agree, and loses exactly the pocket's volume.
    static void CheckPocket(string what, Matrix4x4 place, double depth, int floors)
    {
        var solid = SolidOf(Tool16, p => Vector3.Transform(p, place), thickness: 7);
        var (box, result, _) = CutBox(what, solid, true);
        var mesh = box["mesh"]!.AsObject();
        var directed = new HashSet<(int, int)>();
        foreach (var f in MeshFaces(mesh)) for (int k = 0; k < f.Length; k++) Check(directed.Add((f[k], f[(k + 1) % f.Length])), $"{what}: faces agree which way they face");
        Check(directed.All(d => directed.Contains((d.Item2, d.Item1))), $"{what}: the structure stays closed (no gaps, no cracks)");
        Check(Math.Abs(Volume(mesh) - (0.5 - Hole16 * depth)) < 1e-4, $"{what}: loses exactly the pocket's volume ({Volume(mesh):0.00000} vs {0.5 - Hole16 * depth:0.00000})");
        Check(result.PocketFaces > 0 && mesh["faces"]!.AsArray().Any(f => f!["t"]!.AsArray().All(t => t!.GetValue<int>() == 7)), $"{what}: new walls use the add-on's armour");
        double expect = -2 * Hole16 * 20 / 1000.0 * (floors == 1 ? 0.5 : 1) + (Rim16 * depth + (floors == 1 ? Hole16 : 0)) * 7 / 1000.0;
        Check(Math.Abs(result.ArmourChange - expect) < 1e-5, $"{what}: armour volume counts the new walls ({result.ArmourChange:0.000000} vs {expect:0.000000})");
        Console.WriteLine($"  {what}: {result.PocketFaces} pocket plates, {MeshFaces(mesh).Count(f => f.Length == 4)} quads, {MeshFaces(mesh).Count(f => f.Length == 3)} triangles");
    }

    /// The fill on its own: a square with a round hole, and a many-sided polygon. Faces must exactly cover the region.
    static void CheckFill()
    {
        foreach (bool light in new[] { true, false })
        foreach (var (shape, holeSides, offset) in new[] { ("square with a round hole", 32, new Vector2(0, 0)), ("square with an off-centre hole", 16, new Vector2(0.15f, -0.1f)), ("many-sided polygon", 0, Vector2.Zero) })
        {
            string what = (light ? "light: " : "smooth: ") + shape;
            var pos = new List<Vector3>();
            var outer = new List<int>();
            if (holeSides == 0) for (int i = 0; i < 24; i++) { pos.Add(new Vector3(MathF.Cos(i * MathF.PI / 12), 0, -MathF.Sin(i * MathF.PI / 12))); outer.Add(i); }
            else foreach (var (x, z) in new[] { (-0.5f, 0.5f), (0.5f, 0.5f), (0.5f, -0.5f), (-0.5f, -0.5f) }) { pos.Add(new Vector3(x, 0, z)); outer.Add(pos.Count - 1); }
            var hole = new List<int>();
            for (int i = 0; i < holeSides; i++) { pos.Add(new Vector3(offset.X + 0.2f * MathF.Cos(i * 2 * MathF.PI / holeSides), 0, offset.Y + 0.2f * MathF.Sin(i * 2 * MathF.PI / holeSides))); hole.Add(pos.Count - 1); }
            var normal = Vector3.UnitY;
            double Signed(IList<int> f) => Vector3.Dot(HoleRing.Normal(f.Select(i => pos[i]).ToList()), normal) / 2;
            double want = Signed(outer) + (hole.Count > 0 ? -Math.Abs(Signed(hole)) : 0);
            int before = pos.Count;
            var faces = Fill.Region(pos, outer, hole.Count > 0 ? new List<List<int>> { Enumerable.Reverse(hole).ToList() } : new(), normal, new List<Fill.Added>(), light);
            double got = faces.Sum(Signed), unsigned = faces.Sum(f => Math.Abs(Signed(f)));
            if (Math.Abs(got - want) > 1e-5 || Math.Abs(unsigned - want) > 1e-5)
            {
                // Which faces pile up on the same spot?
                bool In(int[] f, float x, float z)
                {
                    bool inside = false;
                    for (int k = 0; k < f.Length; k++)
                    {
                        Vector3 a = pos[f[k]], b = pos[f[(k + 1) % f.Length]];
                        if ((a.Z > z) != (b.Z > z) && x < a.X + (z - a.Z) / (b.Z - a.Z) * (b.X - a.X)) inside = !inside;
                    }
                    return inside;
                }
                var rng = new Random(1);
                for (int s = 0, shown = 0; s < 4000 && shown < 6; s++)
                {
                    float x = (float)rng.NextDouble() - 0.5f, z = (float)rng.NextDouble() - 0.5f;
                    var hits = faces.Where(f => In(f, x, z)).ToList();
                    if (hits.Count > 1) { shown++; Console.WriteLine($"   ({x:0.000},{z:0.000}) in {string.Join(" ", hits.Select(f => "[" + string.Join(",", f.Select(i => $"{i}({pos[i].X:0.00},{pos[i].Z:0.00})")) + "]"))}"); }
                }
            }
            Check(Math.Abs(got - want) < 1e-5 && Math.Abs(unsigned - want) < 1e-5, $"fill {what}: faces cover exactly the region, all facing the same way ({got:0.00000} / {unsigned:0.00000} vs {want:0.00000})");
            var edges = new Dictionary<(int, int), int>();
            foreach (var f in faces) for (int k = 0; k < f.Length; k++) { var e = (f[k], f[(k + 1) % f.Length]); Check(!edges.ContainsKey(e), $"fill {what}: no edge used twice the same way"); edges[e] = 1; }
            Check(faces.All(f => f.Length is 3 or 4), $"fill {what}: triangles and quads only");
            Console.WriteLine($"  fill {what}: {pos.Count - before} new points, {faces.Count(f => f.Length == 4)} quads, {faces.Count(f => f.Length == 3)} triangles");
        }
    }

    /// Replays a real pocket cut from the test copy's backups (skipped if it isn't there): the add-on's cap was a fan
    /// of triangles to a centre point, and the pocket floor must not keep that fan.
    static void CheckRealPocket(Action<JsonObject, string> checkRefs)
    {
        var backup = new FileInfo(@"D:\Projects\SprocketInteropPatch\TestGame\BepInEx\SprocketQoLBackups\20260925-022900-c003bd4d\original.blueprint");
        if (!backup.Exists) { Console.WriteLine("  (real pocket replay skipped: backup not on this PC)"); return; }
        Fill.Paths.Clear();
        var plan = AddonEdits.PlanCut(File.ReadAllText(backup.FullName), 432, Array.Empty<int>(), true, true);
        Console.WriteLine($"  real pocket replay fills: {string.Join(", ", Fill.Paths)}");
        var after = Conversion.Parse(plan.DesignJson);
        checkRefs(after, "real pocket replay");
        var obj = Conversion.Objects(after)[872];
        int block = obj["structureBlueprintVuid"]!.GetValue<int>();
        int meshId = after["blueprints"]!.AsArray().First(x => x!["id"]!.GetValue<int>() == block)!["blueprint"]!["bodyMeshVuid"]!.GetValue<int>();
        var mesh = after["meshes"]!.AsArray().First(x => x!["vuid"]!.GetValue<int>() == meshId)!["meshData"]!["mesh"]!.AsObject();
        CheckMesh(mesh, "real pocket replay", double.MaxValue);
        // Points the cut made (not in the hull before): none may be the hub of a fan, like the add-on cap's centre was.
        var before = Conversion.Parse(File.ReadAllText(backup.FullName));
        int blockBefore = Conversion.Objects(before)[872]["structureBlueprintVuid"]!.GetValue<int>();
        int meshBefore = before["blueprints"]!.AsArray().First(x => x!["id"]!.GetValue<int>() == blockBefore)!["blueprint"]!["bodyMeshVuid"]!.GetValue<int>();
        var old = MeshVerts(before["meshes"]!.AsArray().First(x => x!["vuid"]!.GetValue<int>() == meshBefore)!["meshData"]!["mesh"]!.AsObject()).ToHashSet();
        var vs = MeshVerts(mesh);
        var busiest = MeshFaces(mesh).SelectMany(f => f).Where(v => !old.Contains(vs[v])).GroupBy(v => v).Max(g => g.Count());
        Check(busiest <= 8, $"real pocket replay: no new point is the hub of a fan (busiest new point has {busiest} faces)");
        Console.WriteLine($"  real pocket replay: {plan.Summary} busiest new point {busiest} faces");
    }

    /// Merge faces: known shapes come out as the fewest faces, facing the same way, covering the same area.
    static void CheckMergeFaces()
    {
        static Vector3 N(IReadOnlyList<Vector3> p, int[] f) => HoleRing.Normal(f.Select(i => p[i]).ToList());
        static double Area(IReadOnlyList<Vector3> p, IEnumerable<int[]> fs) => fs.Sum(f => (double)HoleRing.Normal(f.Select(i => p[i]).ToList()).Length() / 2);
        static List<FaceMerge.Group> Plan(List<Vector3> p, List<int[]> faces, int selectedCount, FaceMerge.SidePoints sides = FaceMerge.SidePoints.Keep) =>
            FaceMerge.Plan(p, faces, Enumerable.Range(0, selectedCount).ToHashSet(), new HashSet<(int, int)>(), sides);
        static List<(int, int)> OpenSides(IEnumerable<int[]> fs) =>
            fs.SelectMany(f => f.Select((v, k) => FaceMerge.Key(v, f[(k + 1) % f.Length]))).GroupBy(e => e).Where(e => e.Count() == 1).Select(e => e.Key).ToList();
        // The first `selectedCount` faces are selected; checks every patch it changes.
        List<FaceMerge.Group> Merge(string what, List<Vector3> p, List<int[]> faces, int selectedCount, int expectFaces, int expectRemoved, FaceMerge.SidePoints sides = FaceMerge.SidePoints.Keep)
        {
            var groups = Plan(p, faces, selectedCount, sides);
            var done = groups.Where(g => g.Why == null).ToList();
            Check(done.Count > 0 && groups.All(g => g.Why == null), $"{what}: merges ({string.Join("; ", groups.Select(g => g.Why))})");
            int made = done.Sum(g => g.NewFaces.Count), removed = done.SelectMany(g => g.Removed).Distinct().Count();
            Check(made == expectFaces, $"{what}: {expectFaces} faces, got {made}");
            Check(removed == expectRemoved, $"{what}: {expectRemoved} points removed, got {removed}");
            var gone = done.SelectMany(g => g.Removed).ToHashSet();
            var untouched = Enumerable.Range(0, faces.Count).Except(done.SelectMany(g => g.Faces)).Select(i => faces[i]).ToList();
            Check(!untouched.Any(f => f.Any(gone.Contains)), $"{what}: no face left alone uses a removed point");
            foreach (var g in done)
            {
                var old = g.Faces.Select(i => faces[i]).ToList();
                Check(g.NewFaces.All(f => f.Length is 3 or 4 && !f.Any(gone.Contains)), $"{what}: tris/quads using only kept points");
                Check(Math.Abs(Area(p, g.NewFaces) - Area(p, old)) < 1e-4, $"{what}: same area");
                var n = N(p, old[0]);
                Check(g.NewFaces.All(f => Vector3.Dot(N(p, f), n) > 0), $"{what}: new faces face the same way");
            }
            // Joined up: no more open sides than the plate had before (running past points opens some, by design).
            int open = OpenSides(untouched.Concat(done.SelectMany(g => g.NewFaces))).Count, before = OpenSides(faces).Count;
            if (sides != FaceMerge.SidePoints.RunPast) Check(open <= before, $"{what}: stays joined (open sides {open}, plate edge had {before})");
            Console.WriteLine($"merge faces, {what}: {done.Sum(g => g.Faces.Count)} -> {made}");
            return groups;
        }
        // A 4 x 2 grid of points, x = 0..3, y = 0..1: point (x, y) is 2x + y.
        var grid = Enumerable.Range(0, 8).Select(i => new Vector3(i / 2, i % 2, 0)).ToList();
        int G(int x, int y) => 2 * x + y;
        var strip = Enumerable.Range(0, 3).Select(x => new[] { G(x, 0), G(x + 1, 0), G(x + 1, 1), G(x, 1) }).ToList();
        Merge("two triangles", grid, new() { new[] { G(0, 0), G(1, 0), G(1, 1) }, new[] { G(0, 0), G(1, 1), G(0, 1) } }, 2, 1, 0);
        var s = Merge("strip of three quads", grid, strip, 3, 1, 4);
        Check(s[0].Joined.Count == 2, "strip: both long sides carry on from the old sides");
        {
            // An unselected triangle below the strip holds one side point.
            var p = grid.Append(new Vector3(1, -1, 0)).Append(new Vector3(2, -1, 0)).ToList();
            Merge("strip, one side point held", p, strip.Append(new[] { G(1, 0), 8, 9 }).ToList(), 3, 2, 3);
        }

        // A fan of 16 triangles round a centre point (a pocket floor): the centre goes, far fewer faces.
        var fan = Enumerable.Range(0, 16).Select(i => new Vector3(MathF.Cos(i * MathF.PI / 8), MathF.Sin(i * MathF.PI / 8), 0)).Append(Vector3.Zero).ToList();
        var f16 = Plan(fan, Enumerable.Range(0, 16).Select(i => new[] { 16, i, (i + 1) % 16 }).ToList(), 16)[0];
        Check(f16.Why == null && f16.Removed.SequenceEqual(new[] { 16 }) && f16.NewFaces.Count <= 8, $"fan: centre removed, 8 or fewer faces (got {f16.NewFaces.Count}, {f16.Why})");

        // A 4 x 4 grid of points with the middle square missing: 8 quads round a hole become 4.
        var sq = Enumerable.Range(0, 16).Select(i => new Vector3(i % 4, i / 4, 0)).ToList();
        var ring = new List<int[]>();
        for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++) if (x != 1 || y != 1) ring.Add(new[] { 4 * y + x, 4 * y + x + 1, 4 * y + x + 5, 4 * y + x + 4 });
        Merge("ring round a hole", sq, ring, 8, 4, 8);

        // Two faces folded 90° are left alone, and so are faces that don't share an edge.
        var fold = new List<Vector3> { new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0), new(1, 0, 1), new(1, 1, 1) };
        Check(Plan(fold, new List<int[]> { new[] { 0, 1, 2, 3 }, new[] { 1, 4, 5, 2 } }, 2).All(g => g.Why != null), "folded faces are left alone");
        Check(Plan(grid, new List<int[]> { strip[0], strip[2] }, 2).All(g => g.Why != null), "faces not sharing an edge are left alone");

        // The in-game case: a strip with a row of unselected quads above and below (y -1..0 and 1..2) sharing its side points.
        var rows = Enumerable.Range(0, 16).Select(i => new Vector3(i / 4, i % 4 - 1, 0)).ToList(); // point (x, y) is 4x + y + 1
        int R(int x, int y) => 4 * x + y + 1;
        var held = Enumerable.Range(0, 3).Select(x => new[] { R(x, 0), R(x + 1, 0), R(x + 1, 1), R(x, 1) }).ToList();
        for (int x = 0; x < 3; x++) held.Add(new[] { R(x, -1), R(x + 1, -1), R(x + 1, 0), R(x, 0) });
        for (int x = 0; x < 3; x++) held.Add(new[] { R(x, 1), R(x + 1, 1), R(x + 1, 2), R(x, 2) });
        var kept = Plan(rows, held, 3);
        Check(kept.Count == 1 && kept[0].Why!.Contains("select those faces too"), $"keep: a strip held by its neighbours says why ({kept[0].Why})");
        var past = Plan(rows, held, 3, FaceMerge.SidePoints.RunPast);
        Check(past.Count == 1 && past[0].Why == null && past[0].NewFaces.Count == 1 && past[0].Removed.Count == 0 && past[0].LeftOn.Count == 4,
            $"run past: one quad, side points left on the neighbours ({past[0].Why}, {past[0].NewFaces.Count} faces, left on {past[0].LeftOn.Count})");
        // Taking the lines out: the strip and each neighbouring row become one quad, all still joined.
        Merge("strip held by neighbours, lines taken out", rows, held, 3, 3, 8, FaceMerge.SidePoints.TakeOutLine);

        // Mirror: a strip right of the centre plane (x = 1..4) and its twin left of it; selecting the right one finds the
        // left one (a hair off, as floats are), and both merge to one quad each.
        {
            var both = Enumerable.Range(0, 8).Select(i => new Vector3(1 + i / 2, i % 2, 0)).ToList();
            both.AddRange(Enumerable.Range(0, 8).Select(i => new Vector3(-(1 + i / 2) + 1e-5f, i % 2, 0)));
            var right = Enumerable.Range(0, 3).Select(x => new[] { G(x, 0), G(x + 1, 0), G(x + 1, 1), G(x, 1) }).ToList();
            var left = right.Select(f => f.Reverse().Select(v => v + 8).ToArray()).ToList(); // mirrored, so turning the other way
            var mesh = right.Concat(left).ToList();
            var twins = FaceMerge.Mirrored(both, mesh, new[] { 0, 1, 2 }, 0.0003f);
            Check(twins.OrderBy(f => f).SequenceEqual(new[] { 3, 4, 5 }), $"mirror: the left strip's faces are the twins (got {string.Join(",", twins)})");
            Check(FaceMerge.Mirrored(both, mesh, new[] { 0 }, 0.000001f).Count == 0, "mirror: nothing matches outside the tolerance");
            Merge("strip and its mirror twin", both, mesh, 6, 2, 8);
        }

        // Round a 90° corner, both selected: a strip on top (z = 0) and one down the side (y = 0) sharing the corner line.
        var corner = grid.ToList();
        for (int x = 0; x < 4; x++) corner.Add(new Vector3(x, 0, -1)); // point 8 + x
        for (int x = 0; x < 4; x++) corner.Add(new Vector3(x, 0, -2)); // point 12 + x
        var side = Enumerable.Range(0, 3).Select(x => new[] { G(x + 1, 0), G(x, 0), 8 + x, 9 + x }).ToList();
        Merge("strips round a corner", corner, strip.Concat(side).ToList(), 6, 2, 6);
        // Only the top strip selected, two rows down the side: the side's lines are taken out down to the plate's edge.
        var side2 = side.Concat(Enumerable.Range(0, 3).Select(x => new[] { 9 + x, 8 + x, 12 + x, 13 + x })).ToList();
        Merge("strip, lines taken out round a corner", corner, strip.Concat(side2).ToList(), 3, 3, 8, FaceMerge.SidePoints.TakeOutLine);
    }

    public static void Run(string factions, Action<JsonObject, string> checkRefs)
    {
        CheckMergeFaces();
        CheckFill();
        CheckRealPocket(checkRefs);
        CheckHole("cut the top", Top, 1);
        CheckHole("cut right through", Through, 2);
        CheckHole("cut a side at an angle", Side, -1);
        {
            // A dented shape (one side corner pushed in) cuts too, and the hole has the dent.
            var ring = Enumerable.Range(0, 16).Select(i => new Vector2(0.1f * MathF.Cos(2 * MathF.PI * i / 16), 0.1f * MathF.Sin(2 * MathF.PI * i / 16))).ToArray();
            ring[0] *= 0.3f;
            double dentedArea = Math.Abs(Enumerable.Range(0, 16).Sum(i => ring[i].X * ring[(i + 1) % 16].Y - ring[(i + 1) % 16].X * ring[i].Y) / 2);
            CheckHole("cut with a dented shape", Top, 1,
                vs => { var c = new Vector3(0.05f, 0, 0.02f); foreach (int i in new[] { 0, 16 }) { var d = vs[i] - c; vs[i] = c + new Vector3(d.X * 0.3f, d.Y, d.Z * 0.3f); } return vs; }, dentedArea);
        }
        {
            var (box, _, _) = CutBox("side cut quads", SolidOf(Tool16, p => Vector3.Transform(p, Side)), false);
            Check(MeshFaces(box["mesh"]!.AsObject()).Count(f => f.Length == 4) > 3, "the faces around a cut include quads, not only triangles");
        }
        {
            var (box, _) = AddonEdits.Cylinder(0.5f, 1f, 4, 20);
            string untouched = box.ToJsonString();
            Check(MeshCut.Cut(box, new[] { SolidOf(Tool16, p => p + new Vector3(3, 0, 0)) }, true).FacesCut == 0 && box.ToJsonString() == untouched, "a shape that doesn't overlap changes nothing");
        }
        CheckPocket("pocket in the top", Top, 0.2, 1);
        CheckPocket("tunnel right through", Through, 1.0, 2);
        {
            // Rivets: one near a corner of the cut face moves onto a new face and stays put; one where the hole goes is
            // removed, and the rivet line between them is broken. (Side face 0 is (0.5,0,0) (0.5,1,0) (0,1,0.5) (0,0,0.5).)
            Vector3 before = default;
            var (box, result, _) = CutBox("rivets", SolidOf(Tool16, p => Vector3.Transform(p, Side)), false, b =>
            {
                var nodes = b["rivets"]!["nodes"]!.AsArray();
                nodes.Add(new JsonObject { ["next"] = 1, ["prev"] = -1, ["face"] = 0, ["u"] = 0.8, ["v"] = 0.1, ["w"] = 0.1, ["faceOffset"] = 1, ["note"] = "keep", ["profile"] = 0, ["flags"] = 1 });
                nodes.Add(new JsonObject { ["next"] = -1, ["prev"] = 0, ["face"] = 0, ["u"] = 0.481, ["v"] = 0.319, ["w"] = 0.2, ["faceOffset"] = 1, ["profile"] = 0, ["flags"] = 1 });
                before = RivetPos(b, nodes[0]!.AsObject());
            });
            var kept = box["rivets"]!["nodes"]!.AsArray();
            Check(kept.Count == 1 && result.RivetsMoved == 1 && result.RivetsDropped == 1, $"rivets: one moved, one in the hole removed ({result.RivetsMoved} moved, {result.RivetsDropped} removed)");
            Check(Vector3.Distance(RivetPos(box, kept[0]!.AsObject()), before) < 1e-4f, "rivets: the moved rivet is still in the same place");
            Check(kept[0]!["next"]!.GetValue<int>() == -1 && kept[0]!["note"]!.GetValue<string>() == "keep", "rivets: the line into the hole is broken, its other settings kept");
        }

        // Real add-ons cutting the shape they sit on, from the saved tanks: holes and pockets.
        int cuts = 0, pockets = 0, notCutting = 0; long slowest = 0;
        foreach (var file in Directory.GetFiles(factions, "*.blueprint", SearchOption.AllDirectories).OrderBy(f => new FileInfo(f).Length))
        {
            if (cuts >= 30) break;
            string json = File.ReadAllText(file);
            if (!json.Contains("\"objects\"")) continue;
            var objs = Conversion.Objects(Conversion.Parse(json));
            foreach (int addon in objs.Values.Where(o => Conversion.GuidOf(o) == Conversion.AddonGuid).Select(o => o["vuid"]!.GetValue<int>()).Take(6))
            {
                bool pocket = cuts % 2 == 1;
                int parent = objs[addon]["pvuid"]!.GetValue<int>();
                double crowdedBefore = double.MaxValue;
                if (!pocket && objs.TryGetValue(parent, out var po) && po["structureBlueprintVuid"] != null)
                {
                    var bp = Conversion.Parse(json);
                    int blk = po["structureBlueprintVuid"]!.GetValue<int>();
                    int mid = bp["blueprints"]!.AsArray().First(x => x!["id"]!.GetValue<int>() == blk)!["blueprint"]!["bodyMeshVuid"]!.GetValue<int>();
                    var m = bp["meshes"]!.AsArray().First(x => x!["vuid"]!.GetValue<int>() == mid)!["meshData"]?["mesh"]?.AsObject();
                    crowdedBefore = m == null ? double.MaxValue : CrowdedLength(m);
                }
                AddonEdits.EditPlan plan;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                try { plan = AddonEdits.PlanCut(json, addon, Array.Empty<int>(), true, pocket); slowest = Math.Max(slowest, watch.ElapsedMilliseconds); }
                catch (Exception ex) when (ex.Message.Contains("doesn't overlap") || ex.Message.Contains("isn't sitting")) { notCutting++; continue; }
                var after = Conversion.Parse(plan.DesignJson);
                checkRefs(after, "real cut");
                var afterObjs = Conversion.Objects(after);
                int block = afterObjs[parent]["structureBlueprintVuid"]!.GetValue<int>();
                int meshId = after["blueprints"]!.AsArray().First(x => x!["id"]!.GetValue<int>() == block)!["blueprint"]!["bodyMeshVuid"]!.GetValue<int>();
                var meshData = after["meshes"]!.AsArray().First(x => x!["vuid"]!.GetValue<int>() == meshId)!["meshData"]!.AsObject();
                CheckMesh(meshData["mesh"]!.AsObject(), pocket ? "real pocket" : "real cut", crowdedBefore);
                Check(plan.MeshIds.TryGetValue(parent, out int liveId) && liveId == meshId, "real cut: the in-place mesh is the one in the edited design");
                int faceCount = meshData["mesh"]!["faces"]!.AsArray().Count;
                var nodes = meshData["rivets"]?["nodes"]?.AsArray() ?? new JsonArray();
                Check(nodes.All(n => n!["face"]!.GetValue<int>() < faceCount && n["next"]!.GetValue<int>() < nodes.Count && n["prev"]!.GetValue<int>() < nodes.Count), "real cut: rivets point at real faces");
                Check(!afterObjs.ContainsKey(addon) || objs.Values.Any(o => o["pvuid"]!.GetValue<int>() == addon), "real cut: cutting add-on removed unless parts hang on it");
                cuts++; if (pocket) pockets++;
            }
        }
        Check(cuts > 0 && pockets > 0, "found real add-ons that cut their structure");
        Console.WriteLine($"CUT_TESTS_OK: {checks} checks, shaped holes + pockets + rivets + {cuts} real cuts ({pockets} pockets, {notCutting} add-ons not touching anything), slowest cut {slowest} ms");
    }
}
