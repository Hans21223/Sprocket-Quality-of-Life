using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SprocketQoL;

/// Pure maths and JSON manipulation for vehicle part copying, cutting, and pasting.
/// Can be tested offline without Unity or BepInEx.
public static class BlueprintClipboard
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// Directory where the clipboard file is saved. In the game this is BepInExRootPath; in tests it defaults to BaseDirectory.
    public static string? StorageDirectory { get; set; }

    static string? memoryJson;

    public static string ClipboardFilePath
    {
        get
        {
            string dir = StorageDirectory ?? "";
            if (string.IsNullOrEmpty(dir))
                dir = Environment.GetEnvironmentVariable("SPROCKET_QOL_DIR") ?? AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(dir, "SprocketQoL_clipboard.json");
        }
    }

    public static bool HasData => !string.IsNullOrEmpty(GetClipboardJson());

    public static string? GetClipboardJson()
    {
        if (!string.IsNullOrEmpty(memoryJson)) return memoryJson;
        try
        {
            if (File.Exists(ClipboardFilePath))
                return memoryJson = File.ReadAllText(ClipboardFilePath);
        }
        catch { }
        return null;
    }

    public static void SetClipboardJson(string json)
    {
        memoryJson = json;
        try { File.WriteAllText(ClipboardFilePath, json); }
        catch { }
    }

    public static void Clear()
    {
        memoryJson = null;
        try { if (File.Exists(ClipboardFilePath)) File.Delete(ClipboardFilePath); }
        catch { }
    }

    static float[] MatrixToArray(Matrix4x4 m) => new[]
    {
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44
    };

    static Matrix4x4 ArrayToMatrix(float[] a) => new(
        a[0], a[1], a[2], a[3],
        a[4], a[5], a[6], a[7],
        a[8], a[9], a[10], a[11],
        a[12], a[13], a[14], a[15]
    );

    /// Expands the set of part VUIDs to include associated assemblies:
    /// 1. If a Turret Body is included, include its Turret Ring.
    /// 2. If a Turret Ring is included, include its Turret Body.
    /// 3. Recursively includes all child/descendant parts (attached add-ons, decals, guns, hatches, crew, etc.).
    public static HashSet<int> ExpandPartHierarchy(Dictionary<int, JsonObject> objects, IEnumerable<int> initialVuids)
    {
        var all = new HashSet<int>(initialVuids.Where(v => objects.ContainsKey(v)));
        var children = objects.Values.GroupBy(o => Conversion.Id(o, "pvuid")).ToDictionary(g => g.Key, g => g.Select(o => Conversion.Id(o, "vuid")).ToList());

        // Connect Turret Bodies <-> Turret Rings
        var rings = objects.Values.Where(o => Conversion.GuidOf(o) == Conversion.RingGuid).ToList();
        var extra = new List<int>();
        foreach (int v in all)
        {
            var o = objects[v];
            int p = Conversion.Id(o, "pvuid");
            if (p >= 0 && objects.TryGetValue(p, out var parent) && Conversion.GuidOf(parent) == Conversion.RingGuid)
                extra.Add(p);

            foreach (var r in rings)
            {
                int bodyId = r["structureID"]?.GetValue<int>()
                    ?? r["compartmentBodyID"]?["structureVuid"]?.GetValue<int>() ?? -1;
                if (bodyId == v) extra.Add(Conversion.Id(r, "vuid"));
                if (Conversion.Id(r, "vuid") == v && bodyId >= 0 && objects.ContainsKey(bodyId)) extra.Add(bodyId);
            }
        }
        foreach (int e in extra) all.Add(e);

        var queue = new Queue<int>(all);
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            if (children.TryGetValue(cur, out var kids))
            {
                foreach (var kid in kids)
                    if (all.Add(kid)) queue.Enqueue(kid);
            }
            if (objects.TryGetValue(cur, out var curObj) && Conversion.GuidOf(curObj) == Conversion.RingGuid)
            {
                int bodyId = curObj["structureID"]?.GetValue<int>()
                    ?? curObj["compartmentBodyID"]?["structureVuid"]?.GetValue<int>() ?? -1;
                if (bodyId >= 0 && objects.ContainsKey(bodyId) && all.Add(bodyId))
                    queue.Enqueue(bodyId);
            }
        }

        return all;
    }

    /// Copies the selected parts (and all their child / descendant parts) from the blueprint JSON,
    /// packaging them with all referenced blueprints, meshes, and root world transforms.
    public static (string ClipboardJson, int Count) Copy(string blueprintJson, ICollection<int> selectedVuids)
    {
        var b = Conversion.Parse(blueprintJson);
        var objects = Conversion.Objects(b);
        if (selectedVuids.Count == 0) return ("", 0);

        var allToCopy = ExpandPartHierarchy(objects, selectedVuids);
        if (allToCopy.Count == 0) return ("", 0);

        var rootVuids = allToCopy.Where(v => !allToCopy.Contains(Conversion.Id(objects[v], "pvuid"))).ToList();
        var worldMatrices = Conversion.WorldMatrices(objects);
        var rootTransforms = new JsonObject();
        foreach (int r in rootVuids)
        {
            var wm = worldMatrices.TryGetValue(r, out var mat) ? mat : Matrix4x4.Identity;
            rootTransforms[r.ToString()] = JsonNode.Parse($"[{string.Join(",", MatrixToArray(wm).Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))}]");
        }

        var blocks = b["blueprints"]?.AsArray() ?? new JsonArray();
        var blueprintIds = new HashSet<int>();
        foreach (int v in allToCopy)
        {
            var o = objects[v];
            foreach (var kv in o)
            {
                if ((kv.Key.EndsWith("BlueprintVuid") || kv.Key.EndsWith("ConstraintsVuid") || kv.Key == "structureBlueprintVuid")
                    && kv.Value is JsonValue val && val.TryGetValue<int>(out int id))
                {
                    blueprintIds.Add(id);
                }
            }
        }

        var queueB = new Queue<int>(blueprintIds);
        while (queueB.Count > 0)
        {
            int bId = queueB.Dequeue();
            var block = blocks.FirstOrDefault(x => Conversion.Id(x!, "id") == bId);
            if (block?["blueprint"] is JsonObject bpObj)
            {
                foreach (var kv in bpObj)
                {
                    if (kv.Key.EndsWith("BlueprintVuid") && kv.Value is JsonValue val && val.TryGetValue<int>(out int subId))
                    {
                        if (blueprintIds.Add(subId)) queueB.Enqueue(subId);
                    }
                }
            }
        }

        var meshes = b["meshes"]?.AsArray() ?? new JsonArray();
        var meshIds = new HashSet<int>();
        foreach (int bId in blueprintIds)
        {
            var block = blocks.FirstOrDefault(x => Conversion.Id(x!, "id") == bId);
            if (block?["blueprint"]?["bodyMeshVuid"] is JsonValue mv && mv.TryGetValue<int>(out int meshId))
                meshIds.Add(meshId);
        }

        var clip = new JsonObject
        {
            ["version"] = 1,
            ["copiedAt"] = DateTime.UtcNow.ToString("o"),
            ["sourceVehicle"] = b["header"]?["name"]?.GetValue<string>() ?? "",
            ["rootVuids"] = JsonNode.Parse($"[{string.Join(",", rootVuids)}]")!.AsArray(),
            ["rootTransforms"] = rootTransforms,
            ["objects"] = new JsonArray(allToCopy.Select(v => JsonNode.Parse(objects[v].ToJsonString())!).ToArray()),
            ["blueprints"] = new JsonArray(blueprintIds.Select(id => blocks.FirstOrDefault(x => Conversion.Id(x!, "id") == id)).Where(x => x != null).Select(x => JsonNode.Parse(x!.ToJsonString())!).ToArray()),
            ["meshes"] = new JsonArray(meshIds.Select(id => meshes.FirstOrDefault(x => x!["vuid"]?.GetValue<int>() == id)).Where(x => x != null).Select(x => JsonNode.Parse(x!.ToJsonString())!).ToArray()),
        };

        return (clip.ToJsonString(Indented), allToCopy.Count);
    }

    /// Pastes parts from clipboard JSON into the target blueprint JSON.
    /// Remaps all IDs to prevent collision, preserves world coordinates, attaches root parts to `targetParentVuid` (or hull),
    /// and returns the modified blueprint JSON, the focused new part VUID, and the count of pasted parts.
    public static (string Json, int FocusVuid, int PastedCount) PasteInto(string targetBlueprintJson, string clipboardJson, int targetParentVuid = -1)
    {
        var b = Conversion.Parse(targetBlueprintJson);
        var clip = JsonNode.Parse(clipboardJson)?.AsObject() ?? throw new Exception("Invalid clipboard data.");
        var clipObjects = clip["objects"]?.AsArray();
        if (clipObjects == null || clipObjects.Count == 0) throw new Exception("Clipboard is empty.");

        var targetObjects = Conversion.Objects(b);
        var targetBlocks = b["blueprints"]?.AsArray() ?? new JsonArray();
        if (b["blueprints"] == null) b["blueprints"] = targetBlocks;
        var targetMeshes = b["meshes"]?.AsArray() ?? new JsonArray();
        if (b["meshes"] == null) b["meshes"] = targetMeshes;

        int hullVuid = targetObjects.Values.FirstOrDefault(o => Conversion.Id(o, "pvuid") == -1) is { } h ? Conversion.Id(h, "vuid") : (targetObjects.ContainsKey(0) ? 0 : targetObjects.Keys.First());
        if (targetParentVuid < 0 || !targetObjects.ContainsKey(targetParentVuid)) targetParentVuid = hullVuid;

        var targetWorld = Conversion.WorldMatrices(targetObjects);
        Matrix4x4 parentWorld = targetWorld.TryGetValue(targetParentVuid, out var pw) ? pw : Matrix4x4.Identity;
        Matrix4x4 invParentWorld = Matrix4x4.Invert(parentWorld, out var ipw) ? ipw : Matrix4x4.Identity;

        int maxVuid = targetObjects.Keys.DefaultIfEmpty(0).Max();
        int maxComp = targetObjects.Values.SelectMany(o => o.Where(kv => kv.Value is JsonValue v && v.TryGetValue<int>(out _) && kv.Key is not ("pvuid" or "flags")).Select(kv => kv.Value!.GetValue<int>())).DefaultIfEmpty(0).Max();
        int nextVuid = Math.Max(maxVuid, maxComp) + 1;
        int nextBlock = targetBlocks.Select(x => Conversion.Id(x!, "id")).DefaultIfEmpty(0).Max() + 1;
        int nextMesh = targetMeshes.Select(m => m!["vuid"]?.GetValue<int>() ?? 0).DefaultIfEmpty(0).Max() + 1;

        var meshMap = new Dictionary<int, int>();
        var blueprintMap = new Dictionary<int, int>();
        var vuidMap = new Dictionary<int, int>();

        foreach (var mNode in clip["meshes"]?.AsArray() ?? new())
        {
            int oldId = Conversion.Id(mNode!, "vuid");
            meshMap[oldId] = nextMesh++;
        }

        foreach (var bNode in clip["blueprints"]?.AsArray() ?? new())
        {
            int oldId = Conversion.Id(bNode!, "id");
            blueprintMap[oldId] = nextBlock++;
        }

        foreach (var oNode in clipObjects)
        {
            var o = oNode!.AsObject();
            int oldId = Conversion.Id(o, "vuid");
            vuidMap[oldId] = nextVuid++;
            foreach (var key in Conversion.ComponentKeys(o))
            {
                if (o[key] is JsonValue cv && cv.TryGetValue<int>(out int oldComp))
                    vuidMap[oldComp] = nextVuid++;
            }
        }

        foreach (var mNode in clip["meshes"]?.AsArray() ?? new())
        {
            var m = JsonNode.Parse(mNode!.ToJsonString())!.AsObject();
            int oldId = Conversion.Id(m, "vuid");
            m["vuid"] = meshMap[oldId];
            targetMeshes.Add(m);
        }

        foreach (var bNode in clip["blueprints"]?.AsArray() ?? new())
        {
            var blk = JsonNode.Parse(bNode!.ToJsonString())!.AsObject();
            int oldId = Conversion.Id(blk, "id");
            blk["id"] = blueprintMap[oldId];
            if (blk["blueprint"] is JsonObject payload)
            {
                if (payload["bodyMeshVuid"] is JsonValue bmv && bmv.TryGetValue<int>(out int oldMesh) && meshMap.TryGetValue(oldMesh, out int newMesh))
                    payload["bodyMeshVuid"] = newMesh;
                if (payload["motorVuid"] is JsonValue mv && mv.TryGetValue<int>(out int oldMotor) && vuidMap.TryGetValue(oldMotor, out int newMotor))
                    payload["motorVuid"] = newMotor;
                if (payload["barrelVuids"] is JsonArray barrels)
                {
                    for (int i = 0; i < barrels.Count; i++)
                        if (barrels[i] is JsonValue bv && bv.TryGetValue<int>(out int oldBv) && vuidMap.TryGetValue(oldBv, out int newBv))
                            barrels[i] = newBv;
                }
                if (payload["operatedBehaviours"] is JsonArray opBeh)
                {
                    for (int i = 0; i < opBeh.Count; i++)
                        if (opBeh[i] is JsonValue ob && ob.TryGetValue<int>(out int oldOb) && vuidMap.TryGetValue(oldOb, out int newOb))
                            opBeh[i] = newOb;
                }
            }
            targetBlocks.Add(blk);
        }

        var targetObjectsArray = b["objects"]!.AsArray();
        var rootTransforms = clip["rootTransforms"] as JsonObject;
        int focusVuid = -1;

        foreach (var oNode in clipObjects)
        {
            var o = JsonNode.Parse(oNode!.ToJsonString())!.AsObject();
            int oldVuid = Conversion.Id(o, "vuid");
            int newVuid = vuidMap[oldVuid];
            o["vuid"] = newVuid;
            if (focusVuid < 0) focusVuid = newVuid;

            foreach (var key in Conversion.ComponentKeys(o))
            {
                if (o[key] is JsonValue cv && cv.TryGetValue<int>(out int oldComp) && vuidMap.TryGetValue(oldComp, out int newComp))
                    o[key] = newComp;
            }

            foreach (var kv in o.Where(kv => kv.Key.EndsWith("BlueprintVuid") || kv.Key.EndsWith("ConstraintsVuid") || kv.Key == "structureBlueprintVuid").ToList())
            {
                if (kv.Value is JsonValue val && val.TryGetValue<int>(out int oldB) && blueprintMap.TryGetValue(oldB, out int newB))
                    o[kv.Key] = newB;
            }

            if (o["structureID"] is JsonValue sVal && sVal.TryGetValue<int>(out int oldStr) && vuidMap.TryGetValue(oldStr, out int newStr))
                o["structureID"] = newStr;

            if (o["compartmentBodyID"]?["structureVuid"] is JsonValue cbVal && cbVal.TryGetValue<int>(out int oldCb) && vuidMap.TryGetValue(oldCb, out int newCb))
                o["compartmentBodyID"]!["structureVuid"] = newCb;

            int oldPvuid = Conversion.Id(o, "pvuid");
            if (vuidMap.TryGetValue(oldPvuid, out int newParent))
            {
                o["pvuid"] = newParent;
            }
            else
            {
                o["pvuid"] = targetParentVuid;
                if (rootTransforms != null && rootTransforms[oldVuid.ToString()] is JsonArray jArr && jArr.Count == 16)
                {
                    float[] vals = jArr.Select(x => x!.GetValue<float>()).ToArray();
                    var oldWorldMat = ArrayToMatrix(vals);
                    var newLocalMat = oldWorldMat * invParentWorld;
                    try { Conversion.WriteTransform(o["transform"]!.AsObject(), newLocalMat); }
                    catch { /* Keep original local transform if decomposition had shear */ }
                }
            }

            if (o["transform"]?["mirrorVuid"] is JsonValue mv && mv.TryGetValue<int>(out int oldM) && vuidMap.TryGetValue(oldM, out int newM))
            {
                o["transform"]!["mirrorVuid"] = newM;
            }
            else
            {
                AddonEdits.Unlink(o);
            }

            targetObjectsArray.Add(o);
        }

        return (b.ToJsonString(Indented), focusVuid, clipObjects.Count);
    }

    /// Removes parts and their descendants from the blueprint JSON, unlinking broken mirror references
    /// and cleaning up orphaned blueprints and meshes. Protects the vehicle hull from deletion.
    public static string RemoveFrom(string blueprintJson, ICollection<int> toRemoveVuids)
    {
        var b = Conversion.Parse(blueprintJson);
        var objects = Conversion.Objects(b);
        if (toRemoveVuids.Count == 0) return blueprintJson;

        var dead = ExpandPartHierarchy(objects, toRemoveVuids);

        // Never remove the base hull
        dead.RemoveWhere(v => objects.TryGetValue(v, out var o) && Conversion.Id(o, "pvuid") == -1);
        if (dead.Count == 0) return blueprintJson;

        var objectArray = b["objects"]!.AsArray();
        for (int i = objectArray.Count - 1; i >= 0; i--)
        {
            if (dead.Contains(Conversion.Id(objectArray[i]!, "vuid")))
                objectArray.RemoveAt(i);
        }

        var surviving = Conversion.Objects(b);
        foreach (var o in surviving.Values)
        {
            if (o["transform"]?["mirrorVuid"] is JsonValue mv && mv.TryGetValue<int>(out int m) && dead.Contains(m))
                AddonEdits.Unlink(o);
        }

        // Clean up dead blueprints
        var liveBlueprintIds = new HashSet<int>();
        foreach (var o in surviving.Values)
        {
            foreach (var kv in o)
            {
                if ((kv.Key.EndsWith("BlueprintVuid") || kv.Key.EndsWith("ConstraintsVuid") || kv.Key == "structureBlueprintVuid")
                    && kv.Value is JsonValue val && val.TryGetValue<int>(out int id))
                {
                    liveBlueprintIds.Add(id);
                }
            }
        }

        var blocks = b["blueprints"]?.AsArray() ?? new JsonArray();
        var queueB = new Queue<int>(liveBlueprintIds);
        while (queueB.Count > 0)
        {
            int bId = queueB.Dequeue();
            var block = blocks.FirstOrDefault(x => Conversion.Id(x!, "id") == bId);
            if (block?["blueprint"] is JsonObject bpObj)
            {
                foreach (var kv in bpObj)
                {
                    if (kv.Key.EndsWith("BlueprintVuid") && kv.Value is JsonValue val && val.TryGetValue<int>(out int subId))
                        if (liveBlueprintIds.Add(subId)) queueB.Enqueue(subId);
                }
            }
        }

        for (int i = blocks.Count - 1; i >= 0; i--)
        {
            if (!liveBlueprintIds.Contains(Conversion.Id(blocks[i]!, "id")))
                blocks.RemoveAt(i);
        }

        // Clean up dead meshes
        var liveMeshIds = new HashSet<int>();
        foreach (var block in blocks)
        {
            if (block?["blueprint"]?["bodyMeshVuid"] is JsonValue mv && mv.TryGetValue<int>(out int mId))
                liveMeshIds.Add(mId);
        }

        var meshes = b["meshes"]?.AsArray() ?? new JsonArray();
        for (int i = meshes.Count - 1; i >= 0; i--)
        {
            if (meshes[i]?["vuid"] is JsonValue mv && mv.TryGetValue<int>(out int mId) && !liveMeshIds.Contains(mId))
                meshes.RemoveAt(i);
        }

        return b.ToJsonString(Indented);
    }
}
