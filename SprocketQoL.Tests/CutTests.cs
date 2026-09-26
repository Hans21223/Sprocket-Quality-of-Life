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
    static (JsonObject Box, MeshCut.Result Result, double Removed) CutBox(string what, MeshCut.Solid tool, bool pocket, Action<JsonObject>? prepare = null, Fill.Mode fill = Fill.Mode.Fewest)
    {
        var (box, _) = AddonEdits.Cylinder(0.5f, 1f, 4, 20);
        prepare?.Invoke(box);
        double before = MeshArea(box["mesh"]!.AsObject());
        var result = MeshCut.Cut(box, new[] { tool }, pocket, fill);
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
        foreach (var mode in new[] { Fill.Mode.Fewest, Fill.Mode.Light, Fill.Mode.Smooth })
        foreach (var (shape, holeSides, offset) in new[] { ("square with a round hole", 32, new Vector2(0, 0)), ("square with an off-centre hole", 16, new Vector2(0.15f, -0.1f)), ("many-sided polygon", 0, Vector2.Zero) })
        {
            string what = Fill.ModeNames[(int)mode] + ": " + shape;
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
            var faces = Fill.Region(pos, outer, hole.Count > 0 ? new List<List<int>> { Enumerable.Reverse(hole).ToList() } : new(), normal,
                                    mode == Fill.Mode.Fewest ? null : new List<Fill.Added>(), mode == Fill.Mode.Light);
            if (mode == Fill.Mode.Fewest) Check(pos.Count == before && faces.SelectMany(f => f).All(i => i < before), $"fill {what}: no new points");
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

    /// Create Hole as the game runs it, with every size the Hole size slider allows (the ring pushed out to the face's
    /// edge) and every segment count, on several face shapes and hole positions, with each fill: every face a real
    /// triangle or quad facing the face's way, none squashed, together covering exactly the face minus the hole.
    static void CheckBigHoles()
    {
        var shapes = new (string Name, Vector3[] Corners)[]
        {
            ("square", new Vector3[] { new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0) }),
            ("long plate", new Vector3[] { new(0, 0, 0), new(4, 0, 0), new(4, 1, 0), new(0, 1, 0) }),
            ("triangle", new Vector3[] { new(0, 0, 0), new(1, 0, 0), new(0.3f, 0.9f, 0) }),
            ("pentagon", Enumerable.Range(0, 5).Select(i => new Vector3(MathF.Cos(i * 2 * MathF.PI / 5), MathF.Sin(i * 2 * MathF.PI / 5), 0)).ToArray()),
        };
        int holes = 0;
        foreach (var (name, outer) in shapes)
        foreach (float at in new[] { 0f, 0.6f })
        foreach (int m in new[] { 4, 6, 8, 16, 32, 64, 96 })
        foreach (float size in new[] { 0.1f, 1f, 1.5f, 3f })
        foreach (var mode in new[] { Fill.Mode.Fewest, Fill.Mode.Light, Fill.Mode.Smooth })
        {
            var mid = outer.Aggregate(Vector3.Zero, (a, p) => a + p) / outer.Length;
            var centre = Vector3.Lerp(mid, outer[0], at); // in the middle, or off towards a corner
            var game = Enumerable.Range(0, m).Select(k => centre + 0.3f * new Vector3(MathF.Cos(k * 2 * MathF.PI / m), MathF.Sin(k * 2 * MathF.PI / m), 0)).ToArray();
            var ring = HoleRing.Fit(outer, game, centre, out _, size);
            var pos = outer.Concat(ring).ToList();
            var normal = HoleRing.Normal(outer);
            var faces = Fill.Region(pos, Enumerable.Range(0, outer.Length).ToList(), new List<List<int>> { Enumerable.Range(outer.Length, m).ToList() }, normal,
                                    mode == Fill.Mode.Fewest ? null : new List<Fill.Added>(), mode == Fill.Mode.Light);
            string what = $"hole in a {name} ({(at == 0 ? "middle" : "off-centre")}, {m} segments, size {size:P0}, {Fill.ModeNames[(int)mode]})";
            Check(faces.Count > 0, what + ": filled");
            double A(IList<int> f) => Vector3.Dot(HoleRing.Normal(f.Select(i => pos[i]).ToList()), Vector3.Normalize(normal)) / 2;
            double want = A(Enumerable.Range(0, outer.Length).ToList()) - Math.Abs(A(Enumerable.Range(outer.Length, m).ToList()));
            Check(faces.All(f => f.Length is 3 or 4 && f.Distinct().Count() == f.Length), what + ": real triangles and quads");
            Check(faces.Min(f => A(f)) > 1e-9 * want, $"{what}: no face squashed or turned over");
            Check(Math.Abs(faces.Sum(f => A(f)) - want) < 1e-4 * want, $"{what}: covers exactly the plate minus the hole");
            var edges = new HashSet<(int, int)>();
            foreach (var f in faces) for (int k = 0; k < f.Length; k++) Check(edges.Add((f[k], f[(k + 1) % f.Length])), what + ": no edge used twice the same way");
            holes++;
        }
        Console.WriteLine($"  big holes: {holes} filled, all sizes, shapes and fills sound");
    }

    /// Replays a real pocket cut from the test copy's backups (skipped if it isn't there): the add-on's cap was a fan
    /// of triangles to a centre point, and the pocket floor must not keep that fan.
    /// A design the mod backed up before an edit (BepInEx\SprocketQoLBackups\{name}\original.blueprint), found under the
    /// folder in the SPROCKET_QOL_BACKUPS environment variable. Null when that isn't set or the backup isn't there.
    static FileInfo? Backup(string name) =>
        Environment.GetEnvironmentVariable("SPROCKET_QOL_BACKUPS") is { Length: > 0 } dir &&
        new FileInfo(Path.Combine(dir, name, "original.blueprint")) is { Exists: true } file ? file : null;

    /// Merging freshly placed add-ons: every unedited palette cube shares one mesh in the file, so the merge target gets
    /// its own mesh number, only it changes, and the merge can still happen in place.
    static void CheckRealMerge()
    {
        if (Backup("20260925-085555-09f0f542") is not { } backup) { Console.WriteLine("  (real merge replay skipped: set SPROCKET_QOL_BACKUPS)"); return; }
        string original = File.ReadAllText(backup.FullName);
        var plan = AddonEdits.PlanMerge(original, 993, new[] { 901 });
        var was = AddonEdits.FaceCounts(original);
        var now = AddonEdits.FaceCounts(plan.DesignJson);
        int sharedMesh = AddonEdits.MeshIdOf(original, 993);
        Check(plan.Live, "real merge: two palette cubes merge in place");
        Check(AddonEdits.MeshIdOf(original, 901) == sharedMesh && AddonEdits.MeshIdOf(plan.DesignJson, 993) != sharedMesh, "real merge: the target gets its own mesh number");
        Check(now[993] == was[993] + was[901], $"real merge: target has both cubes' faces ({now[993]})");
        Check(was.Where(p => p.Key != 993 && p.Key != 901).All(p => now[p.Key] == p.Value), "real merge: no other part's shape changes");
        Console.WriteLine($"  real merge replay: {was[993]} + {was[901]} faces -> {now[993]}, in place");

        // An add-on merged into a turret body (a compartment): its faces join the turret's, the add-on part goes, and
        // nothing else changes shape. A turret can't be merged into an add-on.
        if (Backup("20260925-090307-1ef82734") is not { } turretDesign) return;
        string design = File.ReadAllText(turretDesign.FullName);
        var into = AddonEdits.PlanMerge(design, 426, new[] { 523 });
        var before = AddonEdits.FaceCounts(design);
        var after = AddonEdits.FaceCounts(into.DesignJson);
        Check(after[426] == before[426] + before[523] && !after.ContainsKey(523), $"merge into a turret: {before[426]} + {before[523]} faces -> {after.GetValueOrDefault(426)}, add-on gone");
        Check(before.Where(p => p.Key != 426 && p.Key != 523).All(p => after[p.Key] == p.Value), "merge into a turret: no other part's shape changes");
        bool refused = false;
        try { AddonEdits.PlanMerge(design, 523, new[] { 426 }); } catch (Exception) { refused = true; }
        Check(refused, "a turret can't be merged into an add-on");
        Console.WriteLine($"  merge into a turret: {before[426]} + {before[523]} faces -> {after[426]}, in place={into.Live}");
    }

    /// A design with mirror pairs: hull 1; add-on pair 2/3 (3 flipped); add-on pair 4/5 with a rivet and attached twins
    /// 6/7; add-on 8 on the centre line. Shapes are off-centre so a wrongly mirrored copy shows.
    static string MirrorDesign()
    {
        var objects = new JsonArray();
        var blocks = new JsonArray();
        var meshes = new JsonArray();
        void Part(int vuid, int parent, string guid, int flags, int mirror, float[] pos, float[] rot, int block = 0)
        {
            var o = new JsonObject
            {
                ["guid"] = guid, ["vuid"] = vuid, ["pvuid"] = parent, ["flags"] = flags,
                ["transform"] = new JsonObject
                {
                    ["mirrorVuid"] = mirror, ["pos"] = new JsonArray(pos.Select(x => (JsonNode?)x).ToArray()),
                    ["rot"] = new JsonArray(rot.Append(0).Select(x => (JsonNode?)x).ToArray()), ["scale"] = new JsonArray(1f, 1f, 1f),
                },
            };
            if (block > 0) o["structureBlueprintVuid"] = block;
            objects.Add(o);
        }
        void Shape(int block, float radius, bool rivet, float height = 0.2f)
        {
            var (md, _) = AddonEdits.Cylinder(radius, height, 5, 10);
            var vs = md["mesh"]!["vertices"]!.AsArray();
            for (int i = 0; i < vs.Count; i += 3) vs[i] = vs[i]!.GetValue<float>() + 0.15f;
            if (rivet) md["rivets"]!["nodes"]!.AsArray().Add(new JsonObject { ["next"] = -1, ["prev"] = -1, ["face"] = 0, ["u"] = 0.2f, ["v"] = 0.3f, ["w"] = 0.5f, ["faceOffset"] = 2, ["profile"] = 0, ["flags"] = 2 });
            meshes.Add(new JsonObject { ["vuid"] = 100 + block, ["meshData"] = md });
            blocks.Add(new JsonObject { ["id"] = block, ["type"] = "structure", ["blueprint"] = new JsonObject { ["bodyMeshVuid"] = 100 + block, ["armourVolume"] = 1.0 } });
        }
        Shape(10, 0.8f, false); Shape(11, 0.2f, false); Shape(12, 0.1f, true); Shape(13, 0.1f, false); Shape(14, 0.05f, false, 0.3f);
        const string other = "11111111-2222-3333-4444-555555555555";
        Part(1, -1, Conversion.CompartmentGuid, 2, -1, new[] { 0f, 0, 0 }, new[] { 0f, 0, 0 }, 10);
        Part(2, 1, Conversion.AddonGuid, 6, 3, new[] { -1f, 0.5f, 0.2f }, new[] { 0f, 30, 10 }, 11);
        Part(3, 1, Conversion.AddonGuid, 7, 2, new[] { 1f, 0.5f, 0.2f }, new[] { 0f, -30, -10 }, 11);
        Part(4, 1, Conversion.AddonGuid, 6, 5, new[] { -1.3f, 0.9f, 0.4f }, new[] { 5f, 12, 0 }, 12);
        Part(5, 1, Conversion.AddonGuid, 7, 4, new[] { 1.3f, 0.9f, 0.4f }, new[] { 5f, -12, 0 }, 12);
        Part(6, 4, other, 6, 7, new[] { 0.1f, 0.2f, 0 }, new[] { 0f, 20, 0 });
        Part(7, 5, other, 6, 6, new[] { -0.1f, 0.2f, 0 }, new[] { 0f, -20, 0 });
        Part(8, 1, Conversion.AddonGuid, 2, -1, new[] { 0f, 1.2f, 0 }, new[] { 0f, 0, 0 }, 13);
        // Cutters through the top of pair 2/3: a mirror pair 9/10, and 11 alone on the flipped side (where 3's shape shows).
        Part(9, 2, Conversion.AddonGuid, 6, 10, new[] { 0f, 0.1f, 0 }, new[] { 0f, 0, 0 }, 14);
        Part(10, 3, Conversion.AddonGuid, 7, 9, new[] { 0f, 0.1f, 0 }, new[] { 0f, 0, 0 }, 14);
        Part(11, 3, Conversion.AddonGuid, 2, -1, new[] { -0.3f, 0.1f, 0 }, new[] { 0f, 0, 0 }, 14);
        // Parts saved once that the game shows twice (mirrored mark, no twin): 12 and 13 on the hull, cutters 14 (shown
        // twice too) and 15 (plain) through the top of 13.
        Shape(15, 0.1f, true); Shape(16, 0.2f, false);
        Part(12, 1, Conversion.AddonGuid, 6, -1, new[] { -0.9f, 0.2f, 0.8f }, new[] { 0f, 15, 0 }, 15);
        Part(13, 1, Conversion.AddonGuid, 6, -1, new[] { -0.6f, 0.3f, -0.5f }, new[] { 0f, -20, 5 }, 16);
        Part(14, 13, Conversion.AddonGuid, 6, -1, new[] { 0f, 0.1f, 0 }, new[] { 0f, 0, 0 }, 14);
        Part(15, 13, Conversion.AddonGuid, 2, -1, new[] { 0.05f, 0.1f, 0.03f }, new[] { 0f, 0, 0 }, 14);
        return new JsonObject { ["objects"] = objects, ["blueprints"] = blocks, ["meshes"] = meshes }.ToJsonString();
    }

    /// Every shape point and rivet of a design where it shows in the vehicle (flipped parts mirrored along their own x).
    static List<Vector3> WorldPoints(string json)
    {
        var b = Conversion.Parse(json);
        var objects = Conversion.Objects(b);
        var world = Conversion.WorldMatrices(objects);
        var points = new List<Vector3>();
        foreach (var (v, o) in objects)
        {
            if (o["structureBlueprintVuid"] is not JsonValue id) continue;
            var md = AddonEdits.MeshOf(b["meshes"]!.AsArray(), AddonEdits.Block(b["blueprints"]!.AsArray(), id.GetValue<int>())["blueprint"]!["bodyMeshVuid"]!.GetValue<int>())!;
            var m = (o["flags"]!.GetValue<int>() & 1) != 0 ? Matrix4x4.CreateScale(-1, 1, 1) * world[v] : world[v];
            var vs = MeshVerts(md["mesh"]!.AsObject());
            var faces = MeshFaces(md["mesh"]!.AsObject());
            // A part saved once and shown twice (mirrored mark, no twin part) shows its image too, across the centre.
            bool imaged = (o["flags"]!.GetValue<int>() & 4) != 0 && !(o["transform"]!["mirrorVuid"]!.GetValue<int>() is int t && objects.ContainsKey(t));
            foreach (var shown in imaged ? new[] { m, m * Matrix4x4.CreateScale(-1, 1, 1) } : new[] { m })
            {
                points.AddRange(vs.Select(p => Vector3.Transform(p, shown)));
                foreach (var n in md["rivets"]!["nodes"]!.AsArray())
                {
                    var tri = MeshCut.RivetTriangles[n!["faceOffset"]!.GetValue<int>()];
                    var f = faces[n["face"]!.GetValue<int>()];
                    var at = n["u"]!.GetValue<float>() * vs[f[tri[0]]] + n["v"]!.GetValue<float>() * vs[f[tri[1]]] + n["w"]!.GetValue<float>() * vs[f[tri[2]]];
                    points.Add(Vector3.Transform(at, shown) + new Vector3(0, 100, 0)); // rivets kept apart from shape points
                }
            }
        }
        return points;
    }

    internal static bool SamePoints(List<Vector3> a, List<Vector3> b)
    {
        if (a.Count != b.Count) return false;
        var left = b.ToList();
        foreach (var p in a)
        {
            int i = left.FindIndex(q => Vector3.Distance(p, q) < 1e-3f);
            if (i < 0) return false;
            left.RemoveAt(i);
        }
        return true;
    }

    /// Merging with mirror twins: every shape and rivet stays exactly where it showed, twins go together, links stay right.
    static void CheckMirrorMerge()
    {
        string design = MirrorDesign();
        var points = WorldPoints(design);
        var faces = AddonEdits.FaceCounts(design);
        JsonObject Obj(string json, int v) => Conversion.Objects(Conversion.Parse(json))[v];
        int Mirror(string json, int v) => Obj(json, v)["transform"]!["mirrorVuid"]!.GetValue<int>();
        foreach (var (what, target, others) in new (string, int, int[])[] { ("pair into pair", 2, new[] { 4 }), ("pair into pair, twins selected too", 2, new[] { 4, 5, 3 }), ("pair into its flipped side", 3, new[] { 5 }) })
        {
            var plan = AddonEdits.PlanMerge(design, target, others);
            var now = AddonEdits.FaceCounts(plan.DesignJson);
            Check(SamePoints(points, WorldPoints(plan.DesignJson)), $"{what}: every shape and rivet stays where it was");
            Check(plan.Remove.OrderBy(x => x).SequenceEqual(new[] { 4, 5 }) && plan.MeshIds.Keys.OrderBy(x => x).SequenceEqual(new[] { 2, 3 }) && plan.Live, $"{what}: both add-ons go, both targets change in place");
            Check(now[2] == faces[2] + faces[4] && now[3] == now[2] && Mirror(plan.DesignJson, 2) == 3, $"{what}: the pair shares the merged shape and stays linked");
            Check(Obj(plan.DesignJson, 6)["pvuid"]!.GetValue<int>() == 2 && Obj(plan.DesignJson, 7)["pvuid"]!.GetValue<int>() == 3 && Mirror(plan.DesignJson, 6) == 7, $"{what}: attached twins move onto their side");
        }
        {
            var plan = AddonEdits.PlanMerge(design, 1, new[] { 4 });
            var now = AddonEdits.FaceCounts(plan.DesignJson);
            Check(SamePoints(points, WorldPoints(plan.DesignJson)), "into the centre: both twins land where they showed");
            Check(now[1] == faces[1] + 2 * faces[4] && plan.Remove.Count == 2 && Obj(plan.DesignJson, 7)["pvuid"]!.GetValue<int>() == 1, "into the centre: the hull takes both twins and their parts");
            var hull = AddonEdits.MeshOf(Conversion.Parse(plan.DesignJson)["meshes"]!.AsArray(), 110)!["mesh"]!.AsObject();
            var piece = AddonEdits.MeshOf(Conversion.Parse(design)["meshes"]!.AsArray(), 112)!["mesh"]!.AsObject();
            var hullBefore = AddonEdits.MeshOf(Conversion.Parse(design)["meshes"]!.AsArray(), 110)!["mesh"]!.AsObject();
            Check(Math.Abs(Volume(hull) - Volume(hullBefore) - 2 * Volume(piece)) < 1e-4, "into the centre: the mirrored copy isn't inside out");
        }
        {
            // Mirrored and unmirrored mixed would take the mirror off flipped twins (which can then show unflipped): refused.
            bool refused = false;
            try { AddonEdits.PlanMerge(design, 2, new[] { 4, 8 }); } catch (Exception ex) { refused = ex.Message.Contains("mirrored"); }
            Check(refused, "mixed: a merge that would unmirror a flipped twin is refused");
        }
        int Flags(string json, int v) => Obj(json, v)["flags"]!.GetValue<int>();
        // Parts the game shows twice.
        foreach (var (what, target, others, removed, live, keepsMark) in new (string, int, int[], int[], bool, bool)[]
        {
            ("shown twice, into the hull", 1, new[] { 12 }, new[] { 12 }, true, false),
            ("shown twice, into one shown twice", 13, new[] { 12 }, new[] { 12 }, true, true),
            ("pair into one shown twice", 13, new[] { 4 }, new[] { 4, 5 }, true, true),
            ("plain add-on into one shown twice", 13, new[] { 8 }, new[] { 8 }, false, false),
        })
        {
            var plan = AddonEdits.PlanMerge(design, target, others);
            Check(SamePoints(points, WorldPoints(plan.DesignJson)), $"{what}: everything shows where it did ({plan.Summary})");
            Check(plan.Remove.OrderBy(x => x).SequenceEqual(removed) && plan.Live == live && ((Flags(plan.DesignJson, target) & 4) != 0) == keepsMark, $"{what}: parts removed, in place, mark ({plan.Summary})");
        }
        Console.WriteLine("mirror merges: pair into pair, into the centre, mixed, parts shown twice checked");

        // Cuts: points of a part's shape where they show, and whether a cutter's hole ring is among a part's new points.
        List<Vector3> Shown(string json, int v)
        {
            var b = Conversion.Parse(json);
            var o = Conversion.Objects(b)[v];
            var w = Conversion.WorldMatrices(Conversion.Objects(b))[v];
            var md = AddonEdits.MeshOf(b["meshes"]!.AsArray(), AddonEdits.Block(b["blueprints"]!.AsArray(), o["structureBlueprintVuid"]!.GetValue<int>())["blueprint"]!["bodyMeshVuid"]!.GetValue<int>())!;
            var m = (o["flags"]!.GetValue<int>() & 1) != 0 ? Matrix4x4.CreateScale(-1, 1, 1) * w : w;
            return MeshVerts(md["mesh"]!.AsObject()).Select(p => Vector3.Transform(p, m)).ToList();
        }
        bool HoleAt(string json, int part, int cutterPart)
        {
            var axis = Shown(design, cutterPart); // the cutter's points: its axis is their middle, top to bottom
            var mid = axis.Aggregate(Vector3.Zero, (a, p) => a + p) / axis.Count;
            var fresh = Shown(json, part).Where(p => Shown(design, part).All(q => Vector3.Distance(p, q) > 1e-4f)).ToList();
            return fresh.Any(p => { var d = p - mid; var up = Vector3.Normalize(axis.MaxBy(q => q.Y)! - axis.MinBy(q => q.Y)!); return (d - up * Vector3.Dot(d, up)).Length() < 0.08f; });
        }
        List<Vector3> Mirrored(List<Vector3> ps) => ps.Select(p => new Vector3(-p.X, p.Y, p.Z)).ToList();
        {
            var cut = AddonEdits.PlanCut(design, 9, System.Array.Empty<int>(), true, false);
            Check(cut.MeshIds.Keys.OrderBy(x => x).SequenceEqual(new[] { 2, 3 }) && cut.MeshIds[2] == cut.MeshIds[3] && cut.Live, "mirrored cut: the pair is cut once, in place");
            Check(Mirror(cut.DesignJson, 2) == 3 && cut.Remove.OrderBy(x => x).SequenceEqual(new[] { 9, 10 }), "mirrored cut: the pair stays linked, both cutters go");
            Check(HoleAt(cut.DesignJson, 2, 9) && HoleAt(cut.DesignJson, 3, 10), "mirrored cut: each side has its hole where its cutter was");
            Check(SamePoints(Mirrored(Shown(cut.DesignJson, 2)), Shown(cut.DesignJson, 3)), "mirrored cut: the sides are mirror images");
        }
        {
            // One cutter on the flipped side of a pair: the shared shape is cut, so both sides show it; marks untouched.
            var cut = AddonEdits.PlanCut(design, 11, System.Array.Empty<int>(), true, false);
            Check(cut.MeshIds.Keys.OrderBy(x => x).SequenceEqual(new[] { 2, 3 }) && cut.Live && HoleAt(cut.DesignJson, 3, 11), "one-sided cut on a flipped part: the hole is where the cutter shows, in place");
            Check(SamePoints(Mirrored(Shown(cut.DesignJson, 2)), Shown(cut.DesignJson, 3)) && Mirror(cut.DesignJson, 2) == 3 && Flags(cut.DesignJson, 2) == 6 && Flags(cut.DesignJson, 3) == 7,
                  "one-sided cut: the other side shows it mirrored, the pair stays linked and marked");
        }
        {
            // A cutter shown twice through a target shown twice: one cut, the image follows.
            var cut = AddonEdits.PlanCut(design, 14, System.Array.Empty<int>(), true, false);
            Check(cut.MeshIds.Keys.SequenceEqual(new[] { 13 }) && cut.Live && (Flags(cut.DesignJson, 13) & 4) != 0 && HoleAt(cut.DesignJson, 13, 14), "cut shown twice: one cut in place, the image follows");
        }
        {
            // A plain cutter through a target shown twice: the shared shape is cut where the cutter is; the image shows it
            // mirrored, and the part stays shown twice.
            var cut = AddonEdits.PlanCut(design, 15, System.Array.Empty<int>(), true, false);
            var fresh = Shown(cut.DesignJson, 13).Where(p => Shown(design, 13).All(q => Vector3.Distance(p, q) > 1e-4f)).ToList();
            var cutterAxis = Shown(design, 15);
            var mid = cutterAxis.Aggregate(Vector3.Zero, (a, q) => a + q) / cutterAxis.Count;
            Check(cut.Live && Flags(cut.DesignJson, 13) == 6 && fresh.Count > 0 && fresh.All(q => Vector3.Distance(q, mid) < 0.25f),
                  $"one-sided cut on a part shown twice: in place, still shown twice, the hole where the cutter is ({fresh.Count} new points)");
            // The same cutter on the image's side (mirrored across the centre) makes the same hole.
            var design2 = Conversion.Parse(design);
            var c15 = Conversion.Objects(design2)[15];
            var flip = Matrix4x4.CreateScale(-1, 1, 1);
            c15["pvuid"] = 1; // on the hull, flipped, its shape the mirror image of where it was
            Conversion.WriteTransform(c15["transform"]!.AsObject(), flip * Conversion.WorldMatrices(Conversion.Objects(Conversion.Parse(design)))[15] * flip);
            c15["flags"] = 3;
            var viaImage = AddonEdits.PlanCut(design2.ToJsonString(), 15, new[] { 13 }, true, false);
            Check(viaImage.MeshIds.Keys.SequenceEqual(new[] { 13 }) && Flags(viaImage.DesignJson, 13) == 6 && Shown(viaImage.DesignJson, 13).Count > Shown(design, 13).Count,
                  "a cutter where the image shows cuts the shared shape too");
        }
        Console.WriteLine("mirror cuts: mirrored pair, one side of a flipped part, parts shown twice checked");
    }

    /// Mirrored turrets: a real turret gets a twin ring as the game's Mirror makes it (the ring alone), then everything on
    /// the turret is mirrored onto it: each copy the exact mirror image of its part, linked to it, with its own numbers,
    /// seats and guns naming the copies. A ring saved once and marked mirrored marks its parts instead.
    static void CheckMirrorTurret(string factions, Action<JsonObject, string> checkRefs)
    {
        foreach (var file in Directory.GetFiles(factions, "*.blueprint", SearchOption.AllDirectories).OrderBy(f => new FileInfo(f).Length))
        {
            string json = File.ReadAllText(file);
            if (!json.Contains("\"objects\"")) continue;
            var b = Conversion.Parse(json);
            var objects = Conversion.Objects(b);
            int Under(int top) => objects.Values.Count(o => { for (int p = o["pvuid"]!.GetValue<int>(); objects.TryGetValue(p, out var up); p = up["pvuid"]!.GetValue<int>()) if (p == top) return true; return false; });
            var ring = objects.Values.FirstOrDefault(o => Conversion.GuidOf(o) == Conversion.RingGuid && o["structureID"] != null && Under(o["vuid"]!.GetValue<int>()) >= 3
                                                        && o["transform"]!["mirrorVuid"]!.GetValue<int>() == -1 && objects.Values.All(x => x["transform"]!["mirrorVuid"]!.GetValue<int>() == -1 || x["pvuid"]!.GetValue<int>() != o["vuid"]!.GetValue<int>()));
            if (ring == null) continue;
            int r = ring["vuid"]!.GetValue<int>(), parts = Under(r);

            // The twin ring, as Mirror makes it: the ring alone, mirrored across the centre, linked, its own numbers.
            int next = objects.Values.SelectMany(o => o.Where(kv => kv.Value is JsonValue v && v.TryGetValue<int>(out _)).Select(kv => kv.Value!.GetValue<int>())).Max() + 1;
            var twin = JsonNode.Parse(ring.ToJsonString())!.AsObject();
            int t = next++;
            twin["vuid"] = t;
            foreach (var key in twin.Where(kv => kv.Value is JsonValue v && v.TryGetValue<int>(out _) && char.IsLetter(kv.Key[0]) && kv.Key is not ("vuid" or "pvuid" or "flags" or "structureID") && !kv.Key.EndsWith("Vuid") && !kv.Key.EndsWith("ID")).Select(kv => kv.Key).ToList())
                twin[key] = next++;
            var tr = twin["transform"]!;
            tr["pos"]![0] = -tr["pos"]![0]!.GetValue<float>();
            tr["rot"]![1] = -tr["rot"]![1]!.GetValue<float>();
            tr["rot"]![2] = -tr["rot"]![2]!.GetValue<float>();
            tr["mirrorVuid"] = r;
            ring["transform"]!["mirrorVuid"] = t;
            twin["flags"] = (ring["flags"]!.GetValue<int>() ^ 1) | 4;
            ring["flags"] = ring["flags"]!.GetValue<int>() | 4;
            b["objects"]!.AsArray().Add(twin);
            string design = b.ToJsonString();

            var (result, count, how) = Conversion.MirrorTurret(design, r);
            var after = Conversion.Parse(result);
            checkRefs(after, "mirrored turret");
            var all = Conversion.Objects(after);
            var world = Conversion.WorldMatrices(all);
            var flip = Matrix4x4.CreateScale(-1, 1, 1);
            Matrix4x4 Shape(JsonObject o) => (o["flags"]!.GetValue<int>() & 1) != 0 ? flip * world[o["vuid"]!.GetValue<int>()] : world[o["vuid"]!.GetValue<int>()];
            int onTwin = all.Values.Count(o => { for (int p = o["pvuid"]!.GetValue<int>(); all.TryGetValue(p, out var up); p = up["pvuid"]!.GetValue<int>()) if (p == t) return true; return false; });
            Check(count == parts && onTwin == parts, $"mirrored turret: {count} of {parts} parts copied, {onTwin} on the twin ({how})");
            var original = Conversion.Objects(Conversion.Parse(design));
            foreach (var (v, o) in all.Where(kv => original.ContainsKey(kv.Key) && kv.Key != r && kv.Key != t))
            {
                int m = o["transform"]!["mirrorVuid"]!.GetValue<int>();
                if (m < 0 || !all.ContainsKey(m) || original.ContainsKey(m)) continue;
                Check(all[m]["transform"]!["mirrorVuid"]!.GetValue<int>() == v && Conversion.Near(Shape(o) * flip, Shape(all[m])), $"mirrored turret: part {v} and its copy {m} are linked mirror images");
            }
            // (The game's own designs repeat a number or two, e.g. steering controls numbered as their part: only new ones count.)
            IEnumerable<int> Numbers(IEnumerable<JsonObject> os) => os.SelectMany(o => o.Where(kv => kv.Key == "vuid" || char.IsLetter(kv.Key[0]) && kv.Value is JsonValue jv && jv.TryGetValue<int>(out _)
                && kv.Key is not ("pvuid" or "flags" or "structureID") && !kv.Key.EndsWith("Vuid") && !kv.Key.EndsWith("ID")).Select(kv => kv.Value!.GetValue<int>()));
            var added = Numbers(all.Values.Where(o => !original.ContainsKey(o["vuid"]!.GetValue<int>()))).ToList();
            var existing = Numbers(original.Values).ToHashSet();
            Check(added.Count == added.Distinct().Count() && !added.Any(existing.Contains), "mirrored turret: every new part and component number is its own");
            int twinBody = all[t]["structureID"]!.GetValue<int>();
            Check(twinBody != ring["structureID"]!.GetValue<int>() && all[twinBody]["pvuid"]!.GetValue<int>() == t, "mirrored turret: the twin ring's body is the copy on it");
            // Seats and guns on the copy name the copy's parts, never the original turret's.
            var originalIds = original.Values.SelectMany(o => o.Where(kv => char.IsLetter(kv.Key[0]) && kv.Value is JsonValue jv && jv.TryGetValue<int>(out _)).Select(kv => kv.Value!.GetValue<int>())).ToHashSet();
            var blocks = after["blueprints"]!.AsArray();
            foreach (var o in all.Values.Where(o => !original.ContainsKey(o["vuid"]!.GetValue<int>())))
                foreach (var key in o.Where(kv => kv.Key.EndsWith("BlueprintVuid")).Select(kv => kv.Key))
                {
                    var bp = blocks.First(x => x!["id"]!.GetValue<int>() == o[key]!.GetValue<int>())!["blueprint"]!.AsObject();
                    foreach (var name in new[] { "operatedBehaviours", "barrelVuids" })
                        if (bp[name] is JsonArray named && named.Count > 0)
                            Check(named.All(x => !originalIds.Contains(x!.GetValue<int>()) || all.Values.Any(p => p.Any(kv => kv.Value is JsonValue pv && pv.TryGetValue<int>(out int n) && n == x.GetValue<int>() && !original.ContainsKey(p["vuid"]!.GetValue<int>())))),
                                  $"mirrored turret: {name} of copy {o["vuid"]} names the copies");
                }

            // Saved once, marked mirrored: its parts get the mark.
            ring["transform"]!["mirrorVuid"] = -1;
            var single = Conversion.Parse(json);
            var singleRing = Conversion.Objects(single)[r];
            singleRing["flags"] = singleRing["flags"]!.GetValue<int>() | 4;
            var (marked, markedCount, markedHow) = Conversion.MirrorTurret(single.ToJsonString(), r);
            var mo = Conversion.Objects(Conversion.Parse(marked));
            Check(markedCount == parts && mo.Values.Where(o => Conversion.Objects(Conversion.Parse(json)).ContainsKey(o["vuid"]!.GetValue<int>()) && o["vuid"]!.GetValue<int>() != r)
                                                 .Count(o => (o["flags"]!.GetValue<int>() & 4) != 0) >= parts, $"mirrored turret saved once: {markedCount} parts {markedHow}");
            Console.WriteLine($"mirrored turret: {Path.GetFileName(file)} ring {r}, {count} parts {how}; saved-once form {markedCount} {markedHow}");
            return;
        }
        throw new Exception("found no turret to mirror in the saved designs");
    }

    static void CheckRealPocket(Action<JsonObject, string> checkRefs)
    {
        if (Backup("20260925-022900-c003bd4d") is not { } backup) { Console.WriteLine("  (real pocket replay skipped: set SPROCKET_QOL_BACKUPS)"); return; }
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
            var asPlan = new MeshPlans.Rebuild(done.SelectMany(g => g.Faces).Distinct().ToList(), done.SelectMany(g => g.NewFaces.Select(nf => new MeshPlans.NewFace(nf, g.Faces[0]))).ToList(), new(), null);
            Check(MeshPlans.Check(p, faces, asPlan, gaps: sides != FaceMerge.SidePoints.RunPast) == null, $"{what}: passes the check made before merging ({MeshPlans.Check(p, faces, asPlan, gaps: sides != FaceMerge.SidePoints.RunPast)})");
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

    /// Mesh tools: every result is a closed, consistently turned surface where the input was, with the expected shape.
    static void CheckMeshTools()
    {
        static Vector3 Nw(IReadOnlyList<Vector3> p, IReadOnlyList<int> f) { var n = Vector3.Zero; for (int k = 0; k < f.Count; k++) n += Vector3.Cross(p[f[k]], p[f[(k + 1) % f.Count]]); return n; }
        static (List<Vector3> Pos, List<int[]> Faces) Applied(List<Vector3> pos, List<int[]> faces, MeshPlans.Rebuild r)
        {
            var p = pos.Concat(r.Points.Select(x => x.P)).ToList();
            var gone = r.Remove.ToHashSet();
            return (p, faces.Where((_, i) => !gone.Contains(i)).Concat(r.Add.Select(a => a.Corners)).ToList());
        }
        // Every side is used by exactly two faces running it opposite ways (a closed shape with no gaps or flips).
        static bool Closed(List<int[]> faces)
        {
            var sides = new Dictionary<(int, int), int>();
            foreach (var f in faces) for (int k = 0; k < f.Length; k++) sides[(f[k], f[(k + 1) % f.Length])] = sides.GetValueOrDefault((f[k], f[(k + 1) % f.Length])) + 1;
            return sides.All(s => s.Value == 1 && sides.GetValueOrDefault((s.Key.Item2, s.Key.Item1)) == 1);
        }
        static double Volume(List<Vector3> p, List<int[]> faces) => faces.Sum(f => Enumerable.Range(1, f.Length - 2).Sum(k => Vector3.Dot(p[f[0]], Vector3.Cross(p[f[k]], p[f[k + 1]])) / 6.0));
        static double Area(List<Vector3> p, IEnumerable<int[]> fs) => fs.Sum(f => (double)Nw(p, f).Length() / 2);

        // A unit cube, point x + 2y + 4z, faces turned outward.
        var cube = Enumerable.Range(0, 8).Select(i => new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1)).ToList();
        var cubeFaces = new List<int[]> { new[] { 0, 1, 5, 4 }, new[] { 2, 3, 7, 6 }, new[] { 0, 2, 6, 4 }, new[] { 1, 3, 7, 5 }, new[] { 0, 1, 3, 2 }, new[] { 4, 5, 7, 6 } }
            .Select(f => Vector3.Dot(Nw(cube, f), f.Aggregate(Vector3.Zero, (s, v) => s + cube[v]) / 4 - new Vector3(0.5f)) < 0 ? f.Reverse().ToArray() : f).ToList();
        Check(Closed(cubeFaces) && Math.Abs(Volume(cube, cubeFaces) - 1) < 1e-6, "mesh tools: the test cube is closed, volume 1");

        // Bevel one top edge from corner to corner: a straight chamfer, volume down by exactly half w² along it.
        const float w = 0.1f;
        var one = MeshPlans.Bevel(cube, cubeFaces, new[] { (2, 3) }, w);
        Check(one.Why == null, $"bevel one edge: works ({one.Why})");
        var (p1, f1) = Applied(cube, cubeFaces, one);
        Check(Closed(f1) && f1.All(f => f.Length is 3 or 4), "bevel one edge: closed, tris and quads");
        Check(Math.Abs(Volume(p1, f1) - (1 - 0.5 * w * w)) < 1e-5, $"bevel one edge: volume {Volume(p1, f1):0.00000}, expected {1 - 0.5 * w * w:0.00000}");
        // The four top edges: a chamfered top, still closed; three edges at one corner: a cap closes it.
        var (p4, f4) = Applied(cube, cubeFaces, MeshPlans.Bevel(cube, cubeFaces, new[] { (2, 3), (3, 7), (7, 6), (6, 2) }, w));
        // Chamfering the whole top turns its rim into a frustum: the top slab loses w − w/3·(1 + (1−2w)² + (1−2w)).
        double frustum = 1 - (w - w / 3.0 * (1 + (1 - 2 * w) * (1 - 2 * w) + (1 - 2 * w)));
        Check(Closed(f4) && Math.Abs(Volume(p4, f4) - frustum) < 1e-5, $"bevel a loop: closed, volume {Volume(p4, f4):0.00000}, expected {frustum:0.00000}");
        var (p3, f3) = Applied(cube, cubeFaces, MeshPlans.Bevel(cube, cubeFaces, new[] { (3, 7), (5, 7), (6, 7) }, w));
        Check(Closed(f3) && f3.Any(f => f.Length == 3) && Volume(p3, f3) < 1, "bevel a corner: closed, with a cap");
        Check(MeshPlans.Bevel(cube, cubeFaces, Array.Empty<(int, int)>(), w).Why != null, "bevel nothing: says why");

        // Loop cut round the cube's four side faces: 4 faces become 8, the ring closes, still closed with volume 1.
        var loop = MeshPlans.LoopCut(cube, cubeFaces, new[] { (0, 2) });
        var (pl, fl) = Applied(cube, cubeFaces, loop);
        Check(loop.Why == null && loop.Remove.Count == 4 && loop.Points.Count == 4 && Closed(fl) && Math.Abs(Volume(pl, fl) - 1) < 1e-6, $"loop cut round a cube: {loop.Remove.Count} faces cut, {loop.Points.Count} points, closed ({loop.Why})");
        // On an open strip it runs to the plate's edge; a triangle ends it.
        var grid = Enumerable.Range(0, 8).Select(i => new Vector3(i / 2, i % 2, 0)).ToList();
        var strip = Enumerable.Range(0, 3).Select(x => new[] { 2 * x, 2 * x + 2, 2 * x + 3, 2 * x + 1 }).ToList();
        var along = MeshPlans.LoopCut(grid, strip, new[] { (2, 3) });
        Check(along.Why == null && along.Add.Count == 6 && along.Points.Count == 4 && along.Points.All(p => Math.Abs(p.P.Y - 0.5f) < 1e-6), "loop cut along a strip: 3 quads become 6, points halfway");
        var tri = grid.Append(new Vector3(4, 0.5f, 0)).ToList();
        var withTri = strip.Append(new[] { 6, 8, 7 }).ToList();
        var toTri = MeshPlans.LoopCut(tri, withTri, new[] { (2, 3) });
        var (pt, ft) = Applied(tri, withTri, toTri);
        Check(toTri.Why == null && toTri.Remove.Contains(3) && Math.Abs(Area(pt, ft) - Area(tri, withTri)) < 1e-5, "loop cut into a triangle: it's cut too, same area");

        // Inset a square by 0.1: an inner square and four ring quads, same area, turned the same way.
        var sq = new List<Vector3> { new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0) };
        var ins = MeshPlans.Inset(sq, new List<int[]> { new[] { 0, 1, 2, 3 } }, new[] { 0 }, 0.1f);
        var (pi, fi) = Applied(sq, new List<int[]> { new[] { 0, 1, 2, 3 } }, ins);
        Check(ins.Why == null && fi.Count == 5 && Math.Abs(Area(pi, fi) - 1) < 1e-5 && fi.All(f => Nw(pi, f).Z > 0), "inset a square: 5 faces, same area, all face up");
        Check(ins.Points.Any(p => Vector3.Distance(p.P, new Vector3(0.1f, 0.1f, 0)) < 1e-5), "inset a square: corner 0.1 in both ways");
        var (ps, fs) = Applied(grid, strip, MeshPlans.Inset(grid, strip, new[] { 0, 1, 2 }, 0.1f));
        Check(fs.Count == 3 + 8 && Math.Abs(Area(ps, fs) - 3) < 1e-5, "inset a strip together: 3 inner faces and a ring of 8");
        Check(MeshPlans.Inset(sq, new List<int[]> { new[] { 0, 1, 2, 3 } }, new[] { 0 }, 0.6f).Why != null, "inset too wide: says why");

        // Flatten: bumpy points land on one plane; Level puts them at one height.
        var bumpy = new List<Vector3> { new(0, 0, 0.02f), new(1, 0, -0.01f), new(1, 1, 0.03f), new(0, 1, -0.02f), new(0.5f, 0.5f, 0.01f) };
        var flat = MeshPlans.Flatten(bumpy, new[] { 0, 1, 2, 3, 4 }, MeshPlans.FlattenMode.BestFit);
        var n = Vector3.Normalize(Vector3.Cross(flat[1] - flat[0], flat[3] - flat[0]));
        Check(flat.Values.All(p => Math.Abs(Vector3.Dot(p - flat[0], n)) < 1e-5), "flatten: all points on one plane");
        var level = MeshPlans.Flatten(bumpy, new[] { 0, 1, 2 }, MeshPlans.FlattenMode.Level);
        Check(level.Values.Select(p => p.Y).Distinct().Count() == 1, "flatten level: one height");

        // Select linked flat: from one face of a flat strip it takes the strip, not a face bent 90° away.
        var bent = grid.Concat(new[] { new Vector3(3, 0, 1), new Vector3(3, 1, 1) }).ToList();
        var bentFaces = strip.Append(new[] { 6, 8, 9, 7 }).ToList();
        var linked = MeshPlans.LinkedFlat(bent, bentFaces, new[] { 0 }, 5);
        Check(linked.SetEquals(new[] { 0, 1, 2 }), $"select linked flat: the strip only ({string.Join(",", linked)})");

        // The check run before a tool changes anything: every plan above passes; broken ones don't.
        foreach (var (what, p, f, plan) in new (string, List<Vector3>, List<int[]>, MeshPlans.Rebuild)[]
        {
            ("bevel one edge", cube, cubeFaces, one), ("bevel a loop", cube, cubeFaces, MeshPlans.Bevel(cube, cubeFaces, new[] { (2, 3), (3, 7), (7, 6), (6, 2) }, w)),
            ("bevel a corner", cube, cubeFaces, MeshPlans.Bevel(cube, cubeFaces, new[] { (3, 7), (5, 7), (6, 7) }, w)), ("loop cut round a cube", cube, cubeFaces, loop),
            ("loop cut along a strip", grid, strip, along), ("loop cut into a triangle", tri, withTri, toTri), ("inset a square", sq, new List<int[]> { new[] { 0, 1, 2, 3 } }, ins),
            ("inset a strip", grid, strip, MeshPlans.Inset(grid, strip, new[] { 0, 1, 2 }, 0.1f)),
        })
            Check(MeshPlans.Check(p, f, plan) == null, $"check: {what} passes ({MeshPlans.Check(p, f, plan)})");
        {
            // A bevel ending inside a fan of six triangles (more faces than a box corner): the point stays, and the strip's
            // end runs through it, so no hole is left there.
            var hex = Enumerable.Range(0, 6).Select(i => new Vector3(MathF.Cos(i * MathF.PI / 3), MathF.Sin(i * MathF.PI / 3), 0)).Append(Vector3.Zero).ToList();
            var fanFaces = Enumerable.Range(0, 6).Select(i => new[] { 6, i, (i + 1) % 6 }).ToList();
            var spoke = MeshPlans.Bevel(hex, fanFaces, new[] { (6, 0) }, 0.05f);
            var (ph, fh) = Applied(hex, fanFaces, spoke);
            var openAtCentre = fh.SelectMany(f => f.Select((v, k) => v < f[(k + 1) % f.Length] ? (v, f[(k + 1) % f.Length]) : (f[(k + 1) % f.Length], v)))
                .GroupBy(e => e).Where(e => e.Count() == 1).Count(e => e.Key.Item1 == 6 || e.Key.Item2 == 6);
            Check(spoke.Why == null && MeshPlans.Check(hex, fanFaces, spoke) == null && openAtCentre == 0 && fh.Any(f => f.Contains(6)),
                  $"bevel ending in a fan: the centre stays, no hole there ({spoke.Why}, {MeshPlans.Check(hex, fanFaces, spoke)}, {openAtCentre} open edges at the centre)");
        }
        {
            // Where a rivet goes: onto the face under it (straight down onto a square), or the nearest point of its edge.
            var square = new List<Vector3> { new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0) };
            var over = MeshPlans.Closest(square, new Vector3(0.3f, 0.7f, 0.002f));
            var beside = MeshPlans.Closest(square, new Vector3(1.03f, 0.5f, 0));
            Check(Vector3.Distance(over.Point, new Vector3(0.3f, 0.7f, 0)) < 1e-6 && Math.Abs(over.Distance - 0.002f) < 1e-6
                  && Vector3.Distance(beside.Point, new Vector3(1, 0.5f, 0)) < 1e-6 && Math.Abs(beside.Distance - 0.03f) < 1e-6, "rivets: nearest point of a face");
        }
        var flipped = new MeshPlans.Rebuild(new() { 0 }, new() { new(cubeFaces[0].Reverse().ToArray(), 0) }, new(), null);
        Check(MeshPlans.Check(cube, cubeFaces, flipped)?.Contains("turned over") == true, "check: a face turned over is caught");
        var doubled = new MeshPlans.Rebuild(new(), new() { new(cubeFaces[0], 0) }, new(), null);
        Check(MeshPlans.Check(cube, cubeFaces, doubled)?.Contains("laid over") == true, "check: a face on top of another is caught");
        // A loop cut that forgets the triangle it runs into leaves a point on the triangle's side: a gap.
        var gap = toTri with { Remove = toTri.Remove.Where(i => i != 3).ToList(), Add = toTri.Add.Where(a => a.Source != 3).ToList() };
        Check(MeshPlans.Check(tri, withTri, gap)?.Contains("crack") == true, $"check: a gap is caught ({MeshPlans.Check(tri, withTri, gap)})");
        Check(MeshPlans.Check(sq, new List<int[]> { new[] { 0, 1, 2, 3 } }, new(new() { 0 }, new() { new(new[] { 0, 1, 1 }, 0) }, new(), null))?.Contains("repeat") == true, "check: a repeated corner is caught");
        Check(MeshPlans.Folds(sq, new List<int[]> { new[] { 0, 1, 2, 3 } }, new Dictionary<int, Vector3> { [2] = new(-1, -1, 0), [1] = new(-0.5f, 0, 0) }) != null, "check: flattening that folds a face is caught");
        Check(MeshPlans.Folds(bumpy, new List<int[]> { new[] { 0, 1, 2, 3 } }, flat) == null, "check: a real flatten passes");

        // Proportional editing: nearer points follow more, none beyond the radius.
        var line = new List<Vector3> { new(0, 0, 0), new(0.25f, 0, 0), new(0.5f, 0, 0), new(1.5f, 0, 0) };
        var fall = MeshPlans.Falloff(line, new HashSet<int> { 0 }, 1);
        Check(!fall.ContainsKey(3) && fall[1].Weights[0] > fall[2].Weights[0] && fall[1].Weights[0] < 1, "proportional: falls off with distance");
        Console.WriteLine($"mesh tools: bevel, loop cut, inset, flatten, select linked flat, proportional checked");
    }

    /// Separate (Blender's P): the selected faces leave the part for a new add-on that shows exactly where they were;
    /// twins get a twin, parts shown twice stay so, the hull's goes on the hull, and rivets and armour follow the faces.
    static void CheckSeparate(Action<JsonObject, string> checkRefs)
    {
        string design = MirrorDesign();
        JsonObject Obj(string json, int v) => Conversion.Objects(Conversion.Parse(json))[v];
        JsonObject MeshData(string json, int v)
        {
            var b = Conversion.Parse(json);
            return AddonEdits.MeshOf(b["meshes"]!.AsArray(), AddonEdits.Block(b["blueprints"]!.AsArray(), Obj(json, v)["structureBlueprintVuid"]!.GetValue<int>())["blueprint"]!["bodyMeshVuid"]!.GetValue<int>())!;
        }
        double Armour(string json) => Conversion.Parse(json)["blueprints"]!.AsArray().Sum(x => x!["blueprint"]!["armourVolume"]?.GetValue<double>() ?? 0);
        // Distinct points (a split line's points are now in both parts).
        List<Vector3> Distinct(List<Vector3> ps) { var d = new List<Vector3>(); foreach (var p in ps) if (d.All(q => Vector3.Distance(p, q) > 1e-4f)) d.Add(p); return d; }
        // The faces of part v facing up in its own shape, as the editor would give them.
        List<Vector3[]> UpFaces(int v, bool all = false)
        {
            var mesh = MeshData(design, v)["mesh"]!.AsObject();
            var vs = MeshVerts(mesh);
            return MeshFaces(mesh).Select(f => f.Select(i => vs[i]).ToArray()).Where(c => all || HoleRing.Normal(c.ToList()).Y > 0.5f * HoleRing.Normal(c.ToList()).Length()).ToList();
        }
        foreach (var (what, part, twins, imaged, parent) in new (string, int, bool, bool, int)[]
        {
            ("twin pair", 2, true, false, 1), ("rivets", 4, true, false, 1), ("the hull", 1, false, false, 1), ("shown twice", 12, false, true, 1), ("centre add-on", 8, false, false, 1),
        })
        {
            var up = UpFaces(part);
            var (json, made, log) = AddonEdits.Separate(design, part, up);
            int added = made[0].Added;
            checkRefs(Conversion.Parse(json), "separate " + what);
            var objects = Conversion.Objects(Conversion.Parse(json));
            var o = objects[added];
            int before = MeshFaces(MeshData(design, part)["mesh"]!.AsObject()).Count;
            Check(MeshFaces(MeshData(json, added)["mesh"]!.AsObject()).Count == up.Count && MeshFaces(MeshData(json, part)["mesh"]!.AsObject()).Count == before - up.Count,
                  $"separate {what}: the selected faces move, the rest stay ({log})");
            Check(o["guid"]!.GetValue<string>() == Conversion.AddonGuid && o["pvuid"]!.GetValue<int>() == parent, $"separate {what}: a new add-on on part {parent}");
            Check(SamePoints(Distinct(WorldPoints(design)), Distinct(WorldPoints(json))), $"separate {what}: every point and rivet shows where it did");
            Check(Math.Abs(Armour(json) - Armour(design)) < 1e-6, $"separate {what}: the armour moves with the faces, none made or lost");
            int mirror = o["transform"]!["mirrorVuid"]!.GetValue<int>();
            Check(twins ? objects.ContainsKey(mirror) && objects[mirror]["transform"]!["mirrorVuid"]!.GetValue<int>() == added : mirror == -1, $"separate {what}: twins linked as the part's were");
            Check(((o["flags"]!.GetValue<int>() & 4) != 0) == (twins || imaged), $"separate {what}: mirrored mark as the part's");
        }
        {
            // Rivets go with their face: part 4's rivet sits on face 0 (a side), so separating the sides takes it.
            var sides = UpFaces(4, all: true).Where(c => c.Length == 4).ToList();
            var (json, made, _) = AddonEdits.Separate(design, 4, sides);
            int added = made[0].Added;
            Check(MeshData(json, added)["rivets"]!["nodes"]!.AsArray().Count == 1 && MeshData(json, 4)["rivets"]!["nodes"]!.AsArray().Count == 0, "separate: the rivet goes with its face");

        }
        {
            // Picked pieces: part 8's shape (and pair 2/3's shared one) given extra loose copies; a face picked on a piece
            // takes the whole piece into its own add-on.
            var b = Conversion.Parse(design);
            void AddPieces(int meshId, params Vector3[] shifts)
            {
                var mesh = b["meshes"]!.AsArray().First(m => m!["vuid"]!.GetValue<int>() == meshId)!["meshData"]!["mesh"]!.AsObject();
                var vs = MeshVerts(mesh);
                var faceNodes = mesh["faces"]!.AsArray().Select(f => f!.AsObject()).ToList();
                var ends = mesh["edges"]!.AsArray().Select(x => x!.GetValue<int>()).ToList();
                foreach (var shift in shifts)
                {
                    int baseV = mesh["vertices"]!.AsArray().Count / 3;
                    foreach (var p in vs) foreach (float c in new[] { p.X + shift.X, p.Y + shift.Y, p.Z + shift.Z }) mesh["vertices"]!.AsArray().Add((JsonNode?)c);
                    foreach (int e in ends) mesh["edges"]!.AsArray().Add((JsonNode?)(e + baseV));
                    foreach (int e in ends.Where((_, i) => i % 2 == 0)) mesh["edgeFlags"]!.AsArray().Add((JsonNode?)0);
                    foreach (var f in faceNodes)
                    {
                        var copy = JsonNode.Parse(f.ToJsonString())!.AsObject();
                        copy["v"] = new JsonArray(f["v"]!.AsArray().Select(x => (JsonNode?)(x!.GetValue<int>() + baseV)).ToArray());
                        copy["te"] = 0; // as the game writes a face with no thicken edges picked
                        mesh["faces"]!.AsArray().Add(copy);
                    }
                }
            }
            AddPieces(113, new Vector3(0, 0, 0.6f), new Vector3(0, 0, -0.6f));
            AddPieces(111, new Vector3(0, 0.5f, 0));
            string pieced = b.ToJsonString();
            List<Vector3[]> FaceOf(int v, Vector3 near)
            {
                var md = MeshData(pieced, v)["mesh"]!.AsObject();
                var vs = MeshVerts(md);
                return new() { MeshFaces(md).Select(f => f.Select(i => vs[i]).ToArray()).MinBy(c => Vector3.Distance(c.Aggregate(Vector3.Zero, (a, p) => a + p) / c.Length, near))! };
            }
            int Faces(string json, int v) => MeshFaces(MeshData(json, v)["mesh"]!.AsObject()).Count;
            int each = Faces(design, 8);
            var (one, g1, log1) = AddonEdits.SeparatePieces(pieced, 8, FaceOf(8, new Vector3(0.15f, 0.1f, 0.6f)));
            checkRefs(Conversion.Parse(one), "separate a piece");
            Check(g1.Count == 1 && Faces(one, 8) == 2 * each && Faces(one, g1[0][0].Added) == each, $"separate pieces: one click takes that whole piece only ({log1})");
            Check(SamePoints(Distinct(WorldPoints(pieced)), Distinct(WorldPoints(one))) && Math.Abs(Armour(one) - Armour(pieced)) < 1e-6, "separate pieces: everything shows where it did, armour kept");
            var two = FaceOf(8, new Vector3(0.15f, 0.1f, 0.6f)).Concat(FaceOf(8, new Vector3(0.15f, 0.1f, -0.6f))).ToList();
            var (both, g2, _) = AddonEdits.SeparatePieces(pieced, 8, two);
            Check(g2.Count == 2 && Faces(both, 8) == each && g2.All(g => Faces(both, g[0].Added) == each), "separate pieces: two picked, two add-ons");
            var (allPicked, g3, _) = AddonEdits.SeparatePieces(pieced, 8, two.Concat(FaceOf(8, new Vector3(0.15f, 0.1f, 0))).ToList());
            Check(g3.Count == 2 && Faces(allPicked, 8) == each, "separate pieces: every piece picked, the biggest stays");
            var (pair, g4, _) = AddonEdits.SeparatePieces(pieced, 2, FaceOf(2, new Vector3(0.15f, 0.6f, 0)));
            var pairObjects = Conversion.Objects(Conversion.Parse(pair));
            Check(g4.Count == 1 && g4[0].Count == 2 && pairObjects[g4[0][0].Added]["transform"]!["mirrorVuid"]!.GetValue<int>() == g4[0][1].Added,
                  "separate pieces: a mirrored part's twin gets the twin add-on");
            bool onePiece = false;
            try { AddonEdits.SeparatePieces(design, 12, UpFaces(12)); } catch (Exception ex) { onePiece = ex.Message.Contains("one piece"); }
            Check(onePiece, "separate pieces: a shape in one piece says so");
        }
        bool refused = false;
        try { AddonEdits.Separate(design, 8, UpFaces(8, all: true)); } catch (Exception ex) { refused = ex.Message.Contains("leave at least one"); }
        Check(refused, "separate: every face selected is refused");
        refused = false;
        try { AddonEdits.Separate(design, 8, new List<Vector3[]> { new[] { new Vector3(9, 9, 9), new Vector3(9, 9, 8), new Vector3(8, 9, 9) } }); } catch (Exception ex) { refused = ex.Message.Contains("weren't found"); }
        Check(refused, "separate: faces not in the shape are refused");
        Console.WriteLine("separate: twin pair, rivets, hull, shown twice, centre add-on checked");
    }

    /// A real skirt (saved once and shown twice, flipped, on a tank built on a scaled mantlet) cut by the plain add-on 511
    /// on it. The old cut made the image part of the skirt's shape and took its mirror mark off; the game then showed that
    /// copy metres out from the tank. Now the shared shape is cut in place and the marks stay.
    static void CheckRealSkirt(Action<JsonObject, string> checkRefs)
    {
        if (Backup("20260926-083219-45060c70") is not { } backup) { Console.WriteLine("  (real skirt replay skipped: set SPROCKET_QOL_BACKUPS)"); return; }
        string json = File.ReadAllText(backup.FullName);
        Vector3[] Skirt(string design)
        {
            var b = Conversion.Parse(design);
            int block = Conversion.Objects(b)[601]["structureBlueprintVuid"]!.GetValue<int>();
            return MeshVerts(AddonEdits.MeshOf(b["meshes"]!.AsArray(), AddonEdits.Block(b["blueprints"]!.AsArray(), block)["blueprint"]!["bodyMeshVuid"]!.GetValue<int>())!["mesh"]!.AsObject());
        }
        var plan = AddonEdits.PlanCut(json, 511, Array.Empty<int>(), false, false);
        checkRefs(Conversion.Parse(plan.DesignJson), "real skirt replay");
        var flags = Conversion.Objects(Conversion.Parse(plan.DesignJson))[601]["flags"]!.GetValue<int>();
        Check(flags == 7 && plan.Live && plan.MeshIds.Keys.SequenceEqual(new[] { 601 }), $"real skirt: cut in place, still flipped and shown twice (flags {flags}, {plan.Summary})");
        var old = Skirt(json);
        var (lo, hi) = (old.Aggregate(Vector3.Min) - new Vector3(0.05f), old.Aggregate(Vector3.Max) + new Vector3(0.05f));
        var now = Skirt(plan.DesignJson);
        Check(now.Length > old.Length && now.All(p => Vector3.Clamp(p, lo, hi) == p), $"real skirt: every point of the cut skirt stays on the skirt ({old.Length} -> {now.Length} points)");
        Console.WriteLine($"  real skirt replay: {plan.Summary}");
    }

    /// The pre-change check on real shapes: loop cuts, bevels and insets at random spots on saved tanks, and how often (and
    /// why) the check stops them. A survey for false alarms, printed, not failed.
    static void SurveyChecks(string factions)
    {
        var rng = new Random(1);
        var why = new Dictionary<string, int>();
        int plans = 0, stopped = 0;
        foreach (var file in Directory.GetFiles(factions, "*.blueprint", SearchOption.AllDirectories).OrderBy(f => f).Take(40))
        {
            var b = Conversion.Parse(File.ReadAllText(file));
            if (b["meshes"] is not JsonArray meshes) continue;
            foreach (var m in meshes.Take(30))
            {
                if (m?["meshData"]?["mesh"] is not JsonObject mesh) continue;
                var pos = MeshVerts(mesh).ToList();
                var faces = MeshFaces(mesh);
                if (faces.Count < 4 || faces.Count > 3000) continue;
                for (int t = 0; t < 3; t++)
                {
                    var f = faces[rng.Next(faces.Count)];
                    int k = rng.Next(f.Length);
                    var edge = (f[k], f[(k + 1) % f.Length]);
                    foreach (var (tool, plan) in new[] { ("loop cut", MeshPlans.LoopCut(pos, faces, new[] { edge })), ("bevel", MeshPlans.Bevel(pos, faces, new[] { edge }, 0.01f)),
                                                 ("inset", MeshPlans.Inset(pos, faces, new[] { faces.IndexOf(f) }, 0.005f)) })
                    {
                        if (plan.Why != null) continue;
                        plans++;
                        if (MeshPlans.Check(pos, faces, plan) is not string r) continue;
                        stopped++;
                        why[tool + ": " + r] = why.GetValueOrDefault(tool + ": " + r) + 1; r = tool + ": " + r;
                        if (why[r] <= 2 && Environment.GetEnvironmentVariable("QOL_SURVEY_DETAIL") != null)
                        {
                            var at = pos.Concat(plan.Points.Select(x => x.P)).ToList();
                            string F(int[] c) => "[" + string.Join(" ", c.Select(i => $"{i}({at[i].X:0.###},{at[i].Y:0.###},{at[i].Z:0.###})")) + "]";
                            Console.WriteLine($"    {tool} {Path.GetFileName(file)} mesh {m["vuid"]} edge {edge}: {r}");
                            Console.WriteLine($"      removed: {string.Join(" ", plan.Remove.Select(i => F(faces[i])))}");
                            Console.WriteLine($"      added: {string.Join(" ", plan.Add.Select(a => F(a.Corners) + "<" + a.Source))}");
                            static Dictionary<(int, int), int> U(IEnumerable<int[]> fs) { var u = new Dictionary<(int, int), int>(); foreach (var c in fs) for (int j = 0; j < c.Length; j++) { var key = c[j] < c[(j + 1) % c.Length] ? (c[j], c[(j + 1) % c.Length]) : (c[(j + 1) % c.Length], c[j]); u[key] = u.GetValueOrDefault(key) + 1; } return u; }
                            var gone = plan.Remove.ToHashSet();
                            var ub = U(faces); var ua = U(faces.Where((_, i) => !gone.Contains(i)).Concat(plan.Add.Select(a => a.Corners)));
                            Console.WriteLine($"      open now, not before: {string.Join(" ", ua.Where(kv => kv.Value == 1 && ub.GetValueOrDefault(kv.Key) != 1).Select(kv => $"{kv.Key}(was {ub.GetValueOrDefault(kv.Key)})"))}");
                            Console.WriteLine($"      open before, not now: {string.Join(" ", ub.Where(kv => kv.Value == 1 && ua.GetValueOrDefault(kv.Key) != 1).Select(kv => $"{kv.Key}(now {ua.GetValueOrDefault(kv.Key)})"))}");
                        }
                    }
                }
            }
        }
        Console.WriteLine($"  check survey on real shapes: {stopped} of {plans} tool plans stopped" + (why.Count > 0 ? ": " + string.Join("; ", why.Select(kv => $"{kv.Value}x {kv.Key}")) : ""));
    }

    /// A real "separate picked pieces" (part 3092, 18 loose pieces, two picked) that the game refused to load: corners
    /// with no thicken edge were written as a negative number. Replayed with a face of two of its pieces.
    static void CheckRealPieces(Action<JsonObject, string> checkRefs)
    {
        if (Backup("20260926-135837-0f046d78") is not { } backup) { Console.WriteLine("  (real pieces replay skipped: set SPROCKET_QOL_BACKUPS)"); return; }
        string json = File.ReadAllText(backup.FullName);
        var b = Conversion.Parse(json);
        var md = AddonEdits.MeshOf(b["meshes"]!.AsArray(), AddonEdits.Block(b["blueprints"]!.AsArray(), Conversion.Objects(b)[3092]["structureBlueprintVuid"]!.GetValue<int>())["blueprint"]!["bodyMeshVuid"]!.GetValue<int>())!;
        var vs = MeshVerts(md["mesh"]!.AsObject());
        var faces = MeshFaces(md["mesh"]!.AsObject());
        var pieces = AddonEdits.LooseParts(vs, faces);
        var picked = pieces.Where(p => p.Count == 44).Take(2).Select(p => faces[p[0]].Select(i => vs[i]).ToArray()).ToList();
        var (result, groups, log) = AddonEdits.SeparatePieces(json, 3092, picked);
        checkRefs(Conversion.Parse(result), "real pieces replay");
        Check(groups.Count == 2, $"real pieces replay: two pieces out ({log})");
        Console.WriteLine($"  real pieces replay: {log}");
    }

    public static void Run(string factions, Action<JsonObject, string> checkRefs)
    {
        CheckRealPieces(checkRefs);
        SurveyChecks(factions);
        CheckBigHoles();
        CheckRealSkirt(checkRefs);
        CheckSeparate(checkRefs);
        CheckMeshTools();
        CheckMergeFaces();
        CheckFill();
        CheckRealPocket(checkRefs);
        CheckRealMerge();
        CheckMirrorMerge();
        CheckMirrorTurret(factions, checkRefs);
        {
            // The same hole and pocket with each fill: fewest points adds none beyond the cut's own, and uses fewer
            // points than light rings, which use fewer than smooth.
            foreach (bool pocket in new[] { false, true })
            {
                var counts = new[] { Fill.Mode.Fewest, Fill.Mode.Light, Fill.Mode.Smooth }
                    .Select(m => MeshVerts(CutBox($"{Fill.ModeNames[(int)m]} {(pocket ? "pocket" : "hole")}", SolidOf(Tool16, p => Vector3.Transform(p, Top)), pocket, null, m).Box["mesh"]!.AsObject()).Length).ToArray();
                Check(counts[0] < counts[1] && counts[1] < counts[2], $"fill points for a {(pocket ? "pocket" : "hole")}: fewest {counts[0]}, light {counts[1]}, smooth {counts[2]}");
                Console.WriteLine($"  {(pocket ? "pocket" : "hole")} in the top: {counts[0]} points (fewest), {counts[1]} (light), {counts[2]} (smooth)");
            }
        }
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
        int cuts = 0, pockets = 0, notCutting = 0, openCutters = 0; long slowest = 0; string slowestWhat = "";
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
                try { plan = AddonEdits.PlanCut(json, addon, Array.Empty<int>(), true, pocket); if (watch.ElapsedMilliseconds > slowest) { slowest = watch.ElapsedMilliseconds; slowestWhat = $"{Path.GetFileName(file)} add-on {addon} ({(pocket ? "pocket" : "hole")})"; } }
                catch (Exception ex) when (ex.Message.Contains("doesn't overlap") || ex.Message.Contains("isn't sitting")) { notCutting++; continue; }
                catch (Exception ex) when (ex.Message.Contains("isn't a closed shape")) { openCutters++; continue; }
                var after = Conversion.Parse(plan.DesignJson);
                checkRefs(after, "real cut");
                var afterObjs = Conversion.Objects(after);
                int block = afterObjs[parent]["structureBlueprintVuid"]!.GetValue<int>();
                int meshId = after["blueprints"]!.AsArray().First(x => x!["id"]!.GetValue<int>() == block)!["blueprint"]!["bodyMeshVuid"]!.GetValue<int>();
                var meshData = after["meshes"]!.AsArray().First(x => x!["vuid"]!.GetValue<int>() == meshId)!["meshData"]!.AsObject();
                try { CheckMesh(meshData["mesh"]!.AsObject(), pocket ? "real pocket" : "real cut", crowdedBefore); }
                catch (Exception ex) { throw new Exception($"{ex.Message} ({Path.GetFileName(file)}, add-on {addon} into {parent}: {plan.Summary})"); }
                Check(plan.MeshIds.TryGetValue(parent, out int liveId) && liveId == meshId, "real cut: the in-place mesh is the one in the edited design");
                int faceCount = meshData["mesh"]!["faces"]!.AsArray().Count;
                var nodes = meshData["rivets"]?["nodes"]?.AsArray() ?? new JsonArray();
                Check(nodes.All(n => n!["face"]!.GetValue<int>() < faceCount && n["next"]!.GetValue<int>() < nodes.Count && n["prev"]!.GetValue<int>() < nodes.Count), "real cut: rivets point at real faces");
                Check(!afterObjs.ContainsKey(addon) || objs.Values.Any(o => o["pvuid"]!.GetValue<int>() == addon), "real cut: cutting add-on removed unless parts hang on it");
                cuts++; if (pocket) pockets++;
            }
        }
        Check(cuts > 0 && pockets > 0, "found real add-ons that cut their structure");
        Console.WriteLine($"CUT_TESTS_OK: {checks} checks, shaped holes + pockets + rivets + {cuts} real cuts ({pockets} pockets, {notCutting} add-ons not touching anything, {openCutters} open add-ons refused), slowest cut {slowest} ms: {slowestWhat}");
    }
}
