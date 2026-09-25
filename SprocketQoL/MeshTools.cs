using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.MeshEditing;
using Sprocket.PartImporting;
using Sprocket.PlateMesh;
using Sprocket.PlateMesh.Operations;
using Sprocket.Transformations;
using Sprocket.UI;
using Sprocket.Vehicles.PartImporting;
using Sprocket.Vehicles.PlateStructures.Design;
using UnityEngine;
using UnityEngine.InputSystem;
using Num = System.Numerics.Vector3;

namespace SprocketQoL;

/// Blender-style mesh tools for hand-made structures (MeshPlans does the maths): Flatten (P), Loop cut (T), Inset (I),
/// Bevel (V), Select linked flat faces (U) and Proportional editing (O); plus a 0.5 mm grid and an orthographic view
/// (Numpad 5). Each tool runs as one of the game's own mesh edits, so Ctrl+Z undoes it, and follows the editor's Mirror.
[HarmonyPatch]
public static class MeshTools
{
    // ---------- running a tool as one of the game's mesh edits ----------

    // The game's Delete operation, given nothing to delete, carries a tool so it gets the game's undo and mesh rebuild.
    // Only these instances run a tool; every other Delete runs as normal.
    static readonly Dictionary<IntPtr, (DeleteOp Op, string Name, Func<EditMesh, (bool Done, string Message)> Apply)> ours = new();
    static PlateStructureEditOperations? notify;

    [HarmonyPrefix, HarmonyPatch(typeof(DeleteOp), "ExecuteInternal")]
    static bool Intercept(DeleteOp __instance, EditMesh mesh)
    {
        if (!ours.TryGetValue(__instance.Pointer, out var tool)) return true;
        Ui.Guard(tool.Name, () =>
        {
            var (done, message) = tool.Apply(mesh);
            Plugin.ModLog.LogInfo($"{tool.Name}: {message}");
            if (!done) notify?.NotifyError($"{tool.Name}: {message}");
        });
        return false;
    }

    internal static void Run(PlateStructureEditor editor, string name, Func<EditMesh, (bool, string)> apply) => Ui.Guard(name, () =>
    {
        var op = new DeleteOp(DeleteType.None) { Name = name };
        ours[op.Pointer] = (op, name, apply);
        var ops = notify = editor.operations;
        ops.Execute(editor.meshEditor.CreateTopoOp(op), StructureEditOperationOptions.None, ops.GetNewGroupID());
        editor.meshEditor.SelectFlush();
    });

    /// The mesh as indices: every face's corners (in its own turning), point positions, and what's selected.
    sealed class View
    {
        public readonly List<Vertex> Verts = new();
        public readonly List<Num> Pos = new();
        public readonly List<Face> Faces = new();
        public readonly List<int[]> Corners = new();
        public readonly HashSet<int> SelectedFaces = new(), SelectedPoints = new();
        public readonly List<(int A, int B)> SelectedEdges = new();
        public readonly HashSet<(int, int)> Loose = new();
        readonly Dictionary<IntPtr, int> index = new();

        public int Id(Vertex v)
        {
            if (!index.TryGetValue(v.Pointer, out int i)) { index[v.Pointer] = i = Verts.Count; Verts.Add(v); Pos.Add(HoleQuality.ToNum(v.position)); }
            return i;
        }

        public View(EditMesh mesh)
        {
            var faces = mesh.faces;
            for (int i = 0; i < faces.Count; i++)
            {
                Faces.Add(faces[i]);
                Corners.Add(HoleQuality.Corners(faces[i], Id));
                if (faces[i].HasFlag(ElementFlags.Selected)) SelectedFaces.Add(i);
            }
            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Count; i++) if (vertices[i].HasFlag(ElementFlags.Selected)) SelectedPoints.Add(Id(vertices[i]));
            var edges = mesh.edges;
            for (int i = 0; i < edges.Count; i++)
            {
                int a = Id(edges[i].v0), b = Id(edges[i].v1);
                if (edges[i].HasFlag(ElementFlags.Selected)) SelectedEdges.Add((a, b));
                if (edges[i].loop == null) Loose.Add(FaceMerge.Key(a, b));
            }
        }

