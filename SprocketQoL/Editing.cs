using BepInEx;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.UI;
using Sprocket.VehicleDesigner;
using Sprocket.VehicleDesigner.Access;
using Sprocket.Vehicles;
using Sprocket.Vehicles.MeshGeneration;
using Sprocket.Vehicles.PlateStructures;
using Sprocket.Vehicles.PlateStructures.Design;
using UnityEngine;
using NativeTask = Il2CppSystem.Threading.Tasks.Task;

namespace SprocketQoL;

/// What a design edit produced: the new blueprint, the part to offer Restore on, and a line for the log.
public record EditResult(string Json, int FocusPart, string Log);

/// Runs QoL design edits: save the open design as JSON, change it, back both up, and have the game load the result.
/// The game has no way to change a part's type in place, and its own loader always rebuilds a valid vehicle.
public sealed class DesignEditor : MonoBehaviour
{
    internal static DesignEditor? Instance;
    private VehicleDesignerCore? core;
    private VehicleBlueprintSerializer? serializer;
    private NativeTask? pending;
    private System.Action? queued;
    private Func<string, EditResult>? edit;
    private Func<string, AddonEdits.EditPlan>? liveEdit;
    private static readonly List<object> alive = new(); // delegates handed to the game's undo history must not be collected
    private string editName = "", doneMessage = "", status = "";
    private string? recoveryJson, recoveryDir;
    private bool restoring, ready, busy;
    private int waits;
    private float nextLookup, statusUntil;
    internal int LastEditedPart { get; private set; } = -1;
    internal bool CanRestore => recoveryJson != null && !busy;
    public DesignEditor(IntPtr pointer) : base(pointer) { Instance = this; }

    private void Say(string text, float seconds = 6) { status = text; statusUntil = Time.unscaledTime + seconds; }

    internal void RequestEdit(string name, string done, Func<string, EditResult> change)
    {
        if (busy) return;
        Plugin.ModLog.LogInfo($"QOL requested: {name}");
        editName = name; doneMessage = done; edit = change;
        busy = true; Say(name + "...", 30);
        queued = RunEdit; // runs from Update, outside the game's inspector drawing
    }

    /// An edit applied in place as one of the game's undoable steps (Ctrl+Z), falling back to loading the edited
    /// design when it can't be (a changed part shares its mesh, or applying in place fails).
    internal void RequestLiveEdit(string name, string done, Func<string, AddonEdits.EditPlan> change)
    {
        if (busy) return;
        Plugin.ModLog.LogInfo($"QOL requested: {name}");
        editName = name; doneMessage = done; liveEdit = change;
        busy = true; Say(name + "...", 30);
        queued = RunLiveEdit;
    }

    internal void RequestRestore()
    {
        if (busy || recoveryJson == null) return;
        busy = true; Say("Restoring the design from before the last edit...", 30);
        queued = Restore;
    }

    /// Parts currently selected in the editor, optionally only those of one part type.
    internal List<int> SelectedParts(string? guid = null)
    {
        var selection = core?.Editor?.SelectionReader;
        var found = new List<int>();
        if (selection == null) return found;
        var items = selection.Items;
        for (int i = 0; i < selection.Count; i++)
        {
            var part = items[i]?.VehicleObject;
            if (part != null && (guid == null || part.GUID == guid)) found.Add((int)part.VUID);
        }
        return found.Distinct().ToList();
    }

    /// Every component of every part of the vehicle being edited.
    internal IEnumerable<VehicleComponent> AllComponents()
    {
        if (core?.Target == null) yield break;
        foreach (var part in Each(core.Target.Cast<IVehicleGateway>().ObjectReader.Items))
            if (part != null)
                foreach (var c in Each(part.Components))
                    if (c != null) yield return c;
    }

    /// Turret rings of every selected turret, whether its ring or its body was clicked.
    internal List<int> SelectedTurretRings()
    {
        var rings = new List<int>();
        var selection = core?.Editor?.SelectionReader;
        if (selection == null) return rings;
        var items = selection.Items;
        for (int i = 0; i < selection.Count; i++)
        {
            var component = items[i];
            var part = component?.VehicleObject;
            if (part == null) continue;
            if (part.GUID == Conversion.RingGuid) rings.Add((int)part.VUID);
            else if (component!.VehicleTransform?.Parent?.GetComponent<VehicleObject>() is { } parent && parent.GUID == Conversion.RingGuid)
                rings.Add((int)parent.VUID);
        }
        return rings.Distinct().ToList();
    }

