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
        var ringsByBody = rings.GroupBy(r => r["structureID"]?.GetValue<int>()
            ?? r["compartmentBodyID"]?["structureVuid"]?.GetValue<int>() ?? -1)
            .ToDictionary(g => g.Key, g => g.Select(r => Conversion.Id(r, "vuid")).ToArray());
        var extra = new List<int>();
        foreach (int v in all)
        {
            var o = objects[v];
            int p = Conversion.Id(o, "pvuid");
            // A ring's guns, hatches and decals are independent parts. Only its referenced
            // turret body promotes a selection to the full ring assembly.
            if (ringsByBody.TryGetValue(v, out var owners)) extra.AddRange(owners.Where(r => r == p));
            if (p >= 0 && objects.TryGetValue(p, out var parent) && Conversion.GuidOf(parent) == Conversion.RingGuid
                && Conversion.GuidOf(o) == Conversion.CompartmentGuid) extra.Add(p);
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
        var blocksById = blocks.Where(x => x != null).ToDictionary(x => Conversion.Id(x!, "id"));
        var blueprintIds = new HashSet<int>();
        foreach (int v in allToCopy)
        {
            var o = objects[v];
            foreach (var kv in o)
            {
                if (Conversion.BlueprintKey(kv.Key)
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
            var block = blocksById.GetValueOrDefault(bId);
            if (block?["blueprint"] is JsonObject bpObj)
            {
                foreach (var kv in bpObj)
                {
                    if (Conversion.BlueprintKey(kv.Key) && kv.Value is JsonValue val && val.TryGetValue<int>(out int subId))
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
            var block = blocksById.GetValueOrDefault(bId);
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
            ["blueprints"] = new JsonArray(blueprintIds.Select(id => blocksById.GetValueOrDefault(id)).Where(x => x != null).Select(x => JsonNode.Parse(x!.ToJsonString())!).ToArray()),
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

        var targetWorld = Conversion.WorldMatrices(targetObjects, attachmentFrames: true);
        Matrix4x4 parentWorld = targetWorld.TryGetValue(targetParentVuid, out var pw) ? pw : Matrix4x4.Identity;
        if (!Matrix4x4.Invert(parentWorld, out var invParentWorld)) throw new Exception("The paste target has zero scale; choose another supporting part.");

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
                foreach (var kv in payload.Where(kv => Conversion.BlueprintKey(kv.Key)).ToList())
                    if (kv.Value is JsonValue br && br.TryGetValue<int>(out int oldBlock) && blueprintMap.TryGetValue(oldBlock, out int newBlock))
                        payload[kv.Key] = newBlock;
                if (payload["bodyMeshVuid"] is JsonValue bmv && bmv.TryGetValue<int>(out int oldMesh) && meshMap.TryGetValue(oldMesh, out int newMesh))
                    payload["bodyMeshVuid"] = newMesh;
                if (payload["motorVuid"] is JsonValue mv && mv.TryGetValue<int>(out int oldMotor))
                    payload["motorVuid"] = vuidMap.GetValueOrDefault(oldMotor, -1);
                if (payload["barrelVuids"] is JsonArray barrels)
                {
                    for (int i = barrels.Count - 1; i >= 0; i--)
                        if (barrels[i] is JsonValue bv && bv.TryGetValue<int>(out int oldBv))
                        {
                            if (vuidMap.TryGetValue(oldBv, out int newBv)) barrels[i] = newBv;
                            else barrels.RemoveAt(i);
                        }
                }
                if (payload["operatedBehaviours"] is JsonArray opBeh)
                {
                    for (int i = opBeh.Count - 1; i >= 0; i--)
                        if (opBeh[i] is JsonValue ob && ob.TryGetValue<int>(out int oldOb))
                        {
                            if (vuidMap.TryGetValue(oldOb, out int newOb)) opBeh[i] = newOb;
                            else opBeh.RemoveAt(i);
                        }
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

            foreach (var kv in o.Where(kv => Conversion.BlueprintKey(kv.Key)).ToList())
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
                    // Reject a distorted result; silently falling back to the old local
                    // transform would move the part when attached to another parent.
                    Conversion.WriteTransform(o["transform"]!.AsObject(), newLocalMat);
                }
            }

            if (o["transform"]?["mirrorVuid"] is JsonValue mv && mv.TryGetValue<int>(out int oldM) && oldM >= 0)
            {
                if (vuidMap.TryGetValue(oldM, out int newM)) o["transform"]!["mirrorVuid"] = newM;
                else AddonEdits.Unlink(o);
            }
            else
            {
                // A mirrored part saved once has no explicit twin: the game shows
                // its second image from flag 4. Keep that flag when copying it.
                if (o["transform"] is JsonObject transform) transform["mirrorVuid"] = -1;
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

        var dead = ExpandPartHierarchy(objects, toRemoveVuids.Where(v => objects.TryGetValue(v, out var part) && Conversion.Id(part, "pvuid") >= 0));

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

        // Delete only settings owned by the removed parts. Vehicle-wide paint jobs,
        // registers and inline settings are not all linked through *BlueprintVuid.
        // A whole-vehicle orphan sweep here would erase those unrelated settings.
        IEnumerable<int> References(JsonObject o) => o.Where(kv => Conversion.BlueprintKey(kv.Key)
            && kv.Value is JsonValue v && v.TryGetValue<int>(out _)).Select(kv => kv.Value!.GetValue<int>());
        var deadBlueprintIds = dead.SelectMany(v => References(objects[v])).ToHashSet();
        var deadComponents = dead.SelectMany(v => Conversion.ComponentKeys(objects[v]).Select(k => Conversion.Id(objects[v], k))).ToHashSet();
        var liveBlueprintIds = new HashSet<int>();
        foreach (var o in surviving.Values)
            liveBlueprintIds.UnionWith(References(o));

        var blocks = b["blueprints"]?.AsArray() ?? new JsonArray();
        var blocksById = blocks.Where(x => x != null).ToDictionary(x => Conversion.Id(x!, "id"));
        liveBlueprintIds.UnionWith(blocksById.Keys.Where(id => !deadBlueprintIds.Contains(id)));
        var queueB = new Queue<int>(liveBlueprintIds);
        while (queueB.Count > 0)
        {
            int bId = queueB.Dequeue();
            var block = blocksById.GetValueOrDefault(bId);
            if (block?["blueprint"] is JsonObject bpObj)
            {
                foreach (var kv in bpObj)
                {
                    if (Conversion.BlueprintKey(kv.Key) && kv.Value is JsonValue val && val.TryGetValue<int>(out int subId))
                        if (liveBlueprintIds.Add(subId)) queueB.Enqueue(subId);
                }
                if (bpObj["paintJobIDs"] is JsonArray paintIds)
                    foreach (var paint in paintIds)
                        if (paint is JsonValue value && value.TryGetValue<int>(out int id) && liveBlueprintIds.Add(id)) queueB.Enqueue(id);
            }
        }

        var deadMeshIds = new HashSet<int>();
        for (int i = blocks.Count - 1; i >= 0; i--)
        {
            if (!liveBlueprintIds.Contains(Conversion.Id(blocks[i]!, "id")))
            {
                if (blocks[i]?["blueprint"]?["bodyMeshVuid"] is JsonValue mesh && mesh.TryGetValue<int>(out int meshId)) deadMeshIds.Add(meshId);
                blocks.RemoveAt(i);
            }
            else if (blocks[i]?["blueprint"] is JsonObject payload)
            {
                if (payload["motorVuid"] is JsonValue motor && motor.TryGetValue<int>(out int motorId) && deadComponents.Contains(motorId)) payload["motorVuid"] = -1;
                foreach (string name in new[] { "operatedBehaviours", "barrelVuids" })
                    if (payload[name] is JsonArray ids)
                        for (int j = ids.Count - 1; j >= 0; j--)
                            if (ids[j] is JsonValue component && component.TryGetValue<int>(out int componentId) && deadComponents.Contains(componentId)) ids.RemoveAt(j);
            }
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
            if (meshes[i]?["vuid"] is JsonValue mv && mv.TryGetValue<int>(out int mId) && deadMeshIds.Contains(mId) && !liveMeshIds.Contains(mId))
                meshes.RemoveAt(i);
        }

        return b.ToJsonString(Indented);
    }
}