        /// The corner of face `f` at point `v`, or null.
        public Loop? CornerIn(int f, int v)
        {
            int k = Array.IndexOf(Corners[f], v);
            if (k < 0) return null;
            var l = Faces[f].firstLoop;
            for (int i = 0; i < k; i++) l = l.next;
            return l;
        }
    }

    static float Tolerance => MeshTransformation.MirrorMaxDistance > 0 ? MeshTransformation.MirrorMaxDistance : 0.0003f;

    /// With Mirror on, the mirrored twins of these edges (both ends must have a twin, and a face must use the edge).
    static List<(int, int)> WithTwins(View v, List<(int A, int B)> edges, bool mirror)
    {
        if (!mirror) return edges;
        var twins = MeshPlans.Twins(v.Pos, edges.SelectMany(e => new[] { e.A, e.B }).Distinct(), Tolerance);
        var used = v.Corners.SelectMany(c => c.Select((p, k) => FaceMerge.Key(p, c[(k + 1) % c.Length]))).ToHashSet();
        return edges.Concat(edges.Where(e => twins.ContainsKey(e.A) && twins.ContainsKey(e.B) && used.Contains(FaceMerge.Key(twins[e.A], twins[e.B])))
                                 .Select(e => (twins[e.A], twins[e.B]))).Distinct().ToList();
    }

    static HashSet<int> WithTwins(View v, HashSet<int> faces, bool mirror)
    {
        if (mirror) faces.UnionWith(FaceMerge.Mirrored(v.Pos, v.Corners, faces.ToList(), Tolerance));
        return faces;
    }

    /// Takes out the plan's faces and puts in its new ones, with the game's own mesh calls. Old corners keep their face's
    /// thickness and thickening; new points blend from the corners they come from. Edges and points nothing uses go.
    static (bool, string) Apply(EditMesh mesh, View view, MeshPlans.Rebuild plan, string did)
    {
        if (plan.Why != null) return (false, plan.Why);
        int old = view.Pos.Count;
        var verts = new List<Vertex>(view.Verts);
        var ids = new Dictionary<IntPtr, int>();
        for (int i = 0; i < verts.Count; i++) ids[verts[i].Pointer] = i;
        foreach (var p in plan.Points)
        {
            // CreateVertex copies its prototype's position, so place the new point after creating it.
            var at = new Vector3(p.P.X, p.P.Y, p.P.Z);
            var v = mesh.CreateVertex(verts[p.Blend[0].V], at);
            v.position = at;
            ids[v.Pointer] = verts.Count;
            verts.Add(v);
        }
        var removed = plan.Remove.ToHashSet();
        var anyCorner = new Dictionary<int, Loop>();
        foreach (int f in plan.Remove.Concat(Enumerable.Range(0, view.Faces.Count)))
            foreach (int v in view.Corners[f]) if (!anyCorner.ContainsKey(v)) anyCorner[v] = view.CornerIn(f, v)!;
        Loop Corner(int f, int v) => view.CornerIn(f, v) ?? anyCorner[v];
        float Thickness(int f, int v) => v < old ? Corner(f, v).thickness
            : plan.Points[v - old].Blend.Sum(b => b.W * Thickness(f, b.V)) / plan.Points[v - old].Blend.Sum(b => b.W);
        Loop From(int f, int v) => Corner(f, v < old ? v : plan.Points[v - old].Blend[0].V);

        // Edges and points still in use afterwards: everything the kept and new faces touch, and loose edges.
        var keptEdges = new HashSet<(int, int)>(view.Loose);
        var keptPoints = view.Loose.SelectMany(e => new[] { e.Item1, e.Item2 }).ToHashSet();
        foreach (var c in Enumerable.Range(0, view.Faces.Count).Where(f => !removed.Contains(f)).Select(f => view.Corners[f]).Concat(plan.Add.Select(a => a.Corners)))
            for (int k = 0; k < c.Length; k++) { keptEdges.Add(FaceMerge.Key(c[k], c[(k + 1) % c.Length])); keptPoints.Add(c[k]); }

        var made = new List<(Face Face, int Source)>();
        foreach (var nf in plan.Add)
        {
            var source = view.Faces[nf.Source];
            var vs = nf.Corners.Select(i => verts[i]).ToArray();
            var es = new Edge[vs.Length];
            for (int k = 0; k < vs.Length; k++)
            {
                var a = vs[k];
                var b = vs[(k + 1) % vs.Length];
                if (Edge.GetConnectingEdge(a, b) is { } existing) { es[k] = existing; continue; }
                es[k] = mesh.CreateEdge(a, b, source.firstLoop.edge);
                es[k].DisableFlag(ElementFlags.Sharp);
            }
            made.Add((mesh.CreateFace(new Il2CppReferenceArray<Vertex>(vs), new Il2CppReferenceArray<Edge>(es), source,
                new Il2CppStructArray<ushort>(nf.Corners.Select(i => (ushort)Math.Round(Thickness(nf.Source, i))).ToArray())), nf.Source));
        }
        foreach (int f in removed)
        {
            var l = view.Faces[f].firstLoop;
            for (int k = 0; k < view.Faces[f].vertexCount; k++, l = l.next)
                if (!keptEdges.Contains(FaceMerge.Key(ids[l.vertex.Pointer], ids[l.next.vertex.Pointer]))) l.edge.EnableFlag(ElementFlags.Delete);
            view.Faces[f].EnableFlag(ElementFlags.Delete);
            foreach (int v in view.Corners[f]) if (!keptPoints.Contains(v)) verts[v].EnableFlag(ElementFlags.Delete);
        }
        foreach (var (face, source) in made)
        {
            var l = face.firstLoop;
            for (int k = 0; k < face.vertexCount; k++, l = l.next)
            {
                var from = From(source, ids[l.vertex.Pointer]);
                l.thickenMode = from.thickenMode;
                l.thickenEdge = from.thickenEdge;
            }
        }
        FinishDelete(mesh);
        var problems = made.Select(m => HoleQuality.Problem(m.Face)).Where(p => p != null).Distinct().ToList();
        return (true, $"{did}: {removed.Count} faces became {made.Count}, {plan.Points.Count} new points" +
                      (problems.Count == 0 ? ", mesh checks OK" : ", MESH CHECK FAILED: " + string.Join("; ", problems)));
    }

    /// A corner thickening along an edge being deleted lets the game pick instead; then the marked parts go.
    internal static int FinishDelete(EditMesh mesh)
    {
        int repointed = 0;
        var faces = mesh.faces;
        for (int i = 0; i < faces.Count; i++)
        {
            if (faces[i].HasFlag(ElementFlags.Delete)) continue;
            var l = faces[i].firstLoop;
            for (int k = 0; k < faces[i].vertexCount; k++, l = l.next)
                if (l.thickenEdge != null && l.thickenEdge.HasFlag(ElementFlags.Delete))
                {
                    l.thickenEdge = null;
                    if (l.thickenMode == ThickenMode.AlongEdgeManual) l.thickenMode = ThickenMode.AlongEdgeAuto;
                    repointed++;
                }
        }
        mesh.DeleteMarked();
        mesh.MarkDirty(MeshDirtyFlags.All);
        return repointed;
    }

    // ---------- the tools ----------

    static MeshPlans.FlattenMode flattenMode;
    static readonly string[] FlattenNames = { "Flatten: best-fit plane", "Flatten: level (one height)", "Flatten: sideways (one x)", "Flatten: lengthways (one z)" };
    static float insetMm = 50, bevelMm = 30, flatAngle = 5, radiusMm = 500;
    static bool proportional, halfGrid;

    static void Flatten(PlateStructureEditor e)
    {
        bool mirror = e.meshEditor.Symmetry;
        var mode = flattenMode;
        Run(e, "Flatten", mesh =>
        {
            var v = new View(mesh);
            var points = new HashSet<int>(v.SelectedPoints);
            foreach (int f in v.SelectedFaces) points.UnionWith(v.Corners[f]);
            Num? normal = v.SelectedFaces.Count > 0 ? v.SelectedFaces.Aggregate(Num.Zero, (s, f) => s + HoleRing.Normal(v.Corners[f].Select(p => v.Pos[p]).ToList())) : null;
            var moved = MeshPlans.Flatten(v.Pos, points, mode, normal);
            if (moved.Count == 0) return (false, "select three or more points (two to level them)");
            if (mirror)
                foreach (var (p, twin) in MeshPlans.Twins(v.Pos, points, Tolerance))
                    if (!points.Contains(twin)) moved[twin] = new Num(-moved[p].X, moved[p].Y, moved[p].Z);
            foreach (var (p, at) in moved) v.Verts[p].position = new Vector3(at.X, at.Y, at.Z);
            mesh.MarkDirty(MeshDirtyFlags.All);
            return (true, $"flattened {moved.Count} points");
        });
    }

    static void LoopCut(PlateStructureEditor e)
    {
        if (e.meshEditor.SelectType != MeshEditType.Edge) { e.operations.NotifyError("Loop cut: switch to Edges and select an edge"); return; }
        bool mirror = e.meshEditor.Symmetry;
        Run(e, "Loop cut", mesh =>
        {
            var v = new View(mesh);
            if (v.SelectedEdges.Count == 0) return (false, "select an edge first");
            return Apply(mesh, v, MeshPlans.LoopCut(v.Pos, v.Corners, WithTwins(v, v.SelectedEdges, mirror)), "loop cut");
        });
    }

    static void Inset(PlateStructureEditor e)
    {
        bool mirror = e.meshEditor.Symmetry;
        float width = insetMm / 1000;
        Run(e, "Inset", mesh =>
        {
            var v = new View(mesh);
            return Apply(mesh, v, MeshPlans.Inset(v.Pos, v.Corners, WithTwins(v, new HashSet<int>(v.SelectedFaces), mirror), width), "inset");
        });
    }

    static void Bevel(PlateStructureEditor e)
    {
        if (e.meshEditor.SelectType != MeshEditType.Edge) { e.operations.NotifyError("Bevel: switch to Edges and select edges"); return; }
        bool mirror = e.meshEditor.Symmetry;
        float width = bevelMm / 1000;
        Run(e, "Bevel", mesh =>
        {
            var v = new View(mesh);
            if (v.SelectedEdges.Count == 0) return (false, "select edges first");
            return Apply(mesh, v, MeshPlans.Bevel(v.Pos, v.Corners, WithTwins(v, v.SelectedEdges, mirror), width), "bevel");
        });
    }

    static void SelectFlat(PlateStructureEditor e)
    {
        bool mirror = e.meshEditor.Symmetry;
        float angle = flatAngle;
        Run(e, "Select linked flat", mesh =>
        {
            var v = new View(mesh);
            if (v.SelectedFaces.Count == 0) return (false, "select a face first");
            var found = MeshPlans.LinkedFlat(v.Pos, v.Corners, WithTwins(v, new HashSet<int>(v.SelectedFaces), mirror), angle);
            foreach (int f in found)
            {
                v.Faces[f].EnableFlag(ElementFlags.Selected);
                var l = v.Faces[f].firstLoop;
                for (int k = 0; k < v.Faces[f].vertexCount; k++, l = l.next) { l.vertex.EnableFlag(ElementFlags.Selected); l.edge.EnableFlag(ElementFlags.Selected); }
            }
            mesh.MarkDirty(MeshDirtyFlags.All);
            return (true, $"selected {found.Count} faces ({found.Count - v.SelectedFaces.Count} more)");
        });
    }

    // ---------- keys (from DesignEditor.Update) ----------

    internal static void Keys()
    {
        var keys = Keyboard.current;
        if (keys == null || Typing()) return;
        var e = Hotkeys.Current;
        if (e != null && halfGrid) KeepHalfGrid(e);
        if (keys.ctrlKey.isPressed || keys.altKey.isPressed || keys.shiftKey.isPressed) return; // those belong to the game
        if (keys.numpad5Key.wasPressedThisFrame) ToggleOrtho();
        if (ortho && (keys.numpadPlusKey.wasPressedThisFrame || keys.numpadMinusKey.wasPressedThisFrame))
        {
            orthoZoom = Math.Clamp(orthoZoom * (keys.numpadPlusKey.wasPressedThisFrame ? 1.25f : 0.8f), 0.1f, 10);
            e?.RequestRedraw(); // the panel's zoom slider follows
        }
        if (e == null) return;
        if (keys.pKey.wasPressedThisFrame) Flatten(e);
        else if (keys.tKey.wasPressedThisFrame) LoopCut(e);
        else if (keys.iKey.wasPressedThisFrame) Inset(e);
        else if (keys.vKey.wasPressedThisFrame) Bevel(e);
        else if (keys.uKey.wasPressedThisFrame) SelectFlat(e);
        else if (keys.oKey.wasPressedThisFrame) { proportional = !proportional; e.RequestRedraw(); Plugin.ModLog.LogInfo($"Proportional editing {(proportional ? "on" : "off")}"); }
    }

    /// Typing in a text box (a part's name): the keys are letters then, not tools.
    static bool Typing()
    {
        var go = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
        return go != null && (go.GetComponent<TMPro.TMP_InputField>()?.isFocused == true || go.GetComponent<UnityEngine.UI.InputField>()?.isFocused == true);
    }

    // ---------- 0.5 mm grid ----------

    static float? gridBefore;

    /// Half a millimetre, in whatever unit the game's grid size is in (it shows millimetres; it may store metres).
    static void KeepHalfGrid(PlateStructureEditor e)
    {
        float now = e.meshEditor.GridSize;
        float half = (gridBefore ?? now) >= 0.01f ? 0.5f : 0.0005f;
        gridBefore ??= now;
        if (Math.Abs(now - half) > half * 0.01f) e.meshEditor.GridSize = half;
    }

    // ---------- orthographic view ----------

    static bool ortho, orthoWhole = true, orthoLogged;
    static float orthoZoom = 1; // on top of the orbit distance, which stops at the game's closest zoom
    static float? farBefore;
    const float PullBack = 100; // metres the camera steps back in orthographic view, so it never cuts into the vehicle
    static Sprocket.OrbitalMovementController? orbit;

    static void ToggleOrtho()
    {
        ortho = !ortho;
        if (!ortho) PutCameraBack();
        Plugin.ModLog.LogInfo($"Orthographic view {(ortho ? "on" : "off")}");
    }

    /// Perspective again, where the game put the camera, with its own far cut.
    static void PutCameraBack()
    {
        var cam = Camera.main;
        if (cam == null) return;
        cam.orthographic = false;
        if (orbit != null) cam.transform.position = orbit.AppliedPosition;
        if (farBefore is { } far) { cam.farClipPlane = far; farBefore = null; }
    }

    /// Each time the game's orbit camera places itself: in orthographic view the view is the size a perspective view
    /// shows at the orbit distance (zooming still works; the ground doesn't count), and, with "see the whole vehicle",
    /// the camera steps back along its view so its near cut never slices into the vehicle. An orthographic view looks
    /// the same from any distance. Set from the game's own position every time, so nothing adds up.
    [HarmonyPostfix, HarmonyPatch(typeof(Sprocket.OrbitalMovementController), nameof(Sprocket.OrbitalMovementController.ApplyInputs))]
    static void OrthoCamera(Sprocket.OrbitalMovementController __instance) => Ui.Guard("Orthographic view", () =>
    {
        orbit = __instance;
        if (!ortho) return;
        var cam = Camera.main;
        if (cam == null) return;
        if (!orthoLogged)
        {
            orthoLogged = true;
            Plugin.ModLog.LogInfo($"Orthographic view: orbit distance {__instance.Distance:0.00} m, camera {Vector3.Distance(cam.transform.position, __instance.AppliedPosition):0.000} m from where the orbit put it");
        }
        cam.orthographic = true;
        cam.orthographicSize = Math.Max(0.005f, __instance.Distance * MathF.Tan(cam.fieldOfView * MathF.PI / 360) / orthoZoom);
        if (!orthoWhole) return;
        farBefore ??= cam.farClipPlane;
        cam.transform.position = __instance.AppliedPosition - cam.transform.forward * PullBack;
        cam.farClipPlane = Math.Max(farBefore.Value, __instance.Distance + PullBack + 1000);
    });

    // ---------- proportional editing ----------

    /// One move, scale or rotate: the points it moves, the points near them that follow, and how much.
    sealed class Session
    {
        public EditMesh Mesh = null!;
        public readonly List<Vertex> Movers = new(), Near = new();
        public readonly List<Num> MoverFrom = new(), NearFrom = new();
        public readonly List<(int[] Movers, float[] Weights)> Follow = new();
        public Num[]? Final;
    }
    static readonly Dictionary<IntPtr, Session?> sessions = new();

    static void Begin(MeshTransformation t, Transformation op)
    {
        if (sessions.ContainsKey(op.Pointer)) return;
        sessions[op.Pointer] = null;
        if (!proportional) return;
        var mesh = t.mesh;
        var all = new List<Vertex>();
        var pos = new List<Num>();
        var list = mesh.vertices;
        for (int i = 0; i < list.Count; i++) { all.Add(list[i]); pos.Add(HoleQuality.ToNum(list[i].position)); }
        var moving = Enumerable.Range(0, all.Count).Where(i => all[i].HasFlag(ElementFlags.Selected)).ToHashSet();
        if (Hotkeys.Current?.meshEditor.Symmetry == true) moving.UnionWith(MeshPlans.Twins(pos, moving.ToList(), Tolerance).Values); // the game moves those too
        var s = new Session { Mesh = mesh };
        var slot = new Dictionary<int, int>();
        foreach (int m in moving) { slot[m] = s.Movers.Count; s.Movers.Add(all[m]); s.MoverFrom.Add(pos[m]); }
        foreach (var (v, (movers, weights)) in MeshPlans.Falloff(pos, moving, radiusMm / 1000))
        {
            s.Near.Add(all[v]);
            s.NearFrom.Add(pos[v]);
            s.Follow.Add((movers.Select(m => slot[m]).ToArray(), weights));
        }
        sessions[op.Pointer] = s;
        Plugin.ModLog.LogInfo($"Proportional editing: {s.Movers.Count} points moving, {s.Near.Count} following within {radiusMm:0} mm");
    }

    static void Follow(Transformation op)
    {
        if (!sessions.TryGetValue(op.Pointer, out var s) || s == null) return;
        var delta = s.Movers.Select((m, i) => HoleQuality.ToNum(m.position) - s.MoverFrom[i]).ToArray();
        s.Final = new Num[s.Near.Count];
        for (int i = 0; i < s.Near.Count; i++)
        {
            var (movers, weights) = s.Follow[i];
            var d = Num.Zero;
            for (int j = 0; j < movers.Length; j++) d += weights[j] * delta[movers[j]];
            var at = s.NearFrom[i] + d;
            s.Final[i] = at;
            s.Near[i].position = new Vector3(at.X, at.Y, at.Z);
        }
        s.Mesh.MarkDirty(MeshDirtyFlags.Geometry);
    }

    static void Put(Session? s, bool final)
    {
        if (s == null || final && s.Final == null) return;
        for (int i = 0; i < s.Near.Count; i++)
        {
            var at = final ? s.Final![i] : s.NearFrom[i];
            s.Near[i].position = new Vector3(at.X, at.Y, at.Z);
        }
        s.Mesh.MarkDirty(MeshDirtyFlags.Geometry);
    }

    [HarmonyPrefix, HarmonyPatch(typeof(MeshTransformation), nameof(MeshTransformation.ApplyTranslation))]
    static void MoveStart(MeshTransformation __instance, Transformation op) => Ui.Guard("Proportional editing", () => Begin(__instance, op));
    [HarmonyPrefix, HarmonyPatch(typeof(MeshTransformation), nameof(MeshTransformation.ApplyScale))]
    static void ScaleStart(MeshTransformation __instance, Transformation op) => Ui.Guard("Proportional editing", () => Begin(__instance, op));
    [HarmonyPrefix, HarmonyPatch(typeof(MeshTransformation), nameof(MeshTransformation.ApplyRotation))]
    static void RotateStart(MeshTransformation __instance, Transformation op) => Ui.Guard("Proportional editing", () => Begin(__instance, op));
    [HarmonyPostfix, HarmonyPatch(typeof(MeshTransformation), nameof(MeshTransformation.ApplyTranslation))]
    static void Moved(Transformation op) => Ui.Guard("Proportional editing", () => Follow(op));
    [HarmonyPostfix, HarmonyPatch(typeof(MeshTransformation), nameof(MeshTransformation.ApplyScale))]
    static void Scaled(Transformation op) => Ui.Guard("Proportional editing", () => Follow(op));
    [HarmonyPostfix, HarmonyPatch(typeof(MeshTransformation), nameof(MeshTransformation.ApplyRotation))]
    static void Rotated(Transformation op) => Ui.Guard("Proportional editing", () => Follow(op));

    // Cancelled, undone, redone: the followers go back, or forward again, with the moved points.
    [HarmonyPostfix, HarmonyPatch(typeof(MeshTransformation), nameof(MeshTransformation.Restore))]
    static void Cancelled(Transformation op) => Ui.Guard("Proportional editing", () => Put(sessions.GetValueOrDefault(op.Pointer), false));
    [HarmonyPostfix, HarmonyPatch(typeof(MeshGeometryEditOp), nameof(MeshGeometryEditOp.Revert))]
    static void Undone(MeshGeometryEditOp __instance) => Ui.Guard("Proportional editing", () => Put(sessions.GetValueOrDefault(__instance.transformation?.Pointer ?? IntPtr.Zero), false));
    [HarmonyPostfix, HarmonyPatch(typeof(MeshGeometryEditOp), nameof(MeshGeometryEditOp.Execute))]
    static void Redone(MeshGeometryEditOp __instance) => Ui.Guard("Proportional editing", () => Put(sessions.GetValueOrDefault(__instance.transformation?.Pointer ?? IntPtr.Zero), true));

    // ---------- panel ----------

    [HarmonyPostfix, HarmonyPatch(typeof(PlateStructureEditor), nameof(PlateStructureEditor.OnGUI))]
    static void Draw(PlateStructureEditor __instance, IGUILayout layout) => Ui.Guard("Mesh tools", () =>
    {
        var ui = layout.TryCast<IGUIElementDrawer>();
        if (ui == null || __instance.TryCast<FreeformPlateStructureEditor>() == null) return;
        Ui.Section(layout, "Mesh tools");
        ui.InfoField("Keys in brackets. Mirror applies to all of them.\nCtrl+Z undoes each one.", 2);
        var tip = new UITooltip("Mesh tools", "Flatten: selected points onto one plane. Loop cut: through the ring of quads an edge crosses. " +
                                "Inset: a smaller copy of the selected faces with a ring around it. Bevel: selected edges become chamfer strips. " +
                                "Select linked flat: grows the selection over faces lying flat with it.");
        ui.Button("Flatten (P)", Ui.Callback(() => Flatten(__instance)), ref tip);
        ui.Button(FlattenNames[(int)flattenMode], Ui.Callback(() => { flattenMode = (MeshPlans.FlattenMode)(((int)flattenMode + 1) % FlattenNames.Length); __instance.RequestRedraw(); }), ref tip);
        ui.Button("Loop cut (T): select an edge", Ui.Callback(() => LoopCut(__instance)), ref tip);
        ui.Slider("Inset width (mm)", insetMm, 1, 500, Ui.FloatCallback(v => insetMm = MathF.Round(v)));
        ui.Button("Inset (I)", Ui.Callback(() => Inset(__instance)), ref tip);
        ui.Slider("Bevel width (mm)", bevelMm, 1, 500, Ui.FloatCallback(v => bevelMm = MathF.Round(v)));
        ui.Button("Bevel (V): select edges", Ui.Callback(() => Bevel(__instance)), ref tip);
        ui.Slider("Flat within (°)", flatAngle, 0.5f, 30, Ui.FloatCallback(v => flatAngle = MathF.Round(v * 2) / 2));
        ui.Button("Select linked flat (U)", Ui.Callback(() => SelectFlat(__instance)), ref tip);
        ui.ToggleField("Proportional (O)", proportional, Ui.BoolCallback(v => proportional = v),
            "Moving, scaling or rotating points pulls the points around them too, less the further away (up to the radius).");
        ui.Slider("Proportional radius (mm)", radiusMm, 10, 5000, Ui.FloatCallback(v => radiusMm = MathF.Round(v)));
        ui.ToggleField("0.5 mm grid", halfGrid, Ui.BoolCallback(v =>
        {
            halfGrid = v;
            if (!v && gridBefore is { } before) { __instance.meshEditor.GridSize = before; gridBefore = null; }
        }), "Snapping (hold Ctrl while moving) uses a 0.5 mm grid instead of the game's smallest, 1 mm.");
        ui.Slider("Ortho zoom (%)", orthoZoom * 100, 10, 1000, Ui.FloatCallback(v => orthoZoom = MathF.Round(v) / 100));
        ui.ToggleField("Ortho: whole view", orthoWhole, Ui.BoolCallback(v =>
        {
            orthoWhole = v;
            if (!v && ortho) { var cam = Camera.main; if (cam != null && orbit != null) cam.transform.position = orbit.AppliedPosition; }
        }), "Orthographic view (Numpad 5): the camera steps back so it never cuts into the vehicle when you zoom in close. " +
            "Off: it stays where the game puts it, and zooming in close shows the inside.");
    });
}