    public void Update()
    {
        try
        {
            if (Time.unscaledTime >= nextLookup)
            {
                nextLookup = Time.unscaledTime + 0.5f;
                core = UnityEngine.Object.FindObjectOfType<VehicleDesignerCore>();
            }
            ready = core != null && core.HasEditor && core.editorState == VehicleDesignerCore.EditorState.Running;
            if (pending != null && pending.IsCompleted)
            {
                var task = pending; pending = null; busy = false;
                if (task.IsFaulted || task.IsCanceled)
                {
                    Say("The game could not load the result. Your design from before is backed up; use Restore.", 10);
                    Plugin.ModLog.LogError(task.Exception?.ToString() ?? "Vehicle load cancelled");
                }
                else
                {
                    Say(restoring ? "Design restored." : doneMessage, 8);
                    Plugin.ModLog.LogInfo(restoring ? "QOL_RESTORE_OK" : $"QOL_EDIT_OK: {editName}");
                    if (core?.Target != null && recoveryDir != null)
                        File.WriteAllText(Path.Combine(recoveryDir, "loaded-result.blueprint"), Snapshot());
                    if (restoring) LastEditedPart = -1;
                }
            }
            if (queued != null)
            {
                var action = queued; queued = null;
                action();
            }
        }
        catch (Exception ex)
        {
            busy = false; waits = 0;
            Say("Could not complete: " + ex.Message, 10);
            Plugin.ModLog.LogError(ex);
        }
    }

    private string Snapshot()
    {
        if (core == null || !core.HasEditor || core.Target == null) throw new Exception("Open a vehicle in the editor first.");
        serializer ??= new VehicleBlueprintSerializer();
        return serializer.SerializeToJSON(serializer.ToBlueprint(core.Target.Cast<IVehicleGateway>()), true);
    }

    /// The click also reaches the game's editor; if that started an operation, wait (~2 s at 60 fps) for it to finish.
    private bool EditorIdle(System.Action retry)
    {
        if (!ready || core == null) throw new Exception("Open a vehicle in the editor first.");
        if (!core.DesignIOPossible || (core.Editor != null && core.Editor.OperationInProgress))
        {
            if (waits++ == 0) Plugin.ModLog.LogInfo($"WAIT editor busy: io={core.DesignIOPossible}, op={core.Editor?.OperationInProgress}");
            if (waits > 120) { waits = 0; throw new Exception("The editor stayed busy. Click empty space to deselect, then try again."); }
            queued = retry;
            return false;
        }
        waits = 0;
        return true;
    }

