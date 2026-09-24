using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SprocketQoL;

/// Blueprint edits that create or combine add-on structures. Plain JSON, no game code, so they're tested offline.
public static class AddonEdits
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// A palette part in the same format as the game's cube (Parts\cubePlateStructurePart.json): a cube-sized
    /// n-sided cylinder of 5 mm plate, placed from "Addon Structures" and reshaped with the game's own tools.
    public static string RoundAddonPart(string guid, string name, int sides)
    {
        var (meshData, _) = Cylinder(0.125f, 0.25f, sides, 5);
        var src = meshData["mesh"]!;
        var faces = src["faces"]!.AsArray().Select(f => (JsonNode?)new JsonObject { ["v"] = Clone(f!["v"]), ["t"] = Clone(f["t"]) });
        var part = new JsonObject
        {
            ["v"] = "0.0", ["guid"] = guid, ["name"] = name, ["tags"] = new JsonArray("plateStructurePrefab"), ["transform"] = null,
            ["components"] = new JsonArray(new JsonObject
            {
                ["fileID"] = "plateStructurePrefab", ["type"] = "plateStructurePrefab",
                ["info"] = new JsonObject
                {
                    ["v"] = "0.0",
                    ["mesh"] = new JsonObject
                    {
                        ["v"] = "0.0", ["smoothAngle"] = meshData["smoothAngle"]!.GetValue<float>(), ["gridSize"] = 1, ["format"] = "freeform",
                        ["mesh"] = new JsonObject { ["majorVersion"] = 0, ["minorVersion"] = 0, ["vertices"] = Clone(src["vertices"]), ["faces"] = new JsonArray(faces.ToArray()) },
                    },
                },
            }),
        };
        return part.ToJsonString(Indented);
    }

    /// What an edit changes, so the game can apply it in place as one undoable step: the edited design, the mesh id of
    /// each part whose shape changed (and its face count before), parts to remove, parts to move onto a new parent,
    /// and whether in place is possible (not when a changed part shares its mesh with another part).
    public sealed record EditPlan(string DesignJson, Dictionary<int, int> MeshIds, Dictionary<int, int> OldFaces, List<int> Remove,
                                  List<(int Child, int Parent)> Reparent, bool Live, int Focus, string Summary);

    /// Whether part `id`'s mesh is its own (no mirror twin or copy shares its settings block or mesh).
    static bool OwnsMesh(JsonObject b, Dictionary<int, JsonObject> objects, int id)
    {
        var blocks = b["blueprints"]!.AsArray();
        int blockId = Conversion.Id(objects[id], "structureBlueprintVuid");
        int meshId = Conversion.Id(Block(blocks, blockId)["blueprint"]!, "bodyMeshVuid");
        return objects.Values.Count(o => o["structureBlueprintVuid"]?.GetValue<int>() == blockId) == 1
            && blocks.Count(x => x!["type"]?.GetValue<string>() == "structure" && x["blueprint"]?["bodyMeshVuid"]?.GetValue<int>() == meshId) == 1;
    }

    /// Folds add-ons `others` into `target`: their faces join target's shape at the same place in the world (each keeps
    /// its armour), parts attached to them move onto target, and the emptied parts are removed.
    public static string MergeAddons(string json, int target, IEnumerable<int> others) => PlanMerge(json, target, others).DesignJson;

    public static EditPlan PlanMerge(string json, int target, IEnumerable<int> others)
    {
        var b = Conversion.Parse(json);
        var objects = Conversion.Objects(b);
        var merge = others.Where(v => v != target).Distinct().ToList();
        if (merge.Count == 0) throw new Exception("Select at least one other add-on to merge.");
        foreach (var v in merge.Append(target))
            if (!objects.TryGetValue(v, out var o) || Conversion.GuidOf(o) != Conversion.AddonGuid)
                throw new Exception("Only add-on structures can be merged; refresh your selection.");
        for (int p = Conversion.Id(objects[target], "pvuid"); objects.TryGetValue(p, out var up); p = Conversion.Id(up, "pvuid"))
            if (merge.Contains(p)) throw new Exception("Open the outermost add-on's panel and merge into that one.");

        var blocks = b["blueprints"]!.AsArray();
        var meshes = b["meshes"]!.AsArray();
        var before = Conversion.WorldMatrices(objects);
        if (!Matrix4x4.Invert(before[target], out var intoTarget)) throw new Exception("The add-on's transform can't be inverted.");

        bool live = OwnsMesh(b, objects, target);
        var reparent = new List<(int Child, int Parent)>();
        var (targetStructure, targetMesh) = OwnMesh(b, objects, target, "merged");
        var into = targetMesh["mesh"]!.AsObject();
        var intoRivets = targetMesh["rivets"]?.AsObject();
        int facesBefore = into["faces"]!.AsArray().Count;

        foreach (int v in merge)
        {
            var structure = Block(blocks, Conversion.Id(objects[v], "structureBlueprintVuid"))["blueprint"]!;
            var meshData = MeshOf(meshes, Conversion.Id(structure, "bodyMeshVuid"));
            var mesh = meshData?["mesh"] ?? throw new Exception("Only hand-made (freeform) add-ons can be merged.");
            Append(into, intoRivets, mesh.AsObject(), meshData!["rivets"]?.AsObject(), before[v] * intoTarget);
            targetStructure["armourVolume"] = targetStructure["armourVolume"]!.GetValue<double>() + (structure["armourVolume"]?.GetValue<double>() ?? 0);
            var shift = before[v] * intoTarget; // add-on v's frame expressed in target's frame
            foreach (var child in objects.Values.Where(o => Conversion.Id(o, "pvuid") == v && !merge.Contains(Conversion.Id(o, "vuid"))))
            {
                reparent.Add((Conversion.Id(child, "vuid"), target));
                var t = child["transform"]!.AsObject();
                if (IsTranslation(shift))
                {
                    // Usual case, add-ons not rotated against each other: move the part, keep its exact rotation and scale
                    // (re-deriving Euler angles near 90 degrees loses precision).
                    var p = t["pos"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray();
                    var q = Vector3.Transform(new Vector3(p[0], p[1], p[2]), shift);
                    t["pos"] = Array(q.X, q.Y, q.Z);
                    child["pvuid"] = target;
                    continue;
                }
                try { Conversion.WriteTransform(t, before[Conversion.Id(child, "vuid")] * intoTarget); }
                catch (Exception ex)
                {
                    throw new Exception($"Part {Conversion.Id(child, "vuid")} on add-on {v} can't be moved exactly onto the merged add-on ({ex.Message}); merge cancelled.");
                }
                child["pvuid"] = target;
            }
        }

        RemoveParts(b, objects, merge);
        var after = Conversion.WorldMatrices(Conversion.Objects(b));
        foreach (var (id, matrix) in after)
            if (!Conversion.Near(before[id], matrix)) throw new Exception($"Part {id} would move; merge cancelled.");
        return new EditPlan(b.ToJsonString(Indented), new() { [target] = Conversion.Id(targetStructure, "bodyMeshVuid") }, new() { [target] = facesBefore },
                            merge, reparent, live, target, $"target={target}, merged={string.Join(",", merge)}, moved parts={reparent.Count}");
    }

    /// Boolean cut: the shape of add-on `cutter` (and its mirror twin, if any) is cut out of the other selected
    /// structures, or out of the structure it sits on when nothing else is selected. Any closed shape works. With
    /// `pocket`, the add-on's surface inside the structure becomes walls and a floor (a recess instead of a hole).
    public static EditPlan PlanCut(string json, int cutter, IEnumerable<int> selected, bool removeCutter, bool pocket, bool light = true)
    {
        var b = Conversion.Parse(json);
        var objects = Conversion.Objects(b);
        if (!objects.TryGetValue(cutter, out var cutObj) || Conversion.GuidOf(cutObj) != Conversion.AddonGuid)
            throw new Exception("Only an add-on can cut; refresh your selection.");
        var cutters = new List<int> { cutter };
        if (cutObj["transform"]?["mirrorVuid"] is JsonValue mv && mv.TryGetValue<int>(out int twin) && objects.TryGetValue(twin, out var twinObj)
            && Conversion.GuidOf(twinObj) == Conversion.AddonGuid) cutters.Add(twin);
        bool Structure(int v) => !cutters.Contains(v) && objects.TryGetValue(v, out var o) && o["structureBlueprintVuid"] != null;
        var targets = selected.Where(Structure).Distinct().ToList();
        if (targets.Count == 0) targets = cutters.Select(c => Conversion.Id(objects[c], "pvuid")).Where(Structure).Distinct().ToList();
        if (targets.Count == 0) throw new Exception("Select the shape to cut as well (this add-on isn't sitting on one).");

        var blocks = b["blueprints"]!.AsArray();
        var meshes = b["meshes"]!.AsArray();
        var before = Conversion.WorldMatrices(objects);
        var shapes = cutters.Select(c =>
        {
            var mesh = MeshOf(meshes, Conversion.Id(Block(blocks, Conversion.Id(objects[c], "structureBlueprintVuid"))["blueprint"]!, "bodyMeshVuid"))?["mesh"]
                       ?? throw new Exception("The cutting add-on has no hand-made shape.");
            var raw = mesh["vertices"]!.AsArray().Select(x => MeshCut.F(x)).ToArray();
            var world = Enumerable.Range(0, raw.Length / 3).Select(i => Vector3.Transform(new Vector3(raw[3 * i], raw[3 * i + 1], raw[3 * i + 2]), before[c])).ToList();
            var faces = mesh["faces"]!.AsArray().Select(f => f!.AsObject()).ToList();
            return (World: world,
                    Faces: faces.Select(f => f["v"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray()).ToList(),
                    Thickness: faces.Select(f => f["t"]!.AsArray().Select(x => MeshCut.F(x)).ToArray()).ToList(),
                    Modes: faces.Select(f => Enumerable.Range(0, f["v"]!.AsArray().Count).Select(k => (byte)((ulong)MeshCut.L(f["tm"]) >> (8 * k))).ToArray()).ToList());
        }).ToList();

        bool live = true;
        var meshIds = new Dictionary<int, int>();
        var oldFaces = new Dictionary<int, int>();
        var report = new List<string>();
        foreach (int target in targets)
        {
            if (!Matrix4x4.Invert(before[target], out var intoTarget)) throw new Exception($"Part {target}'s transform can't be inverted.");
            var solids = shapes.Select(s => Outward(s.World.Select(p => Vector3.Transform(p, intoTarget)).ToList(), s.Faces, s.Thickness, s.Modes)).ToList();
            live &= OwnsMesh(b, objects, target); // a part sharing its mesh with a copy only gets its own mesh by reloading
            var (structure, meshData) = OwnMesh(b, objects, target, "cut");
            int facesBefore = meshData["mesh"]!["faces"]!.AsArray().Count;
            var result = MeshCut.Cut(meshData, solids, pocket, light);
            oldFaces[target] = facesBefore;
            if (result.FacesCut == 0) continue;
            structure["armourVolume"] = Math.Max(0, (structure["armourVolume"]?.GetValue<double>() ?? 0) + result.ArmourChange);
            meshIds[target] = Conversion.Id(structure, "bodyMeshVuid");
            report.Add($"{result.FacesCut} faces of part {target}" + (pocket ? $" ({result.PocketFaces} pocket plates)" : "")
                       + (result.RivetsMoved + result.RivetsDropped > 0 ? $", rivets {result.RivetsMoved} moved {result.RivetsDropped} removed" : ""));
        }
        if (report.Count == 0) throw new Exception("The add-on doesn't overlap the shape it would cut; move it into the plate first.");

        string removed = "";
        var remove = new List<int>();
        if (removeCutter)
        {
            if (objects.Values.Any(o => cutters.Contains(Conversion.Id(o, "pvuid")))) removed = " Kept the add-on: other parts are attached to it.";
            else { RemoveParts(b, objects, cutters); remove = cutters; removed = " Removed the cutting add-on."; }
        }
        var after = Conversion.WorldMatrices(Conversion.Objects(b));
        foreach (var (id, matrix) in after)
            if (!Conversion.Near(before[id], matrix)) throw new Exception($"Part {id} would move; cut cancelled.");
        return new EditPlan(b.ToJsonString(Indented), meshIds, oldFaces, remove, new(), live, remove.Count > 0 ? meshIds.Keys.First() : cutter,
                           "Cut " + string.Join(", ", report) + "." + removed);
    }

    /// A cutting shape with its faces turned to face outward (a mirrored add-on's faces point inward).
    static MeshCut.Solid Outward(List<Vector3> verts, List<int[]> faces, List<float[]> thickness, List<byte[]> modes)
    {
        double volume = faces.Sum(f => Enumerable.Range(1, f.Length - 2).Sum(k => Vector3.Dot(verts[f[0]], Vector3.Cross(verts[f[k]], verts[f[k + 1]])) / 6.0));
        if (volume >= 0) return new MeshCut.Solid(verts, faces, thickness, modes);
        return new MeshCut.Solid(verts, faces.Select(f => f.Reverse().ToArray()).ToList(),
            thickness.Select(t => t.Reverse().ToArray()).ToList(), modes.Select(m => m.Reverse().ToArray()).ToList());
    }

    /// The structure block and mesh data of part `id`, copied first if a mirror twin or copy shares them, so an edit
    /// changes this part only.
    static (JsonObject Structure, JsonObject MeshData) OwnMesh(JsonObject b, Dictionary<int, JsonObject> objects, int id, string what)
    {
        var blocks = b["blueprints"]!.AsArray();
        var meshes = b["meshes"]!.AsArray();
        var obj = objects[id];
        var block = Block(blocks, Conversion.Id(obj, "structureBlueprintVuid"));
        if (objects.Values.Any(o => o != obj && o["structureBlueprintVuid"]?.GetValue<int>() == block["id"]!.GetValue<int>()))
        {
            block = Clone(block).AsObject();
            block["id"] = NextId(blocks, "id");
            blocks.Add(block);
            obj["structureBlueprintVuid"] = block["id"]!.GetValue<int>();
        }
        var structure = block["blueprint"]!.AsObject();
        int meshId = Conversion.Id(structure, "bodyMeshVuid");
        if (blocks.Count(x => x!["type"]?.GetValue<string>() == "structure" && x["blueprint"]?["bodyMeshVuid"]?.GetValue<int>() == meshId) > 1)
        {
            var copy = Clone(meshes.First(m => m!["vuid"]!.GetValue<int>() == meshId)).AsObject();
            copy["vuid"] = meshId = NextId(meshes, "vuid");
            meshes.Add(copy);
            structure["bodyMeshVuid"] = meshId;
        }
        var meshData = MeshOf(meshes, meshId);
        if (meshData?["mesh"] is not JsonObject) throw new Exception($"Only hand-made (freeform) shapes can be {what}.");
        return (structure, meshData);
    }

    /// Removes parts, then the settings blocks and meshes only they used, and unlinks mirror twins pointing at them.
    static void RemoveParts(JsonObject b, Dictionary<int, JsonObject> objects, ICollection<int> gone)
    {
        var blocks = b["blueprints"]!.AsArray();
        var meshes = b["meshes"]!.AsArray();
        var goneBlocks = gone.Select(v => Conversion.Id(objects[v], "structureBlueprintVuid")).ToHashSet();
        var goneMeshes = goneBlocks.Select(id => Conversion.Id(Block(blocks, id)["blueprint"]!, "bodyMeshVuid")).ToHashSet();
        var list = b["objects"]!.AsArray();
        for (int i = list.Count - 1; i >= 0; i--) if (gone.Contains(Conversion.Id(list[i]!, "vuid"))) list.RemoveAt(i);
        var liveBlocks = list.SelectMany(o => o!.AsObject()).Where(kv => kv.Key.EndsWith("BlueprintVuid")).Select(kv => kv.Value!.GetValue<int>()).ToHashSet();
        for (int i = blocks.Count - 1; i >= 0; i--)
        {
            int id = Conversion.Id(blocks[i]!, "id");
            if (goneBlocks.Contains(id) && !liveBlocks.Contains(id)) blocks.RemoveAt(i);
        }
        var liveMeshes = blocks.Where(x => x!["type"]?.GetValue<string>() == "structure")
            .Select(x => x!["blueprint"]?["bodyMeshVuid"]?.GetValue<int>() ?? -1).ToHashSet();
        for (int i = meshes.Count - 1; i >= 0; i--)
        {
            int id = meshes[i]!["vuid"]!.GetValue<int>();
            if (goneMeshes.Contains(id) && !liveMeshes.Contains(id)) meshes.RemoveAt(i);
        }
        foreach (var o in list)
            if (o!["transform"]?["mirrorVuid"] is JsonValue mv && mv.TryGetValue<int>(out int mirror) && gone.Contains(mirror)) o["transform"]!["mirrorVuid"] = -1;
    }

    // Adds `mesh` (vertices mapped through `m`) to `into`, keeping each face's armour and any rivets.
    static void Append(JsonObject into, JsonObject? intoRivets, JsonObject mesh, JsonObject? rivets, Matrix4x4 m)
    {
        var verts = into["vertices"]!.AsArray();
        int baseV = verts.Count / 3, baseE = into["edges"]!.AsArray().Count / 2, baseF = into["faces"]!.AsArray().Count;
        bool mirrored = m.GetDeterminant() < 0; // a mirrored placement turns faces inside out unless reversed
        var src = mesh["vertices"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray();
        for (int i = 0; i < src.Length; i += 3)
        {
            var p = Vector3.Transform(new Vector3(src[i], src[i + 1], src[i + 2]), m);
            verts.Add((JsonNode?)p.X); verts.Add((JsonNode?)p.Y); verts.Add((JsonNode?)p.Z); // JsonNode casts, not generic Add<T>
        }
        foreach (var e in mesh["edges"]!.AsArray()) into["edges"]!.AsArray().Add((JsonNode?)(e!.GetValue<int>() + baseV));
        foreach (var f in mesh["edgeFlags"]!.AsArray()) into["edgeFlags"]!.AsArray().Add(Clone(f));
        foreach (var face in mesh["faces"]!.AsArray())
        {
            var copy = Clone(face).AsObject();
            var v = copy["v"]!.AsArray().Select(x => x!.GetValue<int>() + baseV).ToList();
            var t = copy["t"]!.AsArray().Select(x => Clone(x)).ToList();
            if (mirrored) { v.Reverse(); t.Reverse(); }
            copy["v"] = new JsonArray(v.Select(x => (JsonNode?)x).ToArray());
            copy["t"] = new JsonArray(t.ToArray());
            into["faces"]!.AsArray().Add(copy);
        }
        if (rivets == null || intoRivets == null) return;
        var profiles = intoRivets["profiles"]!.AsArray();
        int baseP = profiles.Count, baseN = intoRivets["nodes"]!.AsArray().Count;
        foreach (var p in rivets["profiles"]!.AsArray()) profiles.Add(Clone(p));
        foreach (var n in rivets["nodes"]!.AsArray())
        {
            var node = Clone(n).AsObject();
            node["face"] = node["face"]!.GetValue<int>() + baseF;
            node["profile"] = node["profile"]!.GetValue<int>() + baseP;
            foreach (var link in new[] { "next", "prev" })
                if (node[link]?.GetValue<int>() is int to && to >= 0) node[link] = to + baseN;
            intoRivets["nodes"]!.AsArray().Add(node);
        }
    }

    /// A closed n-sided cylinder standing on y = 0, with the same face layout the game writes.
    internal static (JsonObject MeshData, double ArmourVolume) Cylinder(float r, float h, int n, int thicknessMm)
    {
        var verts = new List<Vector3>();
        for (int ring = 0; ring < 2; ring++)
            for (int i = 0; i < n; i++)
            {
                double a = 2 * Math.PI * i / n;
                verts.Add(new((float)(r * Math.Cos(a)), ring * h, (float)(r * Math.Sin(a))));
            }
        int bottom = verts.Count; verts.Add(new(0, 0, 0));
        int top = verts.Count; verts.Add(new(0, h, 0));
        var faces = new List<int[]>();
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            faces.Add(new[] { i, n + i, n + j, j });   // side, facing out
            faces.Add(new[] { top, n + j, n + i });     // top cap, facing up
            faces.Add(new[] { bottom, i, j });          // bottom cap, facing down
        }
        // Smooth across the sides (360/n degrees apart) but keep the 90-degree cap edges sharp.
        float smooth = n >= 6 ? 360f / n + 1 : 0;
        return (MeshData(verts, faces, thicknessMm, smooth), faces.Sum(f => Area(verts, f)) * thicknessMm / 1000.0);
    }

    static JsonObject MeshData(List<Vector3> verts, List<int[]> faces, int thicknessMm, float smoothAngle)
    {
        var edges = new List<(int, int)>();
        var seen = new HashSet<(int, int)>();
        foreach (var f in faces)
            for (int k = 0; k < f.Length; k++)
            {
                var e = (Math.Min(f[k], f[(k + 1) % f.Length]), Math.Max(f[k], f[(k + 1) % f.Length]));
                if (seen.Add(e)) edges.Add(e);
            }
        return new JsonObject
        {
            ["v"] = "0.2", ["name"] = null, ["smoothAngle"] = smoothAngle, ["gridSize"] = 1, ["format"] = "freeform",
            ["mesh"] = new JsonObject
            {
                ["majorVersion"] = 0, ["minorVersion"] = 3,
                ["vertices"] = new JsonArray(verts.SelectMany(v => new[] { v.X, v.Y, v.Z }).Select(x => (JsonNode?)x).ToArray()),
                ["edges"] = new JsonArray(edges.SelectMany(e => new[] { e.Item1, e.Item2 }).Select(x => (JsonNode?)x).ToArray()),
                ["edgeFlags"] = new JsonArray(edges.Select(_ => (JsonNode?)0).ToArray()),
                ["faces"] = new JsonArray(faces.Select(f => (JsonNode?)new JsonObject
                {
                    ["v"] = new JsonArray(f.Select(x => (JsonNode?)x).ToArray()),
                    ["t"] = new JsonArray(f.Select(_ => (JsonNode?)thicknessMm).ToArray()),
                    ["tm"] = f.Length == 4 ? 0x01010101 : 0x010101, // one byte per corner, as the game writes
                    ["te"] = 0,
                }).ToArray()),
            },
            ["rivets"] = new JsonObject
            {
                ["profiles"] = new JsonArray(new JsonObject { ["model"] = 0, ["spacing"] = 0.1, ["diameter"] = 0.05, ["height"] = 0.025, ["padding"] = 0.04 }),
                ["nodes"] = new JsonArray(),
            },
        };
    }

    static double Area(List<Vector3> v, int[] f)
    {
        var sum = Vector3.Zero;
        for (int k = 1; k < f.Length - 1; k++) sum += Vector3.Cross(v[f[k]] - v[f[0]], v[f[k + 1]] - v[f[0]]);
        return sum.Length() / 2;
    }

    internal static JsonObject Block(JsonArray blocks, int id) =>
        blocks.FirstOrDefault(x => x!["id"]?.GetValue<int>() == id)?.AsObject() ?? throw new Exception($"Settings block {id} is missing.");

    internal static JsonObject? MeshOf(JsonArray meshes, int vuid) =>
        meshes.FirstOrDefault(m => m!["vuid"]?.GetValue<int>() == vuid)?["meshData"]?.AsObject();

    static bool IsTranslation(Matrix4x4 m) =>
        new[] { m.M11 - 1, m.M22 - 1, m.M33 - 1, m.M12, m.M13, m.M21, m.M23, m.M31, m.M32 }.All(x => Math.Abs(x) < 1e-5f);

    static int NextId(JsonArray list, string key) => list.Select(x => x![key]?.GetValue<int>() ?? 0).DefaultIfEmpty(0).Max() + 1;

    // JsonNode.DeepClone only exists from .NET 8; the game runs mods on .NET 6.
    static JsonNode Clone(JsonNode? node) => JsonNode.Parse(node!.ToJsonString())!;

    static JsonArray Array(params float[] values) => new(values.Select(v => (JsonNode?)v).ToArray());
}