/// Turrets can be copied with Alt like other parts: the game's turret ring part says it can't be duplicated, and this
/// lets it, in memory, as the game reads its part files (no game file changes). Copying a ring copies everything on it
/// too (turret body, guns, ...), each copy on the copy of its own parent.
[HarmonyPatch]
public static class TurretCopy
{
    // The parts added to the copy, with their parents: to put each copy on the copy of its parent afterwards.
    static readonly List<(Sprocket.Vehicles.VehicleObject Part, Sprocket.Vehicles.VehicleObject Parent)> added = new();

    [HarmonyPrefix, HarmonyPatch(typeof(Sprocket.VehicleDesigner.Operations.VehicleOperations), nameof(Sprocket.VehicleDesigner.Operations.VehicleOperations.Duplicate))]
    static void WholeTurret(ref Il2CppReferenceArray<Sprocket.Vehicles.ISoftVehicleObject> instances)
    {
        added.Clear();
        Il2CppReferenceArray<Sprocket.Vehicles.ISoftVehicleObject>? more = null;
        var given = instances;
        Ui.Guard("Turret copy", () => more = WithEverythingOnRings(given));
        if (more != null) instances = more;
    }

    static Il2CppReferenceArray<Sprocket.Vehicles.ISoftVehicleObject>? WithEverythingOnRings(Il2CppReferenceArray<Sprocket.Vehicles.ISoftVehicleObject> instances)
    {
        var sources = instances.Select(i => i?.Object).Where(o => o != null).ToList();
        if (!sources.Any(o => o!.GUID == Conversion.RingGuid)) return null;
        var have = sources.Select(o => o!.Pointer).ToHashSet();
        void Take(Sprocket.Vehicles.VehicleObject parent)
        {
            var children = parent.GetComponent<Sprocket.Vehicles.VehicleTransform>()?.Children;
            if (children == null) return;
            for (int i = 0; i < children.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<Sprocket.Vehicles.VehicleTransform>>().Count; i++)
                if (children[i]?.VehicleObject is { } child && have.Add(child.Pointer)) { added.Add((child, parent)); Take(child); }
        }
        foreach (var ring in sources.Where(o => o!.GUID == Conversion.RingGuid).ToList()) Take(ring!);
        if (added.Count == 0) return null;
        Plugin.ModLog.LogInfo($"Turret copy: copying {added.Count} parts on the ring too");
        return new Il2CppReferenceArray<Sprocket.Vehicles.ISoftVehicleObject>(instances.Concat(added.Select(a => a.Part.GetReference())).ToArray());
    }

