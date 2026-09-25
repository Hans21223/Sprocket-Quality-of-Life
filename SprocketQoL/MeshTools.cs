using HarmonyLib;
using Il2CppInterop.Runtime;
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
        bool ctrl = keys.ctrlKey.isPressed;
        if (keys.f5Key.wasPressedThisFrame) ToggleShadows();
        if (keys.f6Key.wasPressedThisFrame) ToggleFlashlight();
        AimFlashlight();
        if (headlight != null && Camera.main != null) headlight.transform.rotation = Camera.main.transform.rotation;
        if (keys.numpad1Key.wasPressedThisFrame) LookFrom(ctrl ? Vector3.forward : Vector3.back);   // front (Ctrl: back)
        if (keys.numpad3Key.wasPressedThisFrame) LookFrom(ctrl ? Vector3.right : Vector3.left);     // right side (Ctrl: left)
        if (keys.numpad7Key.wasPressedThisFrame) LookFrom(ctrl ? Vector3.up : Vector3.down);         // top (Ctrl: from below)
        if (keys.numpad9Key.wasPressedThisFrame) LookFrom(-(held ?? shown));                         // the opposite view
        if (ctrl || keys.altKey.isPressed || keys.shiftKey.isPressed) return; // those belong to the game
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

    // ---------- shadows (F5) ----------

    // Lights whose shadows are off, with what they had, to give it back exactly.
    static readonly List<(Light Light, LightShadows Was)> shadowless = new();

    static void ToggleShadows() => Ui.Guard("Shadows", () =>
    {
        if (shadowless.Count > 0)
        {
            foreach (var (light, was) in shadowless)
                if (light != null)
                {
                    light.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>()?.EnableShadows(true);
                    light.shadows = was;
                }
            DesignEditor.Instance?.Say($"Shadows back on ({shadowless.Count} lights)", 3);
            shadowless.Clear();
            if (headlight != null) UnityEngine.Object.Destroy(headlight);
            headlight = null;
            return;
        }
        Light? sun = null;
        foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Light>()))
            if (o.TryCast<Light>() is { } light && light.shadows != LightShadows.None)
            {
                if (light.type == LightType.Directional && (sun == null || light.intensity > sun.intensity)) sun = light;
                shadowless.Add((light, light.shadows));
                light.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>()?.EnableShadows(false);
                light.shadows = LightShadows.None;
            }
        if (sun != null) headlight = Headlight(sun);
        DesignEditor.Instance?.Say(shadowless.Count > 0 ? $"Shadows off ({shadowless.Count} lights), F5 to turn them back on" : "No light casting shadows found", 3);
        Plugin.ModLog.LogInfo($"Shadows off on {shadowless.Count} lights");
    });

    // With shadows off, a headlight lights whatever the camera looks at, so the sides turned from the sun aren't black.
    static GameObject? headlight;
    const float HeadlightShare = 0.6f; // of the sun's strength

    /// A new shadowless light with the sun's colour and part of its strength (a new object, not a copy of the sun's,
    /// so none of the game's scripts on the sun run twice). Turned with the camera every frame by Keys.
    static GameObject Headlight(Light sun)
    {
        var go = new GameObject("Quality of Life headlight");
        var light = go.AddComponent<Light>();
        light.type = LightType.Directional;
        light.color = sun.color;
        light.useColorTemperature = sun.useColorTemperature;
        light.colorTemperature = sun.colorTemperature;
        light.shadows = LightShadows.None;
        var hd = LikeTheSun(light, sun);
        hd.intensity = SunLux(sun) * HeadlightShare;
        hd.EnableShadows(false);
        light.shadows = LightShadows.None;
        if (Camera.main != null) go.transform.rotation = Camera.main.transform.rotation;
        Plugin.ModLog.LogInfo($"Shadows off: headlight at {hd.intensity:0} ({HeadlightShare:P0} of the sun '{sun.name}')");
        return go;
    }

    /// A new light lights what the sun lights: the same light layers (HDRP lights only reach objects on their layers,
    /// and a new light starts on the default one). Returns its HDRP settings.
    static UnityEngine.Rendering.HighDefinition.HDAdditionalLightData LikeTheSun(Light light, Light sun)
    {
        light.cullingMask = sun.cullingMask;
        light.renderingLayerMask = sun.renderingLayerMask;
        var hd = light.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>() ?? light.gameObject.AddComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>();
        if (sun.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>() is { } sunHd) hd.lightlayersMask = sunHd.lightlayersMask;
        return hd;
    }

    static float SunLux(Light sun) => sun.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>() is { } hd ? hd.intensity : sun.intensity;

    /// The brightest directional light that isn't one of ours.
    static Light? Sun()
    {
        Light? sun = null;
        foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Light>()))
            if (o.TryCast<Light>() is { } light && light.type == LightType.Directional && !light.name.StartsWith("Quality of Life") && (sun == null || light.intensity > sun.intensity)) sun = light;
        return sun;
    }

    // ---------- mouse flashlight (F6) ----------

    static GameObject? flashlight;
    static Light? flashLight;
    static UnityEngine.Rendering.HighDefinition.HDAdditionalLightData? flashHd;
    static float flashLux;
    const float FlashShare = 0.8f; // of the sun's light, landing on what the mouse points at

    static void ToggleFlashlight() => Ui.Guard("Flashlight", () =>
    {
        if (flashlight != null)
        {
            UnityEngine.Object.Destroy(flashlight);
            flashlight = null;
            DesignEditor.Instance?.Say("Flashlight off", 2);
            return;
        }
        var sun = Sun();
        if (sun == null) { DesignEditor.Instance?.Say("Flashlight: no sun in this scene to match", 3); return; }
        flashlight = new GameObject("Quality of Life flashlight");
        flashLight = flashlight.AddComponent<Light>();
        flashLight.type = LightType.Spot;
        flashLight.spotAngle = 40;
        flashLight.color = sun.color;
        flashLight.useColorTemperature = sun.useColorTemperature;
        flashLight.colorTemperature = sun.colorTemperature;
        flashLight.shadows = LightShadows.None;
        flashHd = LikeTheSun(flashLight, sun);
        flashHd.EnableShadows(false);
        flashLight.shadows = LightShadows.None;
        flashLux = SunLux(sun);
        AimFlashlight();
        DesignEditor.Instance?.Say("Flashlight on: it points where the mouse points (F6 to turn off)", 3);
        Plugin.ModLog.LogInfo($"Flashlight on ({FlashShare:P0} of the sun's {flashLux:0} lux where it lands)");
    });

    /// From the camera along the mouse, as strong as needed to put a set share of the sun's light on what it hits
    /// (candela = lux × distance²), so it's as bright near or far.
    static void AimFlashlight()
    {
        if (flashlight == null || flashLight == null || flashHd == null) return;
        var cam = Camera.main;
        if (cam == null) return;
        var ray = Mouse.current is { } mouse ? cam.ScreenPointToRay(mouse.position.ReadValue()) : cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0));
        float distance = float.MaxValue;
        foreach (var hit in Physics.RaycastAll(ray, 2000f))
            if (hit.distance < distance && hit.collider != null) distance = hit.distance;
        if (distance == float.MaxValue) distance = (orbit?.TargetDistance ?? 10) + (ortho && orthoWhole ? PullBack : 0);
        flashlight.transform.SetPositionAndRotation(ray.origin, Quaternion.LookRotation(ray.direction));
        flashLight.range = distance * 1.5f + 5;
        flashHd.SetIntensity(flashLux * FlashShare * distance * distance, UnityEngine.Rendering.LightUnit.Candela);
    }

    // ---------- orthographic view ----------

    static bool ortho, orthoWhole = true, orthoLock = true, orthoLogged;
    static float orthoZoom = 1; // on top of the orbit distance, which stops at the game's closest zoom
    static float? farBefore;
    const float PullBack = 100; // metres the camera steps back in orthographic view, so it never cuts into the vehicle
    static Sprocket.OrbitalMovementController? orbit;

    static void ToggleOrtho()
    {
        ortho = !ortho;
        if (!ortho) { held = null; PutCameraBack(); }
        Plugin.ModLog.LogInfo($"Orthographic view {(ortho ? "on" : "off")}");
    }

    /// Perspective again, where the game put the camera, with its own far cut.
    static void PutCameraBack()
    {
        var cam = Camera.main;
        if (cam == null) return;
        cam.orthographic = false;
        if (orbit != null) cam.transform.SetPositionAndRotation(orbit.AppliedPosition, orbit.AppliedRotation);
        if (farBefore is { } far) { cam.farClipPlane = far; farBefore = null; }
        Ground(cam, default, hide: false);
    }

    // The ground, hidden while looking from below: by its layer, or by its own renderers if a vehicle part shares the
    // layer (then hiding the layer would hide the part too).
    static int? groundLayer;
    static int maskBefore;
    static bool groundTried;
    static readonly List<Renderer> groundHidden = new();

    static void Ground(Camera cam, Vector3 at, bool hide)
    {
        if (!hide)
        {
            if (groundLayer != null) { cam.cullingMask = maskBefore; groundLayer = null; }
            foreach (var r in groundHidden) if (r != null) r.enabled = true;
            groundHidden.Clear();
            groundTried = false;
            return;
        }
        if (groundTried) return;
        groundTried = true;
        // The first thing under the point in view that isn't part of the vehicle.
        Collider? ground = null;
        foreach (var hit in Physics.RaycastAll(at + Vector3.up * 50, Vector3.down, 1000f).OrderBy(h => h.distance))
            if (hit.collider != null && hit.collider.GetComponentInParent<Sprocket.Vehicles.VehicleObject>() == null) { ground = hit.collider; break; }
        if (ground == null) { Plugin.ModLog.LogInfo("View from below: found no ground under the vehicle"); return; }
        int layer = ground.gameObject.layer;
        bool shared = DesignEditor.Instance?.AllParts().Any(p => p.GetComponentsInChildren<Renderer>().Any(r => r.gameObject.layer == layer)) == true;
        if (!shared)
        {
            maskBefore = cam.cullingMask;
            cam.cullingMask &= ~(1 << layer);
            groundLayer = layer;
        }
        else
            foreach (var r in ground.GetComponentsInChildren<Renderer>().Concat(ground.GetComponentsInParent<Renderer>()))
                if (r.enabled) { r.enabled = false; groundHidden.Add(r); }
        Plugin.ModLog.LogInfo($"View from below: hiding the ground '{ground.gameObject.name}' " +
                              (shared ? $"({groundHidden.Count} of its renderers; a vehicle part shares its layer {layer})" : $"(layer {layer})"));
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
        // Sized by the zoom the player set (the target distance), not the distance the orbit is at right now: that one
        // moves a little every frame as the camera keeps out of parts, and would shake the view.
        cam.orthographicSize = Math.Max(0.005f, __instance.TargetDistance * MathF.Tan(cam.fieldOfView * MathF.PI / 360) / orthoZoom);
        float back = orthoWhole ? PullBack : 0;
        // Where the orbit is heading: it only changes when the player turns the camera, not while it's still easing.
        var aim = __instance.TargetRotation * Vector3.forward;
        if (held != null && Vector3.Angle(aim, heldAim) > 2) held = null; // the player orbited: back to snapping
        if (orthoLock || held != null)
        {
            // A view picked with Numpad 1 / 3 / 7, else the straight view nearest to where the orbit is heading; at the
            // point the orbit is heading to look at (steady: no easing or shake in it), so panning still works.
            var at = __instance.TargetPosition + aim * __instance.TargetDistance;
            var dir = held ?? Straight.OrderByDescending(d => Vector3.Dot(d, aim)).First();
            shown = dir;
            // Looking straight down or up, the vehicle's front is at the top of the screen.
            cam.transform.rotation = Quaternion.LookRotation(dir, Mathf.Abs(dir.y) > 0.5f ? Vector3.forward : Vector3.up);
            cam.transform.position = at - dir * (__instance.TargetDistance + back);
            Ground(cam, at, hide: dir.y > 0.5f); // from below, the ground is in the way
        }
        // Where the game puts the camera, set whole every time (the game doesn't always set it again, so a step taken
        // from wherever the camera is would add up and fly away), then straight back along that same view: an
        // orthographic picture doesn't change along its own view, so this can't shake either.
        else
        {
            var rotation = __instance.AppliedRotation;
            cam.transform.SetPositionAndRotation(__instance.AppliedPosition - rotation * Vector3.forward * back, rotation);
            Ground(cam, default, hide: false);
        }
        if (back <= 0) return;
        farBefore ??= cam.farClipPlane;
        cam.farClipPlane = Math.Max(farBefore.Value, __instance.Distance + back + 1000);
    });

    // The straight views, as the direction the camera looks: from the front, back, right, left, and from above.
    // (From below only with Ctrl+Numpad 7: the game's orbit camera can't go under the ground.)
    static readonly Vector3[] Straight = { Vector3.back, Vector3.forward, Vector3.left, Vector3.right, Vector3.down };

    // A view picked with Numpad 1 / 3 / 7, held until the player turns the camera (the orbit's own heading then).
    static Vector3? held;
    static Vector3 shown = Vector3.back; // the straight view on screen
    static Vector3 heldAim;

    /// Numpad 1 / 3 / 7 (Ctrl: the opposite side): orthographic view from the front, the side or the top. The game's
    /// orbit camera isn't turned (it would ease back to its own heading); the view is held until you orbit.
    static void LookFrom(Vector3 dir)
    {
        if (!ortho) ToggleOrtho();
        held = dir;
        Plugin.ModLog.LogInfo($"Orthographic view held looking {dir} (Ctrl {(Keyboard.current?.ctrlKey.isPressed == true ? "held" : "not held")})");
        heldAim = orbit != null ? orbit.TargetRotation * Vector3.forward : Vector3.forward;
    }

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
        ui.ToggleField("Ortho: straight views", orthoLock, Ui.BoolCallback(v => orthoLock = v),
            "Orthographic view snaps to front, back, sides or top (orbiting flips between them). Numpad 1 / 3 / 7: front, side, top; " +
            "with Ctrl, back and the other side. Off: orbit freely.");
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