    private void Backup(string original, string edited)
    {
        recoveryDir = Path.Combine(Paths.BepInExRootPath, "SprocketQoLBackups", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + System.Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(recoveryDir);
        File.WriteAllText(Path.Combine(recoveryDir, "original.blueprint"), original);
        File.WriteAllText(Path.Combine(recoveryDir, "edited.blueprint"), edited);
        File.WriteAllText(Path.Combine(recoveryDir, "README.txt"), $"{editName}. original.blueprint is the whole design before the edit, including unsaved changes; edited.blueprint is the result. Copy either into your faction's Blueprints\\Vehicles folder to load it. No saved blueprint was overwritten.");
    }

    private void RunEdit()
    {
        if (!EditorIdle(RunEdit)) return;
        string original = Snapshot();
        var result = edit!(original);
        var nativeBlueprint = serializer!.DeserializeJSON(result.Json);
        Backup(original, result.Json);
        recoveryJson = original;
        LastEditedPart = result.FocusPart;
        restoring = false;
        Plugin.ModLog.LogInfo($"QOL_EDIT {editName}: {result.Log}; backup={recoveryDir}");
        pending = core!.Load(nativeBlueprint, Il2CppSystem.Threading.CancellationToken.None);
    }

    private void RunLiveEdit()
    {
        if (!EditorIdle(RunLiveEdit)) return;
        string original = Snapshot();
        var plan = liveEdit!(original);
        Backup(original, plan.DesignJson);
        Plugin.ModLog.LogInfo($"QOL_EDIT {editName}: {plan.Summary}; in place={plan.Live}; backup={recoveryDir}");
        if (plan.Live)
        {
            try { ApplyInPlace(plan, original); busy = false; return; }
            catch (Exception ex) { Plugin.ModLog.LogWarning($"QOL_LIVE couldn't apply in place, reloading the design instead: {ex}"); }
        }
        recoveryJson = original;
        LastEditedPart = plan.Focus;
        restoring = false;
        pending = core!.Load(serializer!.DeserializeJSON(plan.DesignJson), Il2CppSystem.Threading.CancellationToken.None);
    }

    sealed record MeshSwap(PlateStructure Structure, PlateStructureMesh Mesh, PlateStructureMeshBlueprint Old, PlateStructureMeshBlueprint New);

    /// Swaps the parts' meshes as one of the game's own undoable operations, grouped with moving attached parts and
    /// removing parts, so Ctrl+Z and Ctrl+Y work on it like on any other edit. The meshes come from the game reading
    /// the design before and after the edit (the same reader a reload uses). Everything is checked before anything changes.
    private void ApplyInPlace(AddonEdits.EditPlan plan, string original)
    {
        var byId = new Dictionary<int, VehicleObject>();
        foreach (var o in Each(core!.Target.Cast<IVehicleGateway>().ObjectReader.Items)) if (o != null) byId[(int)o.VUID] = o;
        var before = Meshes(original);
        var after = Meshes(plan.DesignJson);
        var changes = new List<MeshSwap>();
        foreach (var (vuid, meshId) in plan.MeshIds)
        {
            if (!byId.TryGetValue(vuid, out var obj)) throw new Exception($"part {vuid} isn't in the vehicle");
            var structure = Each(obj.Components).Select(c => c?.TryCast<PlateStructure>()).FirstOrDefault(s => s != null) ?? throw new Exception($"part {vuid} has no plate structure");
            var mesh = structure.Mesh ?? throw new Exception($"part {vuid} has no mesh");
            if (plan.OldFaces.TryGetValue(vuid, out int expected) && mesh.FaceCount != expected)
                throw new Exception($"part {vuid}'s live mesh has {mesh.FaceCount} faces, the saved design {expected}");
            changes.Add(new MeshSwap(structure, mesh,
                before.TryGetValue(meshId, out var old) ? old : throw new Exception($"mesh {meshId} missing before the edit"),
                after.TryGetValue(meshId, out var @new) ? @new : throw new Exception($"mesh {meshId} missing after the edit")));
        }
        VehicleObject Part(int v) => byId.TryGetValue(v, out var o) ? o : throw new Exception($"part {v} isn't in the vehicle");
        var remove = plan.Remove.Select(v => Part(v).GetReference()).ToArray();
        var moves = plan.Reparent.GroupBy(r => r.Parent).Select(g => (Parent: Part(g.Key), Children: g.Select(r => Part(r.Child)).ToList())).ToList();

        var ops = core.Editor.operations;
        int group = ops.GetNewGroupID();
        string name = editName, done = doneMessage;
        bool executed = false;
        var callbacks = new VehicleOperationCallbacks
        {
            Execute = Keep(DelegateSupport.ConvertDelegate<Il2CppSystem.Func<IVehicleOperatorContext, VehicleOPExecutionResult>>(
                new Func<IVehicleOperatorContext, VehicleOPExecutionResult>(_ =>
                {
                    executed = Swap(changes, true, name);
                    if (executed) Say(done + " Ctrl+Z undoes it.", 8);
                    return executed ? VehicleOPExecutionResult.Executed : VehicleOPExecutionResult.Cancelled;
                }))!),
            Revert = Keep(DelegateSupport.ConvertDelegate<Il2CppSystem.Action<IVehicleOperatorContext>>(
                new Action<IVehicleOperatorContext>(_ => Swap(changes, false, name)))!),
        };
        // The game's usual flags plus undo/redo: with Register alone the step is kept in the history but Ctrl+Z skips it.
        var flags = Sprocket.VehicleDesigner.Operations.VehicleOp.InternalOpToVehicleOpFlags(global::Operations.Operation.DefaultFlags)
                    | VehicleOperationFlags.Undo | VehicleOperationFlags.Redo | VehicleOperationFlags.Register;
        var op = ops.CreateOp(name, ref callbacks, flags, group, VehicleOpExecutionMode.Synchronous);
        Plugin.ModLog.LogInfo($"QOL_LIVE {name}: operation created, ran now={executed}, flags={op?.Flags}");
        if (!executed) throw new Exception("the in-place change didn't go through"); // the caller reloads the design instead
        // Parts on a merged add-on move onto the one it merged into (before that add-on goes, or they'd go with it).
        foreach (var (parent, children) in moves)
        {
            var where = children.Select(c => c.transform.position).ToList();
            ops.SetParent(parent.GetReference(), new Il2CppReferenceArray<ISoftVehicleObject>(children.Select(c => c.GetReference()).ToArray()), group);
            float drift = children.Select((c, i) => (c.transform.position - where[i]).magnitude).DefaultIfEmpty(0).Max();
            Plugin.ModLog.LogInfo($"QOL_LIVE moved {children.Count} parts onto part {(int)parent.VUID}, largest shift {drift * 1000:0.#} mm");
        }
        if (remove.Length > 0) ops.Destroy(new Il2CppReferenceArray<ISoftVehicleObject>(remove), group);
        LastEditedPart = -1; // Ctrl+Z replaces the Restore button for in-place edits
    }

    /// The plate meshes of a design, by mesh id, as the game reads them from the blueprint.
    private Dictionary<int, PlateStructureMeshBlueprint> Meshes(string designJson)
    {
        var found = new Dictionary<int, PlateStructureMeshBlueprint>();
        var blueprint = serializer!.DeserializeJSON(designJson).TryCast<Sprocket.Vehicles.Serialization.VehicleBlueprint>() ?? throw new Exception("the design didn't read back");
        foreach (var m in blueprint.Meshes)
            if (m?.Mesh?.TryCast<PlateStructureMeshBlueprint>() is { } mesh) found[m.MeshID] = mesh;
        return found;
    }

    static bool Swap(List<MeshSwap> changes, bool forward, string name)
    {
        try
        {
            foreach (var c in changes) { c.Mesh.LoadBlueprint(forward ? c.New : c.Old); c.Structure.RequestMeshRefresh(); }
            Plugin.ModLog.LogInfo($"QOL_LIVE {(forward ? "applied" : "undone")}: {name} ({string.Join(", ", changes.Select(c => c.Mesh.FaceCount + " faces"))})");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.ModLog.LogError($"QOL_LIVE {(forward ? "apply" : "undo")} failed, putting the meshes back: {ex}");
            foreach (var c in changes) try { c.Mesh.LoadBlueprint(forward ? c.Old : c.New); c.Structure.RequestMeshRefresh(); } catch { }
            return false;
        }
    }

    static T Keep<T>(T value) where T : class { alive.Add(value); return value; }

    static IEnumerable<T> Each<T>(Il2CppSystem.Collections.Generic.IReadOnlyList<T> list)
    {
        int count = list.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<T>>().Count;
        for (int i = 0; i < count; i++) yield return list[i];
    }

    private void Restore()
    {
        if (recoveryJson == null || core == null || !core.DesignIOPossible) throw new Exception("No recovery is available, or the editor is busy.");
        // Keep any work done after the edit before replacing it.
        File.WriteAllText(Path.Combine(recoveryDir!, "before-restore-" + DateTime.Now.ToString("HHmmssfff") + ".blueprint"), Snapshot());
        restoring = true;
        pending = core.Load(serializer!.DeserializeJSON(recoveryJson), Il2CppSystem.Threading.CancellationToken.None);
    }

    // Result message only; the controls live in the game's inspector.
    public void OnGUI()
    {
        if (Time.unscaledTime > statusUntil || string.IsNullOrEmpty(status)) return;
        float width = Math.Min(620, Screen.width - 40);
        GUI.Box(new Rect((Screen.width - width) / 2, 80, width, 30), status); // below the game's vehicle name bar
    }
}

/// "Restore design before last edit" in the panel of the part the last QoL edit produced.
[HarmonyPatch]
public static class RestoreSection
{
    [HarmonyPostfix, HarmonyPatch(typeof(PlateStructureEditor), nameof(PlateStructureEditor.OnGUI))]
    static void Draw(PlateStructureEditor __instance, IGUILayout layout) => Ui.Guard("Restore", () =>
    {
        var editor = DesignEditor.Instance;
        var ui = layout.TryCast<IGUIElementDrawer>();
        if (editor == null || ui == null || !editor.CanRestore || editor.LastEditedPart != (int)__instance.Component.VehicleObject.VUID) return;
        Ui.Section(layout, "Undo last Quality of Life edit");
        var tip = new UITooltip("Restore", "Reloads the design as it was before the last Quality of Life edit (turret conversion, round add-on or merge).");
        ui.Button("Restore design before last edit", Ui.Callback(editor.RequestRestore), ref tip);
    });
}