    [HarmonyPostfix, HarmonyPatch(typeof(Sprocket.VehicleDesigner.Operations.VehicleOperations), nameof(Sprocket.VehicleDesigner.Operations.VehicleOperations.Duplicate))]
    static void Reattach(Sprocket.VehicleDesigner.Operations.VehicleOperations __instance, Sprocket.Vehicles.Operations.Duplicate __result, int groupID) => Ui.Guard("Turret copy", () =>
    {
        if (added.Count == 0 || __result == null) return;
        int moved = 0, missing = 0;
        foreach (var (part, parent) in added)
        {
            var copy = __result.GetDupe(part);
            var copyParent = __result.GetDupe(parent);
            if (copy == null || copyParent == null) { missing++; continue; }
            if (copy.GetComponent<Sprocket.Vehicles.VehicleTransform>()?.Parent?.VehicleObject?.Pointer == copyParent.Pointer) continue;
            __instance.SetParent(copyParent.GetReference(), new Il2CppReferenceArray<Sprocket.Vehicles.ISoftVehicleObject>(new[] { copy.GetReference() }), groupID);
            moved++;
        }
        Plugin.ModLog.LogInfo($"Turret copy: {added.Count} parts copied with the ring; {moved} moved onto the new ring, {added.Count - moved - missing} already on it" +
                              (missing > 0 ? $", {missing} copies not found" : ""));
        added.Clear();
    });

    [HarmonyPostfix, HarmonyPatch(typeof(PartDefinitionIO), nameof(PartDefinitionIO.DeserializePartDefinitionJSON))]
    static void AllowCopy(PartDefinition __result) => Ui.Guard("Turret copy", () =>
    {
        if (__result?.guid != Conversion.RingGuid || __result.transform == null) return;
        __result.transform.options |= PartOptions.Duplication;
        Plugin.ModLog.LogInfo("Turret copy: turret rings can be copied with Alt");
    });
}
