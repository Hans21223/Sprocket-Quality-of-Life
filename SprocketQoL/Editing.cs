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
    private System.Action? afterEdit; // runs once the requested live edit is done (in place or reloaded)
    private Func<string, EditResult>? edit;
    private Func<string, AddonEdits.EditPlan>? liveEdit;
    // Delegates handed to one of the game's undo steps mustn't be collected while it can call them: each step's are
    // kept until the game lets the step go (UndoStepLifetime).
    private static readonly Dictionary<IntPtr, List<object>> alive = new();
    private static List<object> keeping = new();
    private string editName = "", doneMessage = "", status = "";
    private string? recoveryJson, recoveryDir;
    private IntPtr recoveryTarget;
    private readonly VehicleSession vehicles = new();
    private int requestSession, recoverySession; // the vehicle session an edit was asked for in, and its Restore's
    private bool restoring, ready, busy;
    private int waits;
    private float nextLookup, statusUntil;
    internal int LastEditedPart { get; private set; } = -1;
    private bool RecoveryBelongsHere => recoveryTarget != IntPtr.Zero && core?.Target?.Pointer == recoveryTarget && recoverySession == vehicles.Now;
    internal bool CanRestore => recoveryJson != null && !busy && RecoveryBelongsHere;
    internal bool IsReady => ready;
    internal int Session => vehicles.Now; // changes whenever another vehicle is opened or the editor is left
    internal bool IsBusy => busy;
    internal VehicleDesignerCore? Core => core;
    public DesignEditor(IntPtr pointer) : base(pointer) { Instance = this; }

    internal void Say(string text, float seconds = 6) { status = text; statusUntil = Time.unscaledTime + seconds; }

    internal bool CaptureBlocked()
    {
        if (!PhotoShot.Capturing && !DrawingSheet.Capturing) return false;
        Say("Finish the photo or drawing capture before editing the vehicle.", 4);
        return true;
    }

    internal void RequestEdit(string name, string done, Func<string, EditResult> change)
    {
        if (busy || CaptureBlocked()) return;
        Plugin.ModLog.LogInfo($"QOL requested: {name}");
        editName = name; doneMessage = done; edit = change; afterEdit = null; requestSession = vehicles.Now;
        busy = true; Say(name + "...", 30);
        queued = RunEdit; // runs from Update, outside the game's inspector drawing
    }

    /// An edit applied in place as one of the game's undoable steps (Ctrl+Z), falling back to loading the edited
    /// design when it can't be (a changed part shares its mesh, or applying in place fails). `then` runs once it's done.
    internal void RequestLiveEdit(string name, string done, Func<string, AddonEdits.EditPlan> change, System.Action? then = null)
    {
        if (busy || CaptureBlocked()) return;
        Plugin.ModLog.LogInfo($"QOL requested: {name}");
        editName = name; doneMessage = done; liveEdit = change; afterEdit = then; requestSession = vehicles.Now;
        busy = true; Say(name + "...", 30);
        queued = RunLiveEdit;
    }

    private void AfterEdit()
    {
        var then = afterEdit;
        afterEdit = null;
        try { then?.Invoke(); }
        catch (Exception ex) { Plugin.ModLog.LogWarning($"After {editName}: {ex.Message}"); }
    }

    private Func<string, (string Json, List<List<(int Source, int Added)>> Groups, string Log)>? separate;

    /// Separate as one of the game's own steps, so Ctrl+Z undoes it: for each new add-on (a group: the part's copy, and
    /// its twin's) the game duplicates the part (and its twin), then the copies take their faces and the part keeps the
    /// rest. The result is saved to memory and checked against the planned design; if anything differs (or the part is a
    /// hull or turret, which can't be duplicated into an add-on), the planned design is loaded instead.
    internal void RequestSeparate(string name, string done, Func<string, (string Json, List<List<(int Source, int Added)>> Groups, string Log)> plan)
    {
        if (busy || CaptureBlocked()) return;
        Plugin.ModLog.LogInfo($"QOL requested: {name}");
        editName = name; doneMessage = done; separate = plan; afterEdit = null; requestSession = vehicles.Now;
        busy = true; Say(editName + "...", 30);
        queued = RunSeparate;
    }

    private void RunSeparate()
    {
        if (!EditorIdle(RunSeparate)) return;
        string original = Snapshot();
        var (planned, groups, log) = separate!(original);
        Backup(original, planned);
        Plugin.ModLog.LogInfo($"QOL_EDIT {editName}: {log}; backup={recoveryDir}");
        string? why = null;
        try { why = SeparateInPlace(original, planned, groups); }
        catch (Exception ex) { why = ex.Message; Plugin.ModLog.LogWarning($"QOL_LIVE separate: {ex}"); }
        if (why == null) { busy = false; return; }
        Plugin.ModLog.LogInfo($"QOL_LIVE separate not in place ({why}); loading the planned design instead");
        SetRecovery(original, groups[0][0].Added);
        restoring = false;
        doneMessage += " Restore undoes it.";
        vehicles.Loading();
        pending = core!.Load(serializer!.DeserializeJSON(planned), Il2CppSystem.Threading.CancellationToken.None);
    }

    /// Null when done in place; else why not (nothing changed then, or the check found a difference and the planned
    /// design is about to be loaded over it).
    private string? SeparateInPlace(string original, string planned, List<List<(int Source, int Added)>> groups)
    {
        var byId = new Dictionary<int, VehicleObject>();
        foreach (var o in AllParts()) byId[(int)o.VUID] = o;
        PlateStructure StructureOf(VehicleObject o) => Each(o.Components).Select(c => c?.TryCast<PlateStructure>()).FirstOrDefault(s => s?.Mesh != null)
                                                       ?? throw new Exception($"part {(int)o.VUID} has no plate structure");
        var parts = groups[0]; // every new add-on comes from the same part (and its twin)
        var sources = parts.Select(p => byId.TryGetValue(p.Source, out var o) ? o : throw new Exception($"part {p.Source} isn't in the vehicle")).ToList();
        if (sources.Any(o => o.GUID != Conversion.AddonGuid)) return "a hull or turret can't be duplicated into an add-on";
        // The parts' live shape may be shared only with each other and the game's mirror images, or loading it changes others.
        var saved = Conversion.Objects(Conversion.Parse(original)).Keys.ToHashSet();
        var shapes = sources.Select(o => StructureOf(o).Mesh.Pointer).ToHashSet();
        foreach (var (v, o) in byId)
            if (saved.Contains(v) && !parts.Any(p => p.Source == v) && Each(o.Components).Any(c => c?.TryCast<PlateStructure>() is { Mesh: { } m } && shapes.Contains(m.Pointer)))
                return $"part {v} shares the shape";
        var before = Meshes(original);
        var after = Meshes(planned);
        var oldShape = before[AddonEdits.MeshIdOf(original, parts[0].Source)];
        var keptShape = after[AddonEdits.MeshIdOf(planned, parts[0].Source)];
        var newShapes = groups.Select(g => after[AddonEdits.MeshIdOf(planned, g[0].Added)]).ToList();

        var ops = core!.Editor.operations;
        int group = ops.GetNewGroupID();
        var refs = new Il2CppReferenceArray<ISoftVehicleObject>(sources.Select(o => o.GetReference()).ToArray());
        var dups = groups.Select(_ => ops.Duplicate(refs, DuplicateOptions.Copy, group)).ToList(); // one copy per new add-on
        List<VehicleObject> Copies(int i) => sources.Select(s => dups[i].GetDupe(s) ?? throw new Exception($"the game made no copy of part {(int)s.VUID}")).ToList();
        var copies = Enumerable.Range(0, groups.Count).Select(Copies).ToList();
        var allCopies = copies.SelectMany(c => c).ToList();
        if (allCopies.Select(c => c.Pointer).Distinct().Count() != allCopies.Count) return "the game made one copy for two pieces";
        // Parts attached to the part were copied with it: they stay on the part only.
        var extra = allCopies.SelectMany(c => Each(c.GetComponent<VehicleTransform>().Children)).Select(t => t?.VehicleObject).Where(o => o != null).Select(o => o!.GetReference()).ToArray();
        if (extra.Length > 0) ops.Destroy(new Il2CppReferenceArray<ISoftVehicleObject>(extra), group);
        string name = editName, done = doneMessage;
        var paint = PartPaint.SlotsOf(sources.Select(s => (int)s.VUID)); // the copies keep their part's own paint
        void Shapes(bool forward)
        {
            foreach (var s in sources) { StructureOf(s).Mesh.LoadBlueprint(forward ? keptShape : oldShape); StructureOf(s).RequestMeshRefresh(); }
            var copied = paint.Count == 0 ? new Dictionary<int, int>() : Enumerable.Range(0, groups.Count)
                .SelectMany(i => Copies(i).Select((c, j) => (Copy: (int)c.VUID, Source: (int)sources[j].VUID)))
                .Where(p => paint.ContainsKey(p.Source)).ToDictionary(p => p.Copy, p => forward ? paint[p.Source] : 0);
            if (!forward) { PartPaint.Put(copied, paint.Keys); return; }
            for (int i = 0; i < groups.Count; i++)
            {
                var now = Copies(i);
                PlateStructureMesh? own = null; // the copies' own shape (a twin pair's copies share it, as twins do)
                for (int j = 0; j < now.Count; j++)
                {
                    // Exactly where the part is (a copy the game offsets, as for Alt-dragging, goes back onto it).
                    var (from, to) = (sources[j].GetComponent<VehicleTransform>(), now[j].GetComponent<VehicleTransform>());
                    to.SetLocalPositionAndRotation(from.LocalPosition, from.LocalRotation);
                    // The game's copy still shares the part's shape (the game splits them only when one is edited): it
                    // gets a new one from the vehicle's shape register, or loading its faces would change the part too.
                    var structure = StructureOf(now[j]);
                    if (sources.Any(s => StructureOf(s).Mesh.Pointer == structure.Mesh.Pointer))
                        structure.Mesh = own ??= core!.Target.Cast<IVehicleGateway>().Meshes.New<PlateStructureMesh>();
                    structure.Mesh.LoadBlueprint(newShapes[i]);
                    structure.RequestMeshRefresh();
                }
                if (now.Count == 2 && now[0].GetComponent<VehicleTransform>() is { } a && now[1].GetComponent<VehicleTransform>() is { } b && a.Mirror?.Pointer != b.Pointer)
                    VehicleTransform.SetMirrors(a, b);
            }
            PartPaint.Put(copied, paint.Keys); // the copies aren't painted yet: found through their parts' painter
        }
        bool executed = false;
        var callbacks = new VehicleOperationCallbacks
        {
            Execute = Keep(DelegateSupport.ConvertDelegate<Il2CppSystem.Func<IVehicleOperatorContext, VehicleOPExecutionResult>>(
                new Func<IVehicleOperatorContext, VehicleOPExecutionResult>(_ =>
                {
                    try { Shapes(true); executed = true; return VehicleOPExecutionResult.Executed; }
                    catch (Exception ex) { Plugin.ModLog.LogError($"{name}: {ex}"); return VehicleOPExecutionResult.Cancelled; }
                }))!),
            Revert = Keep(DelegateSupport.ConvertDelegate<Il2CppSystem.Action<IVehicleOperatorContext>>(
                new Action<IVehicleOperatorContext>(_ => { try { Shapes(false); } catch (Exception ex) { Plugin.ModLog.LogError($"{name} undo: {ex}"); } }))!),
        };
        var flags = Sprocket.VehicleDesigner.Operations.VehicleOp.InternalOpToVehicleOpFlags(global::Operations.Operation.DefaultFlags)
                    | VehicleOperationFlags.Undo | VehicleOperationFlags.Redo | VehicleOperationFlags.Register;
        KeptFor(ops.CreateOp(name, ref callbacks, flags, group, VehicleOpExecutionMode.Synchronous));
        if (!executed) return "the shapes didn't go in";
        if (allCopies.Any(c => sources.Any(s => StructureOf(s).Mesh.Pointer == StructureOf(c).Mesh.Pointer))) return "a copy still shares the part's shape";

        // The check: saved now, the design must match the plan: every part's face count, and each copy placed, flipped,
        // mirrored and linked as planned.
        string check = Snapshot();
        var want = Conversion.Objects(Conversion.Parse(planned));
        var got = Conversion.Objects(Conversion.Parse(check));
        var wantFaces = AddonEdits.FaceCounts(planned);
        var gotFaces = AddonEdits.FaceCounts(check);
        var added = groups.SelectMany(g => g.Select(p => p.Added)).ToHashSet();
        var copyIds = copies.Select(g => g.Select(c => (int)c.VUID).ToList()).ToList();
        var problems = new List<string>();
        if (got.Count != want.Count) problems.Add($"{got.Count} parts, planned {want.Count}");
        foreach (var (v, n) in wantFaces.Where(kv => want.ContainsKey(kv.Key) && !added.Contains(kv.Key)))
            if (gotFaces.GetValueOrDefault(v) != n) problems.Add($"part {v} has {gotFaces.GetValueOrDefault(v)} faces, planned {n}");
        for (int i = 0; i < groups.Count; i++)
        {
            var pieces = groups[i];
            for (int j = 0; j < pieces.Count; j++)
            {
                int c = copyIds[i][j];
                var (w, g) = (want[pieces[j].Added], got.GetValueOrDefault(c));
                if (g == null) { problems.Add($"copy {c} not saved"); continue; }
                if (gotFaces.GetValueOrDefault(c) != wantFaces[pieces[j].Added]) problems.Add($"copy {c} has {gotFaces.GetValueOrDefault(c)} faces, planned {wantFaces[pieces[j].Added]}");
                if (g["pvuid"]!.GetValue<int>() != w["pvuid"]!.GetValue<int>()) problems.Add($"copy {c} hangs on {g["pvuid"]}, planned {w["pvuid"]}");
                if ((g["flags"]!.GetValue<int>() & 5) != (w["flags"]!.GetValue<int>() & 5)) problems.Add($"copy {c} flags {g["flags"]}, planned {w["flags"]}");
                if (!Conversion.Near(Conversion.Local(g["transform"]!), Conversion.Local(w["transform"]!))) problems.Add($"copy {c} placed elsewhere");
                if (pieces.Count == 2 && g["transform"]!["mirrorVuid"]!.GetValue<int>() != copyIds[i][1 - j]) problems.Add($"copy {c} not linked to its twin");
            }
            // A copy the game should show twice (mirrored mark, no twin): the structure draws its own image (its mirrored
            // meshes, across the centre), which it does when it counts itself mirrored.
            if (pieces.Count == 1 && (want[pieces[0].Added]["flags"]!.GetValue<int>() & 4) != 0 && !StructureOf(copies[i][0]).Mirrored)
                problems.Add($"copy {copyIds[i][0]} isn't shown on the other side");
        }
        if (problems.Count > 0) return "the check found: " + string.Join("; ", problems);
        // The new add-on selected, in the same step: the part's shape editor closes (its own Ctrl+Z covers shape edits
        // only), so Ctrl+Z undoes the separate straight away.
        try { ops.Select(StructureOf(copies[0][0]), Sprocket.Vehicles.Selection.SelectionMode.Set, group); }
        catch (Exception ex) { Plugin.ModLog.LogWarning($"QOL_LIVE {name}: couldn't select the new add-on: {ex.Message}"); }
        Plugin.ModLog.LogInfo($"QOL_LIVE {name}: in place, new add-ons {string.Join(", ", copyIds.Select(g => string.Join(" and ", g)))}, checked against the plan");
        Say(done + " Ctrl+Z undoes it.", 8);
        LastEditedPart = -1;
        return null;
    }

    private readonly HashSet<int> filledRings = new();
    private float nextTurretCheck;

    /// A turret placed or copied with Mirror on gets a twin ring from the game with nothing on it (the game copies only
    /// the ring): once the placing is done, the body, guns and everything else get mirrored onto it. Once per turret.
    private void FillMirroredTurrets()
    {
        if (core?.Editor == null || core.Editor.OperationInProgress || PhotoShot.Capturing || DrawingSheet.Capturing) return;
        foreach (var part in AllParts())
        {
            if (part.GUID != Conversion.RingGuid || part.GetComponent<VehicleTransform>() is not { } ring) continue;
            var twin = ring.Mirror;
            if (twin == null) continue; // Unity's own null check: a twin just deleted counts as none
            if (!Each(ring.Children).Any() || Each(twin.Children).Any() || !filledRings.Add((int)part.VUID)) continue;
            int vuid = (int)part.VUID;
            Plugin.ModLog.LogInfo($"Turret mirror: twin ring {(int)(twin.VehicleObject?.VUID ?? default)} of ring {vuid} has nothing on it; mirroring the turret onto it");
            RequestEdit("Mirroring turret", "The mirrored turret got its body and everything on it. Restore undoes it.", json =>
            {
                var (result, count, how) = Conversion.MirrorTurret(json, vuid);
                int body = Conversion.Objects(Conversion.Parse(result))[vuid]["structureID"]?.GetValue<int>() ?? vuid;
                return new EditResult(result, body, $"ring {vuid}: {count} parts {how}");
            });
            return;
        }
    }

    private readonly Dictionary<IntPtr, (Vector3 Pos, Quaternion Rot)> turretPoses = new();
    private IntPtr turretPosesTarget;

    /// Mirrored turrets stay mirrored. The game's turret ring isn't made to be mirrored (QoL allows it,
    /// TurretCopy.AllowCopy), and moving one of a pair puts its twin off centre and slides the drives off. So after each
    /// change, the side that was moved (the one selected, else the one that changed) is mirrored onto the other: the ring
    /// across the vehicle's centre and each linked part on it onto its twin, and parts on the moved side that moved without
    /// being selected (a drive) go back where they were.
    private void KeepTurretsMirrored()
    {
        if (busy || core?.Editor == null || core.Editor.OperationInProgress || core.Target == null) return;
        if (turretPosesTarget != core.Target.Pointer) { turretPoses.Clear(); turretPosesTarget = core.Target.Pointer; }
        HashSet<int>? selected = null;
        var seen = new HashSet<IntPtr>();
        foreach (var part in AllParts())
        {
            if (part.GUID != Conversion.RingGuid || part.GetComponent<VehicleTransform>() is not { } a || a.Mirror is not { } b
                || !seen.Add(a.Pointer) || !seen.Add(b.Pointer)) continue;
            List<VehicleTransform> Below(VehicleTransform top)
            {
                var found = new List<VehicleTransform>();
                for (var queue = new Queue<VehicleTransform>(new[] { top }); queue.Count > 0;)
                    foreach (var c in Each(queue.Dequeue().Children)) if (c != null) { found.Add(c); queue.Enqueue(c); }
                return found;
            }
            var onB = Below(b).Select(t => t.Pointer).ToHashSet();
            var pairs = new List<(VehicleTransform A, VehicleTransform B)> { (a, b) };
            foreach (var x in Below(a)) if (x.Mirror is { } y && onB.Contains(y.Pointer)) pairs.Add((x, y));
            bool Changed(VehicleTransform t) => turretPoses.TryGetValue(t.Pointer, out var was)
                && (Vector3.Distance(was.Pos, t.LocalPosition) > 1e-4f || Quaternion.Angle(was.Rot, t.LocalRotation) > 0.01f);
            void Remember() { foreach (var (x, y) in pairs) { turretPoses[x.Pointer] = (x.LocalPosition, x.LocalRotation); turretPoses[y.Pointer] = (y.LocalPosition, y.LocalRotation); } }
            bool changedA = pairs.Any(p => Changed(p.A)), changedB = pairs.Any(p => Changed(p.B));
            if (!changedA && !changedB) { Remember(); continue; }
            selected ??= SelectedParts().ToHashSet();
            // A selected part and what it hangs from up to its ring (picking a turret body and dragging moves its ring).
            var picked = new HashSet<IntPtr>();
            foreach (var t in pairs.SelectMany(p => new[] { p.A, p.B }).Concat(Below(a)).Concat(Below(b)))
                if (t.VehicleObject is { } o && selected.Contains((int)o.VUID))
                    for (var u = t; u != null && picked.Add(u.Pointer) && u.Pointer != a.Pointer && u.Pointer != b.Pointer; u = u.Parent) { }
            bool Picked(VehicleTransform t) => picked.Contains(t.Pointer);
            bool pickedA = pairs.Any(p => Picked(p.A)), pickedB = pairs.Any(p => Picked(p.B));
            bool leadA = pickedA != pickedB ? pickedA : changedA != changedB ? changedA : true;
            var lead = pairs.Select(p => leadA ? p.A : p.B).ToList();
            var follow = pairs.Select(p => leadA ? p.B : p.A).ToList();
            // Moved by hand: what moved on that side without being picked was the game, so it goes back.
            int restored = 0;
            if (lead.Any(t => Picked(t) && Changed(t)))
                foreach (var t in lead.Where(t => !Picked(t) && Changed(t)))
                {
                    var was = turretPoses[t.Pointer];
                    t.SetLocalPositionAndRotation(was.Pos, was.Rot);
                    restored++;
                }
            (System.Numerics.Vector3, System.Numerics.Quaternion)? shared = null;
            if (a.Parent != null && b.Parent != null && a.Parent.Pointer == b.Parent.Pointer)
            {
                var (pp, pq) = (a.Parent.LocalPosition, a.Parent.LocalRotation); // the shared parent placed in the vehicle
                for (var u = a.Parent.Parent; u != null; u = u.Parent) { pp = u.LocalPosition + u.LocalRotation * pp; pq = u.LocalRotation * pq; }
                shared = (new(pp.x, pp.y, pp.z), new(pq.x, pq.y, pq.z, pq.w));
            }
            else if (a.Parent?.Mirror?.Pointer != b.Parent?.Pointer) { Remember(); continue; } // not on one parent or twin parents
            int moved = 0;
            for (int i = 0; i < pairs.Count; i++)
            {
                var (pos, rot) = Conversion.MirrorPose(new(lead[i].LocalPosition.x, lead[i].LocalPosition.y, lead[i].LocalPosition.z),
                    new(lead[i].LocalRotation.x, lead[i].LocalRotation.y, lead[i].LocalRotation.z, lead[i].LocalRotation.w), i == 0 ? shared : null);
                var want = (P: new Vector3(pos.X, pos.Y, pos.Z), Q: new Quaternion(rot.X, rot.Y, rot.Z, rot.W));
                if (Vector3.Distance(want.P, follow[i].LocalPosition) <= 1e-4f && Quaternion.Angle(want.Q, follow[i].LocalRotation) <= 0.01f) continue;
                follow[i].SetLocalPositionAndRotation(want.P, want.Q);
                moved++;
            }
            Remember();
            if (moved + restored > 0)
                Plugin.ModLog.LogInfo($"Turret mirror: ring {(int)(lead[0].VehicleObject?.VUID ?? default)} leads; {moved} parts of its twin mirrored, {restored} parts the game shifted on it put back");
        }
    }

    /// Ctrl+J, as in Blender: the selected add-ons join the last add-on, turret or hull selected (the active one).
    private void JoinHotkey()
    {
        if (!Keybinds.Pressed("join") || MeshTools.Typing() || CaptureBlocked()) return;
        var picked = SelectedParts();
        var addons = SelectedParts(Conversion.AddonGuid);
        var bodies = SelectedParts(Conversion.CompartmentGuid);
        int target = picked.LastOrDefault(v => addons.Contains(v) || bodies.Contains(v), -1);
        var others = addons.Where(v => v != target).ToList();
        if (target < 0 || others.Count == 0) { Say($"{Keybinds.Shown("join")}: select add-ons, then last the add-on, turret or hull they join.", 5); return; }
        Plugin.ModLog.LogInfo($"Ctrl+J: joining {string.Join(", ", others)} into {target} (selection order {string.Join(", ", picked)})");
        RequestLiveEdit("Merging add-ons", $"Merged {others.Count} add-on{(others.Count == 1 ? "" : "s")} into the last part selected.", json => AddonEdits.PlanMerge(json, target, others, LiveShapes(json)));
    }

    /// Attaches `children` to `parent` where they are, with the game's own operation (one undo step), like Blender's
    /// Parent to active. A child that `parent` hangs from is left out (that would loop).
    internal void ParentTo(int parent, IReadOnlyCollection<int> children)
    {
        if (!ready || busy || core?.Editor?.operations is not { } ops) return;
        var byId = new Dictionary<int, VehicleObject>();
        foreach (var o in Each(core.Target.Cast<IVehicleGateway>().ObjectReader.Items)) if (o != null) byId[(int)o.VUID] = o;
        if (!byId.TryGetValue(parent, out var to)) return;
        var above = new HashSet<int>(); // `parent` and every part it hangs from
        for (var t = to.transform; t != null; t = t.parent) if (t.GetComponent<VehicleObject>() is { } up) above.Add((int)up.VUID);
        var moving = children.Where(c => c != parent && !above.Contains(c) && byId.ContainsKey(c)).Select(c => byId[c]).ToList();
        if (moving.Count == 0) { Say("Parent to active: select the parts to attach, then last the part they attach to.", 6); return; }
        ops.SetParent(to.GetReference(), new Il2CppReferenceArray<ISoftVehicleObject>(moving.Select(c => c.GetReference()).ToArray()), ops.GetNewGroupID());
        Say($"Attached {moving.Count} part{(moving.Count == 1 ? "" : "s")} to part {parent}. Ctrl+Z undoes it.", 6);
        Plugin.ModLog.LogInfo($"Parent to active: {string.Join(", ", moving.Select(c => (int)c.VUID))} onto {parent}");
    }

    internal void RequestRestore()
    {
        if (!CanRestore || CaptureBlocked()) return;
        busy = true; Say("Restoring the design from before the last edit...", 30); requestSession = vehicles.Now;
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

    /// Every part of the vehicle being edited.
    internal IEnumerable<VehicleObject> AllParts()
    {
        if (core?.Target == null) yield break;
        foreach (var part in Each(core.Target.Cast<IVehicleGateway>().ObjectReader.Items))
            if (part != null) yield return part;
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

    private bool liveShapesFailed;

    /// Where the game shows each part's shape (shape coordinates to vehicle space, by the game's own conversion), for the
    /// saved parts and the images of parts it shows twice. Null if the game won't say: edits then use the design's maths.
    internal AddonEdits.LiveShapes? LiveShapes(string savedJson)
    {
        if (liveShapesFailed) return null;
        try
        {
            var saved = Conversion.Objects(Conversion.Parse(savedJson)).Keys.ToHashSet();
            var parts = new Dictionary<int, System.Numerics.Matrix4x4>();
            var byMesh = new Dictionary<IntPtr, List<int>>();
            var unsaved = new List<(IntPtr Mesh, System.Numerics.Matrix4x4 At)>();
            foreach (var obj in AllParts())
            {
                var s = Each(obj.Components).Select(c => c?.TryCast<PlateStructure>()).FirstOrDefault(x => x?.Mesh != null);
                var t = obj.GetComponent<VehicleTransform>();
                if (s == null || t == null) continue;
                Vector3 At(float x, float y, float z) => t.WorldToVehicleSpace(s.StructureToWorldSpace(new Vector3(x, y, z)));
                Vector3 o = At(0, 0, 0), ex = At(1, 0, 0) - o, ey = At(0, 1, 0) - o, ez = At(0, 0, 1) - o;
                var m = new System.Numerics.Matrix4x4(ex.x, ex.y, ex.z, 0, ey.x, ey.y, ey.z, 0, ez.x, ez.y, ez.z, 0, o.x, o.y, o.z, 1);
                int v = (int)obj.VUID;
                if (!saved.Contains(v)) { unsaved.Add((s.Mesh.Pointer, m)); continue; }
                parts[v] = m;
                (byMesh.TryGetValue(s.Mesh.Pointer, out var list) ? list : byMesh[s.Mesh.Pointer] = new()).Add(v);
            }
            // An image is a part of the game's own, not saved, sharing the shape of the one saved part it mirrors.
            var images = new Dictionary<int, System.Numerics.Matrix4x4>();
            foreach (var (mesh, at) in unsaved)
                if (byMesh.TryGetValue(mesh, out var owners) && owners.Count == 1) images[owners[0]] = at;
            return new AddonEdits.LiveShapes(parts, images);
        }
        catch (Exception ex)
        {
            liveShapesFailed = true;
            Plugin.ModLog.LogWarning($"Live part placement unavailable, using the design's maths: {ex.Message}");
            return null;
        }
    }

    /// Boxes round the parts the design has no shape for (hatches, periscopes, lights, wheels, tracks), by part, where the
    /// game shows them, in the vehicle's space as LiveShapes: each shown mesh's bounds, turned and placed as its renderer
    /// is, as triangles facing out. For the cloth to land on (AddonEdits.PlanDrape).
    internal Dictionary<int, List<(System.Numerics.Vector3 A, System.Numerics.Vector3 B, System.Numerics.Vector3 C)>> PartBoxes()
    {
        var boxes = new Dictionary<int, List<(System.Numerics.Vector3 A, System.Numerics.Vector3 B, System.Numerics.Vector3 C)>>();
        foreach (var part in AllParts())
        {
            if (Each(part.Components).Any(c => c?.TryCast<PlateStructure>() != null) || part.GetComponent<VehicleTransform>() is not { } t) continue;
            var list = new List<(System.Numerics.Vector3 A, System.Numerics.Vector3 B, System.Numerics.Vector3 C)>();
            foreach (var filter in part.GetComponentsInChildren<MeshFilter>())
            {
                if (filter.sharedMesh is not { } mesh || filter.GetComponent<MeshRenderer>() is not { enabled: true } || !filter.gameObject.activeInHierarchy
                    || filter.GetComponentInParent<VehicleObject>()?.Pointer != part.Pointer) continue; // a part on this one counts for itself
                var bounds = mesh.bounds;
                var c = new System.Numerics.Vector3[8]; // corner i: x, y, z from its bits 1, 2, 4
                for (int i = 0; i < 8; i++)
                {
                    var at = t.WorldToVehicleSpace(filter.transform.TransformPoint(new Vector3((i & 1) != 0 ? bounds.max.x : bounds.min.x,
                        (i & 2) != 0 ? bounds.max.y : bounds.min.y, (i & 4) != 0 ? bounds.max.z : bounds.min.z)));
                    c[i] = new(at.x, at.y, at.z);
                }
                var mid = c.Aggregate(System.Numerics.Vector3.Zero, (s, q) => s + q) / 8;
                foreach (int axis in new[] { 1, 2, 4 })
                    foreach (int side in new[] { 0, axis })
                    {
                        var (u, v) = axis == 1 ? (2, 4) : axis == 2 ? (1, 4) : (1, 2);
                        var q = new[] { side, side | u, side | u | v, side | v }.Select(i => c[i]).ToArray();
                        var normal = System.Numerics.Vector3.Cross(q[1] - q[0], q[2] - q[0]);
                        var outside = (q[0] + q[1] + q[2] + q[3]) / 4 - mid;
                        if (outside.LengthSquared() < 1e-12f) outside = side == 0 ? -normal : normal; // a flat mesh: its two faces face apart
                        if (System.Numerics.Vector3.Dot(normal, outside) < 0) System.Array.Reverse(q);
                        list.Add((q[0], q[1], q[2]));
                        list.Add((q[0], q[2], q[3]));
                    }
            }
            if (list.Count > 0) boxes[(int)part.VUID] = list;
        }
        return boxes;
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
        // The per-frame features each on their own: one that fails (every frame) is logged once and stops no other.
        Ui.Guard("Tool preferences", ToolSettings.Tick);
        Ui.Guard("QoL Workshop", WorkshopLibrary.Update);
        Ui.Guard("Tool search", CommandPalette.Update);
        Ui.Guard("Part patterns", PartPatterns.Update);
        Ui.Guard("Black box fix", StopNaNs.Tick);
        if (!EditorModal.BlocksInput || QolMenu.IsOpen) Ui.Guard("QoL settings", QolMenu.Update);
        Ui.Guard("Editor lookup", () =>
        {
            bool wasReady = ready;
            if (Time.unscaledTime >= nextLookup)
            {
                nextLookup = Time.unscaledTime + 0.5f;
                var previousCore = core?.Pointer ?? IntPtr.Zero;
                core = UnityEngine.Object.FindObjectOfType<VehicleDesignerCore>();
                if ((core?.Pointer ?? IntPtr.Zero) != previousCore) { filledRings.Clear(); liveShapesFailed = false; }
            }
            ready = core != null && core.HasEditor && core.editorState == VehicleDesignerCore.EditorState.Running;
            if (ready) vehicles.Saw(core!.Target?.Pointer ?? IntPtr.Zero);
            else if (wasReady) vehicles.Left();
            if (wasReady && !ready)
                // Each on its own: one that couldn't run (1.8.6-1.8.7's Speed & acceleration on Sprocket 0.2.55.5) stopped
                // the rest, and QoL's panel sections were gone after the next visit.
                foreach (var (feature, leave) in new (string, Action)[] { ("Right-click menu", RightClickMenu.Close), ("Mesh tools", MeshTools.LeftEditor), ("Exploded view", ExplodedView.LeftEditor),
                             ("Gear speeds", GearSpeeds.LeftEditor), ("Predictive build assist", BuildAssist.LeftEditor), ("Mesh selection", MeshSelection.LeftEditor),
                             ("QoL Workshop", WorkshopLibrary.LeftEditor), ("Tool search", CommandPalette.LeftEditor), ("Part patterns", PartPatterns.LeftEditor),
                             ("Own paint", PartPaint.LeftEditor), ("OBJ menu", ObjTransfer.LeftEditor), ("QoL panel", QolPanel.Close) })
                    Ui.Guard(feature, leave);
        });
        if (!EditorModal.BlocksInput) Ui.Guard("OBJ menu", ObjTransfer.Update);
        if (ready && !busy && !ObjTransfer.BlocksInput && !EditorModal.BlocksInput) Ui.Guard("Ctrl+J", JoinHotkey);
        if (ready && !busy && !ObjTransfer.BlocksInput && !EditorModal.BlocksInput && Time.unscaledTime >= nextTurretCheck) { nextTurretCheck = Time.unscaledTime + 0.5f; Ui.Guard("Turret mirror", FillMirroredTurrets); Ui.Guard("Turret mirror keep", KeepTurretsMirrored); }
        if (ready && !ObjTransfer.BlocksInput && !EditorModal.BlocksInput)
        {
            if (!busy && !PhotoShot.Capturing && !DrawingSheet.Capturing) Ui.Guard("Right-click menu", RightClickMenu.Update);
            Ui.Guard("Mesh tools keys", MeshTools.Keys);
            Ui.Guard("Exploded view", ExplodedView.Keys);
            Ui.Guard("Own paint", PartPaint.Tick);
            Ui.Guard("Hotkeys", Hotkeys.Keys);
            Ui.Guard("Part clipboard", PartClipboard.Update);
            Ui.Guard("Gear speeds", GearSpeeds.Update);
            Ui.Guard("Mesh selection", MeshSelection.Tick);
            Ui.Guard("Precision measurements", MeshTools.TickPrecision);
        }
        if (ready) Ui.Guard("QoL panel", QolPanel.Update);
        Ui.Guard("Mouse over QoL boxes", ready ? InputShield.Update : InputShield.Release);
        // A photo being taken always runs on, to finish and put the settings and overlay back; only new ones wait for windows.
        if (PhotoShot.Capturing || !ObjTransfer.BlocksInput && !EditorModal.BlocksInput) Ui.Guard("Photo", PhotoShot.Update);
        if (ready && !ObjTransfer.BlocksInput && !EditorModal.BlocksInput) Ui.Guard("Drawing sheet", DrawingSheet.Update);
        // Design edits: a failure ends the edit (so the editor isn't left busy) and says why.
        try
        {
            if (pending != null && pending.IsCompleted)
            {
                var task = pending; pending = null; busy = false;
                if (ready) vehicles.Saw(core!.Target?.Pointer ?? IntPtr.Zero); // the reload's own vehicle, before the load counts as over
                vehicles.Loaded();
                if (requestSession != vehicles.Now)
                {
                    // Another vehicle was opened while this loaded: it gets neither this edit's Restore nor its follow-up.
                    recoveryJson = null; afterEdit = null;
                    Say("Another vehicle was opened while the QoL edit loaded, so it has no Restore. Its backup is in SprocketQoLBackups.", 10);
                    Plugin.ModLog.LogWarning($"QOL_EDIT {editName}: another vehicle was opened while it loaded");
                }
                else if (task.IsFaulted || task.IsCanceled)
                {
                    Say(RecoveryBelongsHere ? "The game could not load the result. Your design from before is backed up; use Restore."
                        : "The game could not load the result. Load original.blueprint from SprocketQoLBackups to recover your previous design.", 10);
                    Plugin.ModLog.LogError(task.Exception?.ToString() ?? "Vehicle load cancelled");
                    afterEdit = null;
                }
                else
                {
                    // Our own reload creates a new Target. Another vehicle loaded
                    // by the user must never inherit this Restore button by VUID.
                    recoveryTarget = core?.Target?.Pointer ?? IntPtr.Zero;
                    recoverySession = vehicles.Now;
                    Say(restoring ? "Design restored." : doneMessage, 8);
                    Plugin.ModLog.LogInfo(restoring ? "QOL_RESTORE_OK" : $"QOL_EDIT_OK: {editName}");
                    if (restoring) afterEdit = null; else AfterEdit();
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
            busy = false; waits = 0; afterEdit = null;
            Say("Could not complete: " + ex.Message, 10);
            Plugin.ModLog.LogError(ex);
        }
    }

    internal string Snapshot()
    {
        if (core == null || !core.HasEditor || core.Target == null) throw new Exception("Open a vehicle in the editor first.");
        serializer ??= new VehicleBlueprintSerializer();
        return serializer.SerializeToJSON(serializer.ToBlueprint(core.Target.Cast<IVehicleGateway>()), true);
    }

    /// The click also reaches the game's editor; if that started an operation, wait (~2 s at 60 fps) for it to finish.
    private bool EditorIdle(System.Action retry)
    {
        if (!ready || core == null) throw new Exception("Open a vehicle in the editor first.");
        if (requestSession != vehicles.Now) throw new Exception("another vehicle was opened before the edit could run; nothing was changed.");
        if (PhotoShot.Capturing || DrawingSheet.Capturing) { queued = retry; return false; }
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
        PruneBackups();
    }

    private void SetRecovery(string original, int focus)
    {
        recoveryJson = original;
        recoveryTarget = core?.Target?.Pointer ?? IntPtr.Zero;
        recoverySession = vehicles.Now;
        LastEditedPart = focus;
    }

    /// Only the newest backups are kept (setting "Backups kept"; 0 keeps all): each is a whole design or two, and they
    /// add up. Only the mod's own backup folders (named by time and a code) are ever taken away.
    private static void PruneBackups()
    {
        int keep = Plugin.BackupsKept?.Value ?? 50;
        if (keep <= 0) return;
        try
        {
            WorkshopStorage.Prune(keep);
        }
        catch (Exception ex) { Plugin.ModLog.LogWarning($"Backups: couldn't tidy the old ones: {ex.Message}"); }
    }

    private void RunEdit()
    {
        if (!EditorIdle(RunEdit)) return;
        string original = Snapshot();
        var result = edit!(original);
        var nativeBlueprint = serializer!.DeserializeJSON(result.Json);
        Backup(original, result.Json);
        SetRecovery(original, result.FocusPart);
        restoring = false;
        Plugin.ModLog.LogInfo($"QOL_EDIT {editName}: {result.Log}; backup={recoveryDir}");
        vehicles.Loading();
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
            try { ApplyInPlace(plan, original); busy = false; AfterEdit(); return; }
            catch (Exception ex) { Plugin.ModLog.LogWarning($"QOL_LIVE couldn't apply in place, reloading the design instead: {ex}"); }
        }
        SetRecovery(original, plan.Focus);
        restoring = false;
        vehicles.Loading();
        pending = core!.Load(serializer!.DeserializeJSON(plan.DesignJson), Il2CppSystem.Threading.CancellationToken.None);
    }

    sealed record MeshSwap(PlateStructure Structure, PlateStructureMesh Mesh, PlateStructureMeshBlueprint Old, PlateStructureMeshBlueprint New);

    /// Swaps the parts' meshes as one of the game's own undoable operations, grouped with moving attached parts and
    /// removing parts, so Ctrl+Z and Ctrl+Y work on it like on any other edit. The meshes come from the game reading
    /// the design before and after the edit (the same reader a reload uses). Everything is checked before anything changes.
    /// A change as one of the game's own undoable steps: `apply` runs now (and again on Ctrl+Y), `undo` on Ctrl+Z.
    /// False (nothing done) when there's no editor to hold the step.
    internal bool Undoable(string name, System.Action apply, System.Action undo)
    {
        var ops = core?.Editor?.operations;
        if (ops == null) return false;
        bool done = false;
        var callbacks = new VehicleOperationCallbacks
        {
            Execute = Keep(DelegateSupport.ConvertDelegate<Il2CppSystem.Func<IVehicleOperatorContext, VehicleOPExecutionResult>>(
                new Func<IVehicleOperatorContext, VehicleOPExecutionResult>(_ =>
                {
                    try { apply(); done = true; return VehicleOPExecutionResult.Executed; }
                    catch (Exception ex) { Plugin.ModLog.LogError($"{name}: {ex}"); return VehicleOPExecutionResult.Cancelled; }
                }))!),
            Revert = Keep(DelegateSupport.ConvertDelegate<Il2CppSystem.Action<IVehicleOperatorContext>>(
                new Action<IVehicleOperatorContext>(_ =>
                {
                    try { undo(); } catch (Exception ex) { Plugin.ModLog.LogError($"{name} undo: {ex}"); }
                }))!),
        };
        var flags = Sprocket.VehicleDesigner.Operations.VehicleOp.InternalOpToVehicleOpFlags(global::Operations.Operation.DefaultFlags)
                    | VehicleOperationFlags.Undo | VehicleOperationFlags.Redo | VehicleOperationFlags.Register;
        KeptFor(ops.CreateOp(name, ref callbacks, flags, ops.GetNewGroupID(), VehicleOpExecutionMode.Synchronous));
        return done;
    }

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
            // The part's shape before: its own mesh number in the original (a shared palette mesh gets a new number when edited).
            int oldId = AddonEdits.MeshIdOf(original, vuid);
            changes.Add(new MeshSwap(structure, mesh,
                before.TryGetValue(oldId, out var old) ? old : throw new Exception($"mesh {oldId} missing before the edit"),
                after.TryGetValue(meshId, out var @new) ? @new : throw new Exception($"mesh {meshId} missing after the edit")));
        }
        VehicleObject Part(int v) => byId.TryGetValue(v, out var o) ? o : throw new Exception($"part {v} isn't in the vehicle");
        CheckOnlyTargetsChange(changes, plan, original, byId);
        var remove = plan.Remove.Select(v => Part(v).GetReference()).ToArray();
        var moves = plan.Reparent.GroupBy(r => r.Parent).Select(g => (Parent: Part(g.Key), Children: g.Select(r => Part(r.Child)).ToList())).ToList();

        var ops = core.Editor.operations;
        int group = ops.GetNewGroupID();
        string name = editName, done = doneMessage;
        bool executed = false;
        var removedPaint = PartPaint.SlotsOf(plan.Remove); // removed parts leave the own paint lists, and come back with Ctrl+Z
        var callbacks = new VehicleOperationCallbacks
        {
            Execute = Keep(DelegateSupport.ConvertDelegate<Il2CppSystem.Func<IVehicleOperatorContext, VehicleOPExecutionResult>>(
                new Func<IVehicleOperatorContext, VehicleOPExecutionResult>(_ =>
                {
                    executed = Swap(changes, true, name);
                    if (executed) { PartPaint.Put(removedPaint.ToDictionary(kv => kv.Key, _ => 0)); Say(done + " Ctrl+Z undoes it.", 8); }
                    return executed ? VehicleOPExecutionResult.Executed : VehicleOPExecutionResult.Cancelled;
                }))!),
            Revert = Keep(DelegateSupport.ConvertDelegate<Il2CppSystem.Action<IVehicleOperatorContext>>(
                new Action<IVehicleOperatorContext>(_ => { Swap(changes, false, name); PartPaint.Put(removedPaint); }))!),
        };
        // The game's usual flags plus undo/redo: with Register alone the step is kept in the history but Ctrl+Z skips it.
        var flags = Sprocket.VehicleDesigner.Operations.VehicleOp.InternalOpToVehicleOpFlags(global::Operations.Operation.DefaultFlags)
                    | VehicleOperationFlags.Undo | VehicleOperationFlags.Redo | VehicleOperationFlags.Register;
        var op = KeptFor(ops.CreateOp(name, ref callbacks, flags, group, VehicleOpExecutionMode.Synchronous));
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

    /// A changed part's live shape may only be shared with other changed parts (its mirror twin): otherwise swapping it
    /// would change that part too. Checked on the live objects, then by a trial swap and a save to memory, where only
    /// the changed parts may differ.
    private void CheckOnlyTargetsChange(List<MeshSwap> changes, AddonEdits.EditPlan plan, string original, Dictionary<int, VehicleObject> byId)
    {
        var owners = new Dictionary<IntPtr, List<int>>();
        foreach (var (vuid, obj) in byId)
            foreach (var s in Each(obj.Components).Select(c => c?.TryCast<PlateStructure>()).Where(s => s?.Mesh != null))
                (owners.TryGetValue(s!.Mesh.Pointer, out var list) ? list : owners[s.Mesh.Pointer] = new()).Add(vuid);
        // A part the game shows twice has its image as a part of its own, not saved in the design: that one follows.
        var saved = Conversion.Objects(Conversion.Parse(original)).Keys.ToHashSet();
        var sharers = changes.SelectMany(c => owners[c.Mesh.Pointer]).Where(v => !plan.MeshIds.ContainsKey(v)).Distinct().ToList();
        if (sharers.Any(saved.Contains)) throw new Exception("a changed part shares its live shape with another part");
        if (sharers.Count > 0) Plugin.ModLog.LogInfo($"QOL_LIVE the game's mirror images {string.Join(", ", sharers)} share the changed shape and follow it");

        // Any trouble here throws: the caller then loads the planned design, which replaces whatever the trial left.
        if (!Swap(changes, true, "trial of " + editName)) throw new Exception("trial: the new shapes didn't go in");
        string trial;
        try { trial = Snapshot(); }
        finally
        {
            if (!Swap(changes, false, "trial of " + editName)) throw new Exception("trial: the old shapes didn't go back");
        }
        // Every other part exactly as it was; changed ones where the plan puts their faces (the game may tidy corner
        // settings as it loads a shape, the same as a reload would).
        var was = AddonEdits.ShapeKeys(original);
        var now = AddonEdits.ShapeKeys(trial);
        var nowFaces = AddonEdits.ShapeKeys(trial, full: false);
        var want = AddonEdits.ShapeKeys(plan.DesignJson, full: false);
        var back = AddonEdits.ShapeKeys(Snapshot());
        foreach (var (vuid, shape) in was)
        {
            bool changed = plan.MeshIds.ContainsKey(vuid);
            if (changed ? nowFaces.GetValueOrDefault(vuid) != want.GetValueOrDefault(vuid) : now.GetValueOrDefault(vuid) != shape)
                throw new Exception($"trial: part {vuid} {(changed ? "didn't take the planned shape" : "changed, though the edit isn't for it")}");
            if (back.GetValueOrDefault(vuid) != shape) throw new Exception($"trial: part {vuid} didn't go back exactly");
        }
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

    static T Keep<T>(T value) where T : class { keeping.Add(value); return value; }

    /// The delegates kept since the last step was made now belong to step `op`.
    static IVehicleOperation? KeptFor(IVehicleOperation? op)
    {
        if (op != null) alive[op.Pointer] = keeping;
        keeping = new();
        return op;
    }

    internal static void Forget(IntPtr step) => alive.Remove(step);

    internal static IEnumerable<T> Each<T>(Il2CppSystem.Collections.Generic.IReadOnlyList<T> list)
    {
        int count = list.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<T>>().Count;
        for (int i = 0; i < count; i++) yield return list[i];
    }

    private void Restore()
    {
        if (PhotoShot.Capturing || DrawingSheet.Capturing) { queued = Restore; return; }
        if (recoveryJson == null || !RecoveryBelongsHere || core == null || !core.DesignIOPossible) throw new Exception("No recovery is available for this vehicle, or the editor is busy.");
        // Keep any work done after the edit before replacing it.
        File.WriteAllText(Path.Combine(recoveryDir!, "before-restore-" + DateTime.Now.ToString("HHmmssfff") + ".blueprint"), Snapshot());
        restoring = true;
        vehicles.Loading();
        pending = core.Load(serializer!.DeserializeJSON(recoveryJson), Il2CppSystem.Threading.CancellationToken.None);
    }

    // Result message and the hotkeys box; the controls live in the game's inspector.
    public void OnGUI()
    {
        if (PhotoShot.Capturing) return; // nothing of ours in the photo
        Ui.Guard("OBJ preview", ObjTransfer.DrawPreview);
        if (!ObjTransfer.BlocksInput)
        {
            Ui.Guard("Hotkeys", Hotkeys.DrawBox);
            Ui.Guard("Measurements", MeshTools.DrawMeasures);
            Ui.Guard("Predictive build assist", BuildAssist.Draw);
            Ui.Guard("Mesh selection", MeshSelection.Draw);
            Ui.Guard("Selection measurements", MeshTools.DrawSelectionMeasures);
            Ui.Guard("Create Hole preview", HoleQuality.DrawHovered);
            Ui.Guard("Tool hover outline", MeshTools.DrawHoverTool);
            Ui.Guard("Right-click menu", RightClickMenu.Draw);
        }
        if (Time.unscaledTime > statusUntil || string.IsNullOrEmpty(status)) return;
        float width = Math.Min(620, Screen.width - 40);
        GUI.Box(new Rect((Screen.width - width) / 2, 80, width, 30), status); // below the game's vehicle name bar
    }
}

/// The delegates QoL hands to one of the game's undo steps live as long as the step: its history dropped or cleared, or the
/// step cancelled, they go (they used to be kept for the whole session).
[HarmonyPatch]
public static class UndoStepLifetime
{
    [HarmonyPostfix, HarmonyPatch(typeof(Sprocket.VehicleDesigner.Operations.VehicleOp), nameof(Sprocket.VehicleDesigner.Operations.VehicleOp.ReleaseInternal))]
    static void Released(Sprocket.VehicleDesigner.Operations.VehicleOp __instance) => DesignEditor.Forget(__instance.Pointer);

    [HarmonyPostfix, HarmonyPatch(typeof(Sprocket.VehicleDesigner.Operations.VehicleOp), nameof(Sprocket.VehicleDesigner.Operations.VehicleOp.CancelInternal))]
    static void Cancelled(Sprocket.VehicleDesigner.Operations.VehicleOp __instance) => DesignEditor.Forget(__instance.Pointer);
}

/// "Restore design before last edit" in the panel of the part the last QoL edit produced.
[HarmonyPatch]
public static class RestoreSection
{
    internal static void Draw(PlateStructureEditor __instance, Panel layout) => Ui.Inspector("Restore", layout, () =>
    {
        var editor = DesignEditor.Instance;
        var ui = Ui.Drawer(layout);
        if (editor == null || ui == null || !editor.CanRestore || editor.LastEditedPart != (int)__instance.Component.VehicleObject.VUID) return;
        Ui.Section(layout, "Undo last Quality of Life edit");
        ui.InfoField("Returns to the snapshot before the last QoL reload. Changes made since then are discarded.", 2);
        var tip = new Tip("Restore previous design", "Reloads the complete design snapshot from before the last Quality of Life operation that reloaded the vehicle, such as turret conversion, repair or paste. Any later edits are discarded. Use Ctrl+Z for ordinary mesh edits instead.");
        ui.Button("Restore previous design", Ui.Callback(editor.RequestRestore), tip);
    });
}
