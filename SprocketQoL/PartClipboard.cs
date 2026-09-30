using BepInEx;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.Vehicles;
using UnityEngine.InputSystem;

namespace SprocketQoL;

/// Hotkey handling and editor integration for Ctrl+C (copy), Ctrl+X (cut), and Ctrl+V (paste) of vehicle parts.
/// Persists copied parts to disk so they can be pasted across different blueprints and sessions.
public static class PartClipboard
{
    static bool initialized;

    static void EnsureInit()
    {
        if (initialized) return;
        try { BlueprintClipboard.StorageDirectory = Paths.BepInExRootPath; }
        catch { }
        initialized = true;
    }

    public static void Update()
    {
        var keys = Keyboard.current;
        if (keys == null || !keys.ctrlKey.isPressed) return;
        if (MeshTools.Typing()) return;

        var editor = DesignEditor.Instance;
        if (editor == null || !editor.IsReady || editor.IsBusy) return;

        EnsureInit();

        if (keys.cKey.wasPressedThisFrame)
        {
            DoCopy(editor);
        }
        else if (keys.xKey.wasPressedThisFrame)
        {
            DoCut(editor);
        }
        else if (keys.vKey.wasPressedThisFrame)
        {
            DoPaste(editor);
        }
    }

    /// Collects the selected part(s) and all attached components/parts:
    /// - If a Turret Body is selected, includes the Turret Ring so all attached decals, addons, and motor are included.
    /// - If a Turret Ring is selected, includes its Turret Body.
    /// - Recursively traverses live Unity VehicleTransform.Children to include any attached decals, addons, etc.
    static List<int> CollectTargetAndAttached(DesignEditor editor)
    {
        var selected = editor.SelectedParts();
        if (selected.Count == 0 && Hotkeys.Current?.Component?.VehicleObject is { } activeObj)
            selected.Add((int)activeObj.VUID);

        if (selected.Count == 0) return selected;

        var byId = new Dictionary<int, VehicleObject>();
        foreach (var o in editor.AllParts()) byId[(int)o.VUID] = o;

        var all = new HashSet<int>(selected);

        // Expand for Turret Ring if Turret Body is selected
        var extra = new List<int>();
        foreach (int v in all)
        {
            if (!byId.TryGetValue(v, out var vo)) continue;
            var parentVo = vo.GetComponent<VehicleTransform>()?.Parent?.VehicleObject;
            if (parentVo != null && parentVo.GUID == Conversion.RingGuid)
                extra.Add((int)parentVo.VUID);
        }
        foreach (int e in extra) all.Add(e);

        // Traverse live Unity scene hierarchy for all attached children (add-ons, decals, etc.)
        var queue = new Queue<int>(all);
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            if (!byId.TryGetValue(cur, out var vo)) continue;
            var vt = vo.GetComponent<VehicleTransform>();
            if (vt?.Children == null) continue;
            var kids = vt.Children;
            int count = kids.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<VehicleTransform>>().Count;
            for (int i = 0; i < count; i++)
            {
                if (kids[i]?.VehicleObject is { } childVo)
                {
                    int childVuid = (int)childVo.VUID;
                    if (all.Add(childVuid)) queue.Enqueue(childVuid);
                }
            }
        }

        return all.ToList();
    }

    static void DoCopy(DesignEditor editor)
    {
        var selected = CollectTargetAndAttached(editor);

        if (selected.Count == 0)
        {
            editor.Say("No parts selected to copy.", 3);
            return;
        }

        try
        {
            string json = editor.Snapshot();
            var (clipJson, count) = BlueprintClipboard.Copy(json, selected);
            if (count == 0)
            {
                editor.Say("No parts copied.", 3);
                return;
            }
            BlueprintClipboard.SetClipboardJson(clipJson);
            editor.Say($"Copied {count} part{(count == 1 ? "" : "s")} to clipboard.", 3);
            Plugin.ModLog.LogInfo($"Clipboard: copied {count} parts to clipboard");
        }
        catch (Exception ex)
        {
            editor.Say($"Copy failed: {ex.Message}", 5);
            Plugin.ModLog.LogWarning($"Clipboard copy failed: {ex}");
        }
    }

    static void DoCut(DesignEditor editor)
    {
        var selected = CollectTargetAndAttached(editor);

        if (selected.Count == 0)
        {
            editor.Say("No parts selected to cut.", 3);
            return;
        }

        try
        {
            string json = editor.Snapshot();
            var b = Conversion.Parse(json);
            var objects = Conversion.Objects(b);
            int hullVuid = objects.Values.FirstOrDefault(o => Conversion.Id(o, "pvuid") == -1) is { } h ? Conversion.Id(h, "vuid") : 0;

            if (selected.Contains(hullVuid))
            {
                if (selected.Count == 1)
                {
                    editor.Say("Cannot cut the vehicle hull.", 3);
                    return;
                }
                selected.Remove(hullVuid);
            }

            var (clipJson, count) = BlueprintClipboard.Copy(json, selected);
            if (count == 0)
            {
                editor.Say("No parts cut.", 3);
                return;
            }
            BlueprintClipboard.SetClipboardJson(clipJson);

            // Collect all associated parts and descendants for deletion
            var allToDestroy = BlueprintClipboard.ExpandPartHierarchy(objects, selected);
            allToDestroy.Remove(hullVuid);

            var byId = new Dictionary<int, VehicleObject>();
            foreach (var o in editor.AllParts()) byId[(int)o.VUID] = o;
            var parts = allToDestroy.Where(v => byId.ContainsKey(v)).Select(v => byId[v].GetReference()).ToArray();

            if (parts.Length > 0 && editor.Core?.Editor?.operations is { } ops)
            {
                int group = ops.GetNewGroupID();
                ops.Destroy(new Il2CppReferenceArray<ISoftVehicleObject>(parts), group);
                editor.Say($"Cut {allToDestroy.Count} part{(allToDestroy.Count == 1 ? "" : "s")}. Ctrl+Z undoes it.", 4);
                Plugin.ModLog.LogInfo($"Clipboard: cut {allToDestroy.Count} parts via ops.Destroy");
            }
            else
            {
                editor.RequestEdit("Cut parts", $"Cut {allToDestroy.Count} parts. Restore undoes it.", currentJson =>
                {
                    string cutJson = BlueprintClipboard.RemoveFrom(currentJson, allToDestroy);
                    return new EditResult(cutJson, -1, $"cut {allToDestroy.Count} parts");
                });
            }
        }
        catch (Exception ex)
        {
            editor.Say($"Cut failed: {ex.Message}", 5);
            Plugin.ModLog.LogWarning($"Clipboard cut failed: {ex}");
        }
    }

    static void DoPaste(DesignEditor editor)
    {
        string? clipJson = BlueprintClipboard.GetClipboardJson();
        if (string.IsNullOrEmpty(clipJson))
        {
            editor.Say("Clipboard is empty.", 3);
            return;
        }

        var selected = editor.SelectedParts();
        int targetParent = selected.Count > 0 ? selected.Last() : -1;

        editor.RequestEdit("Pasting parts", "Pasted parts from clipboard. Restore undoes it.", currentJson =>
        {
            var (newJson, focusVuid, count) = BlueprintClipboard.PasteInto(currentJson, clipJson, targetParent);
            return new EditResult(newJson, focusVuid, $"pasted {count} parts");
        });
    }
}
